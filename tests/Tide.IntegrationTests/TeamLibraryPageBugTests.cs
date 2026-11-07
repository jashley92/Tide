using System.Net;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Bugs found in the team libraries' pages (EP-89): an open edit whose card leaves the page, and moving while editing.</summary>
public class TeamLibraryPageBugTests(TideApp app) : IClassFixture<TideApp>
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly SignIn Robin = new("dev:robin-r1", "Robin", "Analyst");

    private static Task Set<T>(Rendered<T> r, string field, object? value, string? then = null) where T : Microsoft.AspNetCore.Components.IComponent => r.Click(c =>
    {
        typeof(T).GetField(field, Private)!.SetValue(c, value);
        if (then is not null) typeof(T).GetMethod(then, Private)!.Invoke(c, null);
        typeof(Microsoft.AspNetCore.Components.ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(c, null);
        return Task.CompletedTask;
    });

    // An edit open on a playbook a colleague then retired (so it left the list, Show retired being off) lost its form:
    // the page still held the edit, so New playbook and every Edit stayed hidden or disabled, with nothing on screen to
    // save or cancel. The form now stays, above the list, until it's saved or cancelled.
    [Fact]
    public async Task AnEditOpenOnAPlaybookThatLeavesTheList_KeepsItsForm()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        Guid id;
        await using (var scope = SignedIn(app, Robin))
            id = await scope.ServiceProvider.GetRequiredService<PlaybookService>().SaveAsync(new PlaybookForm { Name = "Leaving " + tag, Steps = ["Pull the indicators"] });

        await using var r = await RenderAsync<PlaybooksPage>(app, "/playbooks", @as: Robin);
        await Set(r, "_words", tag, "Refilter");
        var p = r.Component.Rows!.Single(x => x.Playbook.Id == id).Playbook;
        await r.Click(c => { c.Edit(p); return Task.CompletedTask; });
        r.Component.Form!.Name = "Leaving " + tag + " (renamed)";

        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<PlaybookService>().SetRetiredAsync(id, true);
        await r.Click(c => c.OnChangedElsewhere([]));

        r.Component.Rows.Should().NotContain(x => x.Playbook.Id == id, "retired ones are hidden");
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("id=\"pb-f-name\"", "the edit's form is still there to save or cancel")
            .And.Contain("value=\"Leaving " + tag + " (renamed)\"", "with what was typed");
    }

    // Moving a playbook swaps it with its neighbour, and a move changes both (each one's version moves). With an edit open
    // on the neighbour, Move was still offered, so the analyst's own click made their own edit "changed by someone else"
    // and its save was refused. Moving waits, as Edit does, until the form is saved or cancelled.
    [Fact]
    public async Task WhileAPlaybookIsBeingEdited_MovingWaits_SoItsSaveIsntRefusedAsSomeoneElses()
    {
        await using var r = await RenderAsync<PlaybooksPage>(app, "/playbooks", @as: Robin);
        var (first, second) = (r.Component.OnPage[0].Playbook, r.Component.OnPage[1].Playbook);
        await r.Click(c => { c.Edit(second); return Task.CompletedTask; });

        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().MatchRegex($"aria-label=\"Move {System.Text.RegularExpressions.Regex.Escape(first.Name)} down\" disabled", "a move would change the one being edited");
        await r.Click(c => c.Move(first, 1));
        r.Component.OnPage[1].Playbook.Id.Should().Be(second.Id, "nothing moved");

        r.Component.Form!.Description = "Kept by the edit " + Guid.NewGuid().ToString("N")[..6];
        await r.Click(c => c.Save());
        r.Component.Form.Should().BeNull("the edit saved");
    }
}
