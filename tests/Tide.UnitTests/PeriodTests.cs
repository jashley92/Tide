using AwesomeAssertions;
using Tide.Application.Trends;

namespace Tide.UnitTests;

public class PeriodTests
{
    [Fact]
    public void Month_HasItsDaysKeyAndLabel()
    {
        var p = Period.Of(PeriodKind.Month, new DateOnly(2026, 2, 14));
        p.Should().Be(new Period(PeriodKind.Month, 2026, 2));
        (p.Start, p.End).Should().Be((new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));
        p.Key.Should().Be("2026-02");
        p.Label.Should().Be("Feb 2026");
    }

    [Fact]
    public void Quarter_HasItsDaysKeyAndLabel()
    {
        var p = Period.Of(PeriodKind.Quarter, new DateOnly(2026, 8, 31));
        (p.Start, p.End).Should().Be((new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30)));
        p.Key.Should().Be("2026-Q3");
        p.Label.Should().Be("Q3 2026");
        p.Contains(new DateOnly(2026, 9, 30)).Should().BeTrue();
        p.Contains(new DateOnly(2026, 10, 1)).Should().BeFalse();
    }

    [Fact]
    public void Shift_CrossesYears()
    {
        new Period(PeriodKind.Month, 2026, 1).Previous.Should().Be(new Period(PeriodKind.Month, 2025, 12));
        new Period(PeriodKind.Month, 2026, 12).Next.Should().Be(new Period(PeriodKind.Month, 2027, 1));
        new Period(PeriodKind.Quarter, 2026, 1).Shift(-5).Should().Be(new Period(PeriodKind.Quarter, 2024, 4));
        new Period(PeriodKind.Quarter, 2026, 4).Next.Should().Be(new Period(PeriodKind.Quarter, 2027, 1));
    }

    [Theory]
    [InlineData("2026-09", PeriodKind.Month, 9)]
    [InlineData("2026-Q3", PeriodKind.Quarter, 3)]
    [InlineData(" 2026-q1 ", PeriodKind.Quarter, 1)]
    public void Parse_ReadsKeys(string key, PeriodKind kind, int index) =>
        Period.Parse(key).Should().Be(new Period(kind, 2026, index));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-13")]
    [InlineData("2026-Q5")]
    [InlineData("2026-9")]
    [InlineData("26-09")]
    [InlineData("2026/09")]
    [InlineData("+026-09")]
    public void Parse_RefusesAnythingElse(string? key) => Period.Parse(key).Should().BeNull();

    [Fact]
    public void AWeek_IsAnIsoWeek_MondayToSunday_KeyedAndLabelled()
    {
        var week = Period.Of(PeriodKind.Week, new DateOnly(2026, 10, 28));
        week.Should().Be(new Period(PeriodKind.Week, 2026, 44));
        week.Start.Should().Be(new DateOnly(2026, 10, 26));
        week.End.Should().Be(new DateOnly(2026, 11, 1));
        week.Key.Should().Be("2026-W44");
        week.Label.Should().Be("Week of Oct 26");
        Period.Parse("2026-W44").Should().Be(week);
        week.Previous.Key.Should().Be("2026-W43");
        Period.Of(PeriodKind.Week, new DateOnly(2027, 1, 1)).Key.Should().Be("2026-W53", "1 Jan 2027 is a Friday, in 2026's ISO week 53");
        Period.Parse("2026-W53").Should().NotBeNull("2026 is a long ISO year");
        Period.Parse("2027-W53").Should().BeNull("2027 has 52 ISO weeks");
        Period.Parse("2026-W00").Should().BeNull();
    }
}
