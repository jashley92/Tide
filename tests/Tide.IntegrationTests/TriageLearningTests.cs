using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Adversaries;
using Tide.Application.Articles;
using Tide.Application.Intake;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>EP-81: triage ordered by what the team used before, each source's record, and the stories missed this week.</summary>
public class TriageLearningTests : IDisposable
{
    private TestDb _db = new();
    private int _n;

    public TriageLearningTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private Task<Guid> Source(string name) =>
        _db.Intake().SaveSourceAsync(new SourceForm { Name = name, Url = $"https://{name.Replace(' ', '-').ToLowerInvariant()}.example.com/feed", PollMinutes = 30 });

    private async Task<Guid> Item(Guid source, string title, FeedItemStatus status = FeedItemStatus.New, string? cves = null, double daysAgo = 0, double decidedAfterHours = 2)
    {
        var at = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        var item = new FeedItem
        {
            SourceId = source, Title = title, Url = $"https://items.example.com/{++_n}", UrlHash = "h" + _n, FetchedAtUtc = at, PublishedAtUtc = at, Status = status, Cves = cves,
            StatusAtUtc = status == FeedItemStatus.New ? null : at.AddHours(decidedAfterHours),
        };
        await using var ctx = _db.Context();
        ctx.FeedItems.Add(item);
        await ctx.SaveChangesAsync();
        return item.Id;
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task LikelyUsed_RanksByWhatWasPromoted_AndSaysWhyInCounts(string provider)
    {
        _db = _db.On(provider);
        var vendor = await Source("GitLab releases");
        var news = await Source("Industry News");
        await _db.Stack().SaveAsync(new StackForm { Name = "GitLab" });
        for (var i = 0; i < 4; i++) await Item(vendor, $"GitLab {i} security release", i < 3 ? FeedItemStatus.Promoted : FeedItemStatus.Dismissed, daysAgo: 5 + i);
        for (var i = 0; i < 5; i++) await Item(news, $"Webinar {i}", FeedItemStatus.Dismissed, daysAgo: 3 + i);
        for (var i = 0; i < 2; i++) await Item(vendor, $"Release notes {i}", i == 0 ? FeedItemStatus.Promoted : FeedItemStatus.Dismissed, daysAgo: 4 + i);
        await Item(news, "Old webinar", FeedItemStatus.Promoted, daysAgo: 200);
        var webinar = await Item(news, "Another webinar");
        var notes = await Item(vendor, "Runner release notes", daysAgo: 0.1);
        var gitlab = await Item(vendor, "GitLab patches runner flaw", daysAgo: 0.2);

        var newest = await _db.Intake().InboxAsync(new InboxFilter());
        var likely = await _db.Intake().InboxAsync(new InboxFilter(Sort: InboxSort.Likely));

        newest.Select(r => r.Item.Id).Should().Equal(gitlab, webinar, notes);
        newest.Should().OnlyContain(r => r.Likely == null, "reasons come with the sort");
        likely.Select(r => r.Item.Id).Should().Equal(gitlab, notes, webinar);
        likely[0].Likely.Should().Be(new Likelihood(3, 4, "3 of the 4 from GitLab releases naming GitLab decided in 90 days were promoted"));
        likely[1].Likely!.Why.Should().Be("4 of the 6 from GitLab releases decided in 90 days were promoted");
        likely[2].Likely!.Should().Be(new Likelihood(0, 5, "0 of the 5 from Industry News decided in 90 days were promoted"), "the item from 200 days ago isn't counted");
        likely[2].Likely!.Score.Should().BeApproximately(1d / 7, 1e-9);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task LikelyUsed_KeepsKevAndWatchedOnTop_AndThinHistoryGivesNoReason(string provider)
    {
        _db = _db.On(provider);
        var noisy = await Source("Noise");
        var quiet = await Source("Quiet");
        for (var i = 0; i < 4; i++) await Item(noisy, $"Noise {i}", FeedItemStatus.Dismissed, daysAgo: 2);
        await Item(quiet, "One promoted", FeedItemStatus.Promoted, daysAgo: 2);
        var adversary = await _db.Adversaries().SaveAsync(new AdversaryForm { Name = "Scattered Spider", Watched = true });
        await using (var ctx = _db.Context())
        {
            ctx.CveRecords.Add(new CveRecord { CveId = "CVE-2026-1111", IsKev = true });
            await ctx.SaveChangesAsync();
        }
        var kev = await Item(noisy, "Patch now", cves: "CVE-2026-1111", daysAgo: 0.3);
        var watched = await Item(noisy, "Scattered Spider returns", daysAgo: 0.4);
        var thin = await Item(quiet, "Something quiet", daysAgo: 0.1);

        var likely = await _db.Intake().InboxAsync(new InboxFilter(Sort: InboxSort.Likely));

        adversary.Should().NotBeEmpty();
        likely.Select(r => r.Item.Id).Should().Equal(watched, kev, thin);
        likely.Single(r => r.Item.Id == thin).Likely.Should().BeNull($"one decided item is under {TriageLearning.MinHistory}");
    }

    [Fact]
    public async Task LikelyUsed_IsLearnedOnceAMinute_AndAgainAfterADecisionMadeHere()
    {
        var news = await Source("News");
        for (var i = 0; i < 3; i++) await Item(news, $"Webinar {i}", FeedItemStatus.Dismissed, daysAgo: 1 + i);
        var waiting = await Item(news, "Another webinar");
        var other = await Item(news, "One more webinar", daysAgo: 0.5);
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var intake = _db.Intake(time: clock);
        async Task<Likelihood?> Reason() => (await intake.InboxAsync(new InboxFilter(Sort: InboxSort.Likely))).Single(r => r.Item.Id == waiting).Likely;

        (await Reason()).Should().Be(new Likelihood(0, 3, "0 of the 3 from News decided in 90 days were promoted"));
        await Item(news, "Promoted by a colleague", FeedItemStatus.Promoted, daysAgo: 1);
        (await Reason())!.Decided.Should().Be(3, "each keypress doesn't count the window again");
        clock.Now += IntakeService.LearnedFor;
        (await Reason()).Should().Be(new Likelihood(1, 4, "1 of the 4 from News decided in 90 days was promoted"), "a minute on, it's counted again");
        await intake.DismissAsync(other, null);
        (await Reason())!.Decided.Should().Be(5, "a decision made here is counted at once");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task SourceHealth_CountsEachSource_AndSuggestsPausingOneNobodyUses(string provider)
    {
        _db = _db.On(provider);
        var used = await Source("Used");
        var unused = await Source("Unused");
        var young = await Source("Young");
        await using (var ctx = _db.Context())
            await ctx.Sources.Where(s => s.Id != young).ExecuteUpdateAsync(u => u.SetProperty(s => s.CreatedAtUtc, DateTimeOffset.UtcNow.AddDays(-120)));
        for (var i = 0; i < 12; i++) await Item(unused, $"Unused {i}", FeedItemStatus.Dismissed, daysAgo: 1 + i * 4, decidedAfterHours: 3);
        await Item(used, "Kept", FeedItemStatus.Promoted, daysAgo: 2, decidedAfterHours: 0.5);
        await Item(used, "Dropped", FeedItemStatus.Dismissed, daysAgo: 3, decidedAfterHours: 1.5);
        await Item(used, "Waiting", daysAgo: 1);
        for (var i = 0; i < 12; i++) await Item(young, $"Young {i}", FeedItemStatus.Dismissed, daysAgo: 0.1);

        var health = await _db.Intake().SourceHealthAsync();

        var u = health.Single(h => h.Source.Id == used);
        (u.Decided, u.Promoted, u.PromotedShare, u.SuggestPause).Should().Be((2, 1, 0.5, false));
        u.PerWeek.Should().BeApproximately(3 / (90 / 7d), 1e-9);
        u.MedianToTriage.Should().Be(TimeSpan.FromHours(1.5));
        u.LastPromotedAtUtc.Should().NotBeNull();
        var n = health.Single(h => h.Source.Id == unused);
        (n.Decided, n.Promoted, n.PromotedShare, n.LastPromotedAtUtc, n.SuggestPause).Should().Be((12, 0, 0d, (DateTimeOffset?)null, true));
        health.Single(h => h.Source.Id == young).SuggestPause.Should().BeFalse("a source this new hasn't had its chance");

        await _db.Intake().SetSourceEnabledAsync(unused, false);
        (await _db.Intake().SourceHealthAsync()).Single(h => h.Source.Id == unused).SuggestPause.Should().BeFalse("it's paused already");
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await FluentActions.Invoking(() => _db.Intake().SourceHealthAsync()).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => _db.Intake().SetSourceEnabledAsync(used, false)).Should().ThrowAsync<ForbiddenException>();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Missed_ListsDismissedKevAndWatchedStoriesNoArticleCovers(string provider)
    {
        _db = _db.On(provider);
        var news = await Source("News");
        await _db.Adversaries().SaveAsync(new AdversaryForm { Name = "Octo Tempest", Watched = true });
        await using (var ctx = _db.Context())
        {
            ctx.CveRecords.AddRange(new CveRecord { CveId = "CVE-2026-2222", IsKev = true }, new CveRecord { CveId = "CVE-2026-3333", IsKev = true });
            await ctx.SaveChangesAsync();
        }
        var article = await _db.Articles().CreateAsync("Covered", "Vendor", "https://vendor.example.com/covered");
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);
        form.Cves = ["CVE-2026-3333"];
        await _db.Articles().SaveAsync(form);
        var kev = await Item(news, "Exploited flaw", FeedItemStatus.Dismissed, cves: "CVE-2026-2222", daysAgo: 1);
        await Item(news, "Covered flaw", FeedItemStatus.Dismissed, cves: "CVE-2026-3333", daysAgo: 1);
        var octo = await Item(news, "Octo Tempest phones help desks", FeedItemStatus.Dismissed, daysAgo: 2);
        await Item(news, "Octo Tempest last month", FeedItemStatus.Dismissed, daysAgo: 20);
        await Item(news, "Ordinary dismissal", FeedItemStatus.Dismissed, daysAgo: 1);

        var missed = await _db.Intake().MissedAsync();

        missed.Select(m => (m.Item.Id, m.Why)).Should().Equal(
            (kev, "CVE-2026-2222 is KEV-listed and no article covers it"),
            (octo, "It names Octo Tempest (watched), and no recent article is tagged with it"));
        missed[0].SourceName.Should().Be("News");

        await _db.Intake().RestoreAsync(kev);
        (await _db.Intake().MissedAsync()).Select(m => m.Item.Id).Should().Equal(octo);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task RightToDismiss_StopsTheCheckAskingForEveryone_UntilTheItemIsDecidedAgain(string provider)
    {
        _db = _db.On(provider);
        var news = await Source("News");
        await _db.Adversaries().SaveAsync(new AdversaryForm { Name = "Octo Tempest", Watched = true });
        await using (var ctx = _db.Context())
        {
            ctx.CveRecords.Add(new CveRecord { CveId = "CVE-2026-2222", IsKev = true });
            await ctx.SaveChangesAsync();
        }
        var kev = await Item(news, "Exploited flaw in a product we don't run", FeedItemStatus.Dismissed, cves: "CVE-2026-2222", daysAgo: 1);
        var octo = await Item(news, "Octo Tempest phones help desks", FeedItemStatus.Dismissed, daysAgo: 2);
        var fresh = await Item(news, "Still new");
        async Task<IEnumerable<Guid>> Missed() => (await _db.Intake().MissedAsync()).Select(m => m.Item.Id);

        var (n, at) = await _db.Intake().KeepDismissedAsync([kev, fresh]);
        n.Should().Be(1, "only a dismissed item is kept");
        (await Missed()).Should().Equal(octo);
        await using (var ctx = _db.Context())
        {
            var kept = await ctx.FeedItems.SingleAsync(i => i.Id == kev);
            kept.DismissKeptByName.Should().Be(_db.User.DisplayName);
            kept.DismissKeptAtUtc.Should().Be(at);
            kept.DismissKept.Should().BeTrue();
        }
        (await _db.Intake().KeepDismissedAsync([kev])).Count.Should().Be(0, "it's kept already");

        (await _db.Intake().UndoKeepDismissedAsync([kev], at.AddSeconds(-1))).Should().Be(0, "only the keep made at that time is taken back");
        (await _db.Intake().UndoKeepDismissedAsync([kev], at)).Should().Be(1);
        (await Missed()).Should().Equal(kev, octo);

        // Kept, then put back and dismissed again: a new decision outdates the keep, so the check asks about it again.
        await _db.Intake().KeepDismissedAsync([kev, octo]);
        (await Missed()).Should().BeEmpty();
        await _db.Intake().RestoreAsync(kev);
        await _db.Intake().DismissAsync(kev, IntakeService.DismissReasons[0]);
        (await Missed()).Should().Equal(kev);
        await using (var ctx = _db.Context())
            (await ctx.FeedItems.SingleAsync(i => i.Id == kev)).DismissKept.Should().BeFalse("the keep is older than the dismissal");
    }
}

/// <summary>Source health, and triage's order control.</summary>
public class TriageLearningPageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task SourceHealthAndTheTriageOrder_Render()
    {
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<IntakeService>()
                .SaveSourceAsync(new SourceForm { Name = "health-page feed", Url = "https://health.example.com/feed", PollMinutes = 30 });

        await using var health = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.Admin.SourceHealthAdmin>(app, "/admin/source-health");
        (await health.Html()).Should().Contain("Source health").And.Contain("health-page feed").And.Contain("Median to triage");

        await using var triage = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.Triage>(app, "/triage");
        (await triage.Html()).Should().Contain("Likely used").And.Contain("Newest");
    }

    [Fact]
    public async Task TheMissedNotice_OffersRightToDismiss_OnEachAndForAll_AndNamesWhoKeptOne()
    {
        Guid first, second;
        await using (var scope = app.Services.CreateAsyncScope())
        await using (var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync())
        {
            var source = new Source { Name = "missed-notice feed", Url = "https://missed.example.com/feed", PollMinutes = 30 };
            db.Sources.Add(source);
            db.CveRecords.Add(new CveRecord { CveId = "CVE-2026-7777", IsKev = true });
            FeedItem Dismissed(int n) => new()
            {
                Source = source, Title = $"Exploited flaw {n}", Url = $"https://missed.example.com/{n}", UrlHash = $"missed{n}", FetchedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                Status = FeedItemStatus.Dismissed, Cves = "CVE-2026-7777", StatusAtUtc = DateTimeOffset.UtcNow,
            };
            var items = new[] { Dismissed(1), Dismissed(2) };
            db.FeedItems.AddRange(items);
            await db.SaveChangesAsync();
            (first, second) = (items[0].Id, items[1].Id);
        }

        await using (var triage = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.Triage>(app, "/triage"))
        {
            var html = await triage.HtmlOnce("Missed this week?");
            var notice = html[html.IndexOf("tide-missed", StringComparison.Ordinal)..];
            notice.Split(">Right to dismiss</button>").Length.Should().Be(3, "each item has its own");
            notice.Should().Contain(">All 2 were right to dismiss</button>").And.NotContain("Hide until next week");
        }

        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<IntakeService>().KeepDismissedAsync([first, second])).Count.Should().Be(2);
        await using (var after = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.Triage>(app, "/triage"))
            (await after.Html()).Should().NotContain("Missed this week?", "a dismissal said to be right isn't asked about again, for anyone");
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(20d, "20 min")]
    [InlineData(110d, "1 h 50")]
    [InlineData(120d, "2 h")]
    [InlineData(3000d, "2 d")]
    public void Waits_ReadPlainly(double? minutes, string expected) =>
        Tide.Web.Components.Pages.Admin.SourceHealthAdmin.Wait(minutes is { } m ? TimeSpan.FromMinutes(m) : null).Should().Be(expected);
}
