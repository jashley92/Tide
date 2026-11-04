using FluentAssertions;
using Tide.Application.Metrics;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

public class MetricMathTests
{
    private static readonly DateOnly Thu = new(2026, 10, 22), Fri = new(2026, 10, 23), Sat = new(2026, 10, 24), Mon = new(2026, 10, 26);

    [Fact]
    public void PreviousWithValue_Monday_ComparesWithFriday()
    {
        var prev = MetricMath.PreviousWithValue([(Thu, 3m), (Fri, 5m), (Mon, 9m)], Mon);
        prev.Should().Be((Fri, 5m));
    }

    [Fact]
    public void PreviousWithValue_SkipsAHolidayWithNoValue_AndSaysWhichDayItUsed()
    {
        // Friday was a holiday (no brief, no value): Monday compares with Thursday.
        var cmp = MetricMath.Compare(MetricDirection.LowerIsBetter, 4m, MetricMath.PreviousWithValue([(Thu, 7m)], Mon));
        cmp!.Date.Should().Be(Thu);
        cmp.Value.Should().Be(7m);
        cmp.Delta.Should().Be(-3m);
        cmp.Trend.Should().Be(MetricTrend.Better);
    }

    [Fact]
    public void PreviousWithValue_IgnoresWeekendValues_LaterDays_AndNothingEarlier()
    {
        MetricMath.PreviousWithValue([(Sat, 1m), (Fri, 2m)], Mon).Should().Be((Fri, 2m));
        MetricMath.PreviousWithValue([(Mon, 2m)], Fri).Should().BeNull();
        MetricMath.Compare(MetricDirection.Neutral, null, (Fri, 2m)).Should().BeNull("no value today means no comparison");
    }

    [Theory]
    [InlineData(MetricDirection.HigherIsBetter, 2, MetricTrend.Better)]
    [InlineData(MetricDirection.HigherIsBetter, -2, MetricTrend.Worse)]
    [InlineData(MetricDirection.LowerIsBetter, 2, MetricTrend.Worse)]
    [InlineData(MetricDirection.LowerIsBetter, -2, MetricTrend.Better)]
    [InlineData(MetricDirection.Neutral, 2, MetricTrend.Neutral)]
    [InlineData(MetricDirection.HigherIsBetter, 0, MetricTrend.Unchanged)]
    [InlineData(MetricDirection.LowerIsBetter, 0, MetricTrend.Unchanged)]
    [InlineData(MetricDirection.Neutral, 0, MetricTrend.Unchanged)]
    public void TrendOf_FollowsTheDirection_AndZeroIsUnchanged(MetricDirection direction, int delta, MetricTrend expected) =>
        MetricMath.TrendOf(direction, delta).Should().Be(expected);

    [Theory]
    [InlineData(MetricDirection.LowerIsBetter, 4, MetricLevel.Normal)]
    [InlineData(MetricDirection.LowerIsBetter, 5, MetricLevel.Warn)]      // at the threshold trips it
    [InlineData(MetricDirection.LowerIsBetter, 10, MetricLevel.Critical)]
    [InlineData(MetricDirection.Neutral, 6, MetricLevel.Warn)]
    public void LevelOf_LowerIsBetter_TripsAtOrAbove(MetricDirection direction, int value, MetricLevel expected)
    {
        var m = new MetricDefinition { Direction = direction, WarnThreshold = 5, CriticalThreshold = 10 };
        MetricMath.LevelOf(m, value).Should().Be(expected);
    }

    [Theory]
    [InlineData(96, MetricLevel.Normal)]
    [InlineData(95, MetricLevel.Warn)]       // at or below for higher-is-better
    [InlineData(80, MetricLevel.Critical)]
    public void LevelOf_HigherIsBetter_TripsAtOrBelow(int value, MetricLevel expected)
    {
        var m = new MetricDefinition { Direction = MetricDirection.HigherIsBetter, WarnThreshold = 95, CriticalThreshold = 90 };
        MetricMath.LevelOf(m, value).Should().Be(expected);
    }

    [Fact]
    public void ReferenceLabel_IsTheWeekday_ForTheBusinessDayBefore_AndTheDateOtherwise()
    {
        MetricMath.ReferenceLabel(Fri, Mon).Should().Be("Fri", "Monday's usual comparison is the Friday before");
        MetricMath.ReferenceLabel(Thu, Fri).Should().Be("Thu");
        MetricMath.ReferenceLabel(Thu, Mon).Should().Be("Oct 22", "Friday had no value, so Thursday is named by its date");
        MetricMath.ReferenceLabel(Fri.AddDays(-7), Mon).Should().Be("Oct 16", "a Friday a week back isn't last Friday");
    }

    [Fact]
    public void LevelOf_NoValueOrNoThresholds_IsNormal()
    {
        MetricMath.LevelOf(new MetricDefinition { WarnThreshold = 1 }, null).Should().Be(MetricLevel.Normal);
        MetricMath.LevelOf(new MetricDefinition(), 1_000_000m).Should().Be(MetricLevel.Normal);
    }

    [Fact]
    public void ThresholdProblem_CriticalMustBePastWarnInTheDirection()
    {
        MetricMath.ThresholdProblem(MetricDirection.LowerIsBetter, 10, 5).Should().NotBeNull();
        MetricMath.ThresholdProblem(MetricDirection.LowerIsBetter, 5, 10).Should().BeNull();
        MetricMath.ThresholdProblem(MetricDirection.HigherIsBetter, 90, 95).Should().NotBeNull();
        MetricMath.ThresholdProblem(MetricDirection.HigherIsBetter, 95, 90).Should().BeNull();
        MetricMath.ThresholdProblem(MetricDirection.HigherIsBetter, null, 90).Should().BeNull();
    }

    [Fact]
    public void Format_WritesEachKind()
    {
        MetricMath.Format(MetricFormat.Count, 1204m).Should().Be("1,204");
        MetricMath.Format(MetricFormat.Percent, 97.5m).Should().Be("97.5%");
        MetricMath.Format(MetricFormat.Hours, 3.25m).Should().Be("3.25 h");
        MetricMath.FormatDelta(MetricFormat.Count, -3m).Should().Be("−3");
        MetricMath.FormatDelta(MetricFormat.Count, 2m).Should().Be("+2");
        MetricMath.FormatDelta(MetricFormat.Count, 0m).Should().Be("0");
    }

    [Fact]
    public void Sparklines_NeedTwoValues_AndSkipGaps()
    {
        MetricMath.SparkPoints([1m, null, null], 100, 20).Should().BeEmpty();
        MetricMath.SparkPoints([0m, null, 10m], 100, 20).Should().Be("0,20 100,0");
        MetricMath.SparkText([1m, null, 8m]).Should().Be("▁ █");
    }

    [Fact]
    public void BusinessDaysEnding_SkipsWeekends()
    {
        MetricMath.BusinessDaysEnding(Mon, 3).Should().Equal(Thu, Fri, Mon);
        MetricMath.BusinessDaysEnding(Sat, 1).Should().Equal(Fri);
    }

    [Fact]
    public void ChartRange_SpansTheValues_OrStartsAtZero_AndGivesFlatDataRoom()
    {
        MetricMath.ChartRange([3m, 9m, 5m]).Should().Be((3m, 9m));
        MetricMath.ChartRange([3m, 9m, 5m], fromZero: true).Should().Be((0m, 9m));
        MetricMath.ChartRange([-2m, 4m], fromZero: true).Should().Be((-2m, 4m));
        MetricMath.ChartRange([5m, 5m]).Should().Be((4m, 6m));
        MetricMath.ChartRange([0m, 0m], fromZero: true).Should().Be((0m, 1m));
        MetricMath.ChartRange([]).Should().Be((0m, 1m));
    }

    [Fact]
    public void ChartHeight_IsTheShareOfTheRange()
    {
        MetricMath.ChartHeight(0m, (0m, 8m)).Should().Be(0);
        MetricMath.ChartHeight(6m, (0m, 8m)).Should().Be(.75);
        MetricMath.ChartHeight(8m, (0m, 8m)).Should().Be(1);
        MetricMath.ChartHeight(3m, (3m, 3m)).Should().Be(.5);
    }
}
