using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Operations;
using Tide.Application.Preferences;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The tour: on a person's first-ever sign-in, for their role; skippable at any step; again from Help. Someone who used
/// TIDE before gets What's new instead. Never in present mode, never for a Visitor.
/// </summary>
public class TourPagesTests(TideApp app) : IClassFixture<TideApp>
{
    private static SignIn Person(params string[] roles) => new($"dev:tour-{Guid.NewGuid():N}", "Tour tester", roles);

    // A first sign-in, as the user mirror records it.
    private async Task FirstSignIn(SignIn who) =>
        await app.Services.GetRequiredService<IUserDirectory>().TouchAsync(who.UserId, who.Name, null, null, string.Join(',', who.Roles));

    private async Task<string> Pref(SignIn who, string name)
    {
        await using var scope = SignedIn(app, who);
        return await scope.ServiceProvider.GetRequiredService<UserPreferenceService>().GetAsync(name);
    }

    // The host moves to each step's page, which a page rendered outside a circuit can't (it throws).
    private static async Task Do(Rendered<OnboardingHost> r, Func<OnboardingHost, Task> what)
    {
        try { await r.Click(what); }
        catch (NavigationException) { }
    }

    [Fact]
    public async Task AFirstSignIn_MarksTheTourDue_ButSomeoneAlreadyRecordedIsnt()
    {
        var newcomer = Person("Analyst");
        await FirstSignIn(newcomer);
        (await Pref(newcomer, UserPreferences.TourState)).Should().Be(UserPreferences.TourDue);

        var regular = Person("Analyst");
        await FirstSignIn(regular);
        await using (var scope = SignedIn(app, regular))
            await scope.ServiceProvider.GetRequiredService<UserPreferenceService>().SetAsync(UserPreferences.TourState, UserPreferences.TourDone);
        await FirstSignIn(regular with { Name = "Renamed tester" });
        (await Pref(regular, UserPreferences.TourState)).Should().Be(UserPreferences.TourDone, "only a new row marks it due");
    }

    [Fact]
    public async Task ANewcomer_GetsTheTour_NotWhatsNew_AndItsShownOnce()
    {
        var who = Person("Analyst");
        await FirstSignIn(who);
        await using (var r = await RenderAsync<OnboardingHost>(app, "/", @as: who))
        {
            await Do(r, c => c.CheckAsync());
            var html = await r.Html();
            html.Should().Contain("tide-tour").And.Contain("Step 1 of").And.Contain("role=\"dialog\"").And.Contain("aria-modal=\"true\"");
            html.Should().NotContain("tide-whatsnew").And.NotContain("class=\"tide-tip\"");
            html.Should().Contain("Skip tour").And.Contain("tide-tour-scrim", "the page under it can't be clicked");
            (await Pref(who, UserPreferences.TourState)).Should().Be(UserPreferences.TourDue, "a reload mid-tour starts it again");
            (await Pref(who, UserPreferences.WhatsNewSeen)).Should().BeEmpty();
            await Do(r, c => c.EndTourAsync());
        }
        (await Pref(who, UserPreferences.TourState)).Should().Be(UserPreferences.TourDone, "Done or Skip settles it");
        (await Pref(who, UserPreferences.WhatsNewSeen)).Should().Be("1.13.0", "the tour stands in for What's new");
        await using var scope = SignedIn(app, who);
        (await scope.ServiceProvider.GetRequiredService<PageTipService>().DueAsync("triage")).Should().BeNull("nor do they need what-changed tips");

        await using var again = await RenderAsync<OnboardingHost>(app, "/", @as: who);
        await Do(again, c => c.CheckAsync());
        (await again.Html()).Should().NotContain("tide-tour").And.NotContain("tide-whatsnew");
    }

    [Fact]
    public async Task ASecondTab_DoesntStartASecondTour()
    {
        var who = Person("Analyst");
        await FirstSignIn(who);
        await using var first = await RenderAsync<OnboardingHost>(app, "/", @as: who);
        await Do(first, c => c.CheckAsync());
        await using (var second = await RenderAsync<OnboardingHost>(app, "/", @as: who))
        {
            await Do(second, c => c.CheckAsync());
            (await second.Html()).Should().NotContain("tide-tour").And.NotContain("tide-whatsnew");
        }
        (await first.Html()).Should().Contain("tide-tour");
        await Do(first, c => c.EndTourAsync());
    }

    [Fact]
    public async Task LeavingThePageAnotherWay_EndsTheTour_AsSkipped()
    {
        var who = Person("Viewer");
        await FirstSignIn(who);
        await using var r = await RenderAsync<OnboardingHost>(app, "/", @as: who);
        await Do(r, c => c.CheckAsync());
        await Do(r, c => c.PageChangedAsync("actions"));
        (await r.Html()).Should().NotContain("tide-tour");
        (await Pref(who, UserPreferences.TourState)).Should().Be(UserPreferences.TourDone);
    }

    [Fact]
    public async Task SomeoneWhoAlreadyUsesTide_GetsWhatsNew_NotTheTour()
    {
        var who = Person("Viewer");
        await using var r = await RenderAsync<OnboardingHost>(app, "/actions", @as: who);
        await Do(r, c => c.CheckAsync());
        (await r.Html()).Should().Contain("tide-whatsnew").And.NotContain("tide-tour");
    }

    [Fact]
    public async Task TheTour_StepsOn_AndCanBeSkippedAtAnyStep()
    {
        var who = Person("Viewer");
        await FirstSignIn(who);
        await using var r = await RenderAsync<OnboardingHost>(app, "/", @as: who);
        await Do(r, c => c.CheckAsync());
        await Do(r, c => c.NextStepAsync());
        (await r.Html()).Should().Contain("Step 2 of");
        await Do(r, c => c.PreviousStepAsync());
        (await r.Html()).Should().Contain("Step 1 of");
        await Do(r, c => c.NextStepAsync());
        await Do(r, c => c.EndTourAsync());
        (await r.Html()).Should().NotContain("tide-tour");
    }

    [Fact]
    public async Task AVisitor_OrPresentMode_NeverGetsTheTour()
    {
        var visitor = Person("Visitor");
        await FirstSignIn(visitor);
        await using (var r = await RenderAsync<OnboardingHost>(app, "/published", @as: visitor))
        {
            await Do(r, c => c.CheckAsync());
            (await r.Html()).Should().NotContain("tide-tour");
        }
        var follower = Person("Viewer");
        await FirstSignIn(follower);
        await using (var r = await RenderAsync<OnboardingHost>(app, "/present/2026-11-12", @as: follower))
        {
            await Do(r, c => c.CheckAsync());
            (await r.Html()).Should().NotContain("tide-tour");
        }
        (await Pref(follower, UserPreferences.TourState)).Should().Be(UserPreferences.TourDue, "still due once they leave the meeting");
    }

    [Fact]
    public async Task TakeTheTour_InHelp_StartsItAgain()
    {
        var who = Person("Analyst");
        await using var r = await RenderAsync<OnboardingHost>(app, "/help", @as: who);
        var help = r.Scope.ServiceProvider.GetRequiredService<TourRequests>();
        await using (var page = await RenderAsync<HelpPage>(app, "/help", @as: who))
            (await page.Html()).Should().Contain("Take the tour");
        await Do(r, _ => { help.Ask(); return Task.CompletedTask; });
        (await r.HtmlOnce("tide-tour")).Should().Contain("Step 1 of");
    }
}
