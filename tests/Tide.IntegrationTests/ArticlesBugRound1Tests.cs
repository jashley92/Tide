using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Adversaries;
using Tide.Application.Articles;
using Tide.Application.Assist;
using Tide.Application.Intake;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Bug round 1 (articles, intake, assist, search, stack, adversaries): another report's text and feed summary go to the
/// AI provider at their own feed's TLP, two analysts can read one item at once, a product's and a profile's history date
/// an article by the organization's day, the article list sorts titles ignoring case on SQLite too, and a merge keeps
/// whole names.
/// </summary>
public class ArticlesBugRound1Tests : IDisposable
{
    private readonly TestDb _db = new();
    private const string First = "https://one.example.com/ivanti", Second = "https://two.example.com/ivanti";

    private static string Page(string words) => "<html><body><article>" + string.Concat(Enumerable.Range(0, 6).Select(i =>
        $"<p>{words} Attackers chained two Ivanti Connect Secure flaws on gateway {i}, and the vendor shipped fixes for every branch.</p>")) + "</article></body></html>";

    public ArticlesBugRound1Tests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    // Two reports promoted as one article: the first (newest, from a TLP:CLEAR feed) makes it; the second, from a feed
    // marked secondTlp, was read in triage and is an other report of it.
    private async Task<(Guid Article, Guid Lead, Guid Other)> PromotedGroup(TlpLevel secondTlp)
    {
        var one = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "One", Url = "https://one.example.com/feed", PollMinutes = 30 });
        var two = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "Two", Url = "https://two.example.com/feed", PollMinutes = 30, DefaultTlp = secondTlp });
        await using var ctx = _db.Context();
        var a = new FeedItem { SourceId = one, Title = "Ivanti flaws chained", Url = First, UrlHash = "r1-a", FetchedAtUtc = DateTimeOffset.UtcNow };
        var b = new FeedItem { SourceId = two, Title = "Ivanti flaws exploited", Url = Second, UrlHash = "r1-b", FetchedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
        ctx.FeedItems.AddRange(a, b);
        await ctx.SaveChangesAsync();
        _db.Http.Set(Second, Page("More than 20 victims."));
        await _db.Reading().ReadAsync(b.Id);
        var article = await _db.Intake().PromoteAsOneAsync([a.Id, b.Id], new PromoteForm { Title = "Ivanti flaws chained", Classification = Classification.Informational, Tlp = TlpLevel.Clear });
        return (article, a.Id, b.Id);
    }

    [Fact]
    public async Task AnotherReportsTextReadInTriage_IsntOfferedAsTheArticlesOwn()
    {
        // R1-1: the editor sent the kept text as the article's own source, at the article's own feed's TLP. With only
        // the AMBER report read, that put its text in a draft sent under a GREEN limit, and named it after the wrong outlet.
        var (article, _, _) = await PromotedGroup(TlpLevel.Amber);
        (await _db.Articles().GetAsync(article))!.SourceUrl.Should().Be(First);

        (await _db.Assist().KeptTextAsync(article)).Should().BeNull("only the other report was read in triage");
        (await _db.Assist().OtherSourcesAsync(article)).Single().Text.Should().Contain("More than 20 victims.", "it's offered as itself, at its own TLP");
    }

    [Fact]
    public async Task TheArticlesOwnTextReadInTriage_IsStillOffered()
    {
        var (article, lead, _) = await PromotedGroup(TlpLevel.Clear);
        _db.Http.Set(First, Page("The lead report."));
        await _db.Reading().ReadAsync(lead);

        (await _db.Assist().KeptTextAsync(article))!.Text.Should().Contain("The lead report.");
    }

    [Fact]
    public async Task AFeedSummaryFromAnAmberReport_KeepsTheDraftHome_UnderAGreenLimit()
    {
        // R1-1: the feed summary is the first excerpt on the article's items, which can be another report's.
        var (article, _, other) = await PromotedGroup(TlpLevel.Amber);
        await using (var ctx = _db.Context())
            await ctx.FeedItems.Where(i => i.Id == other).ExecuteUpdateAsync(u => u.SetProperty(i => i.Excerpt, "An AMBER feed's summary."));
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);

        await FluentActions.Invoking(() => _db.Assist().DraftAsync(article, form, [new DraftSource("One", Page("Pasted."), null, First)]))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*TLP:AMBER*");
        await FluentActions.Invoking(() => _db.Assist().PromptAsync(article, form, [new DraftSource("One", Page("Pasted."), null, First)]))
            .Should().ThrowAsync<InvalidOperationException>();
        _db.Ai.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task ASourceAtATrackedAddress_GoesAtItsFeedsTlp()
    {
        // R1-1: feed items are stored at their canonical address, so a source given with tracking tags still finds its feed.
        var (article, _, _) = await PromotedGroup(TlpLevel.Amber);
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);

        await FluentActions.Invoking(() => _db.Assist().DraftAsync(article, form, [new DraftSource("Two", "text", null, Second + "?utm_source=rss")]))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*TLP:AMBER*");
        _db.Ai.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task TwoAnalystsReadingOneItemAtOnce_BothGetTheText()
    {
        // R1-2: both found nothing kept, and the second to finish failed on the first's row (an error, not the text).
        var source = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "News", Url = "https://one.example.com/feed", PollMinutes = 30 });
        Guid id;
        await using (var ctx = _db.Context())
        {
            var item = new FeedItem { SourceId = source, Title = "Ivanti flaws chained", Url = First, UrlHash = "r1-read", FetchedAtUtc = DateTimeOffset.UtcNow };
            ctx.FeedItems.Add(item);
            await ctx.SaveChangesAsync();
            id = item.Id;
        }
        _db.Http.Set(First, Page("Read twice."));
        _db.Http.During = async () => await _db.Reading().ReadAsync(id);   // a colleague's read finishes while this one fetches

        var read = await _db.Reading().ReadAsync(id);

        read.HasText.Should().BeTrue();
        await using var check = _db.Context();
        (await check.FeedItemTexts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AProductsAndAProfilesHistory_DateAnArticleByTheOrganizationsDay()
    {
        // R1-3: an article written at 10pm in New York (the next day in UTC) showed under the next day.
        var article = await _db.Articles().CreateAsync("GitLab runners hit by Scattered Spider", "Vendor", null, TlpLevel.Clear);
        var evening = new DateTimeOffset(2026, 10, 2, 2, 0, 0, TimeSpan.Zero);   // 1 Oct, 10pm in New York
        await using (var ctx = _db.Context())
            await ctx.Articles.Where(a => a.Id == article).ExecuteUpdateAsync(u => u.SetProperty(a => a.CreatedAtUtc, evening));
        var product = await _db.Stack().SaveAsync(new StackForm { Name = "GitLab" });
        var profile = await _db.Adversaries().SaveAsync(new AdversaryForm { Name = "Scattered Spider" });

        (await _db.Stack().GetAsync(product))!.Articles.Single().On.Should().Be(new DateOnly(2026, 10, 1));
        (await _db.Adversaries().GetAsync(profile))!.Articles.Single().On.Should().Be(new DateOnly(2026, 10, 1));
    }

    [Fact]
    public async Task TheArticleList_SortsTitlesIgnoringCase()
    {
        // R1-4: SQLite compared titles byte by byte, so "apple" sorted after "Zebra" (SQL Server's collation didn't).
        foreach (var title in new[] { "Zebra botnet", "apple patches", "Mango loader" })
            await _db.Articles().CreateAsync(title, "Vendor", null, TlpLevel.Clear);

        var asc = await _db.Articles().PageAsync(null, null, ArticleSort.Title, descending: false, take: 10);
        var desc = await _db.Articles().PageAsync(null, null, ArticleSort.Title, descending: true, take: 10);

        asc.Rows.Select(r => r.Title).Should().Equal("apple patches", "Mango loader", "Zebra botnet");
        desc.Rows.Select(r => r.Title).Should().Equal("Zebra botnet", "Mango loader", "apple patches");
    }

    [Fact]
    public async Task AMerge_KeepsWholeNames_NoMoreThanTheFormTakes()
    {
        // R1-5: the merged names were cut at the column's length, part way through a name, and could be more than the
        // profile's form accepts, so it couldn't be saved again.
        var names = Enumerable.Range(1, 30).Select(i => $"Alias number {i:00} of the first").ToList();
        var others = Enumerable.Range(1, 30).Select(i => $"Alias number {i:00} of the second").ToList();
        var into = await _db.Adversaries().SaveAsync(new AdversaryForm { Name = "Into", Aliases = string.Join("; ", names) });
        var from = await _db.Adversaries().SaveAsync(new AdversaryForm { Name = "From", Aliases = string.Join("; ", others) });

        await _db.Adversaries().MergeAsync(from, into);

        var kept = StackService.SplitNames((await _db.Adversaries().GetAsync(into))!.Adversary.Aliases);
        kept.Should().OnlyContain(n => names.Contains(n) || others.Contains(n) || n == "From", "every name is whole");
        kept.Count.Should().BeLessThanOrEqualTo(AdversaryService.MaxAliases);
        await _db.Adversaries().SaveAsync(new AdversaryForm { Id = into, Name = "Into", Aliases = string.Join("; ", kept) });
    }
}
