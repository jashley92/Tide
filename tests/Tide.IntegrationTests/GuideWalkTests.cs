using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Operations;
using Tide.Application.Security;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// A guide only points: walked from its first step to Done, or left with Escape part way, on the page it's about, it
/// writes nothing (no row added, changed or audited, no preference, the article's edit version as it was) and nothing
/// goes out of the app.
/// </summary>
public partial class GuideWalkTests(RecordingHttpApp app) : IClassFixture<RecordingHttpApp>
{
    private static SignIn Person(string role) => new($"dev:guidewalk-{Guid.NewGuid():N}", "Walk tester", role);

    // Everything a write would change: each table a page here writes to, the audit log every write adds to, preferences,
    // and each article's and action's edit version.
    private async Task<string> Written()
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var counts = new[]
        {
            await db.Articles.CountAsync(), await db.Actions.CountAsync(), await db.AuditLog.CountAsync(),
            // Bar the one a guide keeps for itself: that it has run, so its page's Show me around link goes (GuideLinkService).
            await db.UserPreferences.CountAsync(p => !p.Name.StartsWith("guide.")), await db.Briefs.CountAsync(), await db.FeedItems.CountAsync(),
            await db.FeedItemTexts.CountAsync(), await db.ArticleIndicators.CountAsync(), await db.ArticleTags.CountAsync(),
        };
        var versions = await db.Articles.AsNoTracking().OrderBy(a => a.Id).Select(a => a.EditVersion).ToListAsync();
        var statuses = await db.Actions.AsNoTracking().OrderBy(a => a.Id).Select(a => a.Status).ToListAsync();
        return string.Join(',', counts) + "|" + string.Join(',', versions) + "|" + string.Join(',', statuses);
    }

    // Starts the guide `key` from the page's Show me around, as the layout's host does on the page open.
    private static async Task<int> Start(Rendered<OnboardingHost> r, string key)
    {
        await r.Click(_ =>
        {
            r.Scope.ServiceProvider.GetRequiredService<TourRequests>().AskGuide(key, false);
            return Task.CompletedTask;
        });
        (await r.Until(h => h.Step is not null, seconds: 5)).Should().BeTrue(key);
        // How many steps it says it has: "Step 1 of N".
        var said = StepOneOf().Match(await r.Html());
        said.Success.Should().BeTrue(key);
        return int.Parse(said.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private async Task<string> PageOf(string key, SignIn who)
    {
        await using var scope = SignedIn(app, who);
        var guide = Guides.Find(key, scope.ServiceProvider.GetRequiredService<ICurrentUser>().Has)!;
        return (await scope.ServiceProvider.GetRequiredService<GuideStarts>().PageAsync(guide))!;
    }

    // An open action, for an action's page to show.
    private async Task AnAction()
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var team = await scope.ServiceProvider.GetRequiredService<TeamService>().AddAsync("Walk team " + Guid.NewGuid().ToString("N")[..6], null, null);
        await scope.ServiceProvider.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = "Walk sweep", TeamId = team });
    }

    // Every guide on a page, and Following the brief, as an analyst and as a Viewer (An analyst's day and Learn to
    // present have walks of their own).
    public static TheoryData<string, string> EveryGuide()
    {
        var data = new TheoryData<string, string>();
        foreach (var role in new[] { AppRole.Analyst, AppRole.Viewer })
            foreach (var g in Guides.For(RoleDefinitions.PermissionsFor([role]).Contains).Where(g => !g.Rehearsal && g.Key != Guides.Day))
                data.Add(g.Key, role.ToString());
        data.Add(Guides.Following, nameof(AppRole.ActionOwner));
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryGuide))]
    public async Task WalkingAGuide_ToTheEnd_WritesNothing_AndNothingGoesOut(string key, string role)
    {
        await TwoArticleBrief(app, 0, $"Walk {key}");
        await AnAction();
        var who = Person(role);
        var page = "/" + await PageOf(key, who);
        await using var host = await RenderAsync<OnboardingHost>(app, page, @as: who);
        var (before, calls) = (await Written(), app.Calls);

        var count = await Start(host, key);
        var walked = 0;
        while (host.Component.Step is not null)
        {
            walked++;
            // A guide across pages opens each step's page, which a page drawn outside a circuit can't: it says where it went.
            try { await host.Click(h => h.NextStepAsync()); } catch (NavigationException) { }
        }

        walked.Should().Be(count, "every step is shown, Next to Done");
        (await Written()).Should().Be(before, $"the {key} guide only points");
        app.Calls.Should().Be(calls, "nothing goes out of the app");
    }

    // Next until the step is `stop`'s (or Done), each page it opens saying where it went; how many steps were shown.
    private static async Task<int> Walk(Rendered<OnboardingHost> host, Func<TourStep, bool>? stop = null)
    {
        var walked = 0;
        while (host.Component.Step is { } step)
        {
            walked++;
            if (stop?.Invoke(step) == true) return walked;
            try { await host.Click(h => h.NextStepAsync()); } catch (NavigationException) { }
        }
        return walked;
    }

    [Fact]
    public async Task AnAnalystsDay_WalkedWholeAcrossItsPagesAndBackFromPresenting_WritesNothing_AndNothingGoesOut()
    {
        await TwoArticleBrief(app, 0, "Walk the day");
        await AnAction();
        var who = Person("Analyst");
        var (before, calls) = (await Written(), app.Calls);
        int count, upToPresenting;
        await using (var host = await RenderAsync<OnboardingHost>(app, "/", @as: who))
        {
            count = await Start(host, Guides.Day);
            upToPresenting = await Walk(host, s => s.Section == Guides.Presenting);
            // Next from Presenting opens present mode in rehearsal, marked as the day's.
            var went = "";
            try { await host.Click(h => h.NextStepAsync()); } catch (NavigationException e) { went = e.Location; }
            went.Should().Contain("rehearse=1").And.Contain(Present.DayGuide);
            host.Component.Step.Should().BeNull("the day goes on in present mode");
        }
        // Present mode's Done comes back to Today, where the day goes on at Send.
        await using (var back = await RenderAsync<OnboardingHost>(app, "/" + Present.BackToTheDayFrom(live: false), @as: who))
        {
            // As its first draw in a browser does; it puts the address back to Today's own, which a page drawn outside a
            // circuit can't.
            try { await back.Click(h => h.ResumeTheDayAsync()); } catch (NavigationException) { }
            (await back.Until(h => h.Step is not null, seconds: 5)).Should().BeTrue();
            back.Component.Step!.Key.Should().Be("send");
            (upToPresenting + await Walk(back)).Should().Be(count, "every step, either side of presenting");
        }
        (await Written()).Should().Be(before, "the day's guide only points, its pages' guides inline included");
        app.Calls.Should().Be(calls, "nothing goes out of the app");
    }

    [Fact]
    public async Task AnAnalystsDay_CountsInItsPart_WithThePartsOnTheirOwnLine_BeforeTheTitle()
    {
        await using var host = await RenderAsync<OnboardingHost>(app, "/", @as: Person("Analyst"));
        await Start(host, Guides.Day);
        var html = await host.Html();
        html.Should().Contain("Today · 1 of 2", "the header counts within the part");
        var parts = html.IndexOf("class=\"tide-tour-parts\"", StringComparison.Ordinal);
        parts.Should().BeGreaterThan(html.IndexOf("class=\"tide-tour-count\"", StringComparison.Ordinal), "after the count")
            .And.BeLessThan(html.IndexOf("class=\"tide-tour-title\"", StringComparison.Ordinal), "and before the title, in the header");
        html.Should().Contain("<strong aria-current=\"step\">Today</strong>").And.Contain("Back to Today", "a part come back to says so");
    }

    [Fact]
    public async Task AnAnalystsDay_SkipsADetailedSection_ToTheStepAfterIt()
    {
        var who = Person("Analyst");
        await using var host = await RenderAsync<OnboardingHost>(app, "/", @as: who);
        await Start(host, Guides.Day);
        await Walk(host, s => s.Section == "triage");
        try { await host.Click(h => h.SkipAsync()); } catch (NavigationException) { }
        host.Component.Step!.Section.Should().Be("article", "Skip the details on Triage's first step goes on to the editor's");
    }

    [Fact]
    public async Task LeavingTheEditorsGuide_PartWay_LeavesTheArticleAsItWas()
    {
        var who = Person("Analyst");
        var page = "/" + await PageOf("article", who);
        await using var host = await RenderAsync<OnboardingHost>(app, page, @as: who);
        var before = await Written();
        await Start(host, "article");
        for (var i = 0; i < 4; i++) await host.Click(h => h.NextStepAsync());
        await host.Click(h => h.EndTourAsync());   // Escape
        host.Component.Step.Should().BeNull();
        (await Written()).Should().Be(before);
    }

    [Fact]
    public async Task LearnToPresent_WalkedToTheEnd_WritesNothing_AndNothingGoesOut()
    {
        var day = await TwoArticleBrief(app, 0, "Walk present");
        await using var r = await Rehearse(app, day, Person("Analyst"));
        var (before, calls) = (await Written(), app.Calls);
        var html = await r.Html();
        html.Should().Contain("Step 1 of ");
        for (var i = 0; i < 20 && (await r.Html()).Contains("tide-tour", StringComparison.Ordinal); i++)
            await Do(r, p => p.GuideNext());
        (await r.Html()).Should().NotContain("tide-tour", "Done ends it");
        (await Written()).Should().Be(before, "a rehearsal's guide only points");
        app.Calls.Should().Be(calls);
    }

    [GeneratedRegex(@"[Ss]tep 1 of (\d+)")]
    private static partial Regex StepOneOf();
}
