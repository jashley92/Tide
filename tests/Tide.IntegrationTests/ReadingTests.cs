using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Assist;
using Tide.Application.Intake;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

public class ReadingServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private const string Link = "https://news.example.com/ivanti-flaws";

    private static readonly string Page = "<html><body><article>" + string.Concat(Enumerable.Range(0, 6).Select(i =>
        $"<p>Attackers chained two Ivanti Connect Secure flaws, CVE-2026-1281 among them, reaching 185.220.101[.]47 from gateway {i}, and the vendor shipped fixes.</p>")) +
        "</article></body></html>";

    public ReadingServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    // A new item in the inbox from a source with the given fetching rule.
    private async Task<Guid> Item(ArticleFetch fetch = ArticleFetch.TideOnly, string url = Link)
    {
        var source = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "News " + fetch, Url = $"https://news.example.com/{fetch}.xml", PollMinutes = 30, ArticleFetch = fetch });
        await using var ctx = _db.Context();
        var item = new FeedItem { SourceId = source, Title = "Ivanti flaws chained", Url = url, UrlHash = Guid.NewGuid().ToString("N"), FetchedAtUtc = DateTimeOffset.UtcNow };
        ctx.FeedItems.Add(item);
        await ctx.SaveChangesAsync();
        return item.Id;
    }

    [Fact]
    public async Task Reading_FetchesOnce_KeepsTheText_AndMarksWhatItNames()
    {
        await _db.Stack().SaveAsync(new StackForm { Name = "Ivanti Connect Secure", Versions = "22.7R2.4" });
        var id = await Item();
        _db.Http.Set(Link, Page);

        var read = await _db.Reading().ReadAsync(id);

        read.HasText.Should().BeTrue();
        read.How.Should().Be("the page's article markup");
        read.Cves.Should().Equal("CVE-2026-1281");
        read.Indicators.Select(i => i.Value).Should().Contain("185.220.101.47");
        read.WeRun.Should().Equal("Ivanti Connect Secure");
        read.Spans.Where(s => s.Mark == ReadingMark.Indicator).Select(s => s.Text).Should().Contain("185.220.101[.]47", "the text is marked as it's written");
        string.Concat(read.Spans.Select(s => s.Text)).Length.Should().Be(read.Length, "the runs are the whole text");

        (await _db.Reading().ReadAsync(id)).Length.Should().Be(read.Length);
        (await _db.Reading().KeptAsync(id)).Should().NotBeNull("a colleague sees it without fetching");
        _db.Http.Requests.Should().HaveCount(1);
        await _db.Reading().ReadAsync(id, again: true);
        _db.Http.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ANeverSource_IsntFetched_AndAFailureSaysToOpenTheSite()
    {
        var never = await Item(ArticleFetch.Never);
        (await _db.Reading().ReadAsync(never)).Problem.Should().Contain("aren't fetched");
        _db.Http.Requests.Should().BeEmpty();

        var failing = await Item(url: "https://other.example.com/x");
        var failed = await _db.Reading().ReadAsync(failing);
        failed.HasText.Should().BeFalse();
        failed.Problem.Should().EndWith("Open the site instead.");
        (await _db.Reading().KeptAsync(failing)).Should().BeNull("nothing was kept");
    }

    [Fact]
    public async Task KeptText_Expires_AndThePruneDeletesIt()
    {
        var id = await Item();
        _db.Http.Set(Link, Page);
        await _db.Reading().ReadAsync(id);

        var weekOn = new ShiftedClock(TimeSpan.FromDays(8));
        (await _db.Reading(time: weekOn).KeptAsync(id)).Should().BeNull();
        (await _db.Reading(new IntakeOptions { ReadingDays = 10 }, weekOn).KeptAsync(id)).Should().NotBeNull();
        (await _db.Reading(time: weekOn).PruneAsync()).Should().Be(1);
        await using var ctx = _db.Context();
        (await ctx.FeedItemTexts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheEditor_StartsWithTheTextReadInTriage()
    {
        var id = await Item();
        _db.Http.Set(Link, Page);
        await _db.Reading().ReadAsync(id);
        var article = await _db.Intake().PromoteAsync(id, new PromoteForm { Title = "Ivanti flaws chained", Classification = Classification.Informational });

        var kept = await _db.Assist().KeptTextAsync(article);

        kept!.Text.Should().Contain("gateway 5");
        kept.Origin.Should().Be("read in triage: the page's article markup");
    }

    [Fact]
    public async Task OnlyAnalystsRead()
    {
        var id = await Item();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Invoking(() => _db.Reading().ReadAsync(id)).Should().ThrowAsync<ForbiddenException>();
    }
}

public class ReadingPaneTests
{
    [Fact]
    public void Found_SaysWhatTheTextNames()
    {
        var text = ReadingService.Mark(Guid.NewGuid(), "GitLab fixed CVE-2026-1. See CVE-2026-12345 and cdn.evil[.]com.", "x", null,
            [new StackEntry(Guid.NewGuid(), "GitLab", ["GitLab", "GitLab CE"])]);

        Triage.Found(text).Should().Be("Found here: 1 CVE · 1 indicator · Our stack: GitLab");
        text.Spans.Select(s => (s.Text, s.Mark)).Should().Contain([("GitLab", ReadingMark.WeRun), ("CVE-2026-12345", ReadingMark.Cve), ("cdn.evil[.]com", ReadingMark.Indicator)]);
        Triage.Found(ReadingService.Mark(Guid.NewGuid(), "Nothing here.", null, null, [])).Should().StartWith("No CVEs");
    }
}

/// <summary>Triage's reading pane: V opens it on the selected item with its kept text marked, and Esc closes it.</summary>
public class TriageReadingPaneTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task V_ShowsTheKeptText_Marked_AndEscClosesIt()
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            var source = new Source { Name = "Reading source", Url = "https://reading.example/feed" };
            var at = DateTimeOffset.UtcNow.AddHours(5);
            var item = new FeedItem { Source = source, Title = "reading-pane: VPN flaw exploited", Url = "https://reading.example/a", UrlHash = "reading-pane-a", PublishedAtUtc = at, FetchedAtUtc = at };
            db.Add(item);
            await db.SaveChangesAsync();
            db.Add(new FeedItemText { FeedItemId = item.Id, Text = "Attackers exploit CVE-2026-4242 from 203.0.113[.]9.", How = "the page's article markup", FetchedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage");
        (await r.HtmlOnce("reading-pane: VPN flaw exploited")).Should().NotContain("tide-reading\"").And.Contain("<span class=\"tide-kbd\">V</span> read it here");

        await r.Click(t => t.OnTriageKey("read"));
        var html = await r.Html();
        html.Should().Contain("aria-label=\"The article\"").And.Contain("<mark class=\"tide-read-cve\">CVE-2026-4242</mark>")
            .And.Contain("<mark class=\"tide-read-ioc\">203.0.113[.]9</mark>").And.Contain("Found here: 1 CVE &#xB7; 1 indicator")
            .And.Contain("Read again").And.Contain("sent or exported");

        await r.Click(t => t.OnTriageKey("close-reading"));
        (await r.Html()).Should().NotContain("aria-label=\"The article\"");
    }
}

/// <summary>EP-77: an article promoted from a group drafts from every report, each numbered, and every claim cites one.</summary>
public class CitedDraftServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private const string First = "https://one.example.com/ivanti", Second = "https://two.example.com/ivanti";

    private static string Page(string words) => "<html><body><article>" + string.Concat(Enumerable.Range(0, 6).Select(i =>
        $"<p>{words} Attackers chained two Ivanti Connect Secure flaws on gateway {i}, and the vendor shipped fixes for every branch.</p>")) + "</article></body></html>";

    public CitedDraftServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<(Guid Article, Guid A, Guid B)> PromotedGroup(TlpLevel secondTlp = TlpLevel.Clear)
    {
        var one = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "One", Url = "https://one.example.com/feed", PollMinutes = 30 });
        var two = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "Two", Url = "https://two.example.com/feed", PollMinutes = 30, DefaultTlp = secondTlp });
        await using var ctx = _db.Context();
        var a = new FeedItem { SourceId = one, Title = "Ivanti flaws chained", Url = First, UrlHash = "c1", FetchedAtUtc = DateTimeOffset.UtcNow, Cves = "CVE-2026-1281" };
        var b = new FeedItem { SourceId = two, Title = "Ivanti flaws exploited", Url = Second, UrlHash = "c2", FetchedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5), Cves = "CVE-2026-1281" };
        ctx.FeedItems.AddRange(a, b);
        await ctx.SaveChangesAsync();
        _db.Http.Set(Second, Page("More than 20 victims."));
        await _db.Reading().ReadAsync(b.Id);
        var article = await _db.Intake().PromoteAsOneAsync([a.Id, b.Id], new PromoteForm { Title = "Ivanti flaws chained", Classification = Classification.Informational, Tlp = TlpLevel.Clear });
        return (article, a.Id, b.Id);
    }

    [Fact]
    public async Task TheOtherReports_ComeWithTheTextReadInTriage()
    {
        var (article, _, _) = await PromotedGroup();
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);

        var others = await _db.Assist().OtherSourcesAsync(article);

        form.SourceUrl.Should().Be(First, "the newest report makes the article");
        others.Select(o => o.Url).Should().Equal(Second);
        others.Single().Text.Should().Contain("More than 20 victims.").And.Contain("gateway 5");
    }

    [Fact]
    public async Task ADraftFromTwoSources_NumbersThem_AndChecksEachCitation()
    {
        var (article, _, _) = await PromotedGroup();
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);
        _db.Ai.Json = """
            { "bottomLine": "Two Ivanti flaws are chained.", "bottomLineSources": [1, 2],
              "bullets": [ { "text": "Over 20 victims.", "sources": [2], "quote": "More than 20 victims." }, { "text": "Made up.", "sources": [], "quote": "" } ],
              "disagreements": [], "whyItMatters": "", "recommendedActions": "", "techniques": [], "tags": [], "indicators": [], "cves": [] }
            """;

        var draft = await _db.Assist().DraftAsync(article, form, [new DraftSource("One", Page("Pasted first."), null, First), new DraftSource("Two", Page("More than 20 victims."), "read in triage", Second)]);

        draft.SourceNames.Should().Equal("One", "Two");
        draft.Suggestions.Bullets.Select(b => b.Supported).Should().Equal(true, false);
        draft.BasedOn.Should().StartWith("[1] One, ").And.Contain("[2] Two, ");
        var asked = _db.Ai.Asked.Single().Input;
        asked.Should().Contain("SOURCE 1 (One):").And.Contain("SOURCE 2 (Two, read in triage):");
    }

    [Fact]
    public async Task AnAmberReport_KeepsTheWholeDraftHome_UnderAGreenLimit()
    {
        var (article, _, _) = await PromotedGroup(secondTlp: TlpLevel.Amber);
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);

        await FluentActions.Invoking(() => _db.Assist().DraftAsync(article, form, [new DraftSource("Two", "text", null, Second)]))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*TLP:AMBER*");
        _db.Ai.Asked.Should().BeEmpty();
    }
}
