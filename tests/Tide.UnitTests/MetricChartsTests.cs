using FluentAssertions;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>Present mode's metric slides: how the groups are cut into slides, and each chart's geometry.</summary>
public class MetricChartsTests
{
    private static readonly DateOnly Day = new(2026, 11, 4);

    private static MetricTile Tile(string name, decimal? value, IReadOnlyList<decimal?>? trend = null, MetricFormat format = MetricFormat.Count,
        decimal? warn = null) =>
        new(new MetricDefinition { Name = name, Key = name.ToLowerInvariant(), Format = format, Direction = MetricDirection.LowerIsBetter, WarnThreshold = warn },
            value, null, null, MetricMath.LevelOf(new MetricDefinition { WarnThreshold = warn }, value), [])
        { Trend = trend ?? [] };

    private static MetricGroupDay Group(string name, params MetricTile[] tiles) =>
        new(new MetricGroup { Name = name, Key = name.ToLowerInvariant().Replace(' ', '-') }, tiles);

    private static MetricDay DayOf(params MetricGroupDay[] groups) => new(Day, groups);

    // --- slides ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Plan_GivesEachGroupASlide_InOrder_KeyedByTheGroup()
    {
        var plan = MetricSlides.Plan(DayOf(Group("Cases", Tile("Open", 4)), Group("Email triage", Tile("Waiting", 2), Tile("Done", 9))));

        plan.Select(s => s.Key).Should().Equal("metrics:cases", "metrics:email-triage");
        plan.Should().OnlyContain(s => s.AllZero.Count == 0);
    }

    [Fact]
    public void Plan_FoldsAnAllZeroGroup_IntoALineOnTheFirstSlide_WhereverItSits()
    {
        var plan = MetricSlides.Plan(DayOf(Group("Cases", Tile("Open", 4)), Group("Email triage", Tile("Waiting", 2)),
            Group("Vulnerabilities", Tile("Past SLA", 0), Tile("Within SLA", 0))));

        plan.Should().HaveCount(2);
        plan[0].AllZero.Select(g => g.Group.Name).Should().Equal("Vulnerabilities");
        plan[1].AllZero.Should().BeEmpty();
        MetricSlides.IndexOf(plan, "metrics:vulnerabilities").Should().Be(0, "a group with no slide of its own is where its line is");
    }

    [Fact]
    public void Plan_KeepsAGroupMissingAValue_AndGivesOneSlideWhenEveryGroupIsZero()
    {
        MetricSlides.IsAllZero(Group("Cases", Tile("Open", 0), Tile("Closed", null))).Should().BeFalse("a gap is worth seeing");
        MetricSlides.IsAllZero(Group("Empty")).Should().BeFalse();

        var plan = MetricSlides.Plan(DayOf(Group("Cases", Tile("Open", 0)), Group("Vulnerabilities", Tile("Past SLA", 0))));
        plan.Should().ContainSingle().Which.Should().Match<MetricSlide>(s => s.Group == null && s.AllZero.Count == 2 && s.Key == "metrics");
        MetricSlides.Plan(null).Should().BeEmpty();
        MetricSlides.Plan(DayOf()).Should().BeEmpty();
    }

    [Fact]
    public void IndexOf_FindsAGroupsSlide_ReadsTheOldKeyAsTheFirst_AndIgnoresOtherKeys()
    {
        var plan = MetricSlides.Plan(DayOf(Group("Cases", Tile("Open", 4)), Group("Email triage", Tile("Waiting", 2))));

        MetricSlides.IndexOf(plan, "metrics:email-triage").Should().Be(1);
        MetricSlides.IndexOf(plan, "metrics").Should().Be(0);
        MetricSlides.IndexOf(plan, "metrics:retired-since").Should().Be(0);
        MetricSlides.IndexOf(plan, "open").Should().BeNull();
        MetricSlides.IndexOf(plan, null).Should().BeNull();
        MetricSlides.IndexOf([], "metrics:cases").Should().BeNull();
    }

    // --- trend ----------------------------------------------------------------------------------------------------

    [Fact]
    public void WorkingDaysWithin_LeavesOutWeekendsAndHolidays()
    {
        var holiday = new WorkingDays(new Dictionary<DateOnly, string> { [new DateOnly(2026, 11, 3)] = "A holiday" });
        var days = MetricMath.WorkingDaysWithin(Day, 30, holiday);

        days[^1].Should().Be(Day);
        days[0].Should().Be(new DateOnly(2026, 10, 6));
        days.Should().NotContain(new DateOnly(2026, 11, 3)).And.OnlyContain(d => d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday);
        MetricMath.WorkingDaysWithin(Day, 0).Should().BeEmpty();
    }

    [Fact]
    public void Trend_BreaksALineAtAMissingDay_RatherThanDrawingItTo0()
    {
        var days = MetricMath.WorkingDaysWithin(Day, 7);   // Oct 29 to Nov 4: five working days
        var chart = MetricCharts.Trend(days, [Tile("Open", 3, [1, 2, null, 4, 3]), Tile("Closed", 1, [null, null, 5, null, 1])])!;

        chart.FirstDate.Should().Be("Oct 29");
        chart.LastDate.Should().Be("Nov 4");
        chart.Lines[0].Lines.Should().HaveCount(2, "the gap splits it in two");
        chart.Lines[0].Lines[0].Split(' ').Should().HaveCount(2);
        chart.Lines[1].Lines.Should().BeEmpty();
        chart.Lines[1].Dots.Should().HaveCount(2, "days on their own are dots");
        chart.Ticks.Select(t => t.Label).Should().Equal("5", "2.5", "0");
        chart.Lines[0].Current.Should().Be("3");
        var top = chart.Lines[1].Dots[0];
        top.X.Should().Be((MetricCharts.PlotLeft + MetricCharts.PlotRight) / 2);
        top.Y.Should().Be(MetricCharts.PlotTop, "the highest value is at the top");
    }

    [Fact]
    public void Trend_MarksTheValuesPastAThreshold()
    {
        var chart = MetricCharts.Trend(MetricMath.WorkingDaysWithin(Day, 7), [Tile("Waiting", 12, [2, 4, 11, 6, 12], warn: 10)])!;

        chart.Lines[0].Breaches.Should().HaveCount(2).And.OnlyContain(b => b.Level == MetricLevel.Warn);
    }

    [Fact]
    public void Trend_IsNothing_WithNoHistory()
    {
        var days = MetricMath.WorkingDaysWithin(Day, 7);
        MetricCharts.Trend(days, [Tile("Open", 3, [null, null, null, null, 3])]).Should().BeNull("one day isn't a trend");
        MetricCharts.Trend(days, [Tile("Open", null)]).Should().BeNull();
        MetricCharts.Trend([], [Tile("Open", 3, [3])]).Should().BeNull();
    }

    [Fact]
    public void Trend_OfFlatZeros_SitsOnTheAxis_AndMixedFormatsLabelAsNumbers()
    {
        var days = MetricMath.WorkingDaysWithin(Day, 7);
        var zeros = MetricCharts.Trend(days, [Tile("Open", 0, [0, 0, 0, 0, 0])])!;
        zeros.Lines[0].Lines.Single().Should().EndWith("," + MetricCharts.PlotBottom.ToString(System.Globalization.CultureInfo.InvariantCulture));
        zeros.Ticks.Select(t => t.Label).Should().Equal("1", "0.5", "0");

        var mixed = MetricCharts.Trend(days, [Tile("Rate", 50, [40, 50, null, null, null], MetricFormat.Percent), Tile("Hours", 2, [1, 2, null, null, null], MetricFormat.Hours)])!;
        mixed.Ticks[0].Label.Should().Be("50");
        MetricCharts.Trend(days, [Tile("Rate", 50, [40, 50, null, null, null], MetricFormat.Percent)])!.Ticks[0].Label.Should().Be("50%");
    }

    // --- share ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Share_SlicesTheTotal_InOrder_WithEachValueAndPercentInTheKey()
    {
        var donut = MetricCharts.Share([Tile("Open", 30), Tile("Waiting", 10), Tile("Closed", 0), Tile("Unknown", null)]);

        donut.Empty.Should().BeFalse();
        donut.Total.Should().Be("40");
        donut.Slices.Select(s => (s.Name, s.Value, s.Percent)).Should().Equal(("Open", "30", "75%"), ("Waiting", "10", "25%"), ("Closed", "0", "0%"), ("Unknown", "—", null));
        donut.Slices.Select(s => s.Path is not null).Should().Equal(true, true, false, false);
        donut.Slices[0].Path.Should().StartWith("M200 10 ").And.Contain(" 0 1 1 ", "three quarters is the large arc");
        donut.Slices[1].Path.Should().Contain(" 0 0 1 ");
    }

    [Fact]
    public void Share_OfOneValue_IsAWholeRing_AndOfZerosIsEmpty()
    {
        var whole = MetricCharts.Share([Tile("Open", 5), Tile("Closed", 0)]);
        whole.Slices[0].Path!.Split('M').Should().HaveCount(3, "a whole ring is drawn as two halves");
        whole.Slices[0].Percent.Should().Be("100%");

        var zeros = MetricCharts.Share([Tile("Open", 0), Tile("Closed", 0)]);
        zeros.Empty.Should().BeTrue();
        zeros.Total.Should().Be("0");
        zeros.Slices.Should().OnlyContain(s => s.Path == null && s.Percent == null);
        MetricCharts.Share([Tile("Open", -3), Tile("Closed", 3)]).Slices[0].Path.Should().BeNull("a share can't be less than nothing");
    }

    [Fact]
    public void Percent_SaysASliverIsThere()
    {
        MetricCharts.Percent(1, 1000).Should().Be("<1%");
        MetricCharts.Percent(0, 1000).Should().Be("0%");
        MetricCharts.Percent(1, 3).Should().Be("33%");
        MetricCharts.Percent(1, 0).Should().Be("0%");
    }

    // --- bars -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Bars_AreFractionsOfTheLongest_AndZerosHaveNoLength()
    {
        MetricCharts.Bars([Tile("Open", 8), Tile("Waiting", 2), Tile("Closed", null), Tile("Back", -1)])
            .Select(b => (b.Name, b.Value, b.Fraction)).Should().Equal(("Open", "8", 1d), ("Waiting", "2", .25), ("Closed", "—", 0d), ("Back", "-1", 0d));
        MetricCharts.Bars([Tile("Open", 0), Tile("Closed", 0)]).Should().OnlyContain(b => b.Fraction == 0 && b.Value == "0");
        MetricCharts.Bars([]).Should().BeEmpty();
    }
}
