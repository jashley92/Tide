using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Today hearing a colleague: an action they raise for you shows in My actions without a reload. Its own app, so
/// the action it adds doesn't reach other tests' Today.</summary>
public class LiveTodayTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Robin = new("dev:robin", "Robin", "Analyst", "Admin");

    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    [Fact]
    public async Task AColleaguesAction_ShowsInMyActions_AndNamesThem()
    {
        await using var r = await RenderAsync<BriefPage>(app, "/", new() { ["Day"] = $"{Today:yyyy-MM-dd}", ["AsToday"] = true });
        (await r.HtmlOnce("id=\"my-actions-h\"")).Should().NotContain("Sweep for the live awareness test");

        await using (var scope = SignedIn(app, Robin))
        {
            var teams = scope.ServiceProvider.GetRequiredService<TeamService>();
            var team = await teams.AddAsync("Live awareness", null, null);
            await teams.AddMemberAsync(team, SignIn.Analyst.UserId, SignIn.Analyst.Name);
            await scope.ServiceProvider.GetRequiredService<ActionService>().CreateAsync(new ActionForm
            {
                Title = "Sweep for the live awareness test", Kind = ActionKind.IocSweep, TeamId = team,
                AssigneeId = SignIn.Analyst.UserId, AssigneeName = SignIn.Analyst.Name,
            });
        }

        (await r.HtmlOnce("Sweep for the live awareness test")).Should().Contain("Sweep for the live awareness test");
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.Message == "Robin updated an action.");
    }
}
