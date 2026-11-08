using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Intake;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Bug round 2 (articles, intake, assist): a triage decision or an attach that lands just after a colleague's promotion
/// no longer undoes it, and a report link given with tracking tags is fetched under its own feed's setting.
/// </summary>
public class ArticlesBugRound2Tests : IDisposable
{
    private readonly TestDb _db = new();

    public ArticlesBugRound2Tests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<Guid[]> Items(params string[] titles)
    {
        var source = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "News", Url = "https://news.example.com/feed", PollMinutes = 30 });
        await using var ctx = _db.Context();
        var items = titles.Select((t, n) => new FeedItem
        {
            SourceId = source, Title = t, Url = $"https://news.example.com/{n}", UrlHash = $"r2-{n}", FetchedAtUtc = DateTimeOffset.UtcNow, Cves = "CVE-2026-1281",
        }).ToList();
        ctx.FeedItems.AddRange(items);
        await ctx.SaveChangesAsync();
        return [.. items.Select(i => i.Id)];
    }

    // A colleague's promotion of the item lands between this service's read and its write: just before the command the
    // matching (by default the write to the item itself).
    private void ColleaguePromotesFirst(Guid item, Guid article, Func<string, bool>? before = null) => _db.Commands.Before(
        before ?? (sql => sql.Contains("UPDATE", StringComparison.Ordinal) && sql.Contains("FeedItems", StringComparison.Ordinal)),
        () =>
        {
            using var ctx = _db.PlainContext();
            ctx.FeedItems.Where(i => i.Id == item).ExecuteUpdate(u => u.SetProperty(i => i.Status, FeedItemStatus.Promoted)
                .SetProperty(i => i.ArticleId, article).SetProperty(i => i.StatusByName, "Robin"));
        });

    private async Task<FeedItem> Item(Guid id)
    {
        await using var ctx = _db.Context();
        return await ctx.FeedItems.AsNoTracking().SingleAsync(i => i.Id == id);
    }

    public static TheoryData<string> Decisions => new() { "dismiss", "shortlist", "restore", "dismiss-many", "shortlist-many", "same-cve" };

    [Theory]
    [MemberData(nameof(Decisions))]
    public async Task ADecisionLandingJustAfterAColleaguesPromotion_LeavesItPromoted(string decision)
    {
        // R2-1: the decision read the item as new, the promotion landed, and the decision's save marked the promoted item
        // Dismissed (or Shortlisted, or New), its article still pointing at it.
        var ids = await Items("Ivanti flaws chained", "Ivanti flaws exploited");
        var article = await _db.Articles().CreateAsync("Robin's article");
        ColleaguePromotesFirst(ids[1], article);

        var intake = _db.Intake();
        var act = decision switch
        {
            "dismiss" => () => intake.DismissAsync(ids[1], null),
            "shortlist" => () => intake.ShortlistAsync(ids[1]),
            "restore" => () => intake.RestoreAsync(ids[1]),
            "dismiss-many" => () => intake.DismissManyAsync([ids[1]], null),
            "shortlist-many" => () => intake.ShortlistManyAsync([ids[1]]),
            _ => (Func<Task>)(() => intake.DismissSameCveAsync(ids[0], [ids[1]])),
        };
        if (decision is "dismiss" or "shortlist" or "restore")
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already an article*");
        else await act();

        var item = await Item(ids[1]);
        item.Status.Should().Be(FeedItemStatus.Promoted);
        item.ArticleId.Should().Be(article);
        item.StatusByName.Should().Be("Robin");
    }

    [Fact]
    public async Task ADecisionOnAnItemNoOneTouched_StillLands()
    {
        var ids = await Items("Ivanti flaws chained", "Ivanti flaws exploited");
        var intake = _db.Intake();

        await intake.DismissAsync(ids[0], "Out of scope");
        (await intake.DismissManyAsync([ids[1]], null)).Should().Be(1);

        var item = await Item(ids[0]);
        (item.Status, item.DismissReason, item.StatusByName).Should().Be((FeedItemStatus.Dismissed, "Out of scope", "Test Analyst"));
        item.StatusAtUtc.Should().NotBeNull();
        await intake.RestoreAsync(ids[0]);
        item = await Item(ids[0]);
        (item.Status, item.DismissReason, item.StatusByName, item.StatusAtUtc).Should().Be((FeedItemStatus.New, (string?)null, (string?)null, (DateTimeOffset?)null));
        await FluentActions.Invoking(() => intake.DismissAsync(Guid.NewGuid(), null)).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task AnAttachLandingJustAfterAColleaguesPromotion_IsRefused()
    {
        // R2-2: the attach read the item as new, the promotion landed, and the attach's save pointed the item at its own
        // article, leaving the colleague's article without its item and the item's link on two articles.
        var ids = await Items("Ivanti flaws chained");
        var theirs = await _db.Articles().CreateAsync("Robin's article");
        var mine = await _db.Articles().CreateAsync("My article");
        ColleaguePromotesFirst(ids[0], theirs, sql => sql.Contains("FROM \"ArticleLinks\"", StringComparison.Ordinal));   // after the item is read

        await FluentActions.Invoking(() => _db.Intake().AttachAsync(ids[0], mine)).Should().ThrowAsync<InvalidOperationException>();

        (await Item(ids[0])).ArticleId.Should().Be(theirs);
        (await _db.Articles().LinksAsync(mine)).Should().BeEmpty("nothing of the item's is kept on the article it wasn't attached to");
        (await _db.Articles().GetAsync(mine))!.Cves.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAttachNoOneRaced_StillAddsTheReport()
    {
        var ids = await Items("Ivanti flaws chained");
        var mine = await _db.Articles().CreateAsync("My article");

        await _db.Intake().AttachAsync(ids[0], mine);

        var item = await Item(ids[0]);
        (item.Status, item.ArticleId).Should().Be((FeedItemStatus.Promoted, (Guid?)mine));
        (await _db.Articles().LinksAsync(mine)).Single().Url.Should().Be("https://news.example.com/0");
        (await _db.Articles().GetAsync(mine))!.Cves.Select(c => c.CveId).Should().Equal("CVE-2026-1281");
    }

    [Fact]
    public async Task AReportLinkWithTrackingTags_IsFetchedUnderItsOwnFeedsSetting()
    {
        // R2-3: the fetch looked the address up as given, found no feed item (they're stored canonical), and fell back to
        // the article's own feed: a report from a feed marked Never was fetched.
        var open = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "Open", Url = "https://one.example.com/feed", PollMinutes = 30 });
        var never = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "Closed", Url = "https://two.example.com/feed", PollMinutes = 30, ArticleFetch = ArticleFetch.Never });
        var article = await _db.Articles().CreateAsync("Ivanti flaws chained", "Open", "https://one.example.com/ivanti");
        await using (var ctx = _db.Context())
        {
            ctx.FeedItems.AddRange(
                new FeedItem { SourceId = open, Title = "Ivanti flaws chained", Url = "https://one.example.com/ivanti", UrlHash = "r2-open", FetchedAtUtc = DateTimeOffset.UtcNow, ArticleId = article, Status = FeedItemStatus.Promoted },
                new FeedItem { SourceId = never, Title = "Ivanti flaws exploited", Url = "https://two.example.com/ivanti", UrlHash = "r2-never", FetchedAtUtc = DateTimeOffset.UtcNow });
            await ctx.SaveChangesAsync();
        }

        var got = await _db.Assist().FetchAsync(article, "https://two.example.com/ivanti?utm_source=rss", null, TlpLevel.Clear);

        got.Text.Should().BeNull();
        got.Problem.Should().Contain("Closed");
        _db.Http.Requests.Should().BeEmpty();
    }
}
