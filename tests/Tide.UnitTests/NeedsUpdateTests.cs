using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Tide.Application.Actions;
using Tide.Application.Org;
using Tide.Application.Settings;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>
/// The meeting's "Needs an update" rule: an Open or In progress action quiet for two business days, or a Blocked one for
/// five (or the organization's limits), counted on its working days; and the last update said in short.
/// </summary>
public class NeedsUpdateTests
{
    // Mon 9 Nov 2026 to Fri 13 Nov; Mon 16 Nov.
    private static readonly DateOnly Mon = new(2026, 11, 9), Tue = Mon.AddDays(1), Wed = Mon.AddDays(2), Fri = Mon.AddDays(4),
        NextMon = Mon.AddDays(7), NextTue = Mon.AddDays(8);

    [Fact]
    public void QuietDays_CountTheWorkingDaysAfterTheLastUpdate_UpToTheDay()
    {
        NeedsUpdate.QuietDays(Mon, Mon, WorkingDays.Weekdays).Should().Be(0, "updated today");
        NeedsUpdate.QuietDays(Mon, Tue, WorkingDays.Weekdays).Should().Be(1);
        NeedsUpdate.QuietDays(Mon, Wed, WorkingDays.Weekdays).Should().Be(2);
        NeedsUpdate.QuietDays(Wed, Mon, WorkingDays.Weekdays).Should().Be(0, "updated after the day asked about");
    }

    [Fact]
    public void QuietDays_SkipTheWeekend()
    {
        NeedsUpdate.QuietDays(Fri, NextMon, WorkingDays.Weekdays).Should().Be(1, "Saturday and Sunday don't count");
        NeedsUpdate.QuietDays(Fri, NextTue, WorkingDays.Weekdays).Should().Be(2);
        NeedsUpdate.QuietDays(Fri.AddDays(1), NextMon, WorkingDays.Weekdays).Should().Be(1, "an update on a Saturday");
    }

    [Fact]
    public void QuietDays_SkipTheOrganizationsHolidays()
    {
        var days = new WorkingDays(new Dictionary<DateOnly, string> { [NextMon] = "A holiday" });
        NeedsUpdate.QuietDays(Fri, NextTue, days).Should().Be(1, "the Monday is a holiday");
        NeedsUpdate.IsStale(ActionStatus.Open, NeedsUpdate.QuietDays(Fri, NextTue, days), StaleLimits.Default)
            .Should().BeFalse("one business day over a long weekend isn't two");
        NeedsUpdate.IsStale(ActionStatus.Open, NeedsUpdate.QuietDays(Fri, NextTue, WorkingDays.Weekdays), StaleLimits.Default)
            .Should().BeTrue("without the holiday it is");
    }

    [Theory]
    [InlineData(ActionStatus.Open, 1, false)]
    [InlineData(ActionStatus.Open, 2, true)]
    [InlineData(ActionStatus.InProgress, 2, true)]
    [InlineData(ActionStatus.InProgress, 1, false)]
    [InlineData(ActionStatus.Blocked, 4, false)]
    [InlineData(ActionStatus.Blocked, 5, true)]
    [InlineData(ActionStatus.Tracking, 30, false)]
    [InlineData(ActionStatus.Done, 30, false)]
    [InlineData(ActionStatus.Cancelled, 30, false)]
    [InlineData(ActionStatus.NotApplicable, 30, false)]
    public void ByDefault_OpenAndInProgressAfterTwo_BlockedAfterFive_NothingElse(ActionStatus status, int quiet, bool stale) =>
        NeedsUpdate.IsStale(status, quiet, StaleLimits.Default).Should().Be(stale);

    [Fact]
    public void TheOrganizationsLimits_AreUsed()
    {
        var limits = new StaleLimits(OpenDays: 4, BlockedDays: 1);
        NeedsUpdate.IsStale(ActionStatus.Open, 3, limits).Should().BeFalse();
        NeedsUpdate.IsStale(ActionStatus.Open, 4, limits).Should().BeTrue();
        NeedsUpdate.IsStale(ActionStatus.Blocked, 1, limits).Should().BeTrue();
        NeedsUpdate.Limit(ActionStatus.Tracking, limits).Should().BeNull();
        NeedsUpdate.Limit(ActionStatus.Blocked, limits).Should().Be(1);
    }

    [Theory]
    [InlineData(null, "comment by Sam")]
    [InlineData("Status: Open → Blocked: waiting on the vendor's window.", "moved to Blocked by Sam")]
    [InlineData("Status: Blocked → In progress.", "moved to In progress by Sam")]
    [InlineData("Update from Network: patched one gateway. Status: Open → In progress.", "moved to In progress by Sam")]
    [InlineData("Ticked \"Block the relay domain\".", "checklist tick by Sam")]
    [InlineData("Unticked \"Block the relay domain\".", "checklist change by Sam")]
    [InlineData("Added \"Check the logs\" to the checklist.", "checklist change by Sam")]
    [InlineData("Changed assignee: nobody → Robin.", "holder change by Sam")]
    [InlineData("Changed assignee: Robin → nobody; due: Nov 12 → Nov 20 at the meeting.", "holder change by Sam")]
    [InlineData("Took it on.", "holder change by Sam")]
    [InlineData("Changed due: Nov 12 → Nov 20.", "due date change by Sam")]
    [InlineData("Created for Network, due Nov 20.", "raised by Sam")]
    [InlineData("Raised from the brief.", "raised by Sam")]
    [InlineData("Handed from Network to Cyber Defense: theirs.", "hand-off by Sam")]
    [InlineData("Update from Network: they're on it.", "team update by Sam")]
    [InlineData("Changed title; priority: P3 → P2.", "edit by Sam")]
    [InlineData("Ran the KQL hunt: no hits.", "edit by Sam")]
    public void TheLastUpdate_IsSaidInShort(string? line, string said) =>
        NeedsUpdate.Describe(new LastUpdate("Sam", line)).Should().Be(said);

    [Fact]
    public void TheLimits_AreWholeNumbersFrom1To30_TwoAndFiveByDefault()
    {
        foreach (var key in new[] { OperationalSettings.StaleOpenDays, OperationalSettings.StaleBlockedDays })
        {
            OperationalSettings.Normalize(key, " 3 ").Should().Be("3");
            OperationalSettings.Normalize(key, "1").Should().Be("1");
            OperationalSettings.Normalize(key, "30").Should().Be("30");
            OperationalSettings.Problem(key, "0").Should().NotBeNull();
            OperationalSettings.Problem(key, "31").Should().NotBeNull();
            OperationalSettings.Problem(key, "2.5").Should().NotBeNull();
            OperationalSettings.Problem(key, "two").Should().NotBeNull();
            Tide.Application.Operations.SettingsCatalog.InApp.Should().Contain(s => s.Key == key);
        }
        var options = new OrgOptions();
        (options.StaleOpenDays, options.StaleBlockedDays).Should().Be((2, 5));
        new OrgClock(Options.Create(options)).StaleLimits.Should().Be(StaleLimits.Default);
    }

    [Fact]
    public void AServerValueOutOfRange_IsHeldToIt() =>
        new OrgClock(Options.Create(new OrgOptions { StaleOpenDays = 0, StaleBlockedDays = 99 })).StaleLimits
            .Should().Be(new StaleLimits(1, 30));

    private static OutstandingAction Act(string title, ActionStatus status = ActionStatus.Open, Priority priority = Priority.P3, int daysLate = 0,
        int quiet = 0, bool stale = false, bool escalated = false)
    {
        var due = new DateOnly(2026, 11, 20);
        var row = new ActionRow(Guid.NewGuid(), title.Length, title, ActionKind.Other, priority, Guid.NewGuid(), "Network",
            "dev:robin", "Robin", due, status, ActionOutcome.None, null, null, null, null, 0, DateTimeOffset.UtcNow, EscalatedForDue: escalated ? due : null);
        return new OutstandingAction(row, daysLate, false) { QuietDays = quiet, Stale = stale };
    }

    [Fact]
    public void EachRow_SaysEveryReasonItIsThere()
    {
        Act("a", daysLate: 3, quiet: 4, stale: true).Reasons.Should().Equal("3 days late", "quiet 4 business days");
        Act("b", escalated: true).Reasons.Should().Equal("escalated");
        Act("c", ActionStatus.Blocked, quiet: 6, stale: true).Reasons.Should().Equal("quiet 6 business days (Blocked)");
        Act("d", daysLate: 1, quiet: 1).Reasons.Should().Equal(["1 day late"], "quiet, but not past its limit");
        Act("e", quiet: 1, stale: true).Reasons.Should().Equal("quiet 1 business day");
    }

    [Fact]
    public void TheOrder_IsEscalated_ThenP1AndP2Overdue_ThenQuietBlocked_ThenOtherOverdue_ThenQuiet()
    {
        OutstandingAction[] rows =
        [
            Act("quiet open 3", quiet: 3, stale: true), Act("quiet open 6", ActionStatus.InProgress, quiet: 6, stale: true),
            Act("overdue 2", daysLate: 2), Act("overdue 5", daysLate: 5, quiet: 9, stale: true),
            Act("blocked 5", ActionStatus.Blocked, quiet: 5, stale: true), Act("blocked 8", ActionStatus.Blocked, quiet: 8, stale: true, daysLate: 1),
            Act("p2 overdue", priority: Priority.P2, daysLate: 1), Act("p1 overdue", priority: Priority.P1, daysLate: 4),
            Act("escalated", escalated: true),
        ];
        rows.OrderBy(o => o.Pressing).ThenByDescending(o => o.Within).Select(o => o.Row.Title).Should().Equal(
            "escalated", "p1 overdue", "p2 overdue", "blocked 8", "blocked 5", "overdue 5", "overdue 2", "quiet open 6", "quiet open 3");
    }

    [Theory]
    [InlineData(ActionStatus.Open, 0, true, "Ask for an update")]
    [InlineData(ActionStatus.Open, 3, true, "Ask for an update")]
    [InlineData(ActionStatus.Open, 3, false, "Agree a new due date")]
    [InlineData(ActionStatus.Blocked, 3, true, "Ask what it's waiting on")]
    public void AnActionThatNeedsAnUpdate_IsAskedForOne_UnlessSomethingIsMorePressing(ActionStatus status, int daysLate, bool stale, string next) =>
        Act("x", status, daysLate: daysLate, quiet: 1, stale: stale).NextInTheRoom(1).Should().Be(next);
}
