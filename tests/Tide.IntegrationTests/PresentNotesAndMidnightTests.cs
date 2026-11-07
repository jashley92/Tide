using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>The app on a clock a test moves on: the org's own time, for a meeting that runs past midnight.</summary>
public sealed class LateMeetingApp : TideApp
{
    // A Wednesday, ten to midnight in the org's zone (New York, EST in November).
    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 11, 4, 23, 50, 0, TimeSpan.FromHours(-5)));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(Clock));
    }
}

/// <summary>A meeting that runs past midnight: what's raised in it and its end are its own day's.</summary>
public class PresentPastMidnightTests(LateMeetingApp app) : IClassFixture<LateMeetingApp>
{
    [Fact]
    public async Task AMeetingRunPastMidnight_RaisesItsActionsForItsDay_AndIsRecordedWhenItEnded()
    {
        var day = app.Services.GetRequiredService<OrgClock>().Today;
        day.Should().Be(new DateOnly(2026, 11, 4));
        Guid article;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            await scope.ServiceProvider.GetRequiredService<TeamService>().AddAsync("Late Night Team", null, null);
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var brief = await briefs.GetOrStartAsync(day);
            article = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Late meeting article", "Vendor", null, TlpLevel.Clear);
            await briefs.AddArticleAsync(brief.Id, article);
        }
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());

        app.Clock.Now += TimeSpan.FromMinutes(20);   // ten past midnight: Thursday
        await Do(pat, p =>
        {
            typeof(Present).GetField("_qaTitle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(p, "Sweep the proxy logs");
            return p.QuickAdd(article);
        });
        await Do(pat, p => p.EndMeeting());

        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var action = (await scope.ServiceProvider.GetRequiredService<ActionService>().ListAsync(new ActionFilter())).Single(a => a.Title == "Sweep the proxy logs");
            action.RaisedOn.Should().Be(day, "it was raised in Wednesday's meeting, which ran past midnight");
            var brief = (await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(day))!;
            brief.Status.Should().Be(BriefStatus.Presented);
            brief.MeetingHeldAtUtc.Should().Be(app.Clock.Now.ToUniversalTime(), "it ended then, not at that day's 10:30 meeting time");
        }
    }
}

/// <summary>A note keeps the article it was begun on when the presenter moves on as it's written.</summary>
public class PresentNoteTargetTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Robin = new("dev:round-robin", "Robin Analyst", "Analyst");

    // The first article's slide: two before the wrap-up.
    private static async Task FirstArticle(Rendered<Present> r)
    {
        await r.Click(p => p.OnPresentKey("last"));
        await r.Click(p => p.OnPresentKey("prev"));
        await r.Click(p => p.OnPresentKey("prev"));
    }

    [Fact]
    public async Task ANoteBegunOnOneArticle_StaysOnIt_WhenThePresenterMovesOnBeforeItsAdded()
    {
        var day = await TwoArticleBrief(app, 171, "Round note");
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());
        await FirstArticle(pat);
        await Do(robin, p => p.Follow());
        await Do(robin, p => p.ToggleNotes());
        (await robin.Html()).Should().Contain("placeholder=\"On Round note first 171\"");

        await Do(robin, p => p.TypeNote("Sweep the proxy logs for the first one"));
        await pat.Click(p => p.OnPresentKey("next"));
        await Do(robin, p => p.Follow());
        (await robin.Html()).Should().Contain("<h2>Round note second 171</h2>").And.Contain("This note is on Round note first 171");

        await Do(robin, p => p.AddNote());
        Guid brief, first;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var b = (await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(day))!;
            (brief, first) = (b.Id, b.Items.OrderBy(i => i.SortOrder).First().ArticleId);
            (await scope.ServiceProvider.GetRequiredService<MeetingService>().GetAsync(brief)).Notes.Single().ArticleId
                .Should().Be(first, "it was written about the first article, whatever the slide shows when it's added");
        }
        (await robin.Html()).Should().NotContain("This note is on");
        EndMeeting(app, day);
    }
}
