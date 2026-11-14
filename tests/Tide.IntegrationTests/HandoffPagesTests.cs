using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// EP-86 on the pages: the board's Tracking column, the parent filter and its link, a parent's rollup and the warning
/// before it's closed with parts open, a part's prefill with no team chosen, a hand-off and a split from the action's
/// page, the reason asked when a team that warns goes over its limit, the count against a limit, and the External teams
/// page.
/// </summary>
public class HandoffPagesTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:ep86-owner", "Owner", "ActionOwner");
    private static readonly SignIn Viewer = new("dev:ep86-viewer", "Viewer", "Viewer");

    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    // A team of the test's own (the app's database is shared by the class), by a name no other test uses.
    private Task<Guid> Team(string name, int? limit = null, ActiveLimitMode mode = ActiveLimitMode.Show) => As(async sp =>
    {
        var teams = sp.GetRequiredService<TeamService>();
        var id = await teams.AddAsync(name, null, null);
        if (limit is not null) await teams.SetLimitAsync(id, limit, mode);
        return id;
    });

    // External team reminders for everyone, an administrator's switch on Teams & SLA (off unless set).
    private Task ExternalReminders(bool on) => As(async sp =>
    {
        await sp.GetRequiredService<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?> { [OperationalSettings.ExternalReminders] = on ? "true" : "false" });
        return true;
    });

    private Task<int> Raise(Guid team, string title, Guid? parent = null, DateOnly? due = null) =>
        As(sp => sp.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = title, TeamId = team, ParentId = parent, DueDate = due }));

    private Task<ActionDetail> Detail(int number) => As(async sp => (await sp.GetRequiredService<ActionService>().GetAsync(number))!);

    private Task<Rendered<ActionPage>> Open(int number) => RenderAsync<ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number });

    [Fact]
    public async Task TheBoard_HasATrackingColumn_BetweenBlockedAndDone_AndItsMoveMenuOffersIt()
    {
        await using var r = await RenderAsync<ActionKanban>(app, "/actions?layout=board", new()
        {
            ["Board"] = new ActionBoard([], false, new HashSet<Guid>()), ["Today"] = DateOnly.FromDateTime(DateTime.UtcNow),
        });
        var html = await r.Html();
        var columns = new[] { "Open", "InProgress", "Blocked", "Tracking", "Done", "Cancelled", "NotApplicable" }
            .Select(s => html.IndexOf($"data-status=\"{s}\"", StringComparison.Ordinal)).ToList();
        columns.Should().OnlyContain(i => i >= 0).And.BeInAscendingOrder("a lane each for the working statuses, then a chip each for the closed ones");
    }

    [Fact]
    public async Task TheParentFilter_ShowsItsParts_AndTheParentLinksToIt_WithItsRollup()
    {
        var team = await Team("EP86 rollup team");
        var parent = await Raise(team, "EP86 parent with parts");
        var parentId = (await Detail(parent)).Action.Id;
        var first = await Raise(team, "EP86 first part", parentId);
        await Raise(team, "EP86 second part", parentId);
        await As(async sp =>
        {
            var svc = sp.GetRequiredService<ActionService>();
            await svc.SetStatusAsync((await svc.GetAsync(first))!.Action.Id, ActionStatus.Cancelled, ActionOutcome.None, null);
            return true;
        });

        await using (var page = await Open(parent))
        {
            var html = WebUtility.HtmlDecode(await page.Html());
            html.Should().Contain("1 of 2 done").And.Contain($"actions?view=open&layout=board&parent={parent}\">Open its 2 actions on the board");
        }

        await using var list = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&parent={parent}");
        var shown = WebUtility.HtmlDecode(await list.HtmlOnce("matching"));
        shown.Should().Contain($"Part of {Ui.ActionLabel(parent)}").And.Contain("EP86 second part").And.NotContain("EP86 first part")
            .And.NotContain(">EP86 parent with parts<");
    }

    [Fact]
    public async Task ClosingAParentWithOpenParts_AsksFirst()
    {
        var team = await Team("EP86 close team");
        var parent = await Raise(team, "EP86 parent to close");
        var id = (await Detail(parent)).Action.Id;
        await Raise(team, "EP86 open part", id);
        await Raise(team, "EP86 another open part", id);

        await using var page = await Open(parent);
        (await page.Html()).Should().Contain("0 of 2 done");
        await using var control = await RenderAsync<ActionStatusControl>(app, "/actions/" + parent, new()
        {
            ["Action"] = (await Detail(parent)).Action, ["OpenParts"] = 2,
        });
        await control.Press(c => c.Pick(ActionStatus.Done));
        var html = WebUtility.HtmlDecode(await control.HtmlOnce("Close anyway"));
        html.Should().Contain($"Close {Ui.ActionLabel(parent)} with parts still open?").And.Contain(ActionPrompts.CloseAnyway(2))
            .And.Contain("2 of its parts are still open");
        html.Should().NotContain("tide-status-prompt", "the outcome is asked only once it's agreed to close it");
    }

    [Fact]
    public async Task ANewPart_StartsFromItsParent_WithNoTeamChosen()
    {
        var team = await Team("EP86 prefill team");
        var due = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(20);
        var parent = await As(sp => sp.GetRequiredService<ActionService>().CreateAsync(new ActionForm
        {
            Title = "EP86 contain the loader", Description = "Block and sweep.", TeamId = team, DueDate = due, Priority = Priority.P2,
        }));
        var id = (await Detail(parent)).Action.Id;

        await using var page = await RenderAsync<ActionPage>(app, $"/actions/new?parent={id}", new() { ["Number"] = (int?)null });
        var form = page.Component.NewForm;
        (form.Title, form.Description, form.DueDate, form.Priority, form.ParentId, form.TeamId, form.AssigneeId)
            .Should().Be(("EP86 contain the loader", "Block and sweep.", due, Priority.P2, id, Guid.Empty, (string?)null));
        (await page.Html()).Should().Contain("Choose a team");
    }

    [Fact]
    public async Task AHandoff_FromTheActionsPage_MovesItAndSaysWhy()
    {
        var from = await Team("EP86 handing team");
        var to = await Team("EP86 receiving team");
        var number = await Raise(from, "EP86 handed over");
        await using var page = await Open(number);
        await page.Press(p => p.StartHandoff());
        (await page.HtmlOnce("Hand it to")).Should().Contain("EP86 receiving team");
        await page.Click(async p =>
        {
            (p.HandTo, p.HandWhy) = (to, "Their firewall");
            (await p.HandOff()).Should().BeTrue(p.HandError);
        });
        var d = await Detail(number);
        d.Action.TeamId.Should().Be(to);
        d.HandedFrom.Should().Be("EP86 handing team");
        await using var again = await Open(number);
        WebUtility.HtmlDecode(await again.Html()).Should().Contain("handed from EP86 handing team by Tester").And.Contain("Their firewall")
            .And.Contain("Handed from EP86 handing team to EP86 receiving team: Their firewall", "and its history line is in the feed");
    }

    [Fact]
    public async Task HandingToAnExternalTeamThatWontBeEmailed_SaysSo_AndExternalTeamsMarksIt()
    {
        var from = await Team("EP86 hands to quiet");
        var quiet = await As(sp => sp.GetRequiredService<TeamService>().AddExternalAsync(new ExternalTeamForm { Name = "EP86 quiet provider", RemindersOn = false }));
        var number = await Raise(from, "EP86 handed to the quiet one");
        await using var page = await Open(number);
        await page.Press(p => p.StartHandoff());
        (await page.HtmlOnce("Hand it to")).Should().Contain("EP86 quiet provider (external)").And.NotContain("won&#x27;t be emailed", "nothing's chosen yet");

        // The note under the team chosen: nothing while external reminders are off for everyone (an administrator's
        // choice, not this team's gap), and none for an internal team. With them on, this team's own switch and its fix,
        // for whoever keeps the external teams.
        var teams = await As(sp => sp.GetRequiredService<TeamService>().ListAsync());
        var off = teams.Single(t => t.Id == quiet);
        await using (var everyone = await RenderAsync<NotEmailedNote>(app, "/", new() { ["Team"] = off }))
            (await everyone.Html()).Trim().Should().BeEmpty("they're off for everyone, so nothing's missing here");
        await ExternalReminders(true);
        try
        {
            ExternalCadence.NotEmailed(off, scanOn: true)!.Cause.Should().Be(NotEmailedCause.TeamOff);
            await using (var note = await RenderAsync<NotEmailedNote>(app, "/", new() { ["Team"] = off }))
                WebUtility.HtmlDecode(await note.Html()).Should().Contain("EP86 quiet provider's reminders are off, so they won't be emailed about this")
                    .And.Contain("bi-envelope-slash").And.Contain("Turn their reminders on");
            await using (var none = await RenderAsync<NotEmailedNote>(app, "/", new() { ["Team"] = teams.Single(t => t.Id == from) }))
                (await none.Html()).Trim().Should().BeEmpty();
            await using (var viewer = await RenderAsync<NotEmailedNote>(app, "/", new() { ["Team"] = off }, Viewer))
                (await viewer.Html()).Should().Contain("won&#x27;t be emailed").And.NotContain("Turn their reminders on", "only whoever keeps the external teams turns them on");
        }
        finally { await ExternalReminders(false); }
        await using (var editing = await RenderAsync<ExternalTeams>(app, $"/teams/external?edit={quiet}"))
            editing.Component.Editing.Should().Be(quiet, "Add a contact email opens that team's form");

        await using var list = await RenderAsync<ExternalTeams>(app, "/teams/external");
        WebUtility.HtmlDecode(await list.Html()).Should().Contain("Not emailed");
    }

    [Fact]
    public async Task ASplit_FromTheActionsPage_AddsAnExternalTeamInline_AndRaisesTheParts()
    {
        var owner = await Team("EP86 split owner");
        var other = await Team("EP86 split other");
        var number = await Raise(owner, "EP86 split me", due: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5));
        await using var page = await Open(number);
        await page.Press(p => p.OpenSplit());
        (await page.HtmlOnce("Add an external team")).Should().Contain("EP86 split other");
        var dialog = page.Component.Splitter!;
        await page.Click(async _ =>
        {
            dialog.Toggle(other, true);
            dialog.Parts[0].DueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(9);
            (dialog.NewName, dialog.NewEmail) = ("EP86 Hosting Provider", "noc@provider.example");
            await dialog.AddExternal();
        });
        dialog.Outside.Should().Contain("noc@provider.example is outside example.org", "a contact outside the organization is asked about first");
        dialog.Parts.Should().HaveCount(1, "nothing added until it's confirmed");
        await page.Click(_ => dialog.AddExternal(confirmOutside: true));   // Add anyway
        dialog.Error.Should().BeNull();
        dialog.Outside.Should().BeNull();
        dialog.Parts.Should().HaveCount(2, "the new external team is ticked");
        await page.Click(async _ => (await dialog.Split()).Should().BeTrue(dialog.Error));

        var d = await Detail(number);
        d.Children.Select(c => c.Title).Should().BeEquivalentTo("EP86 split me – EP86 split other", "EP86 split me – EP86 Hosting Provider");
        var provider = d.Children.Single(c => c.Title.EndsWith("Provider", StringComparison.Ordinal)).TeamId;
        (await As(sp => sp.GetRequiredService<TeamService>().ListAsync())).Single(t => t.Id == provider).IsExternal.Should().BeTrue();
    }

    [Fact]
    public async Task ATeamThatWarns_IsAskedWhy_WhenANewActionTakesItOver()
    {
        var team = await Team("EP86 warning team", 1, ActiveLimitMode.Warn);
        await Raise(team, "EP86 at the limit");
        await using var page = await RenderAsync<ActionPage>(app, "/actions/new", new() { ["Number"] = (int?)null });
        page.Component.NewForm.Title = "EP86 one over";
        page.Component.NewForm.TeamId = team;
        await page.Press(p => p.Create());
        WebUtility.HtmlDecode(await page.HtmlOnce("Why go over the limit?")).Should().Contain("Over EP86 warning team's limit");
        // On the renderer's thread, but not as a new click: that would sign the waiting create out (its sign-in is the press's).
        await page.Renderer.Dispatcher.InvokeAsync(() =>
        {
            page.Component.OverLimit.Reason = "Incident cover";
            return page.Component.OverLimit.Confirm();
        });
        // The create goes on once the dialog closes: its toast says when. (Signing in elsewhere meanwhile would sign it out:
        // the sign-in is one, shared.)
        var toasts = page.Scope.ServiceProvider.GetRequiredService<Tide.Web.Services.ToastService>();
        for (var i = 0; i < 50 && toasts.Toasts.Count == 0; i++) await Task.Delay(100);
        var made = await As(async sp => (await sp.GetRequiredService<ActionService>().ListAsync(new ActionFilter(ActionView.Open, team))).FirstOrDefault(r => r.Title == "EP86 one over"));
        made.Should().NotBeNull(string.Join(" | ", toasts.Toasts.Select(t => t.Message)));
        (await Detail(made!.Number)).Action.Events.Should().Contain(e => e.Text.Contains("(2 of 1 active): Incident cover"));

        await using var workload = await RenderAsync<Workload>(app, "/workload");
        WebUtility.HtmlDecode(await workload.HtmlOnce("EP86 warning team")).Should().Contain("2 of 1 active");
        await using var board = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&team={team}");
        WebUtility.HtmlDecode(await board.HtmlOnce("matching")).Should().Contain("2 of 1 active");
    }

    [Fact]
    public async Task TheExternalTeamsPage_IsKeptByWhoeverWorksActions_AndReadByTheRest()
    {
        await using (var page = await RenderAsync<ExternalTeams>(app, "/teams/external", @as: Owner))
        {
            (await page.Html()).Should().Contain("New external team");
            await page.Click(async p =>
            {
                (p.NewForm.Name, p.NewForm.ContactEmail, p.NewForm.RemindOverdueEveryDays) = ("EP86 Network Engineering", "network@example.org", null);
                await p.Add();
            });
            page.Component.Shown.Should().Contain(t => t.Name == "EP86 Network Engineering" && t.IsExternal && t.CreatedBy == Owner.UserId);
        }
        await using (var again = await RenderAsync<ExternalTeams>(app, "/teams/external", @as: Owner))
            WebUtility.HtmlDecode(await again.Html()).Should().Contain("2 business days before, then once when overdue");
        await using var viewer = await RenderAsync<ExternalTeams>(app, "/teams/external", @as: Viewer);
        var html = await viewer.Html();
        html.Should().Contain("EP86 Network Engineering").And.NotContain("New external team").And.NotContain(">Edit<");

        var internalTeams = await As(async sp => (await sp.GetRequiredService<TeamService>().ListAsync()).Where(t => !t.IsExternal).Select(t => t.Name).ToList());
        internalTeams.Should().NotContain("EP86 Network Engineering");
    }
}
