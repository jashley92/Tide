using System.Text.RegularExpressions;
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
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// An article's indicators in present mode: the slide shows them, defanged, so the room sees them without a click; a chip
/// opens the larger card over the slide (by type, with their notes), and the presenter's opens for everyone following, as
/// a product's card does. The slide links to the source.
/// </summary>
public class PresentIndicatorsTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:iocs-owner", "Olive Owner", "ActionOwner");

    // Read once: a test that straddles midnight mustn't see two days.
    private DateOnly Today { get; } = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    private sealed record Setup(DateOnly Day, Guid WithIocs, Guid Without);

    /// <summary>The slide's indicators: its section's markup, or "" when it has none.</summary>
    private static string Iocs(string html) => PresentSlideTagsTests.Section(html, "tide-slide-iocsec") ?? "";

    // A past day's brief with two articles: the first with a source and two indicators (or `many`), the second with neither.
    private async Task<Setup> Brief(int daysBack, int many = 0)
    {
        var day = Today;
        for (var i = 0; i < daysBack; i++) day = WorkingDays.Weekdays.Previous(day);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var brief = await briefs.GetOrStartAsync(day);
        var withIocs = await articles.CreateAsync($"Indicators first {daysBack}", "Vendor advisory", "https://example.com/advisory", TlpLevel.Clear);
        var without = await articles.CreateAsync($"Indicators second {daysBack}", null, null, TlpLevel.Clear);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Set<ArticleIndicator>().AddRange(
            new ArticleIndicator { ArticleId = withIocs, Type = IndicatorType.Domain, Value = $"bad{daysBack}.example.net", Note = "C2 seen in the advisory" },
            new ArticleIndicator { ArticleId = withIocs, Type = IndicatorType.IpAddress, Value = "203.0.113.45" });
        db.Set<ArticleIndicator>().AddRange(Enumerable.Range(1, Math.Max(0, many - 2)).Select(n =>
            new ArticleIndicator { ArticleId = withIocs, Type = IndicatorType.Domain, Value = $"more{n:00}-{daysBack}.example.org" }));
        await db.SaveChangesAsync();
        await briefs.AddArticleAsync(brief.Id, withIocs);
        await briefs.AddArticleAsync(brief.Id, without);
        return new Setup(day, withIocs, without);
    }

    private static async Task FirstArticle(Rendered<Present> r)
    {
        await r.Click(p => p.OnPresentKey("last"));
        await r.Click(p => p.OnPresentKey("prev"));
        await r.Click(p => p.OnPresentKey("prev"));
    }

    [Fact]
    public async Task TheSlide_ShowsItsIndicators_Defanged_WithoutAClick_AndAChipOpensTheLargerCard()
    {
        var s = await Brief(71);
        await using var pat = await Open(app, s.Day);
        await FirstArticle(pat);
        var html = await pat.Html();
        var iocs = Iocs(html);
        iocs.Should().Contain("Indicators (2)").And.Contain("203.0.113[.]45").And.Contain("bad71[.]example[.]net")
            .And.NotContain("bad71.example.net", "defanged, as the email shows them").And.Contain("IP address: ").And.Contain("Domain: ")
            .And.Contain("C2 seen in the advisory", "a chip's note, as the card shows it").And.Contain("aria-expanded=\"false\"");
        iocs.IndexOf("203.0.113[.]45", StringComparison.Ordinal).Should().BeLessThan(iocs.IndexOf("bad71[.]example[.]net", StringComparison.Ordinal), "by type, as the card");
        html.Should().Contain("href=\"https://example.com/advisory\" target=\"_blank\" rel=\"noopener noreferrer\"").And.Contain("Vendor advisory")
            .And.Contain("bi-box-arrow-up-right").And.NotContain("present-iocs-title").And.NotContain("2 indicators", "no button to see them");

        // A chip opens the larger card: by type, with the notes, and a link to the article.
        await Do(pat, p => p.ShowIndicators(s.WithIocs));
        html = await pat.Html();
        Iocs(html).Should().Contain("aria-expanded=\"true\"");
        html.Should().Contain("present-iocs-title").And.Contain("bad71[.]example[.]net").And.Contain("203.0.113[.]45")
            .And.Contain("C2 seen in the advisory").And.NotContain("bad71.example.net")
            .And.Contain($"href=\"articles/{s.WithIocs}\" target=\"_blank\"");
        await Do(pat, p => p.OnPresentKey("close"));
        (await pat.Html()).Should().NotContain("present-iocs-title", "Esc closes it");

        // The company-wide edition carries no indicators (ADR 0005): none on the slide, and none can be opened.
        await Do(pat, p => p.SetEdition(new ChangeEventArgs { Value = Edition.Public.ToString() }));
        await Do(pat, p => p.ShowIndicators(s.WithIocs));
        (await pat.Html()).Should().NotContain("tide-slide-iocsec").And.NotContain("present-iocs-title").And.NotContain("bad71").And.NotContain("203.0.113");

        // An article without indicators has no section, and nothing to open.
        await Do(pat, p => p.SetEdition(new ChangeEventArgs { Value = Edition.Team.ToString() }));
        await pat.Click(p => p.OnPresentKey("next"));
        (await pat.Html()).Should().NotContain("tide-slide-iocsec");
        await Do(pat, p => p.ShowIndicators(s.Without));
        (await pat.Html()).Should().NotContain("present-iocs-title");
    }

    [Fact]
    public async Task ManyIndicators_TheSlideShowsTen_AndTheRestOpenTheCard()
    {
        var s = await Brief(73, many: 12);
        await using var pat = await Open(app, s.Day);
        await FirstArticle(pat);
        var iocs = Iocs(await pat.Html());
        iocs.Should().Contain("Indicators (12)").And.Contain("2 more</button>");
        Regex.Count(iocs, "class=\"tide-slide-ioc\"").Should().Be(10);
        await Do(pat, p => p.ShowIndicators(s.WithIocs));
        (await pat.Html()).Should().Contain("present-iocs-title").And.Contain("more10-73[.]example[.]org", "the card has them all");
        await Do(pat, p => p.OnPresentKey("close"));

        // Someone who can't open the card (held to the presenter's slide) sees how many more, and nothing to click.
        await Do(pat, p => p.StartMeeting());
        await Do(pat, p => p.ToggleLock());
        await using var owner = await Open(app, s.Day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await Do(owner, p => p.Follow());
        var theirs = Iocs(await owner.Html());
        theirs.Should().Contain("2 more</span>").And.NotContain("on the article", "they may not be able to open it").And.NotContain("<button");
        EndMeeting(app, s.Day);
    }

    [Fact]
    public async Task ThePresentersIndicators_ShowForThoseFollowing_AndGoWhenTheyMoveOn_OrOpenAProductsCard()
    {
        var s = await Brief(72);
        await using var pat = await Open(app, s.Day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(app, s.Day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await FirstArticle(pat);
        await Do(owner, p => p.Follow());

        await Do(pat, p => p.ShowIndicators(s.WithIocs));
        Meetings.Get(s.Day)!.Card.Should().Be(MeetingCard.ForIndicators(s.WithIocs), "the room's card is the article's indicators");
        (await pat.Html()).Should().Contain("present-iocs-title").And.Contain("Shown to everyone following");
        await Do(owner, p => p.Follow());
        var theirs = await owner.Html();
        theirs.Should().Contain("present-iocs-title").And.Contain("Tester is showing this").And.Contain("bad72[.]example[.]net");
        Iocs(theirs).Should().Contain("Indicators (2)").And.Contain("bad72[.]example[.]net").And.Contain("203.0.113[.]45",
            "a follower's slide shows what the presenter's does");

        // An action opened over the slide replaces it, for the room too.
        using (var otherTab = Meetings.Join(s.Day, "dev:tester", "Tester", true, "", Guid.NewGuid(), Meetings.Get(s.Day)!.Slide)!)
        {
            Meetings.Open(otherTab, 1).Should().BeTrue();
            Meetings.Get(s.Day)!.Card.Should().BeNull("one thing open over the slide at a time");
        }

        // Moving on closes it for everyone.
        await Do(pat, p => p.ShowIndicators(s.WithIocs));
        await pat.Click(p => p.OnPresentKey("next"));
        Meetings.Get(s.Day)!.Card.Should().BeNull();
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().NotContain("present-iocs-title");

        EndMeeting(app, s.Day);
    }
}
