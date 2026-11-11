using AwesomeAssertions;
using Tide.Application.Articles;
using Tide.Application.Exports;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

public class ExportServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public ExportServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private ExportService Exports() => new(_db, _db.User);

    private async Task<Guid> Article(string title, TlpLevel tlp, string ip)
    {
        var svc = _db.Articles();
        var id = await svc.CreateAsync(title, "Vendor", null, tlp);
        var form = ArticleForm.From((await svc.GetAsync(id))!);
        form.Bluf = "Summary.";
        form.Indicators = [new() { Type = IndicatorType.IpAddress, Value = ip }];
        await svc.SaveAsync(form);
        return id;
    }

    [Fact]
    public async Task Brief_InRunningOrder_LeavesOutArticlesAboveTheCeiling()
    {
        var day = _db.Clock.CurrentBriefDate;
        var brief = await _db.Briefs().GetOrStartAsync(day);
        var key = $"{day:yyyy-MM-dd}";
        var clear = await Article("Public advisory", TlpLevel.Clear, "203.0.113.1");
        var amber = await Article("Partner report", TlpLevel.Amber, "203.0.113.2");
        var red = await Article("Named victim", TlpLevel.Red, "203.0.113.3");
        foreach (var id in new[] { red, clear, amber }) await _db.Briefs().AddArticleAsync(brief.Id, id);

        var all = (await Exports().GatherAsync(ExportScope.Brief, key, TlpLevel.Red))!;
        all.Articles.Select(a => a.Id).Should().Equal(red, clear, amber);
        all.BriefDates[clear].Should().Equal(day);

        var amberUp = (await Exports().GatherAsync(ExportScope.Brief, key, TlpLevel.Amber))!;
        amberUp.Articles.Select(a => a.Id).Should().Equal(clear, amber);
        amberUp.FileStem.Should().Be($"brief-{day:yyyy-MM-dd}");
        System.Text.Encoding.UTF8.GetString(Stix.Write(amberUp, "CTI")).Should().NotContain("203.0.113.3").And.Contain(Stix.Marking(TlpLevel.Amber));
    }

    [Fact]
    public async Task ArticleAndStory_AreFound_AndUnknownKeysAreNot()
    {
        var a = await Article("Edge VPN zero-day!", TlpLevel.Green, "203.0.113.9");
        var story = await _db.Stories().CreateAsync("Edge devices", null);
        await _db.Stories().LinkAsync(a, story);

        var article = (await Exports().GatherAsync(ExportScope.Article, a.ToString(), TlpLevel.Red))!;
        article.FileStem.Should().Be("article-edge-vpn-zero-day");
        article.Articles.Single().Indicators.Single().Value.Should().Be("203.0.113.9");
        (await Exports().GatherAsync(ExportScope.Story, story.ToString(), TlpLevel.Green))!.Articles.Should().ContainSingle();
        (await Exports().GatherAsync(ExportScope.Story, story.ToString(), TlpLevel.Clear))!.Articles.Should().BeEmpty("the article is TLP:GREEN");
        (await Exports().GatherAsync(ExportScope.Article, Guid.NewGuid().ToString(), TlpLevel.Red)).Should().BeNull();
        (await Exports().GatherAsync(ExportScope.Brief, "not-a-day", TlpLevel.Red)).Should().BeNull();
    }

    [Fact]
    public async Task Readers_CantExport()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Awaiting(() => Exports().GatherAsync(ExportScope.Brief, "2026-10-26", TlpLevel.Red)).Should().ThrowAsync<ForbiddenException>();
    }

    // An article carrying `ip` and another indicator of its own (what the looked-up one travels with), and a CVE.
    private async Task<Guid> Carrying(string title, TlpLevel tlp, string ip, string alongside, string? cve = null)
    {
        var svc = _db.Articles();
        var id = await svc.CreateAsync(title, "Vendor", null, tlp);
        var form = ArticleForm.From((await svc.GetAsync(id))!);
        form.Bluf = title + ": summary.";
        form.Indicators = [new() { Type = IndicatorType.IpAddress, Value = ip }, new() { Type = IndicatorType.Domain, Value = alongside }];
        if (cve is not null) form.Cves = [cve];
        await svc.SaveAsync(form);
        return id;
    }

    [Fact]
    public async Task ALookup_HoldsTheArticlesItsOn_WithWhatItTravelsWith_AndLeavesOutThoseAboveTheCeiling()
    {
        var clear = await Carrying("Lookup clear", TlpLevel.Clear, "198.51.100.7", "clear-only.example.net", "CVE-2031-111111");
        var amber = await Carrying("Lookup amber", TlpLevel.Amber, "198.51.100.7", "amber-only.example.net", "CVE-2031-111111");
        var red = await Carrying("Lookup red", TlpLevel.Red, "198.51.100.7", "red-only.example.net", "CVE-2031-111111");
        await Carrying("Lookup elsewhere", TlpLevel.Clear, "198.51.100.8", "elsewhere.example.net");
        var key = ExportService.LookupKey("198.51.100.7");

        var all = (await Exports().GatherAsync(ExportScope.Lookup, key, TlpLevel.Red))!;
        all.Articles.Select(a => a.Id).Should().BeEquivalentTo([clear, amber, red], "every article it's on, and none it isn't");
        all.FileStem.Should().Be("lookup-198-51-100-7");

        var amberUp = (await Exports().GatherAsync(ExportScope.Lookup, key, TlpLevel.Amber))!;
        amberUp.Articles.Select(a => a.Id).Should().BeEquivalentTo([clear, amber], "a TLP:RED article is left out whole under an AMBER ceiling");
        var stix = System.Text.Encoding.UTF8.GetString(Stix.Write(amberUp, "CTI"));
        stix.Should().Contain("198.51.100.7").And.Contain("amber-only.example.net", "what it travels with goes with it")
            .And.Contain(Stix.Marking(TlpLevel.Amber)).And.Contain(Stix.Marking(TlpLevel.Clear))
            .And.NotContain("red-only.example.net").And.NotContain("Lookup red").And.NotContain(Stix.Marking(TlpLevel.Red))
            .And.NotContain("elsewhere.example.net");
        IndicatorCsv.Write(amberUp, null).Should().Contain("amber-only.example.net").And.NotContain("red-only.example.net").And.NotContain("TLP:RED");

        var clearOnly = (await Exports().GatherAsync(ExportScope.Lookup, key, TlpLevel.Clear))!;
        clearOnly.Articles.Select(a => a.Id).Should().Equal(clear);

        // A defanged value looks up the same articles, as Search does; a CVE the articles about it.
        (await Exports().GatherAsync(ExportScope.Lookup, ExportService.LookupKey("198.51.100[.]7"), TlpLevel.Red))!.Articles.Should().HaveCount(3);
        var byCve = (await Exports().GatherAsync(ExportScope.Lookup, ExportService.LookupKey("cve-2031-111111"), TlpLevel.Amber))!;
        byCve.Articles.Select(a => a.Id).Should().BeEquivalentTo([clear, amber]);
        byCve.FileStem.Should().Be("lookup-cve-2031-111111");
    }

    [Fact]
    public async Task ALookupKey_ThatIsNoIndicatorOrCve_FindsNothing()
    {
        (await Exports().GatherAsync(ExportScope.Lookup, ExportService.LookupKey("just words"), TlpLevel.Red)).Should().BeNull();
        (await Exports().GatherAsync(ExportScope.Lookup, "not base64!", TlpLevel.Red)).Should().BeNull();
        (await Exports().GatherAsync(ExportScope.Lookup, ExportService.LookupKey("unseen.example.org"), TlpLevel.Red))!.Articles.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AppRole.Reader)]
    [InlineData(AppRole.Visitor)]
    public async Task SomeoneWhoCantReadTheTeamEdition_GetsNothingOfALookup(AppRole role)
    {
        await Carrying("Lookup restricted", TlpLevel.Clear, "198.51.100.9", "restricted.example.net");
        _db.User.Roles = new HashSet<AppRole> { role };
        await FluentActions.Awaiting(() => Exports().GatherAsync(ExportScope.Lookup, ExportService.LookupKey("198.51.100.9"), TlpLevel.Clear))
            .Should().ThrowAsync<ForbiddenException>();
    }
}
