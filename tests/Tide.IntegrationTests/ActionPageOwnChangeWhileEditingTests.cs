using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// An action's page with Edit open: the person's own step on the same page (Take it, a status move) isn't a colleague's
/// change, so it doesn't leave their open edit unsavable.
/// </summary>
public class ActionPageOwnChangeWhileEditingTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task MovingItsStatusWhileEditing_StillLetsTheEditSave()
    {
        int number;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
            var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0];
            number = await actions.CreateAsync(new ActionForm { Title = "Own change while editing", Kind = ActionKind.Block, TeamId = team.Id });
        }

        await using var r = await RenderAsync<ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number });
        await r.Click(p => { p.StartEdit(); return Task.CompletedTask; });
        var editing = (ActionForm)typeof(ActionPage).GetField("_editing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(r.Component)!;
        editing.Title = "Edited title by me";

        // The person's own move from the page's status badge (Start work does the same), with Edit still open.
        await r.Click(p => p.StatusControl!.Pick(ActionStatus.InProgress));

        var save = typeof(ActionPage).GetMethod("SaveEdit", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await r.Click(p => (Task)save.Invoke(p, null)!);

        await using var check = SignedIn(app, SignIn.Analyst);
        var saved = (await check.ServiceProvider.GetRequiredService<ActionService>().GetAsync(number))!.Action;
        saved.Status.Should().Be(ActionStatus.InProgress, "the move went through");
        saved.Title.Should().Be("Edited title by me", "nobody else changed the action; the person's own status move shouldn't make their edit stale");
    }
}
