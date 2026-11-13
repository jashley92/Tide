using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Operations;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Search's guide looks something up itself, so its answer, hunt query and export steps have something to point at: a
/// CVE or an indicator on the newest article the person can read, within the team edition's ceiling, read through the
/// services' own checks, never a made-up value. The lookup reads TIDE's own data only: nothing goes out.
/// </summary>
public class SearchGuideTests(RecordingHttpApp app) : IClassFixture<RecordingHttpApp>
{
    private static SignIn Person(string role) => new($"dev:searchguide-{Guid.NewGuid():N}", "Search tester", role);

    // A newest readable article naming `cve`, then a newer one above the team edition's ceiling (RED) naming `red`.
    private async Task Articles(string cve, string red)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seen = await articles.CreateAsync($"Guide lookup {cve}", "Vendor", null, TlpLevel.Green);
        db.ArticleCves.Add(new ArticleCve { ArticleId = seen, CveId = cve });
        await db.SaveChangesAsync();
        await Task.Delay(20);   // created after it
        var hidden = await articles.CreateAsync($"Guide lookup {red}", "Vendor", null, TlpLevel.Red);
        db.ArticleCves.Add(new ArticleCve { ArticleId = hidden, CveId = red });
        await db.SaveChangesAsync();
    }

    private async Task<string?> StartsOn(SignIn who)
    {
        await using var scope = SignedIn(app, who);
        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        return await scope.ServiceProvider.GetRequiredService<GuideStarts>().PageAsync(Guides.Find("search", user.Has)!);
    }

    [Theory]
    [InlineData("Analyst")]
    [InlineData("Viewer")]
    public async Task SearchsGuide_LooksUpWhatThePersonCanSee_NeverAboveTheCeiling(string role)
    {
        var cve = $"CVE-2026-{Random.Shared.Next(10000, 99999)}";
        var red = $"CVE-2027-{Random.Shared.Next(10000, 99999)}";
        await Articles(cve, red);
        var page = await StartsOn(Person(role));
        page.Should().Be("search?q=" + cve, "the newest article within the team edition's ceiling names it");
        page.Should().NotContain(red, "an article above the ceiling is never looked up");
    }

    [Fact]
    public async Task TheGuidesLookup_ShowsTheAnswerAndTheRail_AndNothingGoesOut()
    {
        var cve = $"CVE-2026-{Random.Shared.Next(10000, 99999)}";
        await Articles(cve, $"CVE-2027-{Random.Shared.Next(10000, 99999)}");
        var who = Person("Viewer");
        var page = await StartsOn(who);
        var before = app.Calls;
        await using var r = await RenderAsync<Search>(app, "/" + page, @as: who);
        var html = await r.Html();
        html.Should().Contain("tide-lookup-card", "the answer step has its control");
        html.Should().Contain("lookup-export-h", "and the export step");
        app.Calls.Should().Be(before, "a lookup reads TIDE's own data: no NVD, EPSS, feed or enrichment call");
    }

    [Fact]
    public void SearchsGuide_RingsTheBox_ThenTheAnswerOfItsOwnLookup()
    {
        var steps = Guides.Find("search", _ => true)!.Steps;
        steps[0].Target.Should().Be("#search-q");
        steps[1].Target.Should().Be(".tide-lookup-card h2");
        steps[1].Text.Should().Contain("{lookup}", "it names the value it looked up");
    }
}
