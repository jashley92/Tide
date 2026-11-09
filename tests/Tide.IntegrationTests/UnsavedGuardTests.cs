using System.Net;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Pages.Admin;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Something typed and not saved isn't lost to a stray link: UnsavedGuard asks (Save and leave when there's one save,
/// Discard and leave, Stay), several guards on a page ask one at a time, and the pages that hold a draft say when they
/// do. The "won't be emailed" note's fixes open in a new tab, so a dialog it sits in keeps what was typed; and toasts
/// hold while they're being read, with the newest Undo on a key.
/// </summary>
public class UnsavedGuardTests(TideApp app) : IClassFixture<TideApp>
{
    private static Dictionary<string, object?> Guard(bool dirty, Func<Task<bool>>? save = null, Action? discard = null) => new()
    {
        ["Dirty"] = dirty, ["Message"] = "Your note isn't saved.", ["Save"] = save,
        ["Discard"] = EventCallback.Factory.Create(new object(), discard ?? (() => { })),
    };

    // Starts leaving, and waits for the question.
    private static async Task<(Task<bool> Leaving, string Html)> Leaving(Rendered<UnsavedGuard> r)
    {
        Task<bool>? leaving = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { leaving = r.Component.MayLeave(); });
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("Leave with unsaved changes?"));
        leaving!.IsCompleted.Should().BeFalse("the move waits for an answer");
        return (leaving, html);
    }

    [Fact]
    public async Task NothingUnsaved_GoesWithoutAsking()
    {
        await using var r = await RenderAsync<UnsavedGuard>(app, "/", Guard(dirty: false));
        (await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.MayLeave())).Should().BeTrue();
        (await r.Html()).Should().NotContain("Leave with unsaved changes?");
    }

    [Fact]
    public async Task WithoutASave_OnlyDiscardOrStay_AndDiscardDropsIt()
    {
        var discarded = 0;
        await using var r = await RenderAsync<UnsavedGuard>(app, "/", Guard(dirty: true, discard: () => discarded++));
        var (leaving, html) = await Leaving(r);
        html.Should().Contain("Your note isn't saved.").And.Contain(">Stay</button>").And.Contain(">Discard and leave</button>")
            .And.NotContain("Save and leave", "there's no one save to make");

        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Leave.Close(LeaveDialog.Choice.Discard));
        (await leaving).Should().BeTrue();
        discarded.Should().Be(1);
    }

    [Fact]
    public async Task SaveAndLeave_GoesOnlyIfTheSaveWorks_AndStayStays()
    {
        var works = false;
        await using var r = await RenderAsync<UnsavedGuard>(app, "/", Guard(dirty: true, save: () => Task.FromResult(works)));
        var (refused, html) = await Leaving(r);
        html.Should().Contain(">Save and leave</button>");
        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Leave.Close(LeaveDialog.Choice.Save));
        (await refused).Should().BeFalse("a refused save stays to be sorted out");

        var (stayed, _) = await Leaving(r);
        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Leave.Stay());
        (await stayed).Should().BeFalse();

        works = true;
        var (saved, _) = await Leaving(r);
        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Leave.Close(LeaveDialog.Choice.Save));
        (await saved).Should().BeTrue();
    }

    [Fact]
    public async Task TwoGuardsAskingAboutOneNavigation_AskOneAtATime_AndStayForOneIsStayForBoth()
    {
        await using var r = await RenderAsync<UnsavedGuard>(app, "/", Guard(dirty: true));
        var ctx = new LocationChangingContext { TargetLocation = "http://localhost/elsewhere" };
        Task? first = null, second = null;
        await r.Renderer.Dispatcher.InvokeAsync(() =>
        {
            first = r.Component.ConfirmLeave(ctx);
            second = r.Component.ConfirmLeave(ctx);   // another guard on the page, handed the same navigation
        });
        await r.HtmlOnce("Leave with unsaved changes?");
        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Leave.Stay());
        await first!;
        // The second didn't open a question of its own to wait on: it heard the first's Stay.
        await second!.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task EmailTemplates_TypedWording_Copies_OrTheArchive_AreUnsaved()
    {
        await using var r = await RenderAsync<EmailTemplatesAdmin>(app, "/admin/email-templates");
        await r.HtmlOnce("Preview of the email");
        Task<bool> Unsaved() => r.Renderer.Dispatcher.InvokeAsync(() =>
        {
            var unsaved = false;
            r.Pressed(p => { unsaved = p.AnyUnsaved; return Task.CompletedTask; });
            return unsaved;
        });
        void Set(string field, string? value) => typeof(EmailTemplatesAdmin).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(r.Component, value);

        (await Unsaved()).Should().BeFalse("nothing typed yet");
        await r.Renderer.Dispatcher.InvokeAsync(() => Set("_subject", "A subject of my own"));
        (await Unsaved()).Should().BeTrue("the template's wording is changed");
        await r.Renderer.Dispatcher.InvokeAsync(() => Set("_subject", null));
        await r.Click(p => { typeof(EmailTemplatesAdmin).GetMethod("Revert", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(p, null); return Task.CompletedTask; });
        (await Unsaved()).Should().BeFalse("reverted");
        await r.Renderer.Dispatcher.InvokeAsync(() => Set("_bcc", "soc-lead@example.org"));
        (await Unsaved()).Should().BeTrue("a copy typed and not saved");
        await r.Renderer.Dispatcher.InvokeAsync(() => Set("_bcc", null));
        await r.Renderer.Dispatcher.InvokeAsync(() => Set("_archive", "archive@example.org"));
        (await Unsaved()).Should().BeTrue("the archive address typed and not saved");
    }

    [Fact]
    public async Task TheActionPage_ACommentTypedOrAnEditNotSaved_IsUnsaved()
    {
        int number;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var team = await scope.ServiceProvider.GetRequiredService<TeamService>().AddAsync("Guard test team " + Guid.NewGuid().ToString("N")[..6], null, null);
            number = await scope.ServiceProvider.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = "Guard the comment", TeamId = team });
        }
        await using var page = await RenderAsync<ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number });
        await page.HtmlOnce("Add a comment");
        page.Component.CommentUnsaved.Should().BeFalse();
        await page.Click(p => { p.CommentDraft = "Half a thought"; return Task.CompletedTask; });
        page.Component.CommentUnsaved.Should().BeTrue("a comment typed and not posted");
        await page.Click(p => { p.CommentDraft = "  "; return Task.CompletedTask; });
        page.Component.CommentUnsaved.Should().BeFalse("only blanks: nothing to lose");
    }

    [Fact]
    public async Task NotEmailedNote_ItsFixesOpenInANewTab_AndCompactSpansTheRow()
    {
        Guid quiet;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            quiet = await scope.ServiceProvider.GetRequiredService<TeamService>().AddExternalAsync(new ExternalTeamForm { Name = "Guard quiet provider", RemindersOn = false });
        Tide.Domain.Entities.Team team;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync()).Single(t => t.Id == quiet);

        await using (var note = await RenderAsync<NotEmailedNote>(app, "/", new() { ["Team"] = team }))
        {
            var html = await note.Html();
            html.Should().Contain("href=\"admin/teams#rem-title\" target=\"_blank\" rel=\"noopener noreferrer\"")
                .And.Contain("<span class=\"visually-hidden\"> (opens in a new tab)</span>").And.Contain("tide-missing small mt-2");
        }
        await using (var compact = await RenderAsync<NotEmailedNote>(app, "/", new() { ["Team"] = team, ["Class"] = "tide-missing-compact" }))
            (await compact.Html()).Should().Contain("tide-missing small tide-missing-compact").And.NotContain(" mt-2");
    }

    [Fact]
    public async Task Toasts_HoldWhileThePointerOrFocusIsOnThem_AndTheNewestUndoIsAltZ()
    {
        await using var r = await RenderAsync<ToastHost>(app, "/");
        var toasts = r.Scope.ServiceProvider.GetRequiredService<ToastService>();
        toasts.Show("Took the first off.", () => Task.CompletedTask);
        toasts.Show("Took the second off.", () => Task.CompletedTask);
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("Took the second off."));
        html.Split("data-toast-undo=\"newest\"").Should().HaveCount(2, "only the newest Undo is on the key");
        html.Split("Press Alt+Z to undo.").Should().HaveCount(2, "and only its toast says so");
        html.Should().Contain("aria-keyshortcuts=\"Alt+Z\"").And.Contain("class=\"tide-toast-dismiss\" aria-label=\"Dismiss\"");

        toasts.Undoable!.Message.Should().Be("Took the second off.");

        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Point(true));
        toasts.Held.Should().BeTrue("the pointer is on them");
        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Focus(true));
        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Point(false));
        toasts.Held.Should().BeTrue("focus is still on them");
        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Dismiss(toasts.Toasts[0].Id));
        toasts.Held.Should().BeFalse("the button pressed went with its toast, and focus with it");
    }
}
