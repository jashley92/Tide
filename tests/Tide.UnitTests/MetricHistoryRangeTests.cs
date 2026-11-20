using AwesomeAssertions;
using Tide.Application.Metrics;

namespace Tide.UnitTests;

/// <summary>The days the metric history covers (MetricHistoryRange): its presets, and the range kept in its address.</summary>
public class MetricHistoryRangeTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);

    [Theory]
    [InlineData(30, "2026-10-22")]
    [InlineData(90, "2026-08-23")]
    [InlineData(365, "2025-11-21")]
    [InlineData(1, "2026-11-20")]
    public void APreset_CoversExactlyItsDays_CountingBothEnds(int days, string from)
    {
        var (start, end) = MetricHistoryRange.Last(days, Today);
        end.Should().Be(Today);
        start.Should().Be(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture));
        (end.DayNumber - start.DayNumber + 1).Should().Be(days);
    }

    [Fact]
    public void NoRangeInTheAddress_IsTheLast30Days()
    {
        MetricHistoryRange.Parse(null, null, Today).Should().Be(MetricHistoryRange.Last(30, Today));
    }

    [Fact]
    public void ARangeInTheAddress_IsShownAsItIs()
    {
        MetricHistoryRange.Parse("2026-09-01", "2026-09-30", Today).Should().Be((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)));
    }

    [Theory]
    [InlineData("soon", "later")]
    [InlineData("2026-13-01", "2026-02-30")]
    [InlineData("", "")]
    public void DatesThatArentDates_AreIgnored(string from, string to)
    {
        MetricHistoryRange.Parse(from, to, Today).Should().Be(MetricHistoryRange.Last(30, Today));
    }

    [Fact]
    public void OnlyAnEnd_ShowsTheDefaultSpanToIt_AndOnlyAStart_RunsToToday()
    {
        MetricHistoryRange.Parse(null, "2026-09-30", Today).Should().Be((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)));
        MetricHistoryRange.Parse("2026-09-01", null, Today).Should().Be((new DateOnly(2026, 9, 1), Today));
    }

    [Fact]
    public void TheWrongWayRound_IsPutInOrder()
    {
        MetricHistoryRange.Parse("2026-09-30", "2026-09-01", Today).Should().Be((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)));
    }

    [Fact]
    public void DaysAfterToday_AreCutToToday()
    {
        MetricHistoryRange.Parse("2026-11-01", "2027-03-01", Today).Should().Be((new DateOnly(2026, 11, 1), Today));
        MetricHistoryRange.Parse("2027-01-01", null, Today).Should().Be((Today, Today));
        MetricHistoryRange.Parse("2027-01-01", "2027-02-01", Today).Should().Be((Today, Today));
    }

    [Fact]
    public void ARangeLongerThanThePageReads_KeepsItsLatestDays()
    {
        var (from, to) = MetricHistoryRange.Parse("1990-01-01", "2026-06-30", Today);
        to.Should().Be(new DateOnly(2026, 6, 30));
        (to.DayNumber - from.DayNumber).Should().Be(MetricsService.MaxRangeDays);
    }
}
