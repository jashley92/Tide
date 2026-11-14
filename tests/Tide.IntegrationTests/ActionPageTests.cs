using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The action page: its status as one row of buttons that applies on a click (closing asks for the outcome in place),
/// the facts strip, and empty sections drawn as the buttons that start them.
/// </summary>
public class ActionPageTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:page-owner", "Owner", "ActionOwner");

    /// <summary>Does <paramref name="what"/> with the services, signed in as <paramref name="who"/> (an analyst by default).</summary>
    private async Task<T> As<T>(Func<ActionService, TeamService, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider.GetRequiredService<ActionService>(), scope.ServiceProvider.GetRequiredService<TeamService>());
    }

    private Task As(Func<ActionService, TeamService, Task> what, SignIn? who = null) =>
        As(async (actions, teams) => { await what(actions, teams); return true; }, who);

    private Task<Guid> TeamId(string name) => As(async (_, teams) => (await teams.ListAsync()).First(t => t.Name == name).Id);

    private async Task<ActionItem> NewAction(string title, ActionKind kind = ActionKind.Block, string team = "Cyber Defense")
    {
        var teamId = await TeamId(team);
        return await As(async (actions, _) =>
            (await actions.GetAsync(await actions.CreateAsync(new ActionForm { Title = title, Kind = kind, TeamId = teamId })))!.Action);
    }

    private Task<ActionItem> Reread(ActionItem a) => As(async (actions, _) => (await actions.GetAsync(a.Number))!.Action);

    private static Dictionary<string, object?> For(ActionItem a) => new() { ["Action"] = a };

    private static IReadOnlyList<ToastService.Toast> Toasts<T>(Rendered<T> r) => r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts;

    [Fact]
    public async Task TheStatus_IsOneRowOfToggleButtons_AndAClickOnAnOpenStatus_MovesItStraightAway()
    {
        var a = await NewAction("Block the C2 domain");
        await using var r = await RenderAsync<ActionStatusControl>(app, "/actions/" + a.Number, For(a));
        var html = await r.Html();
        html.Should().Contain("class=\"tide-status-seg\" role=\"group\" aria-labelledby=\"status-label\"")
            .And.Contain("aria-pressed=\"true\" data-status=\"Open\"").And.Contain("aria-pressed=\"false\" data-status=\"InProgress\"")
            .And.NotContain("Update status").And.NotContain("tide-status-prompt");

        await r.Click(c => c.Pick(ActionStatus.InProgress));

        var moved = await Reread(a);
        moved.Status.Should().Be(ActionStatus.InProgress, "one click applies it: there's no separate Update button");
        moved.Events.Select(e => e.Text).Should().Contain("Status: Open → In progress.");
        Toasts(r).Select(t => t.Message).Should().Contain($"A-{a.Number} moved to In progress.");
    }

    [Fact]
    public async Task Done_AsksForTheOutcomeInPlace_RefusesNone_AndEscPutsItBack()
    {
        var a = await NewAction("Sweep for the hashes", ActionKind.IocSweep);
        await using var r = await RenderAsync<ActionStatusControl>(app, "/actions/" + a.Number, For(a));

        await r.Click(c => c.Pick(ActionStatus.Done));
        var html = await r.Html();
        html.Should().Contain("class=\"tide-status-prompt\" role=\"group\"").And.Contain($"Move A-{a.Number} to Done")
            .And.Contain("for=\"status-close-outcome\">Outcome</label>").And.Contain(">Choose…</option>")
            .And.Contain(">No hits</option>").And.NotContain(">Remediated</option>", "a sweep closes with what it found, as on the board")
            .And.Contain("aria-pressed=\"true\" data-status=\"Done\"");
        var confirm = html[..html.IndexOf("Move to Done</button>", StringComparison.Ordinal)];
        confirm[confirm.LastIndexOf("<button", StringComparison.Ordinal)..].Should().Contain("disabled", "Done can't go ahead without an outcome");
        r.Component.IsPrompting.Should().BeTrue();
        (await Reread(a)).Status.Should().Be(ActionStatus.Open, "nothing moves until the outcome is chosen");

        await r.Click(c => c.Confirm());
        (await Reread(a)).Status.Should().Be(ActionStatus.Open, "Done without an outcome is refused");
        var refused = () => As((actions, _) => actions.MoveAsync(a.Id, ActionStatus.Open, ActionStatus.Done));
        await refused.Should().ThrowAsync<ArgumentException>("and the service refuses it too, as it always has");

        await r.Click(c => c.Revert());
        html = await r.Html();
        html.Should().NotContain("tide-status-prompt").And.Contain("aria-pressed=\"true\" data-status=\"Open\"");
        r.Component.IsPrompting.Should().BeFalse();

        await As((actions, _) => actions.RanCleanAsync(a.Id));   // "no hits" needs a query run clean on record
        await r.Click(c => c.Pick(ActionStatus.Done));
        await r.Click(c => { c.Choose(ActionOutcome.NoHits); c.Write("30 days of proxy logs"); return c.Confirm(); });
        var done = await Reread(a);
        done.Status.Should().Be(ActionStatus.Done);
        done.Outcome.Should().Be(ActionOutcome.NoHits);
        done.OutcomeNote.Should().Be("30 days of proxy logs");
        r.Component.IsPrompting.Should().BeFalse();
    }

    [Fact]
    public async Task CancelledAndNotApplicable_AskToo_ButTheOutcomeIsOptional()
    {
        var a = await NewAction("Patch the gateway");
        await using var r = await RenderAsync<ActionStatusControl>(app, "/actions/" + a.Number, For(a));

        await r.Click(c => c.Pick(ActionStatus.NotApplicable));
        var html = await r.Html();
        html.Should().Contain("Outcome (optional)").And.NotContain(">Choose…</option>").And.Contain(">None</option>");
        (await Reread(a)).Status.Should().Be(ActionStatus.Open);

        await r.Click(c => c.Pick(ActionStatus.Cancelled));
        (await r.Html()).Should().Contain($"Move A-{a.Number} to Cancelled", "another closed status changes the prompt, not the action");
        await r.Click(c => c.Confirm());
        (await Reread(a)).Status.Should().Be(ActionStatus.Cancelled);
    }

    [Fact]
    public async Task AMoveSomeoneElseBeatItTo_IsRefused_AndSaysSo()
    {
        var a = await NewAction("Reset the shared account");
        var stale = false;
        await using var r = await RenderAsync<ActionStatusControl>(app, "/actions/" + a.Number,
            new() { ["Action"] = a, ["Stale"] = Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => stale = true) });
        await As((actions, _) => actions.MoveAsync(a.Id, ActionStatus.Open, ActionStatus.Blocked));

        await r.Click(c => c.Pick(ActionStatus.InProgress));
        (await Reread(a)).Status.Should().Be(ActionStatus.Blocked, "theirs stands");
        Toasts(r).Should().Contain(t => t.IsError && t.Message == $"Someone else moved A-{a.Number} to Blocked in the meantime.");
        stale.Should().BeFalse("a one-click move has nothing unsaved: it says so and reloads");

        await r.Click(c => c.Pick(ActionStatus.Done));
        await r.Click(c => { c.Choose(ActionOutcome.Remediated); return c.Confirm(); });
        (await Reread(a)).Status.Should().Be(ActionStatus.Blocked);
        stale.Should().BeTrue("the page shows its changed-elsewhere message over the outcome being written");
        r.Component.IsPrompting.Should().BeTrue("the outcome stays on screen, as an edit form's changes do");
    }

    [Fact]
    public async Task AnActionOwner_ChangesTheirTeamsStatus_ButNotAnotherTeams()
    {
        var ours = await NewAction("Ours", team: "Vulnerability Management");
        var theirs = await NewAction("Theirs", team: "Cyber Defense");
        var team = await TeamId("Vulnerability Management");
        await As((_, teams) => teams.AddMemberAsync(team, Owner.UserId, Owner.Name));

        await using (var page = await RenderAsync<ActionPage>(app, "/actions/" + theirs.Number, new() { ["Number"] = (int?)theirs.Number }, Owner))
        {
            var html = await page.Html();
            html.Should().Contain("Theirs").And.NotContain("tide-status-seg").And.NotContain("class=\"tide-status-badge\"")
                .And.NotContain("Add checklist item", "someone who can't work it sees its status, not the buttons");
        }
        await using (var page = await RenderAsync<ActionPage>(app, "/actions/" + ours.Number, new() { ["Number"] = (int?)ours.Number }, Owner))
        {
            (await page.Html()).Should().Contain("class=\"tide-status-badge\"", "the status badge is the button that opens them");
            var control = page.Component.StatusControl!;
            await page.Click(_ => control.ToggleChoices());
            (await page.Html()).Should().Contain("tide-status-seg");
        }

        // The buttons aren't drawn for them, and the service refuses it regardless.
        await using var r = await RenderAsync<ActionStatusControl>(app, "/actions/" + theirs.Number, For(theirs), Owner);
        await r.Click(c => c.Pick(ActionStatus.InProgress));
        (await Reread(theirs)).Status.Should().Be(ActionStatus.Open);
        Toasts(r).Should().Contain(t => t.IsError && t.Message.Contains("members of the owning team"));
    }

    [Fact]
    public async Task AnActionOwnerOnAnotherTeam_GetsTheCommentBox_ButNoStatusControlsOrAttachments()
    {
        var theirs = await NewAction("Patch the edge firewalls", team: "Cyber Defense");

        await using var page = await RenderAsync<ActionPage>(app, "/actions/" + theirs.Number, new() { ["Number"] = (int?)theirs.Number }, Owner);
        var html = await page.Html();
        html.Should().Contain("aria-label=\"New comment\"").And.Contain(">Comment</button>")
            .And.Contain("Owned by Cyber Defense")
            .And.NotContain("tide-status-seg").And.NotContain("Attach screenshots or evidence").And.NotContain("Take it</button>");
    }

    [Fact]
    public async Task ThePage_PutsTheFactsInACard_AndDrawsEmptySectionsAsTheButtonsThatStartThem()
    {
        var a = await NewAction("Sweep the proxy logs", ActionKind.IocSweep);
        await using var r = await RenderAsync<ActionPage>(app, "/actions/" + a.Number, new() { ["Number"] = (int?)a.Number });
        var html = await r.Html();

        html.Should().Contain("<dl class=\"tide-facts tide-facts-side\">");
        foreach (var fact in new[] { "Team", "Assignee", "Kind", "Ticket", "Created", "Due" })
            html.Should().Contain($"<div class=\"tide-fact\"><dt>{fact}</dt>");
        html.Should().Contain("<dd>Cyber Defense</dd>").And.Contain("<dd>IOC sweep</dd>").And.Contain("<dd>Unassigned</dd>");
        html.Should().Contain("title=\"Priority P3\"", "the priority is a badge in the header");

        html.Should().NotContain("Add hunt query</button>")
            .And.Contain("<h2>Hunt queries", "a sweep's queries are its work: the panel is open, ready to generate one")
            .And.Contain("aria-label=\"Query language\"").And.NotContain("Split off a part for a team");
        html.IndexOf("aria-label=\"Facts\"", StringComparison.Ordinal).Should().BeGreaterThan(html.IndexOf("id=\"feed-h\"", StringComparison.Ordinal),
            "the facts are in the context rail, after the page");
        r.Component.MoreItems.Should().Contain(i => i.Label == "Split off a part" && i.Href == $"actions/new?parent={a.Id}");

        // Its checklist tab: empty, the button that starts one.
        await using (var checklist = await RenderAsync<ActionPage>(app, $"/actions/{a.Number}?tool=checklist", new() { ["Number"] = (int?)a.Number }))
            (await checklist.Html()).Should().Contain("Add checklist item</button>");

        await As((actions, _) => actions.AddChecklistItemAsync(a.Id, "Pull 30 days of logs"));
        await using var filled = await RenderAsync<ActionPage>(app, $"/actions/{a.Number}?tool=checklist", new() { ["Number"] = (int?)a.Number });
        (await filled.HtmlOnce("Pull 30 days of logs")).Should().Contain("Checklist").And.NotContain("Add checklist item</button>",
            "a checklist with items is the card");
    }

    [Fact]
    public async Task AnActionThatIsntAHunt_HasNoHuntButton()
    {
        var a = await NewAction("Block the sender");
        await using var r = await RenderAsync<ActionPage>(app, "/actions/" + a.Number, new() { ["Number"] = (int?)a.Number });
        (await r.Html()).Should().Contain("Add checklist item").And.NotContain("Add hunt query").And.NotContain("<h2>Hunt queries");
    }

    [Fact]
    public async Task AHistoryLineByAPerson_ShowsTheirInitials_AndOnlyTidesOwnLinesShowT()
    {
        // Every history line showed "T" (TIDE's mark), even one naming Robin, beside Robin's own "R" comments.
        var robin = new SignIn("dev:hist-robin", "Robin Hale", "Analyst", "Admin");
        var teamId = await TeamId("Cyber Defense");
        var number = await As((actions, _) => actions.CreateAsync(new ActionForm { Title = "History initials", Kind = ActionKind.Block, TeamId = teamId }), robin);

        await using var page = await RenderAsync<ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number }, robin);
        var html = System.Net.WebUtility.HtmlDecode(await page.HtmlOnce("History initials"));

        var line = html[html.IndexOf("tide-feed-line", StringComparison.Ordinal)..];
        line = line[..line.IndexOf("</li>", StringComparison.Ordinal)];
        line.Should().Contain("Robin Hale").And.Contain(">RH</span>").And.NotContain("tide-feed-sys");
    }
}
