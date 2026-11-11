using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Actions;
using Tide.Application.Org;
using Tide.Application.Preferences;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.TestSupport;

namespace Tide.IntegrationTests;

/// <summary>
/// EP-86 in the services: a team's active limit (show, warn with a reason, refuse) on create, move and hand-off; the
/// Tracking status; hand-offs and splits; a parent's rollup and the parent filter; a part's prefill; external teams (who
/// keeps them, where they're left out) and their reminder emails.
/// </summary>
public class HandoffsAndExternalTeamsTests : IDisposable
{
    private readonly TestDb _db = new();

    public HandoffsAndExternalTeamsTests() => AsAnalyst();

    public void Dispose() => _db.Dispose();

    private void As(string id, string name, params AppRole[] roles) =>
        (_db.User.UserId, _db.User.DisplayName, _db.User.Roles) = (id, name, roles.ToHashSet());

    private void AsAnalyst() => As("S-1-5-21-TEST-1", "Test Analyst", AppRole.Analyst, AppRole.Admin);

    private Task<Guid> Team(string name, string? mailbox = null) => _db.Teams().AddAsync(name, null, mailbox);

    private async Task<Guid> Limited(string name, int limit, ActiveLimitMode mode)
    {
        var team = await Team(name);
        await _db.Teams().SetLimitAsync(team, limit, mode);
        return team;
    }

    private async Task<ActionItem> New(Guid team, string title = "Sweep for the loader", ActionStatus status = ActionStatus.Open, string? reason = null)
    {
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = title, TeamId = team, OverLimitReason = reason });
        var a = (await _db.Actions().GetAsync(number))!.Action;
        if (status != ActionStatus.Open) await _db.Actions().SetStatusAsync(a.Id, status, ActionOutcome.None, null);
        return (await _db.Actions().GetAsync(number))!.Action;
    }

    private async Task<IEnumerable<string>> History(int number) => (await _db.Actions().GetAsync(number))!.Action.Events.Select(e => e.Text);

    private async Task Person(string sid, string email)
    {
        await using var ctx = _db.Context();
        ctx.Users.Add(new AppUser { Sid = sid, DisplayName = sid, Email = email, RolesCsv = "ActionOwner", LastSeenUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();
    }

    // --- the active limit ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ATeamThatShowsItsLimit_OnlyCountsIt()
    {
        var team = await Limited("Network Engineering", 1, ActiveLimitMode.Show);
        await New(team);
        var second = await New(team, "One more");
        (await History(second.Number)).Should().NotContain(t => t.Contains("limit"));
        var active = await _db.Actions().TeamActiveAsync(team);
        active!.Text.Should().Be("2 of 1 active");
        active.Over.Should().BeTrue();
        (await _db.Actions().TeamActiveAsync(await Team("No limit"))).Should().BeNull("a team with no limit sees no change");
    }

    [Fact]
    public async Task ATeamThatWarns_AsksForAReason_OnCreateMoveAndHandoff_AndKeepsItInTheHistory()
    {
        var team = await Limited("Network Engineering", 1, ActiveLimitMode.Warn);
        await New(team);

        var create = () => New(team, "Over on create");
        (await create.Should().ThrowAsync<ActiveLimitException>()).Which.NeedsReason.Should().BeTrue();
        var made = await New(team, "Over on create", reason: "Outage cover");
        (await History(made.Number)).Should().Contain(t => t.EndsWith("Over Network Engineering's limit (2 of 1 active): Outage cover", StringComparison.Ordinal));

        var tracked = await New(await Team("Elsewhere"), "Tracked elsewhere", ActionStatus.Tracking);
        var handOff = () => _db.Handoffs().HandOffAsync(tracked.Id, team, "Theirs now");
        await handOff.Should().NotThrowAsync("a tracked action isn't active, so it doesn't count");
        var move = () => _db.Actions().MoveAsync(tracked.Id, ActionStatus.Tracking, ActionStatus.InProgress);
        (await move.Should().ThrowAsync<ActiveLimitException>()).Which.NeedsReason.Should().BeTrue("moving into an active status adds to the count");
        await _db.Actions().MoveAsync(tracked.Id, ActionStatus.Tracking, ActionStatus.InProgress, overLimitReason: "Escalated");
        (await History(tracked.Number)).Should().Contain(t => t.Contains("Status: Tracking → In progress.") && t.Contains("(3 of 1 active): Escalated"));

        var active = await New(await Team("Sender"), "Active elsewhere");
        var hand = () => _db.Handoffs().HandOffAsync(active.Id, team, "Their fix");
        (await hand.Should().ThrowAsync<ActiveLimitException>()).Which.NeedsReason.Should().BeTrue();
        await _db.Handoffs().HandOffAsync(active.Id, team, "Their fix", "Agreed with their lead");
        (await History(active.Number)).Should().Contain(t => t.Contains("Handed from Sender to Network Engineering: Their fix") && t.Contains("Agreed with their lead"));
    }

    [Fact]
    public async Task ATeamThatRefuses_RefusesCreateMoveAndHandoff_SayingWhy()
    {
        var team = await Limited("Network Engineering", 1, ActiveLimitMode.Refuse);
        var first = await New(team);
        var create = () => New(team, "One too many", reason: "please");
        (await create.Should().ThrowAsync<ActiveLimitException>()).Which.Message.Should().Contain("at its limit of 1 active action (1 of 1 active)");

        await _db.Actions().MoveAsync(first.Id, ActionStatus.Open, ActionStatus.Tracking);
        var second = await New(team, "Room now: the first is tracked");
        var reopen = () => _db.Actions().MoveAsync(first.Id, ActionStatus.Tracking, ActionStatus.Open);
        (await reopen.Should().ThrowAsync<ActiveLimitException>()).Which.NeedsReason.Should().BeFalse();

        var elsewhere = await New(await Team("Sender"));
        var hand = () => _db.Handoffs().HandOffAsync(elsewhere.Id, team, "Theirs");
        await hand.Should().ThrowAsync<ActiveLimitException>();
        (await _db.Actions().GetAsync(elsewhere.Number))!.Action.TeamId.Should().NotBe(team);
        var edit = ActionForm.From((await _db.Actions().GetAsync(elsewhere.Number))!.Action);
        edit.TeamId = team;
        var save = () => _db.Actions().SaveAsync(edit);
        await save.Should().ThrowAsync<ActiveLimitException>("changing the team in the editor is held to the limit too");
        second.Status.Should().Be(ActionStatus.Open);
    }

    [Fact]
    public async Task OnlyAnAdministrator_SetsALimit()
    {
        var team = await Team("Network Engineering");
        As("S-1-5-21-OWNER", "Owner", AppRole.ActionOwner);
        var set = () => _db.Teams().SetLimitAsync(team, 3, ActiveLimitMode.Warn);
        await set.Should().ThrowAsync<ForbiddenException>();
        AsAnalyst();
        var bad = () => _db.Teams().SetLimitAsync(team, 0, ActiveLimitMode.Warn);
        await bad.Should().ThrowAsync<ArgumentException>();
    }

    // --- Tracking -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Tracking_IsOpen_ForTheListBoardFilterApiAndOverdue()
    {
        var team = await Team("Cyber Response");
        await _db.Teams().SetLimitAsync(team, 5, ActiveLimitMode.Show);
        var a = await New(team, "Followed elsewhere", ActionStatus.Tracking);
        await _db.Actions().AssignAsync(a.Id, null, _db.Clock.Today.AddDays(-2));
        var svc = _db.Actions();
        (await svc.ListAsync(new ActionFilter(ActionView.Open))).Should().Contain(r => r.Id == a.Id);
        (await svc.ListAsync(new ActionFilter(ActionView.Overdue))).Should().Contain(r => r.Id == a.Id, "its due date still applies");
        (await svc.PageAsync(new ActionFilter(ActionView.Open) { Status = ActionStatus.Tracking })).Rows.Should().ContainSingle(r => r.Id == a.Id);
        (await svc.BoardAsync(new ActionFilter(ActionView.Open))).In(ActionStatus.Tracking).Should().ContainSingle();
        (await _db.Api().ActionsAsync(ActionView.Open, null)).Single(x => x.Number == a.Number).Status.Should().Be("Tracking");
        (await _db.Api().UpdateActionAsync(a.Number, new Tide.Application.Api.ApiActionUpdate("tracking", null, null), null)).Should().NotBeNull();
        (await svc.WorkloadAsync()).Single(t => t.Team.Id == team).Should().Match<TeamLoad>(t => t.Open == 1 && t.Overdue == 1 && t.Active!.Active == 0);
    }

    // --- hand-offs ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHandoff_ChangesTheTeam_RecordsWhoAndWhy_ClearsAnAssigneeNotOnTheTeam_AndTellsItsMailbox()
    {
        var from = await Team("Cyber Response");
        var to = await Team("Network Engineering", "network@example.org");
        await _db.Teams().AddMemberAsync(from, "S-1-5-21-ROBIN", "Robin");
        var a = await New(from);
        await _db.Actions().AssignAsync(a.Id, "S-1-5-21-ROBIN", null);

        var told = await _db.Handoffs().HandOffAsync(a.Id, to, "  The fix is a firewall change. ");
        told.Should().Be(1);
        var d = (await _db.Actions().GetAsync(a.Number))!;
        d.Action.TeamId.Should().Be(to);
        d.Action.AssigneeId.Should().BeNull("Robin isn't on Network Engineering");
        (d.HandedFrom, d.Action.HandedByName, d.Action.HandoffReason).Should().Be(("Cyber Response", "Test Analyst", "The fix is a firewall change."));
        d.Action.Events.Select(e => e.Text).Should().Contain("Handed from Cyber Response to Network Engineering: The fix is a firewall change. Robin is no longer its assignee.");
        var sent = _db.Email.Sent.Single();
        sent.To.Should().Equal("network@example.org");
        sent.Subject.Should().Be("TIDE: an action handed to Network Engineering");
        sent.Html.Should().Contain("handed this action from Cyber Response to Network Engineering").And.Contain("The fix is a firewall change.");
    }

    [Fact]
    public async Task AHandoff_KeepsAnAssigneeOnTheTeam_TellsItsMembersWithoutAMailbox_AndNeedsAReason()
    {
        var from = await Team("Cyber Response");
        var to = await Team("Network Engineering");
        await Person("S-1-5-21-SAM", "sam@example.org");
        await Person("S-1-5-21-ALEX", "alex@example.org");
        await _db.Teams().AddMemberAsync(from, "S-1-5-21-SAM", "Sam");
        await _db.Teams().AddMemberAsync(to, "S-1-5-21-SAM", "Sam");
        await _db.Teams().AddMemberAsync(to, "S-1-5-21-ALEX", "Alex");
        var a = await New(from);
        await _db.Actions().AssignAsync(a.Id, "S-1-5-21-SAM", null);

        var blank = () => _db.Handoffs().HandOffAsync(a.Id, to, "  ");
        await blank.Should().ThrowAsync<ArgumentException>().WithMessage("*why*");
        var same = () => _db.Handoffs().HandOffAsync(a.Id, from, "Nothing");
        await same.Should().ThrowAsync<ArgumentException>();

        As("S-1-5-21-ALEX", "Alex", AppRole.ActionOwner);
        await _db.NotificationPrefs().SetMineAsync(UserPreferences.NotifyHandoffs, UserPreferences.Off);
        As("S-1-5-21-SAM", "Sam", AppRole.ActionOwner);
        (await _db.Handoffs().HandOffAsync(a.Id, to, "Theirs")).Should().Be(1);
        (await _db.Actions().GetAsync(a.Number))!.Action.AssigneeName.Should().Be("Sam", "Sam is on the receiving team");
        _db.Email.Sent.Single().To.Should().Equal(["sam@example.org"], "Alex turned hand-offs off");

        As("S-1-5-21-NOBODY", "Nobody", AppRole.ActionOwner);
        var refused = () => _db.Handoffs().HandOffAsync(a.Id, from, "Back");
        await refused.Should().ThrowAsync<ForbiddenException>("only whoever may work it hands it off");
    }

    [Fact]
    public async Task AnotherTeamInTheEditor_IsAHandoff_RecordedAndToldAsTheButtonsIs()
    {
        var from = await Team("Cyber Response");
        var to = await Team("Network Engineering", "network@example.org");
        await _db.Teams().AddMemberAsync(from, "S-1-5-21-ROBIN", "Robin");
        var a = await New(from);
        await _db.Actions().AssignAsync(a.Id, "S-1-5-21-ROBIN", null);

        var form = ActionForm.From((await _db.Actions().GetAsync(a.Number))!.Action);
        form.TeamId = to;
        await _db.Actions().SaveAsync(form);
        var d = (await _db.Actions().GetAsync(a.Number))!;
        (d.Action.TeamId, d.HandedFrom, d.Action.HandoffReason, d.Action.AssigneeId)
            .Should().Be((to, "Cyber Response", ActionService.EditorHandoffReason, (string?)null), "Robin isn't on Network Engineering");
        d.Action.Events.Select(e => e.Text).Should().Contain(
            $"Handed from Cyber Response to Network Engineering: {ActionService.EditorHandoffReason} Robin is no longer its assignee.");
        _db.Email.Sent.Single().To.Should().Equal("network@example.org");

        form = ActionForm.From(d.Action);
        (form.TeamId, form.HandoffReason) = (from, "Back: it's ours after all");
        await _db.Actions().SaveAsync(form);
        (await _db.Actions().GetAsync(a.Number))!.Action.HandoffReason.Should().Be("Back: it's ours after all", "the editor's own reason, when given");

        var limited = await Limited("Hosting Provider", 1, ActiveLimitMode.Refuse);
        await New(limited, "Theirs already");
        form = ActionForm.From((await _db.Actions().GetAsync(a.Number))!.Action);
        form.TeamId = limited;
        var save = () => _db.Actions().SaveAsync(form);
        await save.Should().ThrowAsync<ActiveLimitException>("the receiving team's limit holds, as for the button");
    }

    [Fact]
    public async Task TakingAnAction_AtATeamsLimit_IsntRefused_ItWasActiveAlready()
    {
        var team = await Limited("Network Engineering", 1, ActiveLimitMode.Refuse);
        var a = await New(team);
        await _db.Actions().TakeAsync(a.Id);
        var taken = (await _db.Actions().GetAsync(a.Number))!.Action;
        (taken.Status, taken.AssigneeId).Should().Be((ActionStatus.InProgress, _db.User.UserId));

        await _db.Actions().MoveAsync(a.Id, ActionStatus.InProgress, ActionStatus.Tracking);
        await New(team, "Takes the room");
        await _db.Actions().TakeAsync(a.Id);   // a tracked one taken on stays tracked: still not active
        (await _db.Actions().GetAsync(a.Number))!.Action.Status.Should().Be(ActionStatus.Tracking);
    }

    [Fact]
    public async Task AHandoffToAnExternalTeam_EmailsNoOne()
    {
        var a = await New(await Team("Cyber Response"));
        var external = await _db.Teams().AddExternalAsync(new ExternalTeamForm { ConfirmOutside = true, Name = "Hosting Provider", ContactEmail = "noc@provider.example" });
        (await _db.Handoffs().HandOffAsync(a.Id, external, "They host it")).Should().Be(0);
        _db.Email.Sent.Should().BeEmpty("an external team hears only through its reminders");
    }

    private static string Short(DateOnly d) => d.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);

    // --- splits, the rollup, the parent filter and prefill ------------------------------------------------------

    [Fact]
    public async Task ASplit_RaisesAPartPerTeam_Prefilled_Together_AndTheParentRollsThemUp()
    {
        var owner = await Team("Cyber Response");
        var network = await Team("Network Engineering");
        var hosting = await _db.Teams().AddExternalAsync("Hosting Provider", "noc@provider.example", confirmOutside: true);
        var today = _db.Clock.Today;
        var number = await _db.Actions().CreateAsync(new ActionForm
        {
            Title = "Contain the loader", Description = "Block and sweep.", TeamId = owner, DueDate = today.AddDays(10), Kind = ActionKind.Block, Priority = Priority.P2,
        });
        var parent = (await _db.Actions().GetAsync(number))!.Action;
        await _db.Actions().AddIndicatorsAsync(parent.Id, [new Tide.Application.Articles.IndicatorForm { Type = IndicatorType.Domain, Value = "bad.example" }]);

        var made = await _db.Handoffs().SplitAsync(parent.Id,
            [new SplitPart(network, today.AddDays(12), "https://tickets.example.org/NET-1"), new SplitPart(hosting)]);
        made.Should().HaveCount(2);
        var net = (await _db.Actions().GetAsync(made[0]))!.Action;
        (net.Title, net.Description, net.Kind, net.Priority, net.DueDate, net.TicketUrl, net.AssigneeId, net.ParentId)
            .Should().Be(("Contain the loader – Network Engineering", "Block and sweep.", ActionKind.Block, Priority.P2, today.AddDays(12),
                "https://tickets.example.org/NET-1", (string?)null, parent.Id));
        net.Indicators.Select(i => i.Value).Should().Equal("bad.example");
        net.Checklist.Should().BeEmpty();
        (await _db.Actions().GetAsync(made[1]))!.Action.DueDate.Should().Be(today.AddDays(10), "the parent's when none is given");
        (await History(number)).Should().Contain($"Split into A-{made[0]} (Network Engineering), A-{made[1]} (Hosting Provider).");

        await _db.Actions().AssignAsync(net.Id, null, today.AddDays(-1));
        var row = await _db.Actions().RowAsync(parent.Id);
        row!.Rollup.Should().Be("0 of 2 done · 1 overdue · next due " + Short(today.AddDays(-1)));
        await _db.Actions().SetStatusAsync(net.Id, ActionStatus.Cancelled, ActionOutcome.None, null);
        (await _db.Actions().RowAsync(parent.Id))!.Rollup.Should().Be("1 of 2 done · next due " + Short(today.AddDays(10)));

        var parts = await _db.Actions().PageAsync(new ActionFilter(ActionView.Open) { ParentNumber = number });
        parts.Rows.Select(r => r.Number).Should().Equal([made[1]], "its open parts, not the parent");
        (await _db.Actions().PageAsync(new ActionFilter(ActionView.Closed) { ParentNumber = number })).Rows.Select(r => r.Number).Should().Equal(made[0]);
        (await _db.Actions().BoardAsync(new ActionFilter(ActionView.Open) { ParentNumber = number })).Cards.Select(r => r.Number).Should().Equal([made[1]],
            "the board draws the open part; the cancelled one is counted under it");
        (await _db.Actions().ViewCountsAsync(new ActionFilter(ActionView.Open) { ParentNumber = number }, closedDays: ActionService.BoardClosedDays))
            .ClosedAs(ActionStatus.Cancelled).Should().Be(1);

        var again = () => _db.Handoffs().SplitAsync(net.Id, [new SplitPart(owner)]);
        await again.Should().ThrowAsync<ArgumentException>().WithMessage("*one level deep*");
        var twice = () => _db.Handoffs().SplitAsync(parent.Id, [new SplitPart(owner), new SplitPart(owner)]);
        await twice.Should().ThrowAsync<ArgumentException>();
        As("S-1-5-21-OWNER", "Owner", AppRole.ActionOwner);
        var owners = () => _db.Handoffs().SplitAsync(parent.Id, [new SplitPart(owner)]);
        await owners.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task APartRaisedOnItsOwn_NeedsATeam_AndTakesTheParentsIndicators()
    {
        var owner = await Team("Cyber Response");
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = "Contain", TeamId = owner });
        var parent = (await _db.Actions().GetAsync(number))!.Action;
        await _db.Actions().AddIndicatorsAsync(parent.Id, [new Tide.Application.Articles.IndicatorForm { Type = IndicatorType.Domain, Value = "bad.example" }]);

        var noTeam = () => _db.Actions().CreateAsync(new ActionForm { Title = "Contain", ParentId = parent.Id });
        await noTeam.Should().ThrowAsync<ArgumentException>().WithMessage("Choose a team.");
        var part = await _db.Actions().CreateAsync(new ActionForm { Title = "Contain", ParentId = parent.Id, TeamId = await Team("Network Engineering") });
        (await _db.Actions().GetAsync(part))!.Action.Indicators.Select(i => i.Value).Should().Equal("bad.example");
    }

    // --- external teams -----------------------------------------------------------------------------------------

    [Fact]
    public async Task WhoeverWorksActions_KeepsTheExternalTeams_ButNotTheInternalOnes_AndEachChangeIsAudited()
    {
        var internalTeam = await Team("Cyber Response");
        As("S-1-5-21-OWNER", "Owner", AppRole.ActionOwner);
        var id = await _db.Teams().AddExternalAsync(new ExternalTeamForm
        {
            Name = "Network Engineering", ContactName = "Alex", ContactEmail = "network@example.org", Engage = "Raise a ticket in their queue.",
            RemindBeforeDays = 1, RemindOverdueEveryDays = null,
        });
        var form = new ExternalTeamForm { Name = "Network Engineering", ContactEmail = "network@example.org", RemindersOn = false };
        await _db.Teams().UpdateExternalAsync(id, form);
        var team = (await _db.Teams().ListAsync()).Single(t => t.Id == id);
        (team.IsExternal, team.RemindersOn, team.ContactName, team.RemindOverdueEveryDays).Should().Be((true, false, (string?)null, (int?)3));

        var badCadence = () => _db.Teams().UpdateExternalAsync(id, new ExternalTeamForm { Name = "X", RemindBeforeDays = 11 });
        await badCadence.Should().ThrowAsync<ArgumentException>();
        var editInternal = () => _db.Teams().UpdateExternalAsync(internalTeam, form);
        await editInternal.Should().ThrowAsync<ForbiddenException>();
        var retireInternal = () => _db.Teams().SetRetiredAsync(internalTeam, true);
        await retireInternal.Should().ThrowAsync<ForbiddenException>();
        await _db.Teams().SetRetiredAsync(id, true);   // never owned an action: deleted

        As("S-1-5-21-VIEWER", "Viewer", AppRole.Viewer);
        var viewer = () => _db.Teams().AddExternalAsync("Somebody", null);
        await viewer.Should().ThrowAsync<ForbiddenException>();

        await using var ctx = _db.Context();
        (await ctx.AuditLog.Where(e => e.EntityType == nameof(Team) && e.EntityKey == id.ToString()).Select(e => e.Action).ToListAsync())
            .Should().Contain(["Added", "Modified", "Deleted"]);
    }

    [Fact]
    public async Task AnExternalTeam_HasNoMembersOrHolders_ButIsKeptForOwningAndCounting()
    {
        var id = await _db.Teams().AddExternalAsync("Network Engineering", "network@example.org");
        var add = () => _db.Teams().AddMemberAsync(id, "S-1-5-21-SAM", "Sam");
        await add.Should().ThrowAsync<ArgumentException>().WithMessage("*no members*");
        var a = await New(id);
        await _db.Actions().AssignAsync(a.Id, null, _db.Clock.Today.AddDays(-3));
        (await _db.Actions().TeamHasNoMembersAsync(id)).Should().BeFalse("it isn't nobody's: its reminders follow it");
        var load = (await _db.Actions().WorkloadAsync()).Single(t => t.Team.Id == id);
        (load.Overdue, load.ByAssignee.Count).Should().Be((1, 0));
        (await _db.Actions().OutstandingAsync(_db.Clock.Today, TlpLevel.Red)).Rows.Single().NoOneToOwn.Should().BeFalse();
        (await _db.Actions().ListAsync(new ActionFilter(ActionView.Open, id))).Should().ContainSingle();
        (await _db.Actions().GetAsync(a.Number))!.Action.Team!.IsExternal.Should().BeTrue();
    }

    [Fact]
    public async Task TheDontRemindSwitch_IsForAnExternalTeamsActions()
    {
        var a = await New(await Team("Cyber Response"));
        var internalOne = () => _db.Actions().SetNoRemindersAsync(a.Id, true);
        await internalOne.Should().ThrowAsync<InvalidOperationException>();
        var b = await New(await _db.Teams().AddExternalAsync("Network Engineering", null));
        await _db.Actions().SetNoRemindersAsync(b.Id, true);
        (await _db.Actions().GetAsync(b.Number))!.Action.NoReminders.Should().BeTrue();
        (await History(b.Number)).Should().Contain("Turned off its reminders to the external team.");
    }

    // --- external reminders -------------------------------------------------------------------------------------

    // Mon Nov 23 2026, 14:00 UTC (the reminder time has passed), and the days after; Thanksgiving and the day after off.
    private static OrgClock At(int day, int month = 11)
    {
        var clock = new OrgClock(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(2026, month, day, 14, 0, 0, TimeSpan.Zero)));
        clock.UseHolidays(new Dictionary<DateOnly, string> { [new DateOnly(2026, 11, 26)] = "Thanksgiving", [new DateOnly(2026, 11, 27)] = "Day after" });
        return clock;
    }

    private static readonly ReminderOptions On = new() { ExternalReminders = { Enabled = true } };

    // Runs the day's scans at the reminder time, and says how many emails have gone in all.
    private async Task<int> Remind(int day, int month = 11)
    {
        await _db.Reminders(On, At(day, month)).RunDueAsync();
        return _db.Email.Sent.Count;
    }

    private async Task<ActionItem> Due(Guid team, string title, DateOnly due, Action<ActionItem>? set = null)
    {
        var a = await New(team, title);
        await using var ctx = _db.Context();
        var row = await ctx.Actions.SingleAsync(x => x.Id == a.Id);
        row.DueDate = due;
        set?.Invoke(row);
        await ctx.SaveChangesAsync();
        return row;
    }

    [Fact]
    public async Task AnExternalTeam_IsRemindedBeforeAndWhileOverdue_ByItsCadence_SkippingHolidays_AndNeverTwice()
    {
        var team = await _db.Teams().AddExternalAsync(new ExternalTeamForm
        {
            Name = "Network Engineering", ContactName = "Alex", ContactEmail = "network@example.org", RemindBeforeDays = 2, RemindOverdueEveryDays = 3,
        });
        await Due(team, "Block the domains", new DateOnly(2026, 11, 30), a => (a.Description, a.TicketUrl) = ("Add them to the block list.", "https://tickets.example.org/NET-1"));

        (await Remind(23)).Should().Be(0, "Monday: three business days ahead");
        (await Remind(24)).Should().Be(1, "Tuesday: two business days before Monday, over the holidays");
        (await Remind(25)).Should().Be(1, "marked: not again before it's due");
        (await Remind(30)).Should().Be(1, "the due day is the same reminder");

        var email = _db.Email.Sent[0];
        email.To.Should().Equal("network@example.org");
        email.Subject.Should().Be("Cyber Threat Intelligence: 1 request due soon or overdue");
        email.Html.Should().Contain("Hello Alex,").And.Contain("Block the domains").And.Contain("Add them to the block list.")
            .And.Contain("Due Mon Nov 30").And.Contain("https://tickets.example.org/NET-1").And.Contain("Please reply to this email with an update");
        email.Html.Should().NotContain("tide.example.org").And.NotContain("TIDE", "it goes to people without TIDE");
        email.Html.Should().NotMatchRegex(@"\bA-\d", "no TIDE number either");
    }

    [Fact]
    public async Task AnActionHandedToAnotherExternalTeam_RemindsThatTeam_ThoughTheFirstWasRemindedAlready()
    {
        var first = await _db.Teams().AddExternalAsync(new ExternalTeamForm { Name = "Network Engineering", ContactEmail = "network@example.org", RemindBeforeDays = 2 });
        var second = await _db.Teams().AddExternalAsync(new ExternalTeamForm { ConfirmOutside = true, Name = "Hosting Provider", ContactEmail = "noc@provider.example", RemindBeforeDays = 2 });
        var a = await Due(first, "Block the domains", new DateOnly(2026, 11, 30));

        (await Remind(24)).Should().Be(1, "Tuesday: two business days before Monday");
        await _db.Handoffs().HandOffAsync(a.Id, second, "They host the resolvers");
        (await Remind(25)).Should().Be(2, "the team it was handed to hears only through its reminders, so it's reminded too");
        _db.Email.Sent[1].To.Should().Equal("noc@provider.example");
        (await Remind(30)).Should().Be(2, "and once only");

        // A mark written without the team (before it was in them), after the last hand-off, was that team's: not sent again.
        var b = await Due(first, "Rotate the keys", new DateOnly(2026, 12, 3));
        await _db.Handoffs().HandOffAsync(b.Id, second, "They hold the keys");
        await using (var ctx = _db.Context())
        {
            ctx.NotificationMarks.Add(new NotificationMark { Kind = "external-reminder", Key = $"{b.Id:N}:2026-12-03:before", SentAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) });
            await ctx.SaveChangesAsync();
        }
        (await Remind(1, month: 12)).Should().Be(3, "the first action's overdue reminder");
        _db.Email.Sent.Should().NotContain(e => e.Html.Contains("Rotate the keys"));
    }

    [Fact]
    public async Task WhileOverdue_ItsEveryNBusinessDays_OrOnce()
    {
        var every = await _db.Teams().AddExternalAsync(new ExternalTeamForm { Name = "Network Engineering", ContactEmail = "network@example.org", RemindBeforeDays = 0, RemindOverdueEveryDays = 2 });
        var once = await _db.Teams().AddExternalAsync(new ExternalTeamForm { ConfirmOutside = true, Name = "Hosting Provider", ContactEmail = "noc@provider.example", RemindBeforeDays = 0, RemindOverdueEveryDays = null });
        var due = new DateOnly(2026, 11, 20);   // a Friday: Monday the 23rd is a business day late
        await Due(every, "Patch the edge", due);
        await Due(once, "Confirm the version", due);

        (await Remind(23)).Should().Be(2, "both: a business day late");
        (await Remind(24)).Should().Be(2, "two business days late: not yet the next");
        (await Remind(25)).Should().Be(3, "three business days late: every two");
        _db.Email.Sent[2].To.Should().Equal("network@example.org");
        _db.Email.Sent[2].Html.Should().Contain("3 business days overdue");
        (await Remind(30)).Should().Be(3, "four business days late (the holidays aren't counted): the same step");
        (await Remind(1, month: 12)).Should().Be(4, "five: the next step; the other was reminded once only");
    }

    [Fact]
    public async Task NewExternalTeams_StartWithRemindersOn_UnlessAnAdministratorSetThemToStartOff()
    {
        // By default, as before: the add form and the split dialog's quick add both start them on.
        _db.Teams().NewExternalForm().RemindersOn.Should().BeTrue();
        var before = await _db.Teams().AddExternalAsync("Network Engineering", "network@example.org");

        var startOff = new ReminderOptions { ExternalReminders = { Enabled = true, NewTeamsOn = false } };
        var teams = _db.Teams(startOff);
        teams.NewExternalForm().RemindersOn.Should().BeFalse();
        var scheduled = _db.Teams(new ReminderOptions { ExternalReminders = { NewTeamsBeforeDays = 5, NewTeamsEveryDays = 0 } }).NewExternalForm();
        (scheduled.RemindBeforeDays, scheduled.RemindOverdueEveryDays).Should().Be((5, (int?)null), "the schedule set for new teams, 0 meaning once");
        var quick = await teams.AddExternalAsync("Hosting Provider", "noc@provider.example", confirmOutside: true);
        var chosen = teams.NewExternalForm();
        (chosen.Name, chosen.ContactEmail, chosen.RemindersOn) = ("Facilities", "facilities@example.org", true);   // whoever adds it can still turn them on
        var on = await teams.AddExternalAsync(chosen);

        var all = await _db.Teams().ListAsync();
        all.Single(t => t.Id == quick).RemindersOn.Should().BeFalse();
        all.Single(t => t.Id == on).RemindersOn.Should().BeTrue();
        all.Single(t => t.Id == before).RemindersOn.Should().BeTrue("a team already added keeps its own");

        // The team that started off isn't emailed; the others are.
        var due = new DateOnly(2026, 11, 24);
        await Due(before, "Before", due);
        await Due(quick, "Started off", due);
        await Due(on, "Turned on", due);
        await _db.Reminders(startOff, At(24)).RunDueAsync();
        _db.Email.Sent.Select(e => e.To.Single()).Should().BeEquivalentTo(["network@example.org", "facilities@example.org"]);
    }

    [Fact]
    public async Task TurningATeamsRemindersOn_FromTheNote_KeepsItsSchedule_IsAudited_AndMovesItsVersion()
    {
        var id = await _db.Teams().AddExternalAsync(new ExternalTeamForm { ConfirmOutside = true, Name = "Hosting Provider", ContactEmail = "noc@provider.example", RemindersOn = false, RemindBeforeDays = 4 });
        var before = (await _db.Teams().ListAsync()).Single(t => t.Id == id);
        await using (var ctx = _db.Context())
        {
            var audits = ctx.AuditLog.Count(e => e.EntityType == nameof(Team));
            await _db.Teams().TurnRemindersOnAsync(id);
            ctx.AuditLog.Count(e => e.EntityType == nameof(Team)).Should().Be(audits + 1);
        }
        var after = (await _db.Teams().ListAsync()).Single(t => t.Id == id);
        (after.RemindersOn, after.RemindBeforeDays).Should().Be((true, 4));
        after.EditVersion.Should().NotBe(before.EditVersion, "a form opened before can't save them off again unseen");

        var internalTeam = await Team("Network");
        await FluentActions.Invoking(() => _db.Teams().TurnRemindersOnAsync(internalTeam)).Should().ThrowAsync<ArgumentException>();
        As("S-1-5-21-VIEWER", "Viewer", AppRole.Viewer);
        await FluentActions.Invoking(() => _db.Teams().TurnRemindersOnAsync(id)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task TheTeamsSwitch_AndAnActionsSwitch_TurnThemOff_AndOneEmailListsATeamsRequests()
    {
        var team = await _db.Teams().AddExternalAsync(new ExternalTeamForm { Name = "Network Engineering", ContactEmail = "network@example.org" });
        var off = await _db.Teams().AddExternalAsync(new ExternalTeamForm { ConfirmOutside = true, Name = "Hosting Provider", ContactEmail = "noc@provider.example", RemindersOn = false });
        var due = new DateOnly(2026, 11, 24);
        await Due(team, "First request", due);
        await Due(team, "Second request", due);
        await Due(team, "Not this one", due, a => a.NoReminders = true);
        await Due(off, "Team is off", due);

        (await Remind(24)).Should().Be(1);
        var email = _db.Email.Sent.Single();
        email.Subject.Should().Contain("2 requests");
        email.Html.Should().Contain("First request").And.Contain("Second request").And.NotContain("Not this one").And.NotContain("Team is off");
    }

    [Fact]
    public async Task Replies_GoToWhoeverFollowsTheWork()
    {
        var owner = await Team("Cyber Response", "cyber-response@example.org");
        await Person("S-1-5-21-ROBIN", "robin@example.org");
        await _db.Teams().AddMemberAsync(owner, "S-1-5-21-ROBIN", "Robin");
        var team = await _db.Teams().AddExternalAsync(new ExternalTeamForm { Name = "Network Engineering", ContactEmail = "network@example.org" });
        var parentNumber = await _db.Actions().CreateAsync(new ActionForm { Title = "Contain", TeamId = owner, AssigneeId = "S-1-5-21-ROBIN", AssigneeName = "Robin" });
        var parent = (await _db.Actions().GetAsync(parentNumber))!.Action;
        var made = await _db.Handoffs().SplitAsync(parent.Id, [new SplitPart(team, new DateOnly(2026, 11, 24))]);
        var handed = await Due(owner, "Handed over", new DateOnly(2026, 11, 24));
        await _db.Handoffs().HandOffAsync(handed.Id, team, "Theirs");
        _db.Email.Sent.Clear();

        (await Remind(24)).Should().Be(1);
        _db.Email.Sent.Single().ReplyTo.Should().BeEquivalentTo(["robin@example.org", "cyber-response@example.org"],
            "the part's parent's assignee, and the mailbox of the team that handed the other over");
        made.Should().ContainSingle();
    }

    [Fact]
    public async Task TheScanIsOff_UntilAnAdministratorTurnsItOn()
    {
        var team = await _db.Teams().AddExternalAsync(new ExternalTeamForm { Name = "Network Engineering", ContactEmail = "network@example.org" });
        await Due(team, "Block the domains", new DateOnly(2026, 11, 24));
        await _db.Reminders(new ReminderOptions(), At(24)).RunDueAsync();
        _db.Email.Sent.Should().BeEmpty();
        (await _db.Reminders(new ReminderOptions(), At(24)).RunAsync(ReminderKind.ExternalReminder)).Should().Be(1, "run now goes whatever the switch");
    }
}
