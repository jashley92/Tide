using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Intake;
using Tide.Application.Org;
using Tide.Application.Preferences;
using Tide.Application.Search;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

public class SearchServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public SearchServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private SearchService Search() => new(_db, _db.User);

    private async Task<Guid> Article(string title, string bluf = "Summary.", IEnumerable<string>? bullets = null,
        IEnumerable<(IndicatorType Type, string Value)>? indicators = null, IEnumerable<string>? cves = null, IEnumerable<string>? techniques = null)
    {
        var svc = _db.Articles();
        var id = await svc.CreateAsync(title, "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await svc.GetAsync(id))!);
        form.Bluf = bluf;
        form.Bullets = (bullets ?? []).Select(b => new BulletForm { Text = b }).ToList();
        form.Indicators = (indicators ?? []).Select(i => new IndicatorForm { Type = i.Type, Value = i.Value }).ToList();
        form.Cves = (cves ?? []).ToList();
        form.TechniqueIds = (techniques ?? []).ToList();
        await svc.SaveAsync(form);
        return id;
    }

    [Fact]
    public async Task Text_EveryTermMustMatch_InAnyPart_TitleMatchesFirst()
    {
        var inBullet = await Article("Quarterly roundup", bullets: ["The **ransomware** crew moved to edge VPN appliances."]);
        var inTitle = await Article("Ransomware hits edge VPN vendor");
        await Article("Ransomware in healthcare", bullets: ["No appliances involved."]);

        var r = await Search().SearchAsync("ransomware vpn");

        r.Articles.Items.Select(a => a.Id).Should().Equal(inTitle, inBullet);
        r.Articles.Items[1].Snippet.Should().Contain("ransomware crew moved to edge VPN", "the snippet is the bullet, Markdown taken out");
        r.Indicator.Should().BeNull("two words aren't an indicator");
    }

    [Fact]
    public async Task Text_DefangedAndLiveFormsFindEachOther_AndWildcardsAreLiteral()
    {
        var live = await Article("Phishing kit", indicators: [(IndicatorType.Domain, "login-portal.example.net")]);
        var prose = await Article("Follow-up", bullets: ["Still resolving: login-portal[.]example[.]org"]);
        await Article("Discount", bluf: "Half off for 100 days.");

        var byIndicator = (await Search().SearchAsync("login-portal[.]example[.]net")).Articles.Items;
        byIndicator.Select(a => a.Id).Should().Equal(live);
        byIndicator[0].Snippet.Should().Be("login-portal[.]example[.]net", "an indicator in a snippet is shown defanged");
        (await Search().SearchAsync("login-portal.example.org")).Articles.Items.Select(a => a.Id).Should().Equal(prose);
        (await Search().SearchAsync("100%")).Articles.Items.Should().BeEmpty("% is a character to find, not a wildcard");
        (await Search().SearchAsync("_")).Terms.Should().BeEmpty("a single character isn't searched");
    }

    [Fact]
    public async Task Text_ATechniqueNameFindsTaggedArticles()
    {
        var tagged = await Article("Invoice lure wave", techniques: ["T1566.001"]);
        await Article("Patch Tuesday");

        (await Search().SearchAsync("spearphishing")).Articles.Items.Select(a => a.Id).Should().Equal(tagged);
    }

    [Fact]
    public async Task Actions_ByNumber_ByComment_AndStories()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var first = await _db.Actions().CreateAsync(new ActionForm { Title = "Block the domain", Kind = ActionKind.Block, TeamId = team });
        var second = await _db.Actions().CreateAsync(new ActionForm { Title = "Patch the firewall", Kind = ActionKind.PatchRemediate, TeamId = team });
        await _db.Actions().AddCommentAsync((await _db.Actions().GetAsync(second))!.Action.Id, "Waiting on the change board.");
        var story = await _db.Stories().CreateAsync("Edge device campaign", "Appliance exploitation across vendors.");

        (await Search().SearchAsync($"A-{first}")).Actions.Items.Select(a => a.Number).Should().Equal(first);
        (await Search().SearchAsync("change board")).Actions.Items.Select(a => a.Number).Should().Equal(second);
        (await Search().SearchAsync("\"change board\"")).Actions.Items.Should().ContainSingle();
        var s = (await Search().SearchAsync("appliance")).Stories.Items.Single();
        s.Id.Should().Be(story);
        s.Snippet.Should().Contain("Appliance exploitation");
    }

    [Fact]
    public async Task ALoneDigit_FindsThatActionOnly()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var numbers = new List<int>();
        for (var i = 0; i < 2; i++) numbers.Add(await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep 1 for APT29", Kind = ActionKind.IocSweep, TeamId = team }));
        await Article("Scattered Spider 1 note");

        var r = await Search().SearchAsync($"{numbers[0]}");

        r.Actions.Items.Select(a => a.Number).Should().Equal(numbers[0]);
        r.Articles.Items.Should().BeEmpty("a digit is looked for as an action's number, not in the text");
    }

    [Fact]
    public async Task Text_SaysHowManyMatchInAll_AndPagesEachKind()
    {
        for (var i = 0; i < 3; i++) await Article($"Zebrafish lure {i}");
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        for (var i = 0; i < 3; i++) await _db.Actions().CreateAsync(new ActionForm { Title = $"Zebrafish sweep {i}", Kind = ActionKind.IocSweep, TeamId = team });
        for (var i = 0; i < 3; i++) await _db.Stories().CreateAsync($"Zebrafish wave {i}", null);

        var first = await Search().SearchAsync("zebrafish", new SearchPages(Size: 2));
        (first.Articles.Items.Count, first.Articles.Total, first.Articles.More).Should().Be((2, 3, true));
        (first.Actions.Items.Count, first.Actions.Total).Should().Be((2, 3));
        (first.Stories.Items.Count, first.Stories.Total).Should().Be((2, 3));

        var second = await Search().SearchAsync("zebrafish", new SearchPages(Articles: 2, Actions: 2, Stories: 9, Size: 2));
        second.Articles.Items.Should().ContainSingle();
        first.Articles.Items.Select(a => a.Id).Should().NotContain(second.Articles.Items[0].Id, "each page has its own hits");
        second.Actions.Items.Should().ContainSingle();
        first.Actions.Items.Select(a => a.Id).Should().NotContain(second.Actions.Items[0].Id);
        second.Stories.Items.Should().BeEmpty("there's no ninth page");
        second.Articles.Total.Should().Be(3, "the count is of every hit, whatever the page");
        (await Search().SearchAsync("zebrafish", new SearchPages(Size: 0))).Articles.Items.Should().ContainSingle("a page holds at least one");
        (await Search().SearchAsync("zebrafish")).Articles.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task Lookup_Ip_ListsEveryArticle_AndWhatWasDone()
    {
        var a = await Article("Edge VPN zero-day", indicators: [(IndicatorType.IpAddress, "203.0.113.45")]);
        var b = await Article("Same infrastructure, new lure", indicators: [(IndicatorType.IpAddress, "203.0.113.45"), (IndicatorType.IpAddress, "203.0.113.46")]);
        await Article("Unrelated", indicators: [(IndicatorType.IpAddress, "198.51.100.7")]);
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = a });
        var hunt = await _db.Actions().GenerateHuntAsync((await _db.Actions().GetAsync(number))!.Action.Id, QueryDialects.All[0].Key);
        await _db.Actions().RecordHuntResultAsync(hunt.Id, HuntResult.NoHits, null, "Clean.", null);

        var r = await Search().SearchAsync("203.0.113[.]45");

        var ind = r.Indicator!;
        ind.Type.Should().Be(IndicatorType.IpAddress);
        ind.Value.Should().Be("203.0.113.45");
        ind.Sightings.Items.Where(s => !s.Related).Select(s => s.ArticleId).Should().BeEquivalentTo([a, b]);
        ind.Sightings.Items.Should().NotContain(s => s.Value == "203.0.113.46", "another address isn't the same host");
        ind.Actions.Items.Single().Hunted.Should().Be(HuntResult.NoHits);
        r.Articles.Items.Select(x => x.Id).Should().BeEquivalentTo([a, b]);
    }

    [Fact]
    public async Task Lookup_Domain_FindsSubdomainsUrlsAndEmailOnIt_AsRelated()
    {
        var exact = await Article("C2 domain", indicators: [(IndicatorType.Domain, "bad.example.net")]);
        var sub = await Article("Subdomain", indicators: [(IndicatorType.Domain, "cdn.bad.example.net")]);
        var url = await Article("Payload URL", indicators: [(IndicatorType.Url, "https://bad.example.net/payload.bin")]);
        var mail = await Article("Lure sender", indicators: [(IndicatorType.EmailAddress, "billing@bad.example.net")]);
        await Article("Lookalike", indicators: [(IndicatorType.Domain, "notbad.example.net")]);

        var ind = (await Search().SearchAsync("BAD.example.net")).Indicator!;

        ind.Host.Should().Be("bad.example.net");
        ind.Sightings.Items.Where(s => !s.Related).Select(s => s.ArticleId).Should().Equal(exact);
        ind.Sightings.Items.Where(s => s.Related).Select(s => s.ArticleId).Should().BeEquivalentTo([sub, url, mail]);
    }

    [Fact]
    public async Task Lookup_Cve_ShowsTheRecordAndArticleCount()
    {
        await Article("Firewall bug", cves: ["CVE-2026-12345"]);
        await using (var db = _db.Context())
        {
            db.CveRecords.Add(new CveRecord { CveId = "CVE-2026-12345", CvssScore = 9.8m, IsKev = true });
            await db.SaveChangesAsync();
        }

        var r = await Search().SearchAsync("cve-2026-12345");

        r.Cve!.CveId.Should().Be("CVE-2026-12345");
        r.Cve.Record!.IsKev.Should().BeTrue();
        r.Cve.Articles.Total.Should().Be(1);
        r.Articles.Items.Should().ContainSingle();
    }

    // ---- The lookup's first line, its one list of articles, "Seen with" and who is sweeping ----

    private static readonly SearchPages Once = SearchPages.First with { LookupOnce = true };

    [Fact]
    public async Task Lookup_Summary_SaysWhereItIs_WhenItWasBriefed_AndWhoIsOnIt()
    {
        var a = await Article("Edge VPN zero-day", indicators: [(IndicatorType.IpAddress, "203.0.113.45")]);
        var brief = await _db.Briefs().GetOrStartAsync(_db.Clock.CurrentBriefDate);
        await _db.Briefs().AddArticleAsync(brief.Id, a);
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var sweep = await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = a });
        await _db.Actions().CreateAsync(new ActionForm { Title = "Block", Kind = ActionKind.Block, TeamId = team, ArticleId = a });

        var ind = (await Search().SearchAsync("203.0.113.45")).Indicator!;

        ind.Summary.Should().Be(new LookupSummary(1, null, _db.Clock.CurrentBriefDate, 2, 2, HuntResult.NotRun, 0), "a brief still being written hasn't briefed anyone");
        ind.Summary.Unseen.Should().BeFalse();
        ind.Sweeping.Select(x => x.Number).Should().Equal([sweep], "the block isn't a sweep");

        await _db.Briefs().MarkPresentedAsync(brief.Id);
        var after = (await Search().SearchAsync("203.0.113.45")).Indicator!;
        after.Summary.Should().Be(new LookupSummary(1, _db.Clock.CurrentBriefDate, null, 2, 2, HuntResult.NotRun, 0), "the meeting was held");
        after.Sightings.Items[0].Article.Briefs.Should().Equal(new LookupBrief(_db.Clock.CurrentBriefDate, true));
    }

    [Fact]
    public async Task Lookup_Summary_NeverSeen_AndWrittenButNotBriefed_AndAHuntThatCameBack()
    {
        (await Search().SearchAsync("198.51.100.99")).Indicator!.Summary.Should().Be(new LookupSummary(0, null, null, 0, 0, null, 0));
        (await Search().SearchAsync("198.51.100.99")).Indicator!.Summary.Unseen.Should().BeTrue();

        var a = await Article("Draft only", indicators: [(IndicatorType.Domain, "draft-only.example.net")]);
        (await Search().SearchAsync("draft-only.example.net")).Indicator!.Summary.Should().Be(new LookupSummary(1, null, null, 0, 0, null, 0));

        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = a });
        var hunt = await _db.Actions().GenerateHuntAsync((await _db.Actions().GetAsync(number))!.Action.Id, QueryDialects.All[0].Key);
        await _db.Actions().RecordHuntResultAsync(hunt.Id, HuntResult.Hits, 3, "Two hosts.", null);
        (await Search().SearchAsync("draft-only.example.net")).Indicator!.Summary.Hunted.Should().Be(HuntResult.Hits);
    }

    [Fact]
    public async Task Lookup_Summary_CountsOnlyWhatWasDoneAboutTheIndicatorItself_NotARelatedHost()
    {
        await Article("C2 domain", indicators: [(IndicatorType.Domain, "evil.example.net")]);
        var sub = await Article("Subdomain", indicators: [(IndicatorType.Domain, "cdn.evil.example.net")]);
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep the CDN", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = sub });
        var hunt = await _db.Actions().GenerateHuntAsync((await _db.Actions().GetAsync(number))!.Action.Id, QueryDialects.All[0].Key);
        await _db.Actions().RecordHuntResultAsync(hunt.Id, HuntResult.Hits, 3, "Two hosts.", null);

        var ind = (await Search().SearchAsync("evil.example.net")).Indicator!;

        ind.Actions.Items.Select(x => x.Number).Should().Equal([number], "the full list still names what was done about the host");
        ind.Summary.Should().Be(new LookupSummary(1, null, null, 0, 0, null, 0), "a sweep for cdn.evil.example.net didn't hunt for evil.example.net");
    }

    [Fact]
    public async Task Lookup_AnOpenActionCarryingTheIndicatorItself_IsSweepingForIt_AClosedOneIsNot()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var own = await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep from a phone call", Kind = ActionKind.IocSweep, TeamId = team });
        await _db.Actions().AddIndicatorsAsync((await _db.Actions().GetAsync(own))!.Action.Id, [new IndicatorForm { Type = IndicatorType.Domain, Value = "phoned-in.example.net" }]);
        var other = await _db.Actions().CreateAsync(new ActionForm { Title = "Another sweep", Kind = ActionKind.IocSweep, TeamId = team });
        await _db.Actions().AddIndicatorsAsync((await _db.Actions().GetAsync(other))!.Action.Id, [new IndicatorForm { Type = IndicatorType.Domain, Value = "elsewhere.example.net" }]);

        var ind = (await Search().SearchAsync("phoned-in[.]example[.]net")).Indicator!;

        ind.Sweeping.Select(x => x.Number).Should().Equal(own);
        ind.Actions.Items.Select(x => x.Number).Should().Equal([own], "an action that carries the indicator is what was done about it");
        ind.Summary.Should().Be(new LookupSummary(0, null, null, 1, 1, HuntResult.NotRun, 0));
        ind.Summary.Unseen.Should().BeFalse("someone is on it, though no article has it");

        await using (var db = _db.Context())
        {
            (await db.Actions.SingleAsync(x => x.Number == own)).Status = ActionStatus.Done;
            await db.SaveChangesAsync();
        }
        (await Search().SearchAsync("phoned-in.example.net")).Indicator!.Sweeping.Should().BeEmpty("a closed sweep isn't sweeping");
    }

    [Fact]
    public async Task Lookup_ListsEachArticleOnce_WithItsBadges_AndLeavesItOutOfTheTextHits()
    {
        var a = await Article("C2 domain", indicators: [(IndicatorType.Domain, "bad.example.net")]);
        var sub = await Article("Subdomain", indicators: [(IndicatorType.Domain, "cdn.bad.example.net")]);
        var prose = await Article("Follow-up", bullets: ["Still resolving: bad[.]example[.]net"]);

        var r = await Search().SearchAsync("bad.example.net", Once);

        r.Indicator!.Sightings.Items.Select(s => s.ArticleId).Should().Equal(a, sub);
        var first = r.Indicator.Sightings.Items[0].Article;
        (first.Title, first.Tlp, first.Classification, first.Status).Should().Be(("C2 domain", TlpLevel.Clear, Classification.Informational, ArticleStatus.Draft));
        r.Articles.Items.Select(x => x.Id).Should().Equal([prose], "the lookup's articles are listed with the lookup");
        r.Articles.Total.Should().Be(1);
        (await Search().SearchAsync("bad.example.net")).Articles.Items.Should().HaveCount(3, "anything else that searches still gets every hit");
        (await Search().SearchAsync("resolving", Once)).Articles.Items.Select(x => x.Id).Should().Equal([prose], "a search for words is as it was");
    }

    [Fact]
    public async Task Lookup_SeenWith_NamesWhatElseIsOnTheSameArticles()
    {
        var a = await Article("Campaign", indicators: [(IndicatorType.Domain, "bad.example.net"), (IndicatorType.IpAddress, "203.0.113.45"), (IndicatorType.FileHash, new string('a', 64))],
            cves: ["CVE-2026-12345"], techniques: ["T1566.001"]);
        await Article("Unrelated", indicators: [(IndicatorType.IpAddress, "198.51.100.7")], cves: ["CVE-2026-99999"]);
        var story = await _db.Stories().CreateAsync("Edge device campaign", null);
        await _db.Stories().LinkAsync(a, story);

        var seen = (await Search().SearchAsync("bad.example.net")).Indicator!.SeenWith;

        seen.Indicators.Select(i => i.Value).Should().BeEquivalentTo(["203.0.113.45", new string('a', 64)], "not the indicator looked up, nor another article's");
        seen.Cves.Should().Equal("CVE-2026-12345");
        seen.Techniques.Should().Equal("T1566.001");
        seen.Stories.Should().Equal(new SeenStory(story, "Edge device campaign"));

        var cve = (await Search().SearchAsync("CVE-2026-12345")).Cve!;
        cve.SeenWith.Cves.Should().BeEmpty("the CVE looked up isn't seen with itself");
        cve.SeenWith.Indicators.Should().HaveCount(3);
        (await Search().SearchAsync("198.51.100.200")).Indicator!.SeenWith.Any.Should().BeFalse();
    }

    [Fact]
    public async Task Lookup_Cve_ListsItsArticles_AndSaysWhoIsOnIt()
    {
        var a = await Article("Firewall bug", cves: ["CVE-2026-12345"]);
        await Article("Firewall bug, exploited", cves: ["CVE-2026-12345"]);
        var team = await _db.Teams().AddAsync("Network", null, null);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch", Kind = ActionKind.PatchRemediate, TeamId = team, ArticleId = a });

        var r = await Search().SearchAsync("CVE-2026-12345", Once);

        r.Cve!.Articles.Items.Select(x => x.Title).Should().BeEquivalentTo("Firewall bug, exploited", "Firewall bug");
        r.Cve.Summary.Should().Be(new LookupSummary(2, null, null, 1, 1, null, 0));
        r.Articles.Items.Should().BeEmpty("both are listed with the CVE");
        (await Search().SearchAsync("CVE-2026-00001")).Cve!.Summary.Unseen.Should().BeTrue();
    }

    [Fact]
    public async Task Lookup_Cve_ClaimsNoHuntResult_ButNamesTheSweepOfItsArticlesIndicators()
    {
        var a = await Article("Firewall bug", indicators: [(IndicatorType.IpAddress, "203.0.113.45")], cves: ["CVE-2026-12345"]);
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var sweep = await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = a });
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch", Kind = ActionKind.PatchRemediate, TeamId = team, ArticleId = a });
        var hunt = await _db.Actions().GenerateHuntAsync((await _db.Actions().GetAsync(sweep))!.Action.Id, QueryDialects.All[0].Key);
        await _db.Actions().RecordHuntResultAsync(hunt.Id, HuntResult.NoHits, null, "Clean.", null);

        var cve = (await Search().SearchAsync("CVE-2026-12345")).Cve!;

        cve.Summary.Hunted.Should().BeNull("a hunt searches for indicators, not for a CVE");
        cve.Sweeps.Select(x => (x.Number, x.Hunted)).Should().Equal((sweep, HuntResult.NoHits));
        (await Search().SearchAsync("203.0.113.45")).Indicator!.Summary.Hunted.Should().Be(HuntResult.NoHits, "the indicator was hunted for");
    }

    [Fact]
    public async Task Lookup_AsksTheDatabaseTheSameNumberOfTimes_HoweverManyArticlesTheIndicatorIsOn()
    {
        await Article("First", indicators: [(IndicatorType.IpAddress, "203.0.113.45")], cves: ["CVE-2026-12345"]);
        _db.Commands.Reset();
        await Search().SearchAsync("203.0.113.45", Once);
        var one = _db.Commands.Sql.Count;

        for (var i = 0; i < 6; i++) await Article($"More {i}", indicators: [(IndicatorType.IpAddress, "203.0.113.45"), (IndicatorType.Domain, $"more{i}.example.net")], cves: [$"CVE-2026-1000{i}"]);
        _db.Commands.Reset();
        await Search().SearchAsync("203.0.113.45", Once);

        _db.Commands.Sql.Count.Should().Be(one, "the summary, the list and Seen with are read for all the articles at once");
    }

    [Fact]
    public async Task Readers_CantSearch()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Awaiting(() => Search().SearchAsync("anything")).Should().ThrowAsync<ForbiddenException>();
    }
}

/// <summary>
/// The Search page on a lookup: its first line says whether the indicator or CVE is known and whether anyone is on it,
/// each article is listed once, the hunt query is there on the first render in the language the person used last (and
/// looking at it saves nothing), "Seen with" links to the other values, and nobody below the team edition gets any of it.
/// </summary>
public partial class SearchLookupPageTests(TideApp app) : IClassFixture<TideApp>
{
    // Each test signs in as its own person, so a saved query language can't reach another test.
    private static SignIn Person(string role = "Analyst") => new($"dev:lookup-{Guid.NewGuid():N}", "Lookup tester", role);

    // Values of each test's own, so tests sharing the database don't see each other's.
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    private static string NewCve() => $"CVE-2031-{Random.Shared.Next(100000, 999999)}";

    private OrgClock Clock => app.Services.GetRequiredService<OrgClock>();

    private DateOnly Today => Clock.CurrentBriefDate;

    // A working day before today, a brief of this test's own.
    private DateOnly Past(int days)
    {
        var day = Today;
        for (var i = 0; i < days; i++) day = WorkingDays.Weekdays.Previous(day);
        return day;
    }

    // How the first line names a brief that hasn't been presented.
    private string NotPresented(DateOnly day) => day == Clock.Today ? "on today's brief, not presented yet" : $"on the brief for {day:ddd MMM d}, not presented yet";

    private async Task<Guid> Article(string title, IEnumerable<(IndicatorType Type, string Value)> indicators, IEnumerable<string>? cves = null,
        IEnumerable<string>? techniques = null, DateOnly? brief = null, bool presented = false)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var svc = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var id = await svc.CreateAsync(title, "Vendor", null, TlpLevel.Amber);
        var form = ArticleForm.From((await svc.GetAsync(id))!);
        form.Bluf = "Summary.";
        form.Indicators = indicators.Select(i => new IndicatorForm { Type = i.Type, Value = i.Value }).ToList();
        form.Cves = (cves ?? []).ToList();
        form.TechniqueIds = (techniques ?? []).ToList();
        await svc.SaveAsync(form);
        if (brief is { } day)
        {
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var on = (await briefs.GetOrStartAsync(day)).Id;
            await briefs.AddArticleAsync(on, id);
            if (presented) await briefs.MarkPresentedAsync(on);
        }
        return id;
    }

    private async Task<int> Action(string title, ActionKind kind, Guid articleId)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var team = await scope.ServiceProvider.GetRequiredService<TeamService>().AddAsync(Unique("Lookup team"), null, null);
        return await scope.ServiceProvider.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = title, Kind = kind, TeamId = team, ArticleId = articleId });
    }

    private async Task<string> Page(string q, SignIn? who = null)
    {
        await using var r = await RenderAsync<Search>(app, "/search?q=" + Uri.EscapeDataString(q), @as: who ?? Person());
        return await r.Html();
    }

    // One element's content by its id, as it reads: decoded, with the markup taken out.
    private static string Text(string html, string id) => WebUtility.HtmlDecode(Tags().Replace(Inside(html, id), " ")).Trim();

    private static string Inside(string html, string id)
    {
        var m = Regex.Match(html, $"<(\\w+)[^>]*id=\"{id}\"[^>]*>(.*?)</\\1>", RegexOptions.Singleline);
        m.Success.Should().BeTrue($"the page has #{id}");
        return m.Groups[2].Value;
    }

    // The first line as it reads: a link's words run on into the comma after it.
    private static string Summary(string html) =>
        Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(Inside(html, "lookup-summary"), "")), " ").Trim();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static int Count(string html, string what) => html.Split(what).Length - 1;

    [Fact]
    public async Task TheFirstLine_SaysItsKnown_WhenItWasBriefed_AndWhoIsOnIt()
    {
        var domain = Unique("briefed") + ".example.net";
        var day = Past(41);
        var id = await Article("Lookup briefed article", [(IndicatorType.Domain, domain)], brief: day, presented: true);
        var sweep = await Action("Lookup sweep for it", ActionKind.IocSweep, id);
        await Action("Lookup block for it", ActionKind.Block, id);

        var html = await Page(domain);

        Summary(html).Should().Be($"On 1 article · briefed {day:ddd MMM d} · 2 actions open · no hunt result yet");
        Inside(html, "lookup-summary").Should().Contain($"href=\"briefs/{day:yyyy-MM-dd}\"");
        var sweeping = Inside(html, "lookup-sweeping");
        sweeping.Should().Contain("Lookup sweep for it").And.Contain("already sweeps for this").And.NotContain("Lookup block for it")
            .And.Contain($"href=\"actions/{sweep}\"");
        WebUtility.HtmlDecode(Tags().Replace(sweeping, "")).Should().MatchRegex(@"unassigned\)\s+already sweeps for this\.", "the words don't run together");
    }

    [Fact]
    public async Task ABriefThatHasntBeenPresented_IsntCalledBriefed()
    {
        var today = Unique("today") + ".example.net";
        var id = await Article("Lookup on today's brief", [(IndicatorType.Domain, today)], brief: Today);
        var html = await Page(today);
        Summary(html).Should().Be($"On 1 article · {NotPresented(Today)} · no action raised");
        var row = html[html.IndexOf($"href=\"articles/{id}\"", StringComparison.Ordinal)..];
        row = WebUtility.HtmlDecode(Tags().Replace(row[..row.IndexOf("</tr>", StringComparison.Ordinal)], " "));
        row.Should().Contain("On the brief for").And.Contain("not presented yet").And.NotContain("Briefed");

        var earlier = Unique("earlier") + ".example.net";
        var day = Past(43);
        await Article("Lookup on an unpresented brief", [(IndicatorType.Domain, earlier)], brief: day);
        Summary(await Page(earlier)).Should().Be($"On 1 article · on the brief for {day:ddd MMM d}, not presented yet · no action raised");
    }

    [Fact]
    public async Task ACveLookup_ClaimsNoHuntResult_AndNamesTheSweepOfItsArticlesIndicators()
    {
        var cve = NewCve();
        var id = await Article("Lookup CVE with a sweep", [(IndicatorType.Domain, Unique("cvesweep") + ".example.net")], cves: [cve]);
        var sweep = await Action("Lookup CVE article sweep", ActionKind.IocSweep, id);
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
            var hunt = await actions.GenerateHuntAsync((await actions.GetAsync(sweep))!.Action.Id, QueryDialects.All[0].Key);
            await actions.RecordHuntResultAsync(hunt.Id, HuntResult.NoHits, null, "Clean.", null);
        }

        var html = await Page(cve);

        Summary(html).Should().Be("On 1 article · not on a brief yet · 1 action open");
        var swept = Inside(html, "lookup-swept");
        swept.Should().Contain("Lookup CVE article sweep").And.Contain($"href=\"actions/{sweep}\"");
        WebUtility.HtmlDecode(Tags().Replace(swept, " ")).Should().Contain("indicators on these articles");
    }

    [Fact]
    public async Task ALanguageWithNoQueryForTheValue_SaysSo_AndOffersNoCopy()
    {
        var url = $"https://{Unique("nofql")}.example.net/payload.bin";
        await Article("Lookup URL no Falcon query", [(IndicatorType.Url, url)]);
        var who = Person();
        Inside(await Page(url, who), "lookup-query-tools").Should().NotContain("disabled");
        await using (var scope = SignedIn(app, who))
            await scope.ServiceProvider.GetRequiredService<UserPreferenceService>().SetAsync(UserPreferences.HuntDialect, "fql");

        var html = await Page(url, who);

        Inside(html, "lookup-query-tools").Should().MatchRegex("<button[^>]*data-copy-target=\"lookup-query\"[^>]*disabled", "there's nothing to run");
        Text(html, "lookup-query-none").Should().Contain("can't search for this kind of value");
    }

    [Fact]
    public async Task TheFirstLine_SaysNotSeenBefore_AndNotBriefedYet()
    {
        Summary(await Page(Unique("never") + ".example.net")).Should().Be("Not seen before");

        var domain = Unique("written") + ".example.net";
        await Article("Lookup written, not briefed", [(IndicatorType.Domain, domain)]);
        Summary(await Page(domain)).Should().Be("On 1 article · not on a brief yet · no action raised");

        var cve = NewCve();
        Summary(await Page(cve)).Should().Be("Not seen before");
        await Article("Lookup CVE article", [], cves: [cve]);
        Summary(await Page(cve)).Should().Be("On 1 article · not on a brief yet · no action raised");
    }

    [Fact]
    public async Task TheArticle_IsListedOnce_WithItsBadges_WhatMatched_AndWhen()
    {
        var domain = Unique("once") + ".example.net";
        var day = Past(42);
        var id = await Article("Lookup listed once", [(IndicatorType.Domain, domain)], brief: day, presented: true);

        var html = await Page(domain);

        Count(html, $"href=\"articles/{id}\"").Should().Be(1, "the article is in one list, not two");
        html.Should().MatchRegex("<table class=\"tide-table tide-lookup-articles\">\\s*<thead><tr><th scope=\"col\">Article</th><th scope=\"col\">Indicator</th><th scope=\"col\">Briefed</th><th scope=\"col\">Written</th></tr></thead>",
            "the article is a row of a table: what matched, when it was briefed and when it was written each a column");
        var row = html[html.IndexOf($"href=\"articles/{id}\"", StringComparison.Ordinal)..];
        row = WebUtility.HtmlDecode(row[..row.IndexOf("</tr>", StringComparison.Ordinal)]);
        row.Should().Contain("TLP:AMBER").And.Contain("Informational").And.Contain("This indicator")
            .And.Contain($"{day:ddd MMM d}").And.Contain("</td><td");
        html.Should().Contain("<h3 class=\"tide-card-title\">Actions raised</h3>", "the card's heading starts at its top, with no band above it");

        var cve = NewCve();
        var about = await Article("Lookup CVE listed once", [], cves: [cve]);
        Count(await Page(cve), $"href=\"articles/{about}\"").Should().Be(1);
    }

    [Fact]
    public async Task TheHuntQuery_IsThereOnTheFirstRender_InTheLanguageUsedLast_AndLookingSavesNothing()
    {
        var domain = Unique("query") + ".example.net";
        var id = await Article("Lookup query article", [(IndicatorType.Domain, domain)]);
        await Action("Lookup query sweep", ActionKind.IocSweep, id);
        var who = Person();
        async Task<(int Hunts, int Events, int Preferences)> Rows()
        {
            await using var scope = SignedIn(app, SignIn.Analyst);
            await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
            return (await db.HuntQueries.CountAsync(), await db.ActionEvents.CountAsync(), await db.UserPreferences.CountAsync(p => p.UserId == who.UserId));
        }
        var before = await Rows();

        var html = await Page(domain, who);

        var query = Text(html, "lookup-query");
        query.Should().Contain($"\"{domain}\"").And.Contain("dataset = xdr_data", "XQL is the first language, until one is chosen");
        html.Should().Contain("data-copy-target=\"lookup-query\"").And.Contain("aria-label=\"Query language\"").And.NotContain("Build query");
        (await Rows()).Should().Be(before, "looking a value up writes no hunt, no history and no preference");

        await using (var scope = SignedIn(app, who))
            await scope.ServiceProvider.GetRequiredService<UserPreferenceService>().SetAsync(UserPreferences.HuntDialect, "kql");
        var again = await Page(domain, who);
        Text(again, "lookup-query").Should().Contain("DeviceNetworkEvents").And.NotContain("dataset = xdr_data");
        again.Should().MatchRegex("<option value=\"kql\" selected[^>]*>");
    }

    [Fact]
    public async Task ChoosingALanguage_RebuildsTheQuery_AndIsRememberedForThatPersonOnly()
    {
        var domain = Unique("choose") + ".example.net";
        await Article("Lookup choose article", [(IndicatorType.Domain, domain)]);
        var who = Person("Viewer");
        await using var r = await RenderAsync<Search>(app, "/search?q=" + Uri.EscapeDataString(domain), @as: who);

        // The selector's change, then the render the event would be followed by.
        await r.Click(async p =>
        {
            await p.ChooseDialect("fql");
            await p.SetParametersAsync(ParameterView.Empty);
        });

        Text(await r.Html(), "lookup-query").Should().Contain("#event_simpleName=DnsRequest");
        await using (var scope = SignedIn(app, who))
            (await scope.ServiceProvider.GetRequiredService<UserPreferenceService>().GetAsync(UserPreferences.HuntDialect)).Should().Be("fql");
        await using (var scope = SignedIn(app, Person()))
            (await scope.ServiceProvider.GetRequiredService<UserPreferenceService>().GetAsync(UserPreferences.HuntDialect)).Should().Be("xql");
    }

    [Fact]
    public async Task AValueWithQuotesAndBackslashes_IsEscapedInTheQuery_AndEncodedOnThePage()
    {
        var host = Unique("quote") + ".example.net";
        var url = $"https://{host}/a\"b\\c<script>alert(1)</script>";
        await Article("Lookup quoted URL", [(IndicatorType.Url, url)]);

        var html = await Page(url);

        html.Should().NotContain("<script>alert(1)").And.NotContain("a\"b\\c<");
        WebUtility.HtmlDecode(Inside(html, "lookup-query"))
            .Should().Contain($"\"https://{host}/a\\\"b\\\\c<script>alert(1)</script>\"", "the quote and the backslash are escaped inside the string literal");
        Summary(html).Should().StartWith("On 1 article");
    }

    [Fact]
    public async Task SeenWith_LinksToALookupOfEachOtherValue_Encoded()
    {
        var domain = Unique("seen") + ".example.net";
        var other = "https://" + Unique("other") + ".example.net/a?b=1&c=<x>";
        var cve = NewCve();
        var id = await Article("Lookup seen with", [(IndicatorType.Domain, domain), (IndicatorType.Url, other)], cves: [cve], techniques: ["T1566.001"]);
        Guid story;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var stories = scope.ServiceProvider.GetRequiredService<StoryService>();
            story = await stories.CreateAsync("Lookup story " + Unique("s"), null);
            await stories.LinkAsync(id, story);
        }

        var seen = Inside(await Page(domain), "lookup-seen-with");

        seen.Should().Contain($"href=\"search?q={Uri.EscapeDataString(other)}\"").And.Contain($"href=\"search?q={cve}\"")
            .And.Contain("href=\"search?q=T1566.001\"").And.Contain($"href=\"stories/{story}\"");
        seen.Should().NotContain("<x>").And.NotContain(Uri.EscapeDataString(domain), "the value looked up isn't seen with itself");
        WebUtility.HtmlDecode(seen).Should().Contain("hxxps://", "an indicator is shown defanged");
    }

    // A new item in Triage's inbox naming the CVE (and one already dismissed, and one naming a longer id), on no article.
    private async Task InTriage(string cve)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        var feed = new Source { Name = Unique("Lookup feed"), Url = $"https://{Unique("feed")}.example.net/rss" };
        var now = DateTimeOffset.UtcNow;
        FeedItem Item(string title, string cves, FeedItemStatus status) => new()
        {
            Source = feed, Title = title, Url = $"https://{Unique("item")}.example.net/", UrlHash = Unique("hash"), Cves = cves,
            Status = status, PublishedAtUtc = now, FetchedAtUtc = now,
        };
        db.AddRange(Item($"Advisory for {cve}", $"CVE-2020-0001,{cve}", FeedItemStatus.New),
            Item($"Old note on {cve}", cve, FeedItemStatus.Dismissed), Item("A longer id", cve + "1", FeedItemStatus.New));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ACveInTheTriageInbox_IsNotCalledUnseen_AndTriageIsNamedToThoseWhoTriage()
    {
        var cve = NewCve();
        await InTriage(cve);

        var analyst = await Page(cve);
        Summary(analyst).Should().Be("Not on any article yet · no action raised · 1 new in triage");
        Inside(analyst, "lookup-summary").Should().Contain($"href=\"triage?q={cve}\"");

        // A viewer doesn't see Triage: nothing of it is named, and nothing is called unseen that someone might have.
        Summary(await Page(cve, Person("Viewer"))).Should().Be("Not on any article yet · no action raised");
    }

    [Fact]
    public async Task SomeoneBelowTheTeamEdition_GetsNothingOfTheLookup()
    {
        var domain = Unique("reader") + ".example.net";
        var cve = NewCve();
        var id = await Article("Lookup restricted article", [(IndicatorType.Domain, domain)], cves: [cve], brief: Today);
        await Action("Lookup restricted sweep", ActionKind.IocSweep, id);

        foreach (var role in new[] { "Reader", "Visitor" })
            foreach (var q in new[] { domain, cve })
            {
                var html = await Page(q, Person(role));
                html.Should().NotContain("lookup-summary", role).And.NotContain("Lookup restricted").And.NotContain("lookup-query")
                    .And.NotContain("lookup-seen-with").And.NotContain("On 1 article").And.NotContain("Not seen before");
            }
        await using var scope = SignedIn(app, Person("Reader"));
        await FluentActions.Awaiting(() => scope.ServiceProvider.GetRequiredService<UserPreferenceService>().GetAsync(UserPreferences.HuntDialect))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ASearchForWords_ListsItsArticlesAsBefore_WithNoLookup()
    {
        var word = Unique("zylophone");
        var id = await Article($"Lookup {word} words", [(IndicatorType.Domain, Unique("words") + ".example.net")]);

        var html = await Page(word);

        Count(html, $"href=\"articles/{id}\"").Should().Be(1);
        html.Should().Contain("Articles <span class=\"tide-count tide-muted fw-normal\">(1)</span>").And.NotContain("lookup-summary").And.NotContain("lookup-query");
    }
}
