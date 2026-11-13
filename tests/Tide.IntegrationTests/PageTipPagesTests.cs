using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Operations;
using Tide.Application.Preferences;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Page tips: one per changed page, shown on the first visit, dismissed with Got it and never shown again, only on pages
/// the person can use, never in present mode, and back again from Help's Show tips again.
/// </summary>
public class PageTipPagesTests(TideApp app) : IClassFixture<TideApp>
{
    private static SignIn Person(params string[] roles) => new($"dev:tips-{Guid.NewGuid():N}", "Tip tester", roles);

    private async Task<PageTip?> Due(SignIn who, string path)
    {
        await using var scope = SignedIn(app, who);
        return await scope.ServiceProvider.GetRequiredService<PageTipService>().DueAsync(path);
    }

    [Fact]
    public async Task ATip_ShowsOnTheFirstVisit_AndGotItPutsItAwayForGood()
    {
        var who = Person("Analyst");
        await using (var r = await RenderAsync<OnboardingHost>(app, "/triage", @as: who))
        {
            await r.Click(c => c.CheckTipAsync());
            var html = await r.Html();
            html.Should().Contain("class=\"tide-tip\"").And.Contain("The reading pane").And.Contain("Got it");
            html.Should().Contain("role=\"status\"", "the tip is announced politely");
            html.Should().NotContain("aria-modal", "a tip is never modal");
            await r.Click(c => c.DismissTipAsync());
            (await r.Html()).Should().NotContain("class=\"tide-tip\"");
        }
        (await Due(who, "triage")).Should().BeNull("dismissed, it's never shown again");
        await using var scope = SignedIn(app, who);
        (await scope.ServiceProvider.GetRequiredService<UserPreferenceService>().GetAsync("tip.triage")).Should().Be("1.13.0");
    }

    [Fact]
    public async Task ATip_ShowsOnlyOnAPageThePersonCanUse()
    {
        var viewer = Person("Viewer");
        (await Due(viewer, "triage")).Should().BeNull("a Viewer can't open Triage");
        (await Due(viewer, "actions")).Should().NotBeNull();
        (await Due(Person("Visitor"), "")).Should().BeNull();
        (await Due(Person("Reader"), "")).Should().BeNull();
    }

    [Theory]
    [InlineData("/present/2026-11-12")]
    [InlineData("/present/2026-11-12?as=Jordan")]
    public async Task PresentMode_NeverShowsATip(string path)
    {
        await using var r = await RenderAsync<OnboardingHost>(app, path, @as: Person("Analyst"));
        await r.Click(c => c.CheckTipAsync());
        (await r.Html()).Should().NotContain("class=\"tide-tip\"");
    }

    [Fact]
    public async Task ShowTipsAgain_InHelp_BringsBackEveryTipThePersonCanUse()
    {
        var who = Person("Viewer");
        await using (var scope = SignedIn(app, who))
        {
            var tips = scope.ServiceProvider.GetRequiredService<PageTipService>();
            await tips.DismissAsync(PageTips.For("actions")!);
        }
        (await Due(who, "actions")).Should().BeNull();

        await using (var help = await RenderAsync<HelpPage>(app, "/help", @as: who))
        {
            (await help.Html()).Should().Contain("Show tips again");
            await help.Click(c => c.ShowTipsAgainAsync());
        }
        (await Due(who, "actions")).Should().NotBeNull("Help's Show tips again resets them");
        (await Due(who, "triage")).Should().BeNull("still only on pages the person can use");
    }

    [Fact]
    public async Task WhatsNew_ComesFirst_AndATipWaitsUntilItsClosed()
    {
        var who = Person("Analyst");
        await using var r = await RenderAsync<OnboardingHost>(app, "/", @as: who);
        await r.Click(c => c.CheckAsync());
        var html = await r.Html();
        html.Should().Contain("tide-whatsnew").And.NotContain("class=\"tide-tip\"", "one thing at a time");
        await r.Click(c => c.WhatsNew!.CloseAsync());
        (await r.HtmlOnce("class=\"tide-tip\"")).Should().Contain("class=\"tide-tip\"").And.Contain("context rail");
    }
}
