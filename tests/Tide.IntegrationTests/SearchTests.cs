using FluentAssertions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Search;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

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
        r.Cve.Articles.Should().Be(1);
        r.Articles.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Readers_CantSearch()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Awaiting(() => Search().SearchAsync("anything")).Should().ThrowAsync<ForbiddenException>();
    }
}
