using FluentAssertions;
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
/// An article's indicators in present mode: the slide's "N indicators" button opens them over the slide, defanged and by
/// type, and the presenter's opens for everyone following, as a product's card does. The slide links to the source.
/// </summary>
public class PresentIndicatorsTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:iocs-owner", "Olive Owner", "ActionOwner");

    // Read once: a test that straddles midnight mustn't see two days.
    private DateOnly Today { get; } = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    private sealed record Setup(DateOnly Day, Guid WithIocs, Guid Without);

    // A past day's brief with two articles: the first with a source and two indicators, the second with neither.
    private async Task<Setup> Brief(int daysBack)
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
    public async Task TheSlidesButton_OpensItsIndicators_Defanged_ByType_AndTheSlideLinksToTheSource()
    {
        var s = await Brief(71);
        await using var pat = await Open(app, s.Day);
        await FirstArticle(pat);
        var html = await pat.Html();
        html.Should().Contain("tide-slide-iocs").And.Contain("2 indicators")
            .And.Contain("href=\"https://example.com/advisory\" target=\"_blank\" rel=\"noopener noreferrer\"").And.Contain("Vendor advisory")
            .And.Contain("bi-box-arrow-up-right").And.NotContain("present-iocs-title");

        await Do(pat, p => p.ShowIndicators(s.WithIocs));
        html = await pat.Html();
        html.Should().Contain("present-iocs-title").And.Contain($"bad71[.]example[.]net").And.Contain("203.0.113[.]45")
            .And.Contain("C2 seen in the advisory").And.NotContain("bad71.example.net", "defanged, as the email shows them")
            .And.Contain($"href=\"articles/{s.WithIocs}\" target=\"_blank\"");
        await Do(pat, p => p.OnPresentKey("close"));
        (await pat.Html()).Should().NotContain("present-iocs-title", "Esc closes it");

        // The company-wide edition carries no indicators (ADR 0005): no button, and none can be opened.
        await Do(pat, p => p.SetEdition(new ChangeEventArgs { Value = Edition.Public.ToString() }));
        await Do(pat, p => p.ShowIndicators(s.WithIocs));
        (await pat.Html()).Should().NotContain("tide-slide-iocs").And.NotContain("present-iocs-title").And.NotContain("bad71");

        // An article without indicators has nothing to open.
        await Do(pat, p => p.SetEdition(new ChangeEventArgs { Value = Edition.Team.ToString() }));
        await Do(pat, p => p.ShowIndicators(s.Without));
        (await pat.Html()).Should().NotContain("present-iocs-title");
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
        Meetings.Get(s.Day)!.Product.Should().Be(s.WithIocs, "the room's card is the article's indicators");
        (await pat.Html()).Should().Contain("present-iocs-title").And.Contain("Shown to everyone following");
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().Contain("present-iocs-title").And.Contain("Tester is showing this").And.Contain("bad72[.]example[.]net");

        // An action opened over the slide replaces it, for the room too.
        using (var otherTab = Meetings.Join(s.Day, "dev:tester", "Tester", true, "", Guid.NewGuid(), Meetings.Get(s.Day)!.Slide)!)
        {
            Meetings.Open(otherTab, 1).Should().BeTrue();
            Meetings.Get(s.Day)!.Product.Should().BeNull("one thing open over the slide at a time");
        }

        // Moving on closes it for everyone.
        await Do(pat, p => p.ShowIndicators(s.WithIocs));
        await pat.Click(p => p.OnPresentKey("next"));
        Meetings.Get(s.Day)!.Product.Should().BeNull();
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().NotContain("present-iocs-title");

        EndMeeting(app, s.Day);
    }
}
