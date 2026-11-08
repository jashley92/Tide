using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>EP-88 part C's pure rules: how the escalation ladder's steps are written and checked, the day each comes due
/// by the organization's calendar, which run when several have come, and what a step does to an action.</summary>
public class EscalationLadderTests
{
    // Mon Nov 23 2026 to Fri Dec 11, with Thanksgiving and the day after off.
    private static readonly DateOnly Mon = new(2026, 11, 23), Tue = new(2026, 11, 24), Wed = new(2026, 11, 25), Thanksgiving = new(2026, 11, 26),
        DayAfter = new(2026, 11, 27), Sat = new(2026, 11, 28), NextMon = new(2026, 11, 30), NextTue = new(2026, 12, 1),
        NextFri = new(2026, 12, 4);

    private static readonly WorkingDays Days = new(new Dictionary<DateOnly, string> { [Thanksgiving] = "Thanksgiving", [DayAfter] = "Day after" });

    private static EscalationStep Step(int offset, EscalationDoes does) => new(offset, does);

    [Fact]
    public void TheDefaults_RemindBefore_CopyTheContact_RaiseAndFlag_ThenFlagAgain()
    {
        EscalationLadder.DefaultText.Should().Be("before 2: remind; after 3: contact; after 7: raise, flag; after 14: flag");
        new EscalationOptions().Should().Match<EscalationOptions>(o => !o.Enabled && o.Scope == EscalationScope.All && o.Steps == EscalationLadder.DefaultText,
            "off until an administrator turns it on, with the defaults ready");
        EscalationLadder.Parse(EscalationLadder.DefaultText).Should().Equal(EscalationLadder.Defaults);
    }

    [Fact]
    public void Steps_AreWrittenAsWhenAndWhat_AndReadBackAsTheyreStored()
    {
        EscalationLadder.Normalize(" BEFORE 1 : Remind , contact\nDue: flag;after 0:raise ; after 60: remind").Should()
            .Be("before 1: remind, contact; due: flag; due: raise; after 60: remind", "0 days before or after is the due day");
        EscalationLadder.Parse("").Should().BeEmpty("no steps is a ladder that does nothing");
        EscalationLadder.Parse(null).Should().BeEmpty();

        var step = Step(7, EscalationDoes.RaisePriority | EscalationDoes.Flag);
        (step.Text, step.When, step.What).Should().Be(("after 7: raise, flag", "7 business days after", "Raise priority and flag escalated"));
        Step(-1, EscalationDoes.Remind).When.Should().Be("1 business day before");
        Step(0, EscalationDoes.Remind | EscalationDoes.CopyContact | EscalationDoes.Flag).What.Should().Be("Remind the team, copy the escalation contact and flag escalated");
        Step(0, EscalationDoes.Remind).When.Should().Be("On the due day");
    }

    [Theory]
    [InlineData("after 3 contact", "is a when and what it does")]
    [InlineData("soon: remind", "when is \"before N\"")]
    [InlineData("after three: remind", "0 to 60 business days")]
    [InlineData("after 61: remind", "0 to 60 business days")]
    [InlineData("after -1: remind", "0 to 60 business days")]
    [InlineData("after 3: shout", "\"shout\" isn't one of remind, contact, raise, flag")]
    [InlineData("after 3:", "Step 1 does nothing")]
    [InlineData("after 3: flag; due: remind; after 3: flag", "Steps 1 and 3 are the same")]
    [InlineData("after 1: flag; after 2: flag; after 3: flag; after 4: flag; after 5: flag; after 6: flag; after 7: flag; after 8: flag; after 9: flag", "at most 8 steps")]
    public void ABadLadder_IsRefused_SayingWhichStep(string text, string why)
    {
        var parse = () => EscalationLadder.Parse(text);
        parse.Should().Throw<ArgumentException>().WithMessage($"*{why}*");
        EscalationLadder.ParseOrNone(text).Should().BeEmpty("a saved value that no longer reads climbs nothing");
    }

    [Fact]
    public void TheSameDayWithDifferentActions_IsNotADuplicate_AndTheCheckHoldsOnBuiltSteps()
    {
        EscalationLadder.Parse("after 3: flag; after 3: remind").Should().HaveCount(2);
        var none = () => EscalationLadder.Check([Step(2, EscalationDoes.None)]);
        none.Should().Throw<ArgumentException>().WithMessage("Step 1 does nothing*");
        var far = () => EscalationLadder.Check([Step(-61, EscalationDoes.Flag)]);
        far.Should().Throw<ArgumentException>().WithMessage("*0 to 60*");
        var many = () => EscalationLadder.Check([.. Enumerable.Range(1, 9).Select(d => Step(d, EscalationDoes.Flag))]);
        many.Should().Throw<ArgumentException>().WithMessage("*at most 8*");
    }

    [Fact]
    public void AStepsDay_CountsBusinessDays_BeforeOnOrAfterTheDueDate_SkippingHolidays()
    {
        // Due Tue Nov 24: two business days before is Fri Nov 20; after it, Wed, then Mon (the holidays and the weekend skipped).
        EscalationLadder.DayOf(Step(-2, EscalationDoes.Remind), Tue, Days).Should().Be(new DateOnly(2026, 11, 20));
        EscalationLadder.DayOf(Step(0, EscalationDoes.Remind), Tue, Days).Should().Be(Tue);
        EscalationLadder.DayOf(Step(1, EscalationDoes.Flag), Tue, Days).Should().Be(Wed);
        EscalationLadder.DayOf(Step(2, EscalationDoes.Flag), Tue, Days).Should().Be(NextMon, "Thanksgiving, the day after and the weekend aren't counted");
        EscalationLadder.DayOf(Step(3, EscalationDoes.Flag), Tue, Days).Should().Be(NextTue);

        // Due on a day off: before it and on it count from the working day before; after it, from the next working day.
        EscalationLadder.DayOf(Step(0, EscalationDoes.Remind), Sat, Days).Should().Be(Wed);
        EscalationLadder.DayOf(Step(-1, EscalationDoes.Remind), Sat, Days).Should().Be(Tue);
        EscalationLadder.DayOf(Step(1, EscalationDoes.Flag), Sat, Days).Should().Be(NextMon);
    }

    [Fact]
    public void OnADay_TheLatestStepToHaveCome_Runs_AndTheEarlierOnesDont()
    {
        var steps = EscalationLadder.Defaults;
        // Due Tue Nov 24.
        EscalationLadder.Plan(steps, Tue, new DateOnly(2026, 11, 19), Days).Should().BeEmpty("three business days before: nothing yet");
        EscalationLadder.Plan(steps, Tue, new DateOnly(2026, 11, 20), Days).Should().Equal([steps[0]]);
        EscalationLadder.Plan(steps, Tue, Tue, Days).Should().Equal([steps[0]], "the reminder is still the latest on the due day");
        EscalationLadder.Plan(steps, Tue, NextTue, Days).Should().Equal([steps[1]], "three business days late, over the holidays");
        EscalationLadder.Plan(steps, Tue, new DateOnly(2026, 12, 7), Days).Should().Equal([steps[2]],
            "seven business days late: raise and flag only; turned on this late, the earlier steps aren't sent all at once");

        var together = EscalationLadder.Plan([Step(3, EscalationDoes.Flag), Step(3, EscalationDoes.Remind), Step(1, EscalationDoes.Remind)], Tue, NextTue, Days);
        together.Should().HaveCount(2, "steps that share a day run together");
    }

    [Fact]
    public void RaisingPriority_GoesUpOne_NeverAboveP1()
    {
        EscalationLadder.Raised(Priority.P4).Should().Be(Priority.P3);
        EscalationLadder.Raised(Priority.P2).Should().Be(Priority.P1);
        EscalationLadder.Raised(Priority.P1).Should().Be(Priority.P1);
    }

    [Fact]
    public void TheHistory_SaysHowLateInBusinessDays()
    {
        EscalationLadder.Lateness(Tue, new DateOnly(2026, 11, 20), Days).Should().Be("2 business days before its due date");
        EscalationLadder.Lateness(Tue, Tue, Days).Should().Be("on its due date");
        EscalationLadder.Lateness(Tue, NextMon, Days).Should().Be("2 business days late");
        EscalationLadder.Lateness(Tue, Wed, Days).Should().Be("1 business day late");
    }

    [Fact]
    public void Escalated_IsOpen_AndFlaggedForTheDueDateItHas_SoClosingOrMovingTheDateEndsIt()
    {
        var at = DateTimeOffset.UtcNow;
        ActionItem Flagged(DateOnly? due, ActionStatus status = ActionStatus.Tracking) =>
            new() { DueDate = due, Status = status, EscalatedForDue = Mon, EscalatedAtUtc = at };

        Flagged(Mon).Should().Match<ActionItem>(a => a.IsEscalated && a.EscalatedSince == at, "open, and still due then");
        Flagged(Tue).IsEscalated.Should().BeFalse("moved later");
        Flagged(new DateOnly(2026, 11, 20)).IsEscalated.Should().BeFalse("moved earlier: a new date is a new ladder");
        Flagged(null).IsEscalated.Should().BeFalse("no due date");
        var closed = Flagged(Mon, ActionStatus.Done);
        (closed.IsEscalated, closed.EscalatedSince).Should().Be((false, (DateTimeOffset?)null));
        new ActionItem { DueDate = Mon }.IsEscalated.Should().BeFalse("never flagged");
        ActionItem.EscalatedFor(ActionStatus.Open, NextFri, NextFri).Should().BeTrue();
    }

    [Fact]
    public void TheReach_IsTheLatestDueDateAStepBeforeItCanHaveCome()
    {
        ExternalCadence.LastDueInReach(Wed, 0, Days).Should().Be(Sat.AddDays(1), "the days off after today are reached from it");
        ExternalCadence.LastDueInReach(Wed, 2, Days).Should().Be(NextTue);
    }
}
