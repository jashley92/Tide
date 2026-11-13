using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Operations;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Layout;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// A guide only points: walked from its first step to Done, or left with Escape part way, on the page it's about, it
/// writes nothing (no row added, changed or audited, no preference, the article's edit version as it was) and nothing
/// goes out of the app.
/// </summary>
public class GuideWalkTests(RecordingHttpApp app) : IClassFixture<RecordingHttpApp>
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
            await db.UserPreferences.CountAsync(), await db.Briefs.CountAsync(), await db.FeedItems.CountAsync(),
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
        var said = Regex.Match(await r.Html(), @"Step 1 of (\d+)");
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

    [Theory]
    [InlineData("today", "Analyst")]
    [InlineData("today", "Viewer")]
    [InlineData("triage", "Analyst")]
    [InlineData("article", "Analyst")]
    [InlineData("actions", "Analyst")]
    [InlineData("actions", "Viewer")]
    [InlineData("action", "Analyst")]
    [InlineData("action", "Viewer")]
    [InlineData("search", "Viewer")]
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
            await host.Click(h => h.NextStepAsync());
        }

        walked.Should().Be(count, "every step is shown, Next to Done");
        (await Written()).Should().Be(before, $"the {key} guide only points");
        app.Calls.Should().Be(calls, "nothing goes out of the app");
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
}
