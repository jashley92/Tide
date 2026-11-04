using FluentAssertions;
using Tide.Application.Rotation;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

public class RotationMathTests
{
    private static readonly DateOnly Mon = new(2026, 10, 26), Wed = new(2026, 10, 28), Fri = new(2026, 10, 30), NextMon = new(2026, 11, 2);

    [Fact]
    public void WeekStart_IsTheMonday()
    {
        RotationService.WeekStart(Wed).Should().Be(Mon);
        RotationService.WeekStart(Mon).Should().Be(Mon);
        RotationService.WeekStart(new DateOnly(2026, 11, 1)).Should().Be(Mon, "Sunday belongs to the week that started the Monday before");
    }

    [Fact]
    public void Runs_JoinConsecutiveBusinessDays_AcrossAWeekend()
    {
        RotationDay Day(DateOnly d, string id) => new(d, id, id.ToUpperInvariant(), id, id, null);
        var runs = RotationService.Runs([Day(Mon, "a"), Day(Mon.AddDays(1), "a"), Day(Wed, "b"), Day(Mon.AddDays(3), "a"), Day(Fri, "a"), Day(NextMon, "a")]);
        runs.Select(r => (r.AnalystId, r.From, r.To)).Should().Equal(("a", Mon, Mon.AddDays(1)), ("b", Wed, Wed), ("a", Mon.AddDays(3), NextMon));
    }

    [Fact]
    public void Describe_SaysWhoTakesWhat()
    {
        var cover = new RotationChange { Kind = RotationChangeKind.Cover, RequesterName = "Sam", PartnerName = "Robin", GiveFrom = Mon, GiveTo = Wed };
        RotationService.Describe(cover).Should().Be("Robin covers Sam's brief days Mon Oct 26 to Wed Oct 28.");
        var swap = new RotationChange { Kind = RotationChangeKind.Swap, RequesterName = "Sam", PartnerName = "Robin", GiveFrom = Fri, GiveTo = Fri, TakeFrom = NextMon, TakeTo = NextMon };
        RotationService.Describe(swap, brief: true).Should().Be("Sam and Robin swap Fri Oct 30 / Mon Nov 2");
    }

    [Fact]
    public void Ics_WritesAllDayEventsWithAnExclusiveEnd()
    {
        var ics = Ics.Write([(Mon, Fri, "Presenting; CTI, daily")], "TIDE brief rotation");
        ics.Should().StartWith("BEGIN:VCALENDAR\r\n").And.EndWith("END:VCALENDAR\r\n");
        ics.Should().Contain("DTSTART;VALUE=DATE:20261026\r\n").And.Contain("DTEND;VALUE=DATE:20261031\r\n");
        ics.Should().Contain("SUMMARY:Presenting\\; CTI\\, daily\r\n");
    }
}
