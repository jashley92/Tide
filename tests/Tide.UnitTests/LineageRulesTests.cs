using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Application.Org;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>EP-88's pure rules: how a part's state is told, the lineage's words, the remediation timeline's geometry,
/// and how a recorded update reads.</summary>
public class LineageRulesTests
{
    // Thu Nov 26 2026 is a holiday, so the Wednesday before has Friday as its next business day.
    private static readonly DateOnly Mon = new(2026, 11, 23), Wed = new(2026, 11, 25), Thanksgiving = new(2026, 11, 26), Fri = new(2026, 11, 27);
    private static readonly WorkingDays Days = new(new Dictionary<DateOnly, string> { [Thanksgiving] = "Thanksgiving" });

    // A part as the service reads it, its state worked out for today (Wednesday unless said).
    private static LineageNode Part(int n, ActionStatus status, DateOnly started, DateOnly? due, DateOnly? closed = null, string? from = null,
        DateOnly? today = null) =>
        new(Guid.NewGuid(), n, "Part " + n, "Network Engineering", false, status, due, closed, started,
            LineageRules.StateOf(status, due, today ?? Wed), from);

    [Fact]
    public void AState_IsClosedFirst_ThenOverdue_ThenTracking_ThenDueSoonByTheCalendar()
    {
        LineageRules.StateOf(ActionStatus.Cancelled, Mon, Wed).Should().Be(LineageState.Done);
        LineageRules.StateOf(ActionStatus.Tracking, Mon, Wed).Should().Be(LineageState.Overdue, "an overdue part is the one to chase, whoever does it");
        LineageRules.StateOf(ActionStatus.Tracking, Fri, Wed).Should().Be(LineageState.Tracking);
        LineageRules.StateOf(ActionStatus.Open, Fri, Wed, Days).Should().Be(LineageState.DueSoon, "Friday is the next business day after the holiday");
        LineageRules.StateOf(ActionStatus.Open, Fri, Wed).Should().Be(LineageState.OnTrack, "without the holiday Thursday comes first");
        LineageRules.StateOf(ActionStatus.InProgress, null, Wed).Should().Be(LineageState.OnTrack);
        LineageRules.Legend.Select(LineageRules.StateName).Should().Equal("Done or closed", "On track", "Due soon", "Overdue", "Tracking");
    }

    [Fact]
    public void APart_SaysWhenItsDue_HowLateItIs_OrWhenItClosed_AndWhoHandedIt()
    {
        LineageRules.DueText(Part(1, ActionStatus.Open, Mon, Fri), Wed).Should().Be("Due Nov 27");
        LineageRules.DueText(Part(1, ActionStatus.Open, Mon, Mon), Wed).Should().Be("2 days late");
        LineageRules.DueText(Part(1, ActionStatus.Open, Mon, Mon), Mon.AddDays(1)).Should().Be("1 day late");
        LineageRules.DueText(Part(1, ActionStatus.Open, Mon, null), Wed).Should().Be("No due date");
        LineageRules.DueText(Part(1, ActionStatus.Done, Mon, Fri, Wed), Wed).Should().Be("Closed Nov 25");
        LineageRules.DueText(Part(1, ActionStatus.Done, Mon, Fri), Wed).Should().Be("Closed");
        LineageRules.HandoffText(Part(1, ActionStatus.Open, Mon, Fri, from: "Security Operations")).Should().Be("From Security Operations → Network Engineering");
        LineageRules.HandoffText(Part(1, ActionStatus.Open, Mon, Fri)).Should().BeNull();
    }

    [Fact]
    public void TheRollup_FromThePartsThemselves_CountsAsTheGroupedOneDoes()
    {
        ActionRollup.Of([(ActionStatus.Done, Wed), (ActionStatus.Open, Mon), (ActionStatus.Tracking, Fri), (ActionStatus.NotApplicable, null)], Wed)
            .Should().Be(ActionRollup.Text(4, 2, 1, Mon)).And.Be("2 of 4 done · 1 overdue · next due Nov 23");
        ActionRollup.Of([(ActionStatus.Done, Mon)], Wed).Should().Be("1 of 1 done");
        ActionRollup.Of([], Wed).Should().BeEmpty();
    }

    [Fact]
    public void TheTimeline_IsNotDrawn_WhenNoPartHasADueDate()
    {
        RemediationTimeline.Build([Part(1, ActionStatus.Open, Mon, null)], Wed).Should().BeNull();
        RemediationTimeline.Build([], Wed).Should().BeNull();
    }

    [Fact]
    public void TheTimeline_RunsFromTheFirstStartToTheLatestDate_WithTodayInTheMiddleOfItsDay()
    {
        // Mon Nov 23 to Thu Dec 2: ten days, each 10%.
        var onTrack = Part(1, ActionStatus.Open, Mon, Mon.AddDays(9));
        var t = RemediationTimeline.Build([onTrack], Wed)!;
        (t.From, t.To).Should().Be((Mon, Mon.AddDays(9)));
        t.Today.Should().Be(25, "Wednesday is the third day, and its middle is a quarter of the way");
        var row = t.Rows.Single();
        (row.Start, row.Width, row.Over, row.ClosedAt, row.Part.State).Should().Be((0, 100, 0, (double?)null, LineageState.OnTrack));
        t.Ticks.Select(x => x.Day).Should().Equal(Mon, Mon.AddDays(2), Mon.AddDays(4), Mon.AddDays(7), Mon.AddDays(9));
        t.Ticks[0].At.Should().Be(5);
        t.Ticks.Should().HaveCount(RemediationTimeline.MaxTicks);
    }

    [Fact]
    public void AnOverduePart_RunsOnToToday_InTheDangerColour_AndALateCloseRunsToItsDay()
    {
        // Mon to Wed of the next week, plus today: ten days from Mon Nov 23 to Wed Dec 2.
        var today = Mon.AddDays(9);
        var overdue = Part(1, ActionStatus.Open, Mon, Mon.AddDays(4), today: today);
        var late = Part(2, ActionStatus.Done, Mon, Mon.AddDays(1), Mon.AddDays(3), today: today);
        var t = RemediationTimeline.Build([overdue, late], today)!;
        var (o, l) = (t.Rows[0], t.Rows[1]);
        (o.Part.State, o.Start, o.Width, o.Over).Should().Be((LineageState.Overdue, 0, 50, 50));
        o.Text.Should().Be("Started Nov 23 · due Nov 27 · 5 days late");
        (l.Part.State, l.Width, l.Over, l.ClosedAt).Should().Be((LineageState.Done, 20, 20, (double?)35));
        l.Text.Should().Be("Started Nov 23 · due Nov 24 · closed Nov 26, 2 days late");
        t.Today.Should().Be(95);
    }

    [Fact]
    public void ADoneEarlyPart_ShowsItsCloseInsideItsBar_AndOthersSayWhatTheyAre()
    {
        var early = Part(1, ActionStatus.Done, Mon, Mon.AddDays(9), Wed);
        var onTheDay = Part(2, ActionStatus.Done, Mon, Wed, Wed);
        var cancelled = Part(3, ActionStatus.Cancelled, Mon, null, Wed);
        var tracking = Part(4, ActionStatus.Tracking, Wed, Mon.AddDays(6));
        // A part raised after its due date (a split to catch up on a date already gone) starts at the due date.
        var raisedLate = Part(5, ActionStatus.Open, Wed, Mon);
        var t = RemediationTimeline.Build([early, onTheDay, cancelled, tracking, raisedLate], Wed)!;
        t.Rows[0].ClosedAt.Should().Be(25);
        t.Rows[0].Text.Should().Be("Started Nov 23 · due Dec 2 · closed Nov 25, 7 days early");
        t.Rows[1].Text.Should().Be("Started Nov 23 · due Nov 25 · closed Nov 25, on the day");
        (t.Rows[2].Width, t.Rows[2].Text).Should().Be((0, "Started Nov 23 · no due date · closed Nov 25"));
        (t.Rows[3].Part.State, t.Rows[3].Start).Should().Be((LineageState.Tracking, 20));
        (t.Rows[4].Start, t.Rows[4].Width, t.Rows[4].Over).Should().Be((0, 10, 20));
        RemediationTimeline.Words(Part(6, ActionStatus.Open, Mon, null), Wed).Should().Be("Started Nov 23 · no due date");
    }

    [Fact]
    public void ADayLongScale_HasOneTick()
    {
        var t = RemediationTimeline.Build([Part(1, ActionStatus.Open, Wed, Wed)], Wed)!;
        (t.Ticks.Single().Day, t.Ticks.Single().At, t.Today).Should().Be((Wed, 50, 50));
    }

    [Fact]
    public void AnUpdate_ReadsAsTheTeams_OnOneLine_AndQuotesTheirReply()
    {
        TeamUpdates.ReplyComment("Network Engineering", "Hi,\r\nDone.\n\nThanks ")
            .Should().Be("**Reply from Network Engineering**, recorded by email:\n\n> Hi,  \n> Done.\n>\n> Thanks");
        TeamUpdates.Line("Network Engineering", "Patched the edge.", (Mon, Fri, "their window moved"), replied: true)
            .Should().Be("Update from Network Engineering: Patched the edge. Due: Mon Nov 23 → Fri Nov 27 (their window moved). Their reply is kept as a comment.");
        TeamUpdates.Line("Network Engineering", null, (null, Fri, "first date"), replied: false)
            .Should().Be("Update from Network Engineering. Due: none → Fri Nov 27 (first date).");
    }
}
