using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Preferences;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Saved views on the Actions page: Save view keeps what's shown (never the list's page) under a name, each view is a
/// pill beside the tabs that opens it, the one shown is marked, and its menu renames, moves or deletes it.
/// </summary>
public class ActionsSavedViewTests(TideApp app) : IClassFixture<TideApp>
{
    // Each test signs in as its own person, so what one saves can't reach another (or the rest of the suite's tester).
    private static SignIn Person(string role = "Analyst") => new($"dev:views-{Guid.NewGuid():N}", "Views tester", role);

    private async Task<IReadOnlyList<ActionsSavedView>> Saved(SignIn who)
    {
        await using var scope = SignedIn(app, who);
        return await scope.ServiceProvider.GetRequiredService<SavedViewService>().ListAsync();
    }

    private async Task<ActionsSavedView> Save(SignIn who, string name, string query)
    {
        await using var scope = SignedIn(app, who);
        return await scope.ServiceProvider.GetRequiredService<SavedViewService>().SaveAsync(name, query);
    }

    [Fact]
    public async Task SaveView_KeepsWhatsShown_WithoutThePage_AndMarksIt()
    {
        var who = Person();
        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list&priority=p1,p2&page=2", @as: who))
        {
            (await r.Html()).Should().Contain("Save view");
            await r.Press(p => p.StartSave());
            (await r.HtmlOnce("Save this view")).Should().Contain("saved-view-name");
            await r.Click(async p =>
            {
                p.NewName = "  Urgent  ";
                (await p.SaveName()).Should().BeTrue(p.NameError);
            });
            r.Component.Saved.Should().ContainSingle().Which.Name.Should().Be("Urgent", "the name is trimmed");
            r.Component.ActiveSaved!.Name.Should().Be("Urgent", "what's shown is the view just saved");
        }
        (await Saved(who)).Should().ContainSingle().Which.Query.Should().Be("view=open&layout=list&priority=p1,p2", "the list's page isn't part of a view");

        // Its pill opens it, and is marked while it's what's shown.
        await using var again = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list&priority=p1,p2", @as: who);
        WebUtility.HtmlDecode(await again.Html()).Should().Contain("href=\"actions?view=open&layout=list&priority=p1,p2\" aria-current=\"page\">Urgent</a>");
    }

    [Fact]
    public async Task ASavedView_ShowsBesideTheTabs_MarkedOnlyWhenItsFiltersAreShown()
    {
        var who = Person();
        await Save(who, "Blocked list", "view=open&layout=list&status=blocked");
        await Save(who, "Mine by kind", "view=mine&layout=list&group=kind");

        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list&status=blocked", @as: who))
        {
            var html = WebUtility.HtmlDecode(await r.Html());
            html.Should().Contain("aria-label=\"Saved views\"").And.Contain("aria-current=\"page\">Blocked list</a>")
                .And.Contain(">Mine by kind</a>").And.NotContain("aria-current=\"page\">Mine by kind</a>");
            html.Should().Contain("Blocked list: rename, move or delete", "each view's menu is named for it");
        }

        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list&status=blocked&priority=p1", @as: who))
            r.Component.ActiveSaved.Should().BeNull("another filter on top is no longer the saved view");

        await using (var other = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list&status=blocked", @as: Person()))
            WebUtility.HtmlDecode(await other.Html()).Should().NotContain("Blocked list", "someone else's views aren't shown");
    }

    [Fact]
    public async Task TheMenu_RenamesMovesAndDeletesAView()
    {
        var who = Person();
        var first = await Save(who, "First", "view=open&layout=list");
        var second = await Save(who, "Second", "view=mine&layout=list");

        await using var r = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list", @as: who);
        await r.Press(p => p.StartRename(first));
        WebUtility.HtmlDecode(await r.HtmlOnce("Only the name changes")).Should().Contain("Rename \"First\"");
        await r.Click(async p =>
        {
            p.NewName = "Second";
            (await p.SaveName()).Should().BeFalse("Second is taken");
            p.NameError.Should().Contain("already have a view named");
            p.NewName = "All open";
            (await p.SaveName()).Should().BeTrue(p.NameError);
        });

        await r.Click(p => p.MoveSaved(second, -1));
        r.Component.Saved.Select(v => v.Name).Should().Equal("Second", "All open");

        await r.Press(p => p.DeleteSaved(second));
        (await r.HtmlOnce("Delete view")).Should().Contain("Only your shortcut goes");
        await ConfirmDelete(r);
        (await r.Until(p => p.Saved.Count == 1)).Should().BeTrue(string.Join("; ", r.Scope.ServiceProvider.GetRequiredService<Tide.Web.Services.ToastService>().Toasts.Select(t => t.Message)));
        (await Saved(who)).Should().ContainSingle().Which.Name.Should().Be("All open");
    }

    // The confirm dialog's Delete view, pressed as a click on it would. Straight on the renderer's thread: the delete
    // carries on in the flow that asked (Press), which a second Press would sign out.
    private static Task ConfirmDelete(Rendered<ActionsBoard> r) => r.Renderer.Dispatcher.InvokeAsync(() =>
    {
        var confirm = (Tide.Web.Components.Shared.ConfirmDialog)typeof(ActionsBoard)
            .GetField("_confirm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(r.Component)!;
        return confirm.Confirm();
    });

    [Fact]
    public async Task AtTwelveViews_SaveViewIsHeldBack()
    {
        var who = Person();
        for (var i = 1; i <= SavedViewService.MaxViews; i++) await Save(who, $"View {i}", $"view=open&parent={i}");
        await using var r = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list", @as: who);
        WebUtility.HtmlDecode(await r.Html()).Should().Contain("You can keep up to 12 views");
        r.Component.Saved.Should().HaveCount(SavedViewService.MaxViews);
    }
}
