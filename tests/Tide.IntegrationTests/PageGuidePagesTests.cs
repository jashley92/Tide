using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Each page's guide: listed in Help and offered by the page's Show me around only to someone who can use the page,
/// started only when asked, on that page.
/// </summary>
public class PageGuidePagesTests(TideApp app) : IClassFixture<TideApp>
{
    private static SignIn Person(params string[] roles) => new($"dev:pageguide-{Guid.NewGuid():N}", "Guide tester", roles);

    // Asks the layout's host for a guide; one from Help opens its page, which a page rendered outside a circuit can't (it
    // throws, saying where it was going).
    private static async Task<string?> Ask(Rendered<OnboardingHost> r, string key, bool fromHelp)
    {
        try
        {
            await r.Click(_ =>
            {
                r.Scope.ServiceProvider.GetRequiredService<TourRequests>().AskGuide(key, fromHelp);
                return Task.CompletedTask;
            });
            await r.Until(h => h.Step is not null, seconds: 3);
        }
        catch (NavigationException e) { return e.Location; }
        return null;
    }

    [Fact]
    public async Task Help_ListsEachRolesGuides_AndNoOthers()
    {
        await using (var r = await RenderAsync<HelpPage>(app, "/help", @as: Person("Viewer")))
        {
            var html = await r.Html();
            html.Should().Contain("id=\"help-guide-metrics\"").And.Contain("id=\"help-guide-search\"").And.Contain("id=\"help-guide-actions\"");
            html.Should().NotContain("help-guide-triage").And.NotContain("help-guide-coverage").And.NotContain("help-guide-article");
            html.Should().NotContain("running order", "a Viewer's Today guide is described by its own two steps");
        }
        await using (var r = await RenderAsync<HelpPage>(app, "/help", @as: Person("Analyst")))
        {
            var html = await (r.Html());
            html.Should().Contain("id=\"help-guide-triage\"").And.Contain("id=\"help-guide-coverage\"").And.Contain("id=\"help-guide-article\"");
            html.Should().Contain("aria-label=\"Start the guide: The article editor\"").And.NotContain("Start the The").And.NotContain("Start the An");
        }
    }

    [Theory]
    [InlineData("/triage", "Analyst", true)]
    [InlineData("/metrics", "Viewer", true)]
    [InlineData("/actions", "ActionOwner", true)]
    [InlineData("/actions/12", "Viewer", true)]
    [InlineData("/coverage", "Viewer", false)]
    [InlineData("/help", "Analyst", false)]
    public async Task ShowMeAround_IsOnAPageWithAGuide_ForSomeoneWhoCanUseIt(string path, string role, bool shown)
    {
        var who = Person(role);
        await using (var scope = SignedIn(app, who))   // new today: the links show
            await scope.ServiceProvider.GetRequiredService<Tide.Application.Preferences.UserPreferenceService>().SetAsync(
                Tide.Application.Operations.GuideLinkService.Since, scope.ServiceProvider.GetRequiredService<Tide.Application.Org.OrgClock>().Today
                    .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        await using var r = await RenderAsync<GuideLink>(app, path, @as: who);
        var html = await r.Html();
        if (shown) html.Should().Contain("Show me around").And.Contain("id=\"tide-guide-start\"");
        else html.Should().NotContain("Show me around");
    }

    [Fact]
    public async Task ShowMeAround_StartsThePagesGuide_InPlace()
    {
        await using var r = await RenderAsync<OnboardingHost>(app, "/metrics", @as: Person("Viewer"));
        (await Ask(r, "metrics", fromHelp: false)).Should().BeNull("it starts on the page open");
        var html = await r.Html();
        html.Should().Contain("Metric history").And.Contain("Step 1 of 5").And.Contain("End guide").And.Contain("role=\"dialog\"");
        await r.Click(h => h.EndTourAsync());
        (await r.Html()).Should().NotContain("tide-tour");
    }

    [Fact]
    public async Task AGuide_IsNeverStarted_ForSomeoneWhoCantUseItsPage()
    {
        await using var r = await RenderAsync<OnboardingHost>(app, "/triage", @as: Person("Viewer"));
        (await Ask(r, "triage", fromHelp: false)).Should().BeNull();
        (await r.Html()).Should().NotContain("tide-tour");
    }

    private async Task<string?> StartsOn(SignIn who, string key)
    {
        await using var scope = SignedIn(app, who);
        var user = scope.ServiceProvider.GetRequiredService<Tide.Application.Abstractions.ICurrentUser>();
        var guide = Tide.Application.Operations.Guides.Find(key, user.Has)!;
        return await scope.ServiceProvider.GetRequiredService<GuideStarts>().PageAsync(guide);
    }

    [Fact]
    public async Task FromHelp_AGuide_OpensItsPage()
    {
        (await StartsOn(Person("Analyst"), "trends")).Should().Be("trends");
        (await StartsOn(Person("Analyst"), "today")).Should().Be("");
    }

    [Fact]
    public async Task FromHelp_TheEditorsAndAnActionsGuides_OpenARealOne()
    {
        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<Tide.Application.Articles.ArticleService>().CreateAsync("Guide's article", "Vendor", null, Tide.Domain.Enums.TlpLevel.Clear);
        (await StartsOn(Person("Analyst"), "article")).Should().MatchRegex(@"^articles/[0-9a-f-]{36}$");
        (await StartsOn(Person("Viewer"), "action")).Should().MatchRegex(@"^actions/\d+$");
    }
}
