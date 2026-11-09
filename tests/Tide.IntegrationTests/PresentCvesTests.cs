using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// An article's CVEs on its slide: a summary ("3 CVEs, 2 on KEV"), the most pressing chip, and "+N more". On the team
/// edition that opens every CVE over the slide, the presenter's for everyone following and anyone else's for themselves;
/// the company-wide slide, which opens no cards, opens its list in place for whoever asked.
/// </summary>
public class PresentCvesTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:cves-owner", "Olive Owner", "ActionOwner");

    private DateOnly Today { get; } = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    private sealed record Setup(DateOnly Day, Guid Article, string[] Cves);

    // A past day's brief with one article and three CVEs: one on KEV at 8.8, one on KEV at 5.5, one off KEV at 9.8.
    private async Task<Setup> Brief(int daysBack)
    {
        var day = Today;
        for (var i = 0; i < daysBack; i++) day = WorkingDays.Weekdays.Previous(day);
        var ids = new[] { $"CVE-2026-{daysBack}01", $"CVE-2026-{daysBack}02", $"CVE-2026-{daysBack}03" };
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var brief = await briefs.GetOrStartAsync(day);
        var article = await articles.CreateAsync($"CVE slide {daysBack}", "Vendor advisory", null, TlpLevel.Clear);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Set<ArticleCve>().AddRange(ids.Select(id => new ArticleCve { ArticleId = article, CveId = id }));
        db.CveRecords.AddRange(
            new CveRecord { CveId = ids[0], CvssScore = 5.5m, IsKev = true },
            new CveRecord { CveId = ids[1], CvssScore = 8.8m, IsKev = true, Vendor = "VMware", Product = "ESX", KevAddedDate = new DateOnly(2026, 10, 28) },
            new CveRecord { CveId = ids[2], CvssScore = 9.8m });
        await db.SaveChangesAsync();
        await briefs.AddArticleAsync(brief.Id, article);
        return new Setup(day, article, ids);
    }

    private static async Task FirstArticle(Rendered<Present> r)
    {
        await r.Click(p => p.OnPresentKey("last"));
        await r.Click(p => p.OnPresentKey("prev"));
    }

    private static string Cves(string html)
    {
        var at = html.IndexOf("<div class=\"tide-slide-cves\"", StringComparison.Ordinal);
        return System.Net.WebUtility.HtmlDecode(html[at..html.IndexOf("</div>", at, StringComparison.Ordinal)]);
    }

    [Fact]
    public void TheMostPressingComeFirst_AndTheSummarySaysHowManyAreOnKev()
    {
        var known = new Dictionary<string, CveRecord>
        {
            ["CVE-1"] = new() { CveId = "CVE-1", CvssScore = 9.8m },
            ["CVE-2"] = new() { CveId = "CVE-2", CvssScore = 5.5m, IsKev = true },
            ["CVE-3"] = new() { CveId = "CVE-3", CvssScore = 8.8m, IsKev = true },
        };
        var list = SlideCves.For(["CVE-4", "CVE-1", "CVE-2", "CVE-3"], known);
        list.Items.Select(x => x.Id).Should().Equal(["CVE-3", "CVE-2", "CVE-1", "CVE-4"], "on KEV first, then by CVSS; one not enriched yet last");
        list.Items[0].Record.Should().BeSameAs(known["CVE-3"]);
        (list.Kev, list.Summary).Should().Be((2, "4 CVEs, 2 on KEV"));
        SlideCves.For(["CVE-2", "CVE-3"], known).Summary.Should().Be("2 CVEs, all on KEV");
        SlideCves.For(["CVE-1", "CVE-4"], known).Summary.Should().Be("2 CVEs");
    }

    [Fact]
    public async Task TheSlide_SumsThemUp_ShowsTheMostPressing_AndItsMoreOpensThemAll()
    {
        var s = await Brief(61);
        await using var pat = await Open(app, s.Day);
        await FirstArticle(pat);
        var row = Cves(await pat.Html());
        row.Should().Contain("3 CVEs, 2 on KEV").And.Contain("tide-slide-cve-sum-kev").And.Contain(s.Cves[1], "the KEV one at 8.8 is the most pressing")
            .And.NotContain(s.Cves[0]).And.NotContain(s.Cves[2]).And.Contain("+2 more</button>");

        await Do(pat, p => p.ShowCves(s.Article));
        var html = await pat.Html();
        html.Should().Contain("present-cves-title").And.Contain(s.Cves[0]).And.Contain(s.Cves[2]).And.Contain("VMware ESX").And.Contain("On CISA KEV since 2026-10-28");
        html.IndexOf(s.Cves[1], html.IndexOf("present-cves-title", StringComparison.Ordinal), StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf(s.Cves[0], html.IndexOf("present-cves-title", StringComparison.Ordinal), StringComparison.Ordinal), "most pressing first");
        await Do(pat, p => p.OnPresentKey("close"));
        (await pat.Html()).Should().NotContain("present-cves-title");

        // The company-wide slide opens no cards: its "+2 more" opens the list in place, for this screen.
        await Do(pat, p => p.SetEdition(new ChangeEventArgs { Value = Edition.Public.ToString() }));
        await Do(pat, p => p.ShowCves(s.Article));
        (await pat.Html()).Should().NotContain("present-cves-title");
        await Do(pat, p => p.ToggleCves(s.Article));
        Cves(await pat.Html()).Should().Contain(s.Cves[0]).And.Contain(s.Cves[2]).And.Contain("Show fewer").And.Contain("aria-expanded=\"true\"");
        await Do(pat, p => p.ToggleCves(s.Article));
        Cves(await pat.Html()).Should().NotContain(s.Cves[0]).And.Contain("+2 more");
    }

    [Fact]
    public async Task ThePresentersCves_ShowForEveryoneFollowing_AndAViewerOpensThemForThemselves()
    {
        var s = await Brief(62);
        await using var pat = await Open(app, s.Day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(app, s.Day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await FirstArticle(pat);
        await Do(owner, p => p.Follow());

        // A viewer's own: theirs alone, the room's card unchanged.
        await Do(owner, p => p.ShowCves(s.Article));
        (await owner.Html()).Should().Contain("present-cves-title");
        Meetings.Get(s.Day)!.Card.Should().BeNull("a viewer opens it for themselves");
        (await pat.Html()).Should().NotContain("present-cves-title");
        await Do(owner, p => p.CloseCard());

        // The presenter's: for everyone following.
        await Do(pat, p => p.ShowCves(s.Article));
        Meetings.Get(s.Day)!.Card.Should().Be(MeetingCard.ForCves(s.Article));
        (await pat.Html()).Should().Contain("present-cves-title").And.Contain("Shown to everyone following");
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().Contain("present-cves-title").And.Contain("Tester is showing this").And.Contain(s.Cves[2]);

        EndMeeting(app, s.Day);
    }
}
