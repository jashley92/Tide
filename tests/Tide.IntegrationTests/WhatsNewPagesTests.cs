using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Tide.Application.Preferences;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The "What's new" pop-up and Help: shown once per person, with their role groups' highlights, never in present mode
/// or to a Visitor; and Help lists every release's notes.
/// </summary>
public class WhatsNewPagesTests(TideApp app) : IClassFixture<TideApp>
{
    // Each test signs in as its own people, so what one has seen can't reach another.
    private static SignIn Person(params string[] roles) => new($"dev:whatsnew-{Guid.NewGuid():N}", "New tester", roles);

    private async Task<string> Seen(SignIn who)
    {
        await using var scope = SignedIn(app, who);
        return await scope.ServiceProvider.GetRequiredService<UserPreferenceService>().GetAsync(UserPreferences.WhatsNewSeen);
    }

    [Fact]
    public async Task AnAnalyst_SeesTheReleasesHighlightsOnce_ForAnalystsAndPresenters_WithAllChangesInHelp()
    {
        var who = Person("Analyst");
        (await Seen(who)).Should().BeEmpty();
        await using (var r = await RenderAsync<WhatsNewDialog>(app, "/", @as: who))
        {
            (await r.Html()).Should().NotContain("tide-whatsnew", "nothing is drawn before it's checked");
            await r.Click(c => c.CheckAsync());
            var html = await r.Html();
            html.Should().Contain("What&#x27;s new in TIDE 1.13.0").And.Contain("role=\"dialog\"").And.Contain("aria-modal=\"true\"");
            html.Should().Contain("For analysts").And.Contain("For presenters").And.NotContain("For administrators")
                .And.NotContain("For viewers and action owners");
            html.Should().Contain("href=\"help?release=1.13.0#v1-13-0\"").And.Contain("See all changes");
            r.Component.IsOpen.Should().BeTrue();
            (await Seen(who)).Should().BeEmpty("it's marked seen as it's closed, so a reload while it's up shows it again");
            await r.Click(c => c.CloseAsync());
            (await r.Html()).Should().NotContain("tide-whatsnew");
        }
        (await Seen(who)).Should().Be("1.13.0", "it's marked seen as it closes");

        await using var again = await RenderAsync<WhatsNewDialog>(app, "/triage", @as: who);
        await again.Click(c => c.CheckAsync());
        again.Component.IsOpen.Should().BeFalse("it's shown once per person");
    }

    [Fact]
    public async Task AnAdministrator_AlsoReadsTheAdministratorsHighlights()
    {
        await using var r = await RenderAsync<WhatsNewDialog>(app, "/", @as: Person("Admin"));
        await r.Click(c => c.CheckAsync());
        (await r.Html()).Should().Contain("For analysts").And.Contain("For presenters").And.Contain("For administrators");
    }

    [Fact]
    public async Task AViewer_ReadsTheViewersHighlightsOnly()
    {
        var who = Person("Viewer");
        await using var r = await RenderAsync<WhatsNewDialog>(app, "/actions", @as: who);
        await r.Click(c => c.CheckAsync());
        var html = await r.Html();
        html.Should().Contain("For viewers and action owners").And.NotContain("For analysts").And.NotContain("For presenters");
    }

    [Fact]
    public async Task AVisitor_OrAReader_IsNeverShownIt()
    {
        foreach (var role in new[] { "Visitor", "Reader" })
        {
            await using var r = await RenderAsync<WhatsNewDialog>(app, "/published", @as: Person(role));
            await r.Click(c => c.CheckAsync());
            r.Component.IsOpen.Should().BeFalse($"a {role} reads no highlights");
        }
    }

    [Theory]
    [InlineData("/present/2026-11-12")]
    [InlineData("/present/2026-11-12?as=Jordan")]
    public async Task PresentMode_NeverShowsIt_AsPresenterOrFollower(string path)
    {
        foreach (var roles in new[] { new[] { "Analyst" }, ["Viewer"] })
        {
            var who = Person(roles);
            await using var r = await RenderAsync<WhatsNewDialog>(app, path, @as: who);
            await r.Click(c => c.CheckAsync());
            r.Component.IsOpen.Should().BeFalse("the room shares present mode's screen");
            (await Seen(who)).Should().BeEmpty("it's still due once they leave the meeting");
        }
    }

    [Fact]
    public void OnlyTheMainLayoutDrawsIt_NotPresentModes()
    {
        var layouts = Path.Combine(EditorRailLayoutTests.WebRoot(), "Components", "Layout");
        File.ReadAllText(Path.Combine(layouts, "MainLayout.razor")).Should().Contain("<OnboardingHost />");
        File.ReadAllText(Path.Combine(layouts, "BlankLayout.razor")).Should().NotContain("WhatsNew").And.NotContain("Onboarding");
    }

    [Theory]
    [InlineData("present", false)]
    [InlineData("present/2026-11-12", false)]
    [InlineData("Present/2026-11-12?x=1", false)]
    [InlineData("", true)]
    [InlineData("presentations", true)]
    [InlineData("triage?tab=new", true)]
    public void PresentModesAddresses_AreNeverOffered(string path, bool allowed) =>
        OnboardingGate.AllowedAt(path).Should().Be(allowed);

    private sealed class Env(string name) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tide.Web";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static OnboardingGate Gate(string environment, params (string Key, string Value)[] settings) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))).Build(), new Env(environment));

    private static ClaimsPrincipal Someone(bool askedOnTheDemo = false) => new(new ClaimsIdentity(
        askedOnTheDemo ? [new Claim(OnboardingGate.DemoClaim, "on")] : Array.Empty<Claim>(), "Test"));

    [Fact]
    public void ItsOfferedUnasked_ExceptOnTheScreenshotDemo_OrWhereTurnedOff()
    {
        Gate("Production").Automatic(Someone()).Should().BeTrue();
        Gate("Development").Automatic(Someone()).Should().BeTrue();
        Gate("Development", ("Demo:Seed", "true")).Automatic(Someone()).Should().BeFalse("the screenshot tools' pages mustn't open under it");
        Gate("Development", ("Demo:Seed", "true")).Automatic(Someone(askedOnTheDemo: true)).Should().BeTrue("?onboarding=on asks for it");
        Gate("Production", ("Onboarding:Enabled", "false")).Automatic(Someone(askedOnTheDemo: true)).Should().BeFalse(
            "the dev sign-in's claim means nothing outside Development");
        app.Services.GetRequiredService<OnboardingGate>().Automatic(Someone()).Should().BeFalse("never in tests unless asked");
    }

    [Fact]
    public async Task Help_ShowsEachReaderTheirHighlights_AndOnlyAnAdministratorTheFullOperatorNotes()
    {
        await using (var r = await RenderAsync<HelpPage>(app, "/help?release=1.13.0", @as: Person("Viewer")))
        {
            var html = await r.Html();
            html.Should().Contain("id=\"v1-13-0\"").And.Contain("For viewers and action owners").And.NotContain("For analysts");
            html.Should().NotContain("All changes in", "the full notes are the operators': upgrade steps, migrations, settings");
            html.Should().NotContain("Upgrade-Tide.ps1").And.NotContain("id=\"v1-0-0\"", "a release with nothing for them isn't listed");
        }
        await using (var analyst = await RenderAsync<HelpPage>(app, "/help", @as: Person("Analyst")))
            (await analyst.Html()).Should().Contain("For analysts").And.NotContain("All changes in").And.NotContain("For administrators");

        await using var admin = await RenderAsync<HelpPage>(app, "/help?release=1.13.0", @as: Person("Admin"));
        var page = await admin.Html();
        page.Should().Contain("For administrators").And.Contain("id=\"v1-0-0\"", "every release's notes are listed for an administrator");
        page.Should().Contain("<details class=\"tide-help-notes\" open").And.Contain("All changes in 1.13.0").And.Contain("Upgrade-Tide.ps1");
    }
}
