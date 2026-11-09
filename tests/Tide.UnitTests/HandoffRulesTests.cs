using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>EP-86's pure rules: the active limit's modes, a parent's rollup, a split part's title, the Tracking status,
/// and an external team's reminder cadence and Reply-To.</summary>
public class HandoffRulesTests
{
    // Mon Nov 23 2026 to Fri Dec 4, with Thanksgiving and the day after off.
    private static readonly DateOnly Mon = new(2026, 11, 23), Tue = new(2026, 11, 24), Wed = new(2026, 11, 25), Thanksgiving = new(2026, 11, 26),
        DayAfter = new(2026, 11, 27), Sat = new(2026, 11, 28), NextMon = new(2026, 11, 30), NextTue = new(2026, 12, 1), NextWed = new(2026, 12, 2),
        NextThu = new(2026, 12, 3), NextFri = new(2026, 12, 4);

    private static readonly WorkingDays Days = new(new Dictionary<DateOnly, string> { [Thanksgiving] = "Thanksgiving", [DayAfter] = "Day after" });

    [Fact]
    public void AnExternalTeamThatWontBeEmailed_SaysWhy_AndOneThatWill_SaysNothing()
    {
        Team Ext(bool on = true, string? email = "noc@provider.example") => new() { Name = "Hosting", IsExternal = true, RemindersOn = on, ContactEmail = email };
        ExternalCadence.NotEmailedWhy(Ext(), scanOn: true).Should().BeNull();
        ExternalCadence.NotEmailedWhy(new Team { Name = "Network" }, scanOn: false).Should().BeNull("an internal team isn't emailed this way at all");
        ExternalCadence.NotEmailedWhy(Ext(), scanOn: false).Should().Be("External team reminders are off for everyone, so Hosting won't be emailed about this.");
        ExternalCadence.NotEmailedWhy(Ext(on: false), scanOn: true).Should().Be("Hosting's reminders are off, so they won't be emailed about this.");
        ExternalCadence.NotEmailedWhy(Ext(email: " "), scanOn: true).Should().Be("Hosting has no contact email, so they won't be emailed about this.");
    }

    [Fact]
    public void ANewExternalTeamsSchedule_IsKeptInItsRange_AndZeroIsOnce()
    {
        new ReminderOptions.ExternalReminderOptions().Should().Match<ReminderOptions.ExternalReminderOptions>(o => o.BeforeDays == 2 && o.EveryDays == 3);
        new ReminderOptions.ExternalReminderOptions { NewTeamsBeforeDays = 40, NewTeamsEveryDays = 0 }.Should()
            .Match<ReminderOptions.ExternalReminderOptions>(o => o.BeforeDays == Team.MaxRemindBefore && o.EveryDays == null);
        new ReminderOptions.ExternalReminderOptions { NewTeamsBeforeDays = -1, NewTeamsEveryDays = 99 }.Should()
            .Match<ReminderOptions.ExternalReminderOptions>(o => o.BeforeDays == 0 && o.EveryDays == Team.MaxRemindEvery);
        Tide.Application.Settings.OperationalSettings.Normalize(Tide.Application.Settings.OperationalSettings.ExternalRemindersNewEvery, "0").Should().Be("0");
        Tide.Application.Settings.OperationalSettings.Problem(Tide.Application.Settings.OperationalSettings.ExternalRemindersNewEvery, "21").Should().NotBeNull();
        Tide.Application.Settings.OperationalSettings.Problem(Tide.Application.Settings.OperationalSettings.ExternalRemindersNewBefore, "11").Should().NotBeNull();
    }

    [Fact]
    public void TheLimit_ShowsOnly_WarnsForAReason_OrRefuses()
    {
        ActiveLimits.Decide("Network Engineering", 5, 5, ActiveLimitMode.Show, null).Should().BeNull("show only counts");
        ActiveLimits.Decide("Network Engineering", 4, 5, ActiveLimitMode.Refuse, null).Should().BeNull("reaching the limit isn't going over it");

        var refused = () => ActiveLimits.Decide("Network Engineering", 5, 5, ActiveLimitMode.Refuse, "please");
        refused.Should().Throw<ActiveLimitException>().Where(e => !e.NeedsReason && e.Message.Contains("5 of 5 active"));

        var asked = () => ActiveLimits.Decide("Network Engineering", 5, 5, ActiveLimitMode.Warn, "  ");
        asked.Should().Throw<ActiveLimitException>().Where(e => e.NeedsReason && e.Active == 5 && e.Limit == 5 && e.Team == "Network Engineering");

        ActiveLimits.Decide("Network Engineering", 6, 5, ActiveLimitMode.Warn, "Outage cover")
            .Should().Be("Over Network Engineering's limit (7 of 5 active): Outage cover");
        ActiveLimits.Text(4, 5).Should().Be("4 of 5 active");
    }

    [Fact]
    public void AChange_AddsToTheReceivingTeam_OnlyIntoAnActiveStatus_OrIntoAnotherTeamWhileActive()
    {
        Guid ours = Guid.NewGuid(), theirs = Guid.NewGuid();
        ActiveLimits.Adds((null, null), (ours, ActionStatus.Open)).Should().BeTrue("a new action");
        ActiveLimits.Adds((ours, ActionStatus.Open), (ours, ActionStatus.InProgress)).Should().BeFalse("Take: it was active already");
        ActiveLimits.Adds((ours, ActionStatus.Tracking), (ours, ActionStatus.Blocked)).Should().BeTrue("back from Tracking");
        ActiveLimits.Adds((ours, ActionStatus.Done), (ours, ActionStatus.Open)).Should().BeTrue("reopened");
        ActiveLimits.Adds((ours, ActionStatus.InProgress), (ours, ActionStatus.Tracking)).Should().BeFalse("into Tracking");
        ActiveLimits.Adds((ours, ActionStatus.InProgress), (theirs, ActionStatus.InProgress)).Should().BeTrue("handed on while active");
        ActiveLimits.Adds((ours, ActionStatus.Tracking), (theirs, ActionStatus.Tracking)).Should().BeFalse("handed on while tracked");
    }

    [Fact]
    public void Tracking_IsOpen_ButNotActive_AndSitsBetweenBlockedAndDone()
    {
        new ActionItem { Status = ActionStatus.Tracking }.IsOpen.Should().BeTrue();
        ActionItem.IsClosedStatus(ActionStatus.Tracking).Should().BeFalse();
        ActionItem.IsActiveStatus(ActionStatus.Tracking).Should().BeFalse();
        ActionItem.IsActiveStatus(ActionStatus.Blocked).Should().BeTrue();
        ((int)ActionStatus.Tracking).Should().Be(6, "stored as a number: appended, never renumbered");
        ActionItem.StatusOrder.Should().Equal(ActionStatus.Open, ActionStatus.InProgress, ActionStatus.Blocked, ActionStatus.Tracking,
            ActionStatus.Done, ActionStatus.Cancelled, ActionStatus.NotApplicable);
        ActionItem.StatusOrder.Should().BeEquivalentTo(Enum.GetValues<ActionStatus>(), "every status has its place");
        ActionSla.StateOf(ActionStatus.Tracking, Mon, null, Tue).Should().Be(SlaState.Overdue, "its due date still applies");
        ActionService.StatusName(ActionStatus.Tracking).Should().Be("Tracking");
    }

    [Fact]
    public void TheRollup_SaysWhatsDone_Overdue_AndNextDue()
    {
        ActionRollup.Text(5, 3, 1, new DateOnly(2026, 11, 20)).Should().Be("3 of 5 done · 1 overdue · next due Nov 20");
        ActionRollup.Text(2, 0, 0, null).Should().Be("0 of 2 done");
        ActionRollup.Text(2, 2, 0, new DateOnly(2026, 11, 20)).Should().Be("2 of 2 done", "nothing is next once all are done");
        ActionRollup.Text(0, 0, 0, null).Should().BeEmpty();
        new ActionRow(Guid.NewGuid(), 1, "Parent", ActionKind.Other, Priority.P3, Guid.NewGuid(), "Team", null, null, null, ActionStatus.Open,
            ActionOutcome.None, null, null, null, null, 4, DateTimeOffset.UtcNow, ChildrenClosed: 1).ChildrenOpen.Should().Be(3);
    }

    [Fact]
    public void APartsDueDate_LaterThanItsParent_IsWarned()
    {
        ActionRollup.LaterThanParent(Tue, Wed).Should().BeTrue();
        ActionRollup.LaterThanParent(Tue, Tue).Should().BeFalse();
        ActionRollup.LaterThanParent(null, Wed).Should().BeFalse();
        ActionRollup.LaterThanParent(Tue, null).Should().BeFalse();
        ActionRollup.LaterText(Tue).Should().Contain("Tue Nov 24");
    }

    [Fact]
    public void APartsTitle_IsTheParentsWithTheTeam_CutToFit()
    {
        ActionRollup.PartTitle("Patch the VPN", "Network Engineering").Should().Be("Patch the VPN – Network Engineering");
        var cut = ActionRollup.PartTitle(new string('x', 299), "Network Engineering");
        cut.Should().HaveLength(ActionItem.MaxTitle).And.EndWith("… – Network Engineering");
    }

    [Fact]
    public void BeforeTheDueDate_RemindsFromNBusinessDaysAhead_SkippingHolidays()
    {
        // Due Mon Nov 30, two business days before: Tue Nov 24 (Thursday and Friday are holidays).
        ExternalCadence.WindowStart(NextMon, 2, Days).Should().Be(Tue);
        ExternalCadence.Step(Mon, NextMon, 2, 3, Days).Should().BeNull();
        ExternalCadence.Step(Tue, NextMon, 2, 3, Days).Should().Be(ExternalCadence.Before);
        ExternalCadence.Step(NextMon, NextMon, 2, 3, Days).Should().Be(ExternalCadence.Before, "the due day is still before it's late");

        ExternalCadence.Step(Wed, NextMon, 0, 3, Days).Should().BeNull("0: on the due day only");
        ExternalCadence.Step(NextMon, NextMon, 0, 3, Days).Should().Be(ExternalCadence.Before);
        ExternalCadence.WindowStart(Sat, 0, Days).Should().Be(Wed, "a due date on a day off is reminded the working day before");
    }

    [Fact]
    public void WhileOverdue_RemindsEveryNBusinessDays_OrOnce()
    {
        // Due Tue Nov 24: Wed is 1 business day late; then Mon 2, Tue 3, Wed 4.
        ExternalCadence.Step(Wed, Tue, 2, 3, Days).Should().Be("late-0");
        ExternalCadence.Step(NextMon, Tue, 2, 3, Days).Should().Be("late-0", "the holidays aren't counted");
        ExternalCadence.Step(NextTue, Tue, 2, 3, Days).Should().Be("late-0");
        ExternalCadence.Step(NextWed, Tue, 2, 3, Days).Should().Be("late-1", "every three business days a new reminder");
        ExternalCadence.Step(NextFri, Tue, 2, 1, Days).Should().Be("late-5");
        ExternalCadence.Step(NextThu, Tue, 2, null, Days).Should().Be("late-0", "once only");
        ExternalCadence.Step(Sat, DayAfter, 2, 3, Days).Should().BeNull("past a due date on a day off, but not a working day late yet");
    }

    [Fact]
    public void ReplyTo_IsTheParentsAssignee_ThenItsTeam_OrForATopLevelAction_ItsOwn_TheHandingTeam_ThenItsCreator()
    {
        var parentTeam = Guid.NewGuid();
        var handing = Guid.NewGuid();
        var people = new Dictionary<string, string> { ["robin"] = "robin@example.org", ["sam"] = "sam@example.org", ["creator"] = "creator@example.org" };
        var mailboxes = new Dictionary<Guid, string> { [parentTeam] = "vm@example.org", [handing] = "cd@example.org" };
        string? Person(string? id) => id is not null && people.TryGetValue(id, out var e) ? e : null;
        string? Mailbox(Guid? id) => id is { } t && mailboxes.TryGetValue(t, out var e) ? e : null;
        var a = new ActionItem { AssigneeId = "sam", HandedFromTeamId = handing, CreatedBy = "creator" };

        ExternalCadence.ReplyTo(a, ("robin", parentTeam), Person, Mailbox).Should().Be("robin@example.org");
        ExternalCadence.ReplyTo(a, (null, parentTeam), Person, Mailbox).Should().Be("vm@example.org");
        ExternalCadence.ReplyTo(a, null, Person, Mailbox).Should().Be("sam@example.org");
        ExternalCadence.ReplyTo(new ActionItem { HandedFromTeamId = handing, CreatedBy = "creator" }, null, Person, Mailbox).Should().Be("cd@example.org");
        ExternalCadence.ReplyTo(new ActionItem { CreatedBy = "creator" }, null, Person, Mailbox).Should().Be("creator@example.org");
        ExternalCadence.ReplyTo(new ActionItem { CreatedBy = "nobody" }, null, Person, Mailbox).Should().BeNull();
    }
}
