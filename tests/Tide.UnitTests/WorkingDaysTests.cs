using FluentAssertions;
using Tide.Application.Actions;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Application.Rotation;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>EP-82: a holiday is a day off as a weekend is, in every rule that counts days.</summary>
public class WorkingDaysTests
{
    private static readonly DateOnly Tue = new(2026, 11, 24), Wed = new(2026, 11, 25), Thanksgiving = new(2026, 11, 26),
        DayAfter = new(2026, 11, 27), Mon = new(2026, 11, 30), NextTue = new(2026, 12, 1);

    private static readonly WorkingDays Days = new(new Dictionary<DateOnly, string>
    {
        [Thanksgiving] = "Thanksgiving", [DayAfter] = "Day after Thanksgiving",
    });

    [Fact]
    public void AHoliday_IsADayOff_AndNamed()
    {
        Days.IsWorkingDay(Thanksgiving).Should().BeFalse();
        Days.IsWorkingDay(Wed).Should().BeTrue();
        Days.HolidayOn(Thanksgiving).Should().Be("Thanksgiving");
        Days.DayOffReason(Thanksgiving).Should().Be("Thanksgiving is a holiday");
        Days.DayOffReason(new DateOnly(2026, 11, 28)).Should().Be("It's a weekend");
        Days.DayOffReason(Wed).Should().BeNull();
        WorkingDays.Weekdays.IsWorkingDay(Thanksgiving).Should().BeTrue("Monday to Friday alone knows no holidays");
    }

    [Fact]
    public void NextAndPrevious_StepOverHolidaysAndTheWeekend()
    {
        Days.Next(Wed).Should().Be(Mon);
        Days.Next(Thanksgiving, includeToday: true).Should().Be(Mon);
        Days.Previous(Mon).Should().Be(Wed);
    }

    [Fact]
    public void AnSlaTarget_CountsWorkingDays_SoADeadlineMovesPastTheHoliday()
    {
        ActionSla.DueDate(Tue, 3, Days).Should().Be(NextTue, "Wed, Mon and Tue; Thanksgiving and the day after aren't counted");
        ActionSla.DueDate(Tue, 3).Should().Be(DayAfter, "without the holidays it lands on one");
        ActionSla.BusinessDaysUntil(Wed, Mon, Days).Should().Be(1);
    }

    [Fact]
    public void DueSoon_AndThisWeek_ReadTheCalendar()
    {
        ActionSla.StateOf(ActionStatus.Open, Mon, null, Wed, Days).Should().Be(SlaState.DueSoon, "Monday is the next working day");
        ActionSla.StateOf(ActionStatus.Open, Mon, null, Wed).Should().Be(SlaState.OnTrack);
        ActionSla.EndOfBusinessWeek(Tue, Days).Should().Be(Wed, "the week's last working day is the Wednesday");
        ActionSla.UrgencyOf(ActionStatus.Open, Wed, Tue, Days).Should().Be(ActionUrgency.ThisWeek);
    }

    [Fact]
    public void Sparklines_AndTheRota_SkipHolidays()
    {
        MetricMath.BusinessDaysEnding(Mon, 3, Days).Should().Equal(Tue, Wed, Mon);
        MetricMath.ReferenceLabel(Wed, Mon, Days).Should().Be("Wed", "Wednesday is Monday's previous working day");
        RotationService.BusinessDays(Tue, Mon, Days).Should().Equal(Tue, Wed, Mon);
        var runs = RotationService.Runs([Day(Wed), Day(Mon)], Days);
        runs.Should().ContainSingle("a holiday doesn't break a run").Which.Should().Match<RotationRun>(r => r.From == Wed && r.To == Mon);
    }

    [Fact]
    public void ACalendarFile_GivesItsAllDayEvents_AndSaysWhatItSkipped()
    {
        const string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" +
            "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20261126\r\nDTEND;VALUE=DATE:20261128\r\nSUMMARY:Thanksgiving\\, and the day after\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20261225\r\nSUMMARY:Christmas \r\n Day\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nDTSTART:20270101T000000Z\r\nSUMMARY:New Year's Day\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20260704\r\nRRULE:FREQ=YEARLY\r\nSUMMARY:Independence Day\r\nEND:VEVENT\r\n" +
            "END:VCALENDAR\r\n";

        var (days, skipped) = HolidayService.ReadCalendar(ics);

        days.Should().Equal(
            (Thanksgiving, "Thanksgiving, and the day after"), (DayAfter, "Thanksgiving, and the day after"),
            (new DateOnly(2026, 12, 25), "Christmas Day"), (new DateOnly(2027, 1, 1), "New Year's Day"));
        skipped.Should().ContainSingle().Which.Should().StartWith("Independence Day: it repeats");
    }

    [Fact]
    public void AMidWeekHoliday_DoesntEndTheWeekEarly()
    {
        // Wednesday Nov 11 off: Thursday and Friday are still this week's.
        var veterans = new WorkingDays(new Dictionary<DateOnly, string> { [new DateOnly(2026, 11, 11)] = "Veterans Day" });
        var mon = new DateOnly(2026, 11, 9);

        ActionSla.EndOfBusinessWeek(mon, veterans).Should().Be(new DateOnly(2026, 11, 13));
        ActionSla.UrgencyOf(ActionStatus.Open, new DateOnly(2026, 11, 12), mon, veterans).Should().Be(ActionUrgency.ThisWeek);
        ActionSla.EndOfBusinessWeek(new DateOnly(2026, 11, 13), veterans).Should().Be(new DateOnly(2026, 11, 13));
    }

    [Fact]
    public void TheBriefDaysFile_LeavesAHolidayOut_AndKeepsAWeekendInsideARun()
    {
        RotationService.AroundHolidays(Tue, Mon, Days).Should().Equal((Tue, Wed), (Mon, Mon));
        RotationService.AroundHolidays(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), Days)
            .Should().Equal((new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23)));
    }

    [Fact]
    public void ACalendarFile_ReadsAnyLineEnding_QuotedParameters_UtcTimes_AndOnlyYearsItKeeps()
    {
        var crOnly = "BEGIN:VCALENDAR\rBEGIN:VEVENT\rDTSTART;VALUE=DATE:20261126\rSUMMARY:Thanksgiving\rEND:VEVENT\rEND:VCALENDAR\r";
        HolidayService.ReadCalendar(crOnly).Days.Should().Equal((Thanksgiving, "Thanksgiving"));

        var quoted = "BEGIN:VEVENT\nDTSTART;TZID=\"Region:East\":20261126T090000\nSUMMARY:Thanksgiving\nEND:VEVENT\n";
        HolidayService.ReadCalendar(quoted).Days.Should().Equal((Thanksgiving, "Thanksgiving"));

        // Boxing Day written as midnight in Sydney, which is 13:00 UTC the day before.
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var utc = "BEGIN:VEVENT\nDTSTART:20261225T130000Z\nSUMMARY:Boxing Day\nEND:VEVENT\n";
        HolidayService.ReadCalendar(utc, sydney).Days.Should().Equal((new DateOnly(2026, 12, 26), "Boxing Day"));

        var far = "BEGIN:VEVENT\nDTSTART;VALUE=DATE:99991231\nSUMMARY:The end\nEND:VEVENT\n";
        var read = HolidayService.ReadCalendar(far);
        read.Days.Should().BeEmpty();
        read.Skipped.Should().ContainSingle().Which.Should().Contain("9999 isn't a year");
    }

    private static RotationDay Day(DateOnly d) => new(d, "a", "Robin", "a", "Robin", null);
}
