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
}
