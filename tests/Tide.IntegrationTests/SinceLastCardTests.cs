using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.History;
using Tide.Application.Preferences;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Today;

namespace Tide.IntegrationTests;

/// <summary>The "Since you last looked" card as Today draws it: a heading that folds it, its groups as lists of links, and
/// Mark all seen, for someone who was away; on Today only, not on another day's brief.</summary>
public class SinceLastCardTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly ComponentHarness.SignIn Away = new("dev:since-away", "Away Analyst", "Analyst");
    private static readonly ComponentHarness.SignIn Colleague = new("dev:since-colleague", "Since Colleague", "Analyst", "Admin");

    [Fact]
    public async Task SomeoneWhoWasAway_SeesWhatChanged_AsHeadedLists_WithMarkAllSeen()
    {
        Guid team;
        await using (var scope = ComponentHarness.SignedIn(app, Colleague))
        {
            var teams = scope.ServiceProvider.GetRequiredService<TeamService>();
            team = await teams.AddAsync("Since Team", null, null);
            await teams.AddMemberAsync(team, Away.UserId, Away.Name);
        }
        await using (var scope = ComponentHarness.SignedIn(app, Away))
            await scope.ServiceProvider.GetRequiredService<UserPreferenceService>()
                .SetAsync(UserPreferences.SeenToday, SeenMark.Format(DateTimeOffset.UtcNow.AddHours(-3)));
        int number;
        await using (var scope = ComponentHarness.SignedIn(app, Colleague))
            number = await scope.ServiceProvider.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = "Since: sweep the logs", TeamId = team });

        await using var r = await ComponentHarness.RenderAsync<SinceLastCard>(app, "/", null, Away);
        var html = await r.Html();

        html.Should().Contain("<section class=\"tide-card\" aria-labelledby=\"since-h\">")
            .And.Contain("aria-expanded=\"true\" aria-controls=\"since-body\"")
            .And.Contain("<h3 class=\"small fw-semibold mt-2 mb-1\" id=\"since-ForYou\">For you</h3>")
            .And.Contain("aria-labelledby=\"since-ForYou\"")
            .And.Contain($"<a href=\"actions/{number}\">A-{number} Since: sweep the logs</a>")
            .And.Contain("Raised by a colleague", "the harness signs in without the sign-in mirror, which names people")
            .And.Contain(">Mark all seen</button>");
    }

    [Fact]
    public async Task AnotherDaysBrief_DoesntDrawIt()
    {
        var day = new DateOnly(2026, 1, 6);
        await using var r = await ComponentHarness.RenderAsync<BriefPage>(app, $"/briefs/{day:yyyy-MM-dd}",
            new() { ["Day"] = $"{day:yyyy-MM-dd}", ["AsToday"] = false }, ComponentHarness.SignIn.Analyst);
        (await r.Html()).Should().NotContain("since-h");
    }
}
