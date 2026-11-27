using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Intake;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// P4, the service: several items promoted as one whatever their feeds (held to a group's size), and the KEV round-up
/// (bigger, every item naming a KEV-listed CVE, the draft with a bullet per CVE from TIDE's records).
/// </summary>
public class KevRoundupServiceTests : IDisposable
{
    private readonly TestDb _db = new(connectionPerContext: true);

    public void Dispose() => _db.Dispose();

    // Items from one feed, newest first, each naming a CVE of its own; those in kev are on KEV in TIDE's records.
    private async Task<List<Guid>> Seed(int count, Func<int, bool>? kev = null, FeedItemStatus status = FeedItemStatus.New, int hoursAgo = 0)
    {
        kev ??= _ => true;
        await using var ctx = _db.Context();
        var source = new Source { Name = "KEV feed " + Guid.NewGuid().ToString("N")[..6], Url = $"https://kev.example/{Guid.NewGuid():N}", DefaultTlp = TlpLevel.Clear };
        ctx.Add(source);
        var at = DateTimeOffset.UtcNow.AddHours(-hoursAgo);
        var items = Enumerable.Range(0, count).Select(n =>
        {
            var cve = $"CVE-2026-{Random.Shared.Next(10000, 99999)}";
            if (kev(n))
                ctx.Add(new CveRecord { CveId = cve, IsKev = true, CvssScore = 8.8m, EpssScore = .5m, KevDueDate = new DateOnly(2026, 12, 14), Vendor = "Plex", Product = "Media Server" });
            return new FeedItem
            {
                Source = source, Title = $"KEV addition {n}: {cve}", Url = $"https://kev.example/{cve}", UrlHash = cve, Cves = cve,
                PublishedAtUtc = at.AddMinutes(-n), FetchedAtUtc = at.AddMinutes(-n), Status = status,
            };
        }).ToList();
        ctx.AddRange(items);
        await ctx.SaveChangesAsync();
        return [.. items.Select(i => i.Id)];
    }

    private static PromoteForm Form(bool roundup) => new() { Title = "KEV round-up", Classification = Classification.Threat, Priority = Priority.P2, AsKevRoundup = roundup };

    [Fact]
    public async Task ARoundUp_OfMoreThanAGroup_FromOneFeed_IsOneDraft_WithABulletPerKevCve_AndTheRecommendation()
    {
        var ids = await Seed(12);

        var outcome = await _db.Intake().PromoteOrJoinAsync(ids, Form(roundup: true));

        outcome.Made.Should().BeTrue();
        await using var ctx = _db.Context();
        var article = await ctx.Articles.Include(a => a.Bullets).Include(a => a.Cves).SingleAsync(a => a.Id == outcome.ArticleId);
        article.Bullets.Should().HaveCount(12).And.OnlyContain(b => b.Text.Contains("Plex Media Server. CVSS 8.8, EPSS 50%, due 2026-12-14") && b.Tlp == TlpLevel.Clear);
        article.Bullets.Select(b => b.SortOrder).Should().BeEquivalentTo(Enumerable.Range(0, 12));
        article.RecommendedActions.Should().Be("- " + KevRoundup.Recommended);
        article.Cves.Should().HaveCount(12);
        (await ctx.FeedItems.Where(i => ids.Contains(i.Id)).ToListAsync()).Should().OnlyContain(i => i.Status == FeedItemStatus.Promoted && i.ArticleId == outcome.ArticleId);
    }

    [Fact]
    public async Task ARoundUp_CanBeUndone_ItsBulletsGoWithTheDraft()
    {
        var ids = await Seed(3);
        var outcome = await _db.Intake().PromoteOrJoinAsync(ids, Form(roundup: true));

        (await _db.Intake().UndoPromoteAsync(outcome.Undo!)).Should().BeTrue();

        await using var ctx = _db.Context();
        (await ctx.Articles.AnyAsync(a => a.Id == outcome.ArticleId)).Should().BeFalse();
        (await ctx.FeedItems.Where(i => ids.Contains(i.Id)).ToListAsync()).Should().OnlyContain(i => i.Status == FeedItemStatus.New && i.ArticleId == null);
    }

    [Fact]
    public async Task APromoteOfSeveral_IsHeldToAGroupsSize_AndARoundUp_ToItsOwn()
    {
        var nine = await Seed(TriageGroups.MaxSize + 1);
        await FluentActions.Awaiting(() => _db.Intake().PromoteOrJoinAsync(nine, Form(roundup: false)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*1 to 8*");

        var tooMany = await Seed(KevRoundup.MaxItems + 1);
        await FluentActions.Awaiting(() => _db.Intake().PromoteOrJoinAsync(tooMany, Form(roundup: true)))
            .Should().ThrowAsync<ArgumentException>().WithMessage($"*1 to {KevRoundup.MaxItems}*");

        await using var ctx = _db.Context();
        (await ctx.FeedItems.CountAsync(i => i.Status == FeedItemStatus.Promoted)).Should().Be(0, "nothing was claimed");
    }

    [Fact]
    public async Task ARoundUp_RefusesAnItemNamingNoKevCve_AndClaimsNothing()
    {
        var ids = await Seed(3, kev: n => n != 1);

        await FluentActions.Awaiting(() => _db.Intake().PromoteOrJoinAsync(ids, Form(roundup: true)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*KEV addition 1*names no CVE on CISA's KEV list*");

        await using var ctx = _db.Context();
        (await ctx.FeedItems.Where(i => ids.Contains(i.Id)).ToListAsync()).Should().OnlyContain(i => i.Status == FeedItemStatus.New);
    }

    [Fact]
    public async Task TheRoundUpsItems_AreTheNewKevOnesSinceTheBrief_NewestFirst_HeldToItsSize()
    {
        var older = await Seed(2, hoursAgo: 30);
        var notKev = await Seed(2, kev: _ => false);
        var decided = await Seed(1, status: FeedItemStatus.Dismissed);
        var fresh = await Seed(KevRoundup.MaxItems + 2);

        var items = await _db.Intake().KevRoundupAsync(DateTimeOffset.UtcNow.AddHours(-24));

        items.Total.Should().Be(KevRoundup.MaxItems + 2);
        items.Ids.Should().Equal(fresh.Take(KevRoundup.MaxItems));
        items.Cves.Should().HaveCount(KevRoundup.MaxItems);
        items.Ids.Should().NotIntersectWith(older.Concat(notKev).Concat(decided));

        (await _db.Intake().KevRoundupAsync(null)).Total.Should().Be(KevRoundup.MaxItems + 4, "before any brief, every new KEV item counts");
    }
}

/// <summary>P4, the page: Space and Shift+J choose several items on this page, and the selection bar decides them
/// together; the KEV filter offers the round-up.</summary>
public class TriageMultiSelectTests(StayApp app) : IClassFixture<StayApp>
{
    private static string TitleOf(string key, int n) => $"pick-{key} report {n}: a gateway flaw is exploited";

    // Items of their own from one feed, newest first, each with a CVE of its own (on KEV when asked). Opened with
    // ?q=pick-<key>, the inbox lists only these.
    private async Task<Guid[]> Seed(string key, int count, bool kev = false)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        var source = new Source { Name = "Pick source " + key, Url = "https://pick.example/feed/" + key, DefaultTlp = TlpLevel.Green };
        db.Add(source);
        var at = DateTimeOffset.UtcNow.AddDays(1);
        var items = Enumerable.Range(1, count).Select(n =>
        {
            var cve = $"CVE-2025-{Math.Abs((key + n).GetHashCode()) % 90000 + 10000}";
            if (kev) db.Add(new CveRecord { CveId = cve, IsKev = true, CvssScore = 9.8m, KevDueDate = new DateOnly(2026, 12, 14) });
            return new FeedItem
            {
                Source = source, Title = TitleOf(key, n), Url = $"https://pick.example/{key}/{n}", UrlHash = $"pick-{key}-{n}", Cves = cve,
                PublishedAtUtc = at.AddMinutes(-n), FetchedAtUtc = at.AddMinutes(-n),
            };
        }).ToList();
        db.AddRange(items);
        await db.SaveChangesAsync();
        return [.. items.Select(i => i.Id)];
    }

    private async Task<List<FeedItem>> Items(IEnumerable<Guid> ids)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var wanted = ids.ToList();
        return await scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>().FeedItems.AsNoTracking()
            .Where(i => wanted.Contains(i.Id)).ToListAsync();
    }

    private static string Bar(string html)
    {
        var at = html.IndexOf("tide-triage-selbar", StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, "the selection bar is drawn");
        return html[at..html.IndexOf("</section>", at, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task SpaceAndShiftJ_ChooseItems_TheBarSaysHowMany_AndSShortlistsThemAll()
    {
        var ids = await Seed("s", 4);
        await using var r = await RenderAsync<Triage>(app, "/triage?q=pick-s");
        await r.HtmlOnce(TitleOf("s", 1));

        await r.Click(t => t.OnTriageKey("select-toggle"));
        await r.Click(t => t.OnTriageKey("select-next"));
        r.Component.PickedIds.Should().BeEquivalentTo(ids[..2]);
        var bar = Bar(await r.Html());
        bar.Should().Contain("<strong><span class=\"tide-odo\"><span class=\"tide-roll\" data-tide-tick>2</span></span> selected</strong> on this page").And.Contain("turning the page or changing the tab or filters clears the choice")
            .And.Contain("Promote as one").And.Contain("Shortlist").And.Contain("Dismiss");
        (await r.Html()).Should().Contain("aria-label=\"" + TitleOf("s", 1) + " (chosen)\"");

        await r.Click(t => t.OnTriageKey("shortlist"));

        (await Items(ids)).Count(i => i.Status == FeedItemStatus.Shortlisted).Should().Be(2);
        (await Items(ids[2..])).Should().OnlyContain(i => i.Status == FeedItemStatus.New, "only the chosen ones");
        r.Component.PickedIds.Should().BeEmpty();
        (await r.Html()).Should().NotContain("tide-triage-selbar");
    }

    [Fact]
    public async Task SpaceAgain_LeavesAnItemOut_AndEscClearsTheChoice()
    {
        await Seed("c", 3);
        await using var r = await RenderAsync<Triage>(app, "/triage?q=pick-c");
        await r.HtmlOnce(TitleOf("c", 1));

        await r.Click(t => t.OnTriageKey("select-next"));
        await r.Click(t => t.OnTriageKey("select-toggle"));
        r.Component.PickedIds.Should().HaveCount(1, "Space on the second, chosen by Shift+J, left it out");

        await r.Click(t => t.OnTriageKey("select-clear"));
        r.Component.PickedIds.Should().BeEmpty();
        (await r.Html()).Should().NotContain("tide-triage-selbar");
    }

    [Fact]
    public async Task P_OnTheChosen_PromotesThemAsOne_AndKeepsTriaging()
    {
        var ids = await Seed("p", 4);
        await using var r = await RenderAsync<Triage>(app, "/triage?q=pick-p");
        await r.HtmlOnce(TitleOf("p", 1));

        await r.Click(t => t.OnTriageKey("select-toggle"));
        await r.Click(t => t.OnTriageKey("select-next"));
        await r.Click(t => t.OnTriageKey("select-next"));
        await r.Click(t => t.OnTriageKey("promote"));
        var form = Bar(await r.Html());
        form.Should().Contain("tide-promote-form").And.Contain("Promote 3 as one and write");

        await r.Click(t => t.SubmitPromote(stay: true));

        var made = await Items(ids[..3]);
        made.Should().OnlyContain(i => i.Status == FeedItemStatus.Promoted && i.ArticleId == made[0].ArticleId && i.ArticleId != null);
        (await Items([ids[3]])).Single().Status.Should().Be(FeedItemStatus.New);
        r.Component.PickedIds.Should().BeEmpty("the promoted items left New, and the choice with them");
    }

    [Fact]
    public async Task TheKevFilter_OffersTheRoundUp_WhoseFormSaysWhatTheDraftGets()
    {
        await Seed("k", 3, kev: true);
        await using var r = await RenderAsync<Triage>(app, "/triage?q=pick-k&cve=kev");
        var html = await r.HtmlOnce("tide-triage-roundup");
        html.Should().Contain("tide-triage-roundup").And.Contain("KEV items").And.Contain("as one…</button>");

        // A render after it, as the button's click would have (the form has the keys: "toggle" only renders).
        await r.Click(async t =>
        {
            await t.OpenRoundup();
            await t.OnTriageKey("toggle");
        });

        Bar(await r.Html()).Should().Contain("A KEV round-up of").And.Contain(KevRoundup.Recommended).And.Contain("tide-promote-form");
    }

    [Fact]
    public void TheRoundUpsButton_SaysHowManyOfHowMany()
    {
        Triage.RoundupLabel(new KevRoundupItems([Guid.NewGuid(), Guid.NewGuid()], 2, [], TlpLevel.Clear), sinceBrief: true)
            .Should().Be("Promote the 2 KEV items since the last brief as one");
        Triage.RoundupLabel(new KevRoundupItems([.. Enumerable.Range(0, 20).Select(_ => Guid.NewGuid())], 27, [], TlpLevel.Clear), sinceBrief: false)
            .Should().Be("Promote the 20 newest of the 27 new KEV items as one");
    }
}
