using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Bug round 1, actions: the board says how long ago a card was opened in the organization's days.</summary>
public class ActionsRound1RenderTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TheBoard_SaysACardOpenedLastEvening_WasOpenedYesterday_NotToday()
    {
        var clock = app.Services.GetRequiredService<OrgClock>();
        var today = clock.Today;
        // 9 pm yesterday, the organization's time: already today in UTC west of Greenwich.
        var lastEvening = clock.StartOfDay(today).AddHours(-3);
        var card = new ActionRow(Guid.NewGuid(), 9001, "Sweep for the loader", ActionKind.IocSweep, Priority.P3, Guid.NewGuid(), "Cyber Defense",
            null, null, null, ActionStatus.Open, ActionOutcome.None, null, null, null, null, 0, lastEvening);

        await using var r = await RenderAsync<ActionKanban>(app, "/actions?layout=board", new()
        {
            ["Board"] = new ActionBoard([card], false, new HashSet<Guid>()), ["Today"] = today,
        });

        (await r.Html()).Should().Contain("Opened 1d ago").And.NotContain("Opened today");
    }
}
