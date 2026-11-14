using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Actions;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Application.Preferences;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.TestSupport;
using Tide.Web.Components.Pages.Admin;

namespace Tide.IntegrationTests;

/// <summary>
/// EP-88 part C in the services: the escalation ladder climbed by the reminder sweep, each step once per action and due
/// date, by the organization's calendar; what each step does (the emails and who gets them, the priority, the flag); the
/// flag on Today and in the meeting's list, and when it clears; the scope; and the teams' escalation contacts.
/// </summary>
public class EscalationTests : IDisposable
{
    private readonly TestDb _db = new();

    public EscalationTests() => As("S-1-5-21-TEST-1", "Test Analyst", AppRole.Analyst, AppRole.Admin);

    public void Dispose() => _db.Dispose();

    private void As(string id, string name, params AppRole[] roles) =>
        (_db.User.UserId, _db.User.DisplayName, _db.User.Roles) = (id, name, roles.ToHashSet());

    // Thu Nov 19 2026 to mid December, 14:00 UTC (the reminder time has passed); Thanksgiving and the day after off.
    private static OrgClock At(int day, int month = 11)
    {
        var clock = new OrgClock(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(2026, month, day, 14, 0, 0, TimeSpan.Zero)));
        clock.UseHolidays(new Dictionary<DateOnly, string> { [new DateOnly(2026, 11, 26)] = "Thanksgiving", [new DateOnly(2026, 11, 27)] = "Day after" });
        return clock;
    }

    private static readonly DateOnly Tuesday = new(2026, 11, 24);

    private static ReminderOptions Ladder(string? steps = null, EscalationScope scope = EscalationScope.All) =>
        new() { Escalation = { Enabled = true, Scope = scope, Steps = steps ?? EscalationLadder.DefaultText } };

    // Runs the day's scans at the reminder time with the ladder given (the defaults), and says how many emails have gone in all.
    private async Task<int> Climb(int day, int month = 11, ReminderOptions? options = null)
    {
        await _db.Reminders(options ?? Ladder(), At(day, month)).RunDueAsync();
        return _db.Email.Sent.Count;
    }

    private async Task<Guid> Internal(string name = "Network Engineering", string? mailbox = "network-team@example.org", string? contact = "pat.lee@example.org")
    {
        var id = await _db.Teams().AddAsync(name, null, mailbox);
        if (contact is not null) await _db.Teams().SetEscalationContactAsync(id, "Pat Lee", contact, confirmOutside: true);
        return id;
    }

    private Task<Guid> External(string name = "Network Engineering", string? contact = "duty-manager@network.example", bool remindersOn = true) =>
        _db.Teams().AddExternalAsync(new ExternalTeamForm
        {
            Name = name, ContactName = "Alex", ContactEmail = "network@network.example", RemindersOn = remindersOn, ConfirmOutside = true,
            EscalationName = contact is null ? null : "Morgan", EscalationEmail = contact,
        });

    private async Task<ActionItem> Due(Guid team, string title, DateOnly? due, Action<ActionItem>? set = null)
    {
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = title, TeamId = team, Priority = Priority.P3 });
        await using var ctx = _db.Context();
        var row = await ctx.Actions.SingleAsync(x => x.Number == number);
        row.DueDate = due;
        set?.Invoke(row);
        await ctx.SaveChangesAsync();
        return row;
    }

    private async Task<ActionItem> Read(ActionItem a)
    {
        await using var ctx = _db.Context();
        return await ctx.Actions.AsNoTracking().Include(x => x.Events).SingleAsync(x => x.Id == a.Id);
    }

    private async Task<List<string>> Escalations(ActionItem a) =>
        [.. (await Read(a)).Events.Where(e => e.ByName == ReminderService.EscalatedBy).OrderBy(e => e.AtUtc).Select(e => e.Text)];

    private async Task Person(string sid, string email)
    {
        await using var ctx = _db.Context();
        ctx.Users.Add(new AppUser { Sid = sid, DisplayName = sid, Email = email, RolesCsv = "ActionOwner", LastSeenUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();
    }

    // --- the ladder by the calendar -------------------------------------------------------------------------------

    [Fact]
    public async Task AnInternalAction_ClimbsTheDefaultLadder_ByTheCalendar_EachStepOnce()
    {
        var team = await Internal();
        var a = await Due(team, "Block the domains at the proxy", Tuesday);

        (await Climb(19)).Should().Be(0, "Thursday: three business days ahead");
        (await Climb(20)).Should().Be(1, "Friday: two business days before Tuesday");
        _db.Email.Sent[0].To.Should().Equal("network-team@example.org");
        _db.Email.Sent[0].Subject.Should().Be("TIDE: 1 action due soon", "the due-soon reminder's own template");
        (await Climb(23)).Should().Be(1, "marked: once per due date");
        (await Climb(30)).Should().Be(1, "Monday is two business days late, over the holidays: no step yet");

        (await Climb(1, month: 12)).Should().Be(2, "three business days late: the escalation contact is copied");
        var copy = _db.Email.Sent[1];
        copy.To.Should().Equal("pat.lee@example.org");
        copy.Subject.Should().Be("Cyber Threat Intelligence: 1 request to Network Engineering needing attention");
        copy.Html.Should().Contain("Hello Pat Lee,").And.Contain("You're the escalation contact for Network Engineering")
            .And.Contain("Block the domains at the proxy").And.Contain($"https://tide.example.org/actions/{a.Number}", "an internal team uses TIDE");

        (await Climb(7, month: 12)).Should().Be(2, "seven business days late: no email");
        var raised = await Read(a);
        (raised.Priority, raised.IsEscalated, raised.EscalatedForDue).Should().Be((Priority.P2, true, (DateOnly?)Tuesday));
        (await Climb(8, month: 12)).Should().Be(2);
        (await Read(a)).Priority.Should().Be(Priority.P2, "raised once, not every day");
        await Climb(16, month: 12);

        (await Escalations(a)).Should().Equal(
            "Escalated, 2 business days before its due date: reminded network-team@example.org.",
            "Escalated, 3 business days late: copied Pat Lee, the escalation contact for Network Engineering.",
            "Escalated, 7 business days late: priority P3 → P2; flagged escalated.",
            "Escalated, 14 business days late: still flagged escalated.");
        await using var ctx = _db.Context();
        (await ctx.NotificationMarks.CountAsync(m => m.Kind == "escalation")).Should().Be(4, "a mark per step");
        (await ctx.AuditLog.AnyAsync(e => e.EntityType == nameof(ActionItem) && e.EntityKey == a.Id.ToString() && e.ChangesJson!.Contains("EscalatedForDue")))
            .Should().BeTrue("the flag is audited like any change");
    }

    [Fact]
    public async Task TurnedOnLate_OnlyTheLatestStepRuns_AndANewDueDateStartsTheLadderAgain()
    {
        var team = await Internal();
        var a = await Due(team, "Patch the edge routers", Tuesday);

        (await Climb(7, month: 12)).Should().Be(0, "seven business days late: raise and flag only, not the earlier emails too");
        (await Escalations(a)).Should().ContainSingle().Which.Should().StartWith("Escalated, 7 business days late: priority P3 → P2");
        (await Climb(8, month: 12)).Should().Be(0, "the earlier steps never become the latest again, so they need no mark");
        await using (var marks = _db.Context())
            (await marks.NotificationMarks.CountAsync(m => m.Kind == "escalation")).Should().Be(1, "only the step that ran is marked");

        await using (var ctx = _db.Context())
        {
            (await ctx.Actions.SingleAsync(x => x.Id == a.Id)).DueDate = new DateOnly(2026, 12, 2);
            await ctx.SaveChangesAsync();
        }
        (await Climb(8, month: 12)).Should().Be(1, "a new due date: its own ladder, at its latest step (four business days late: copy the contact)");
        (await Read(a)).IsEscalated.Should().BeFalse("moved, even earlier: that escalation was for the old date");
        (await Climb(9, month: 12)).Should().Be(1);
        await Climb(11, month: 12);
        (await Read(a)).IsEscalated.Should().BeTrue("seven business days past the new date, it's flagged again, for it");
    }

    [Fact]
    public async Task RaisingPriority_StopsAtP1_AndAStepRepeatsAsANoOp()
    {
        var team = await Internal();
        var a = await Due(team, "Contain the loader", Tuesday, x => x.Priority = Priority.P1);
        var steps = "after 1: raise, flag; after 2: raise, flag";

        await Climb(25, options: Ladder(steps));
        await Climb(30, options: Ladder(steps));

        (await Read(a)).Priority.Should().Be(Priority.P1);
        (await Escalations(a)).Should().Equal(
            "Escalated, 1 business day late: priority already P1; flagged escalated.",
            "Escalated, 2 business days late: priority already P1; still flagged escalated.");
    }

    [Fact]
    public async Task NothingRuns_OnADayOff_OrWithTheLadderOff_OrForAClosedAction()
    {
        var team = await Internal();
        var open = await Due(team, "Still open", Tuesday);
        var closed = await Due(team, "Done already", Tuesday, x => (x.Status, x.Outcome, x.ClosedDate) = (ActionStatus.Done, ActionOutcome.Remediated, Tuesday));
        var tracking = await Due(team, "Others are doing it", Tuesday, x => x.Status = ActionStatus.Tracking);

        (await Climb(26, options: Ladder("after 1: flag"))).Should().Be(0);
        (await Read(open)).IsEscalated.Should().BeFalse("Thanksgiving is a day off: the sweep doesn't run");
        await _db.Reminders(new ReminderOptions(), At(25)).RunDueAsync();
        (await Read(open)).IsEscalated.Should().BeFalse("the ladder is off until an administrator turns it on");

        await Climb(25, options: Ladder("after 1: flag"));
        (await Read(open)).IsEscalated.Should().BeTrue();
        (await Read(tracking)).IsEscalated.Should().BeTrue("Tracking is open");
        (await Escalations(closed)).Should().BeEmpty("no step for a closed action");
        (await Read(closed)).EscalatedForDue.Should().BeNull();
    }

    [Fact]
    public async Task RunNow_ClimbsWhateverTheSwitch_AndABadServerValueClimbsNothing()
    {
        var team = await Internal();
        var a = await Due(team, "Review the firewall rule", Tuesday);
        var off = new ReminderOptions { Escalation = { Enabled = false, Steps = "after 1: flag" } };
        (await _db.Reminders(off, At(25)).RunAsync(ReminderKind.Escalation)).Should().Be(0, "no emails, but it ran");
        (await Read(a)).IsEscalated.Should().BeTrue("run now goes whatever the switch, as the other scans' do");

        var b = await Due(team, "Another", Tuesday);
        var log = new ReminderLog();
        await _db.Reminders(Ladder("after one: flag"), At(25), log).RunDueAsync();
        (await Read(b)).IsEscalated.Should().BeFalse("a hand-edited value that doesn't read climbs nothing");
        log.Last(ReminderKind.Escalation)!.Should().Match<ReminderRun>(r => r.Sent == 0 && r.Error == null
            && r.Note!.StartsWith("its steps don't read: Step 1:"), "and the run says why, rather than a silent 0 sent");
    }

    // --- remind ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Remind_GoesToTheAssignee_UnlessTheyOptedOut_AndToTheTeamsMembersWithoutAMailbox()
    {
        var team = await Internal("Endpoint Engineering", mailbox: null, contact: null);
        await Person("S-1-5-21-ROBIN", "robin@example.org");
        await Person("S-1-5-21-SAM", "sam@example.org");
        await _db.Teams().AddMemberAsync(team, "S-1-5-21-ROBIN", "Robin");
        await _db.Teams().AddMemberAsync(team, "S-1-5-21-SAM", "Sam");
        var robins = await Due(team, "Robin's", Tuesday, x => (x.AssigneeId, x.AssigneeName) = ("S-1-5-21-ROBIN", "Robin"));
        var sams = await Due(team, "Sam's", Tuesday, x => (x.AssigneeId, x.AssigneeName) = ("S-1-5-21-SAM", "Sam"));
        var nobodys = await Due(team, "Nobody's", Tuesday);
        await using (var ctx = _db.Context())
        {
            ctx.UserPreferences.Add(new UserPreference { UserId = "S-1-5-21-SAM", Name = UserPreferences.NotifyOverdue, Value = UserPreferences.Off, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await ctx.SaveChangesAsync();
        }

        await Climb(25, options: Ladder("after 1: remind"));

        var email = _db.Email.Sent.Single();
        (email.To.Single(), email.Subject).Should().Be(("robin@example.org", "TIDE: 2 overdue actions"),
            "the overdue reminder's template: Robin's own and the team's unassigned one; Sam turned overdue reminders off");
        (await Escalations(robins)).Single().Should().Be("Escalated, 1 business day late: reminded robin@example.org.");
        (await Escalations(sams)).Single().Should().Be("Escalated, 1 business day late: not reminded: they opted out.");
        (await Escalations(nobodys)).Single().Should().Be("Escalated, 1 business day late: reminded robin@example.org.", "who it went to: Sam opted out");
    }

    [Fact]
    public async Task AnExternalTeam_IsRemindedByItsOwnEmail_AndItsEscalationContactsHasNoTideLinks()
    {
        var team = await External();
        var off = await External("Hosting Provider", contact: null, remindersOn: false);
        var a = await Due(team, "Block the domains", Tuesday, x => (x.Description, x.TicketUrl) = ("Add them to the block list.", "https://tickets.example.org/NET-1"));
        var quiet = await Due(team, "Not this one", Tuesday, x => x.NoReminders = true);
        var theirs = await Due(off, "Hosting's", Tuesday);

        (await Climb(20, options: Ladder("before 2: remind; after 3: contact"))).Should().Be(1);
        var reminder = _db.Email.Sent[0];
        reminder.To.Should().Equal("network@network.example");
        reminder.Subject.Should().Be("Cyber Threat Intelligence: 1 request due soon or overdue", "the external team's own reminder");
        reminder.Html.Should().Contain("Block the domains").And.NotContain("Not this one");

        (await Climb(1, month: 12, options: Ladder("before 2: remind; after 3: contact"))).Should().Be(2);
        var copy = _db.Email.Sent[1];
        copy.To.Should().Equal("duty-manager@network.example");
        copy.Html.Should().Contain("Hello Morgan,").And.Contain("Block the domains").And.Contain("Not this one", "the contact copy isn't a reminder")
            .And.Contain("Add them to the block list.").And.Contain("https://tickets.example.org/NET-1");
        copy.Html.Should().NotContain("tide.example.org").And.NotContain("TIDE", "it goes to someone outside TIDE");
        copy.Html.Should().NotMatchRegex(@"\bA-\d", "no TIDE number either");

        (await Escalations(a)).Should().Equal(
            "Escalated, 2 business days before its due date: reminded Network Engineering at network@network.example.",
            "Escalated, 3 business days late: copied Morgan, the escalation contact for Network Engineering.");
        (await Escalations(quiet))[0].Should().EndWith("not reminded: its reminders are off.");
        (await Escalations(theirs)).Should().Equal(
            "Escalated, 2 business days before its due date: not reminded: Hosting Provider's reminders are off.",
            "Escalated, 3 business days late: no escalation contact to copy for Hosting Provider.");
    }

    [Fact]
    public async Task AClimbStoppedPartway_KeepsTheStepsWhoseEmailWent_AndDoesntEmailThemAgain()
    {
        var one = await _db.Teams().AddExternalAsync(new ExternalTeamForm { ConfirmOutside = true, Name = "Network Engineering", ContactEmail = "network@network.example" });
        var two = await _db.Teams().AddExternalAsync(new ExternalTeamForm { ConfirmOutside = true, Name = "Hosting Provider", ContactEmail = "noc@hosting.example" });
        await Due(one, "Block the domains", Tuesday);
        await Due(two, "Rotate the keys", Tuesday);
        var calls = 0;
        _db.Email.Fault = _ => ++calls == 2 ? new InvalidOperationException("the relay dropped the connection") : null;

        (await Climb(20, options: Ladder("before 2: remind"))).Should().Be(1, "the first team's reminder went; the second's send stopped the run");
        _db.Email.Fault = null;
        (await Climb(20, options: Ladder("before 2: remind"))).Should().Be(2, "the next poll sends only the one that didn't go");
        _db.Email.Sent.Select(e => e.To.Single()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task EveryEmailFailing_WaitsForTheNextPoll_ButEmailTurnedOff_StillClimbs()
    {
        var team = await Internal();
        var a = await Due(team, "Rotate the keys", Tuesday);
        _db.Email.Result = new EmailSendResult(EmailSendStatus.Failed, "relay down");

        await Climb(1, month: 12, options: Ladder("after 3: contact, raise"));
        (await Read(a)).Priority.Should().Be(Priority.P3, "nothing went, so the step waits for the next poll");
        (await Escalations(a)).Should().BeEmpty();

        _db.Email.Result = new EmailSendResult(EmailSendStatus.Disabled);
        await Climb(1, month: 12, options: Ladder("after 3: contact, raise"));
        (await Read(a)).Priority.Should().Be(Priority.P2);
        (await Escalations(a)).Single().Should().Be("Escalated, 3 business days late: priority P3 → P2; not emailed: email is off.");
    }

    // --- the scope -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheScope_LimitsTheLadder_ToASplitsParts_OrToExternalTeams()
    {
        var owner = await Internal("Cyber Response");
        var partTeam = await Internal("Identity", contact: null);
        var outside = await External();
        var top = await Due(owner, "Top-level", Tuesday);
        var parts = await _db.Handoffs().SplitAsync(top.Id, [new SplitPart(partTeam, Tuesday)]);
        var external = await Due(outside, "External's", Tuesday);
        await using (var ctx = _db.Context())
        {
            var part = await ctx.Actions.SingleAsync(x => x.Number == parts[0]);
            part.DueDate = Tuesday;
            await ctx.SaveChangesAsync();
        }
        async Task<HashSet<string>> Flagged()
        {
            await using var ctx = _db.Context();
            return [.. await ctx.Actions.Where(x => x.EscalatedForDue != null).Select(x => x.Title).ToListAsync()];
        }

        await Climb(25, options: Ladder("after 1: flag", EscalationScope.PartsOnly));
        (await Flagged()).Should().BeEquivalentTo(["Top-level – Identity"]);
        await Climb(25, options: Ladder("after 1: flag", EscalationScope.ExternalOnly));
        (await Flagged()).Should().BeEquivalentTo(["Top-level – Identity", "External's"]);
        await Climb(25, options: Ladder("after 1: flag"));
        (await Flagged()).Should().HaveCount(3, "all actions");
        external.Id.Should().NotBe(top.Id);
    }

    // --- the flag: Today, the meeting, clearing ---------------------------------------------------------------------

    private async Task Flag(ActionItem a)
    {
        await using var ctx = _db.Context();
        var row = await ctx.Actions.SingleAsync(x => x.Id == a.Id);
        (row.EscalatedForDue, row.EscalatedAtUtc) = (row.DueDate, DateTimeOffset.UtcNow);
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Today_ListsTheEscalated_ForTheTeam_TheAssignee_AndTheParentsAssignee()
    {
        var today = _db.Clock.Today;
        var network = await Internal();
        var other = await Internal("Identity", contact: null);
        await _db.Teams().AddMemberAsync(network, "S-1-5-21-NET", "Nat");
        var teams = await Due(network, "Network's", today.AddDays(-5));
        var assigned = await Due(other, "Robin's on Identity", today.AddDays(-5), x => (x.AssigneeId, x.AssigneeName) = ("S-1-5-21-ROBIN", "Robin"));
        var parent = await Due(other, "Robin's parent", today.AddDays(5), x => (x.AssigneeId, x.AssigneeName) = ("S-1-5-21-ROBIN", "Robin"));
        var part = await Due(other, "A part of it", today.AddDays(-2), x => x.ParentId = parent.Id);
        var notFlagged = await Due(network, "Overdue, not escalated", today.AddDays(-5));
        var closed = await Due(network, "Closed", today.AddDays(-5), x => x.Status = ActionStatus.Cancelled);
        foreach (var a in new[] { teams, assigned, part, closed }) await Flag(a);

        As("S-1-5-21-NET", "Nat", AppRole.ActionOwner);
        (await _db.Actions().EscalatedAsync()).Select(r => r.Title).Should().Equal(["Network's"], "their team's; not a closed one, nor one not escalated");
        (await _db.Actions().TeamQueueAsync()).Escalated.Select(r => r.Title).Should().Equal(["Network's"],
            "Today reads them with its queue, even with the ladder off while any is still flagged");
        As("S-1-5-21-ROBIN", "Robin", AppRole.ActionOwner);
        var robins = await _db.Actions().EscalatedAsync();
        robins.Select(r => r.Title).Should().Equal(["Robin's on Identity", "A part of it"], "theirs, and a part of theirs; soonest due first");
        robins.Should().OnlyContain(r => r.Escalated);
        (await _db.Actions().TeamQueueAsync(ladderOn: true)).Should().Match<TeamQueue>(q => !q.OnATeam && q.Escalated.Count == 2, "on no team, still theirs");
        notFlagged.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task TheMeeting_ListsTheEscalatedFirst_MarkedSo_EvenBeforeTheyreDue()
    {
        var today = _db.Clock.Today;
        var team = await Internal();
        var urgent = await Due(team, "Overdue P1", today.AddDays(-9), x => x.Priority = Priority.P1);
        var early = await Due(team, "Flagged before its date", today.AddDays(3));
        var late = await Due(team, "Flagged and late", today.AddDays(-1));
        await Flag(early);
        await Flag(late);

        var open = await _db.Actions().OutstandingAsync(today, TlpLevel.Red);
        open.Rows.Select(o => o.Row.Title).Should().Equal("Flagged and late", "Flagged before its date", "Overdue P1");
        open.Rows.Take(2).Should().OnlyContain(o => o.Row.Escalated);
        open.Rows.Single(o => o.Row.Title == "Flagged before its date").DaysLate.Should().Be(0);
        urgent.Priority.Should().Be(Priority.P1);
    }

    [Fact]
    public async Task Escalated_EndsWhenItCloses_OrItsDueDateMovesEitherWay_AndAnEditThatKeepsTheDateKeepsIt()
    {
        var today = _db.Clock.Today;
        var team = await Internal();
        var earlier = await Due(team, "Moved earlier", today.AddDays(-5));
        var later = await Due(team, "Moved later", today.AddDays(-5));
        var closing = await Due(team, "Closing", today.AddDays(-5));
        var updated = await Due(team, "Updated by the team", today.AddDays(-5));
        var edited = await Due(team, "Edited", today.AddDays(-5));
        foreach (var a in new[] { earlier, later, closing, updated, edited }) await Flag(a);
        (await _db.Actions().OutstandingAsync(today, TlpLevel.Red)).Rows.Should().OnlyContain(o => o.Row.Escalated);

        await _db.Actions().SetDueAsync(earlier.Id, today.AddDays(-7));
        (await Read(earlier)).IsEscalated.Should().BeFalse("a new date, even an earlier one, is a new ladder");
        await _db.Actions().SetDueAsync(later.Id, today.AddDays(10));
        (await Read(later)).IsEscalated.Should().BeFalse();
        await _db.Actions().SetStatusAsync(closing.Id, ActionStatus.Cancelled, ActionOutcome.None, null);
        (await Read(closing)).IsEscalated.Should().BeFalse();
        await _db.Actions().RecordUpdateAsync(new TeamUpdate
        {
            ActionId = updated.Id, From = ActionStatus.Open, Status = ActionStatus.Open, DueDate = today.AddDays(4), DueReason = "Change window on Thursday",
        });
        (await Read(updated)).IsEscalated.Should().BeFalse("a recorded update's new date ends it too");
        (await Read(updated)).Events.Should().NotContain(e => e.Text.StartsWith("No longer escalated"), "nothing was cleared: it's read from the date");

        var form = ActionForm.From((await _db.Actions().GetAsync(edited.Number))!.Action);
        form.Title = "Edited, same date";
        await _db.Actions().SaveAsync(form);
        (await Read(edited)).IsEscalated.Should().BeTrue("an edit that leaves the date keeps it");

        var rows = (await _db.Actions().ListAsync(new ActionFilter(ActionView.Open, team))).ToDictionary(r => r.Title);
        rows["Edited, same date"].Should().Match<ActionRow>(r => r.Escalated && r.EscalatedSince != null);
        rows["Moved earlier"].Should().Match<ActionRow>(r => !r.Escalated && r.EscalatedSince == null, "its flag is for the old date");
        (await _db.Actions().OutstandingAsync(today, TlpLevel.Red)).Rows.Where(o => o.Row.Escalated).Select(o => o.Row.Title)
            .Should().Equal("Edited, same date");
    }

    [Fact]
    public async Task TheClimbs_AreSavedTogether_AndAColleaguesSaveAtThatMoment_IsKept_AndTheStepRunsOnce()
    {
        var team = await Internal();
        var a = await Due(team, "Raced", Tuesday);
        var b = await Due(team, "Not raced", Tuesday);
        // A colleague's save lands just before the ladder's one save of both climbs (after it read them).
        var raced = false;
        _db.BeforeSave = context =>
        {
            if (raced || context.ChangeTracker.Entries<ActionItem>().Count(e => e.State == EntityState.Modified) < 2) return;
            raced = true;
            using var colleague = _db.PlainContext();
            colleague.Actions.Where(x => x.Id == a.Id)
                .ExecuteUpdate(u => u.SetProperty(x => x.Title, "Renamed by a colleague").SetProperty(x => x.EditVersion, Guid.NewGuid()));
        };

        await Climb(7, month: 12, options: Ladder("after 7: raise, flag"));

        raced.Should().BeTrue("the ladder saved both together first");
        var row = await Read(a);
        (row.Title, row.Priority, row.IsEscalated).Should().Be(("Renamed by a colleague", Priority.P2, true),
            "the whole save was lost to the clash, so it was read again: the colleague's rename stays, and the step still ran");
        (await Escalations(a)).Should().ContainSingle();
        (await Read(b)).Priority.Should().Be(Priority.P2);
        (await Escalations(b)).Should().ContainSingle("the retry saves each once");
        await using var ctx = _db.Context();
        (await ctx.NotificationMarks.CountAsync(m => m.Kind == "escalation")).Should().Be(2);
    }

    // --- the escalation contacts -----------------------------------------------------------------------------------

    [Fact]
    public async Task AnInternalTeamsContact_IsAnAdministrators_AnExternalTeamsWhoeverWorksActions_AndEachChangeIsAudited()
    {
        var inside = await Internal();
        var outside = await External("Network Engineering partner");   // a name of its own: no two teams share one
        (await _db.Teams().ListAsync()).Single(t => t.Id == inside).Should().Match<Team>(t => t.EscalationName == "Pat Lee" && t.EscalationEmail == "pat.lee@example.org");
        ExternalTeamForm.From((await _db.Teams().ListAsync()).Single(t => t.Id == outside)).EscalationEmail.Should().Be("duty-manager@network.example");

        var bad = () => _db.Teams().SetEscalationContactAsync(inside, "Pat", "not an address");
        await bad.Should().ThrowAsync<ArgumentException>().WithMessage("*isn't an email address*");
        await _db.Teams().SetEscalationContactAsync(inside, "  ", " ");
        (await _db.Teams().ListAsync()).Single(t => t.Id == inside).EscalationEmail.Should().BeNull("blank clears it");

        As("S-1-5-21-OWNER", "Owner", AppRole.ActionOwner);
        var internalOne = () => _db.Teams().SetEscalationContactAsync(inside, "Sam", "sam@example.org");
        await internalOne.Should().ThrowAsync<ForbiddenException>();
        await _db.Teams().SetEscalationContactAsync(outside, "Jo", "jo@network.example", confirmOutside: true);
        var form = ExternalTeamForm.From((await _db.Teams().ListAsync()).Single(t => t.Id == outside));
        (form.EscalationName, form.EscalationEmail).Should().Be(("Jo", "jo@network.example"));
        (form.EscalationEmail, form.ConfirmOutside) = ("kim@network.example", true);
        await _db.Teams().UpdateExternalAsync(outside, form);
        (await _db.Teams().ListAsync()).Single(t => t.Id == outside).EscalationEmail.Should().Be("kim@network.example");

        As("S-1-5-21-VIEWER", "Viewer", AppRole.Viewer);
        var viewer = () => _db.Teams().SetEscalationContactAsync(outside, "X", "x@example.org");
        await viewer.Should().ThrowAsync<ForbiddenException>();
        var gone = () => _db.Teams().SetEscalationContactAsync(Guid.NewGuid(), "X", "x@example.org");
        await gone.Should().ThrowAsync<KeyNotFoundException>();

        await using var ctx = _db.Context();
        (await ctx.AuditLog.CountAsync(e => e.EntityType == nameof(Team) && e.ChangesJson!.Contains("EscalationEmail"))).Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void TheEscalationTemplate_IsAnAdministratorsToReword_AndPreviewsWithSamples()
    {
        EmailTemplateCatalog.ById("escalation").Should().BeSameAs(EmailTemplateCatalog.Escalation);
        EmailTemplateCatalog.Problem(EmailTemplateCatalog.Escalation, EmailPart.Body, "Hello").Should().Contain("{{RequestList}}");
        var preview = _db.EmailTemplateAdmin().Preview(EmailTemplateCatalog.Escalation.Id, EmailTemplateCatalog.Escalation.DefaultSubject,
            EmailTemplateCatalog.Escalation.DefaultHeading, EmailTemplateCatalog.Escalation.DefaultBody);
        preview.Html.Should().Contain("You're the escalation contact for").And.NotContain("{{");
    }

    [Fact]
    public void ThePagesForm_EditsTheStepsInOrder_AndWritesThemAsTheSettingStoresThem()
    {
        var form = EscalationForm.From(new EscalationOptions { Enabled = true, Scope = EscalationScope.ExternalOnly });
        (form.On, form.Scope, form.Steps.Count).Should().Be((true, EscalationScope.ExternalOnly, 4));
        form.Steps[0].Should().Match<StepRow>(s => s.When == StepWhen.Before && s.Days == 2 && s.Has(EscalationDoes.Remind) && !s.Has(EscalationDoes.Flag));
        form.StepsText().Should().Be(EscalationLadder.DefaultText);

        form.Move(form.Steps[3], -1);
        form.Move(form.Steps[0], -1);   // already first: stays
        form.Move(form.Steps[3], 1);    // already last: stays
        form.StepsText().Should().Be("before 2: remind; after 3: contact; after 14: flag; after 7: raise, flag", "the order is the administrator's");
        form.Remove(form.Steps[2]);
        form.Add();
        form.Steps[^1].Should().Match<StepRow>(s => s.When == StepWhen.After && s.Days == 8 && s.Does == EscalationDoes.None);
        form.Steps[^1].Set(EscalationDoes.Flag, true);
        form.Steps[^1].Set(EscalationDoes.Remind, true);
        form.Steps[^1].Set(EscalationDoes.Remind, false);
        form.Steps[^1].When = StepWhen.Due;
        form.StepsText().Should().Be("before 2: remind; after 3: contact; after 7: raise, flag; due: flag");
        while (form.CanAdd) form.Add();
        form.Steps.Should().HaveCount(EscalationLadder.MaxSteps);
        form.Add();
        form.Steps.Should().HaveCount(EscalationLadder.MaxSteps, "no more than the most");

        form.UseDefaults();
        form.StepsText().Should().Be(EscalationLadder.DefaultText);
        form.Steps[0].Days = -1;
        var negative = () => form.StepsText();
        negative.Should().Throw<ArgumentException>().WithMessage("Step 1:*");
        EscalationForm.From(new EscalationOptions { Steps = "nonsense" }).Steps.Should().BeEmpty();
        EscalationForm.Scopes.Select(s => s.Scope).Should().Equal(Enum.GetValues<EscalationScope>());
    }
}
