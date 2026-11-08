using AwesomeAssertions;
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
    public void TrendDaysWithin_KeepsAHolidayWithAValue_AndTheBriefsOwnDate_ButNeverAWeekend()
    {
        var (entered, empty) = (new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 2));
        var holidays = new WorkingDays(new Dictionary<DateOnly, string> { [entered] = "A holiday", [empty] = "Another", [Day] = "The day itself" });
        var saturday = new DateOnly(2026, 10, 31);

        var days = MetricMath.TrendDaysWithin(Day, 7, [entered, saturday], holidays);

        days.Should().Equal(new DateOnly(2026, 10, 29), new DateOnly(2026, 10, 30), entered, Day);
        MetricMath.TrendDaysWithin(Day, 7, []).Should().Equal(MetricMath.WorkingDaysWithin(Day, 7), "with no holidays it's the working days");
        MetricMath.TrendDaysWithin(Day, 0, [Day]).Should().BeEmpty();
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

    // --- one trend per metric -------------------------------------------------------------------------------------

    [Fact]
    public void SmallMultiples_GivesEachMetricItsOwnScale_SoASmallOneIsntFlattened()
    {
        var days = MetricMath.WorkingDaysWithin(Day, 7);
        var chart = MetricCharts.SmallMultiples(days, [Tile("Alerts", 1200, [800, 1000, 900, 1100, 1200]), Tile("Escalated", 4, [1, 2, 3, 2, 4])])!;

        chart.Columns.Should().Be(2);
        chart.FirstDate.Should().Be("Oct 29");
        chart.LastDate.Should().Be("Nov 4");
        var (big, small) = (chart.Panels[0], chart.Panels[1]);
        big.Max.Label.Should().Be("1,200");
        big.Min.Label.Should().Be("0");
        small.Max.Label.Should().Be("4", "its own scale, not the big one's");
        small.Latest!.Y.Should().Be(MetricCharts.PanelTop, "its highest value reaches the top of its own panel");
        big.Latest!.Y.Should().Be(MetricCharts.PanelTop);
        small.Latest.X.Should().Be(MetricCharts.PanelRight);
        small.Line.Current.Should().Be("4");
        small.Line.Series.Should().Be(1, "it keeps its colour from the tiles");
    }

    [Fact]
    public void SmallMultiples_BreakAtAMissingDay_FillUnderEachRun_AndRingTheBreaches()
    {
        var days = MetricMath.WorkingDaysWithin(Day, 7);
        var panel = MetricCharts.SmallMultiples(days, [Tile("Waiting", 12, [2, 4, null, 11, 12], warn: 10)])!.Panels.Single();

        panel.Line.Lines.Should().HaveCount(2, "the missed day splits the line");
        panel.Areas.Should().HaveCount(2);
        panel.Areas[0].Split(' ').Should().HaveCount(4, "two days and the two corners on the axis")
            .And.StartWith($"{MetricCharts.PanelLeft},{MetricCharts.PanelBottom}");
        panel.Line.Breaches.Should().HaveCount(2).And.OnlyContain(b => b.Level == MetricLevel.Warn);
        panel.Latest!.Level.Should().Be(MetricLevel.Warn);
        panel.Level.Should().Be(MetricLevel.Warn);
    }

    [Fact]
    public void SmallMultiples_ShowAMetricWithoutValues_AndAreNothingWithNoHistory()
    {
        var days = MetricMath.WorkingDaysWithin(Day, 7);
        var chart = MetricCharts.SmallMultiples(days, [Tile("Open", 3, [1, 2, 3, 2, 3]), Tile("New", null), Tile("Once", 5, [null, null, 5, null, null])])!;
        chart.Panels[1].Latest.Should().BeNull();
        chart.Panels[1].Line.Lines.Should().BeEmpty();
        chart.Panels[2].Line.Dots.Should().ContainSingle("a day on its own is a dot");
        chart.Panels[2].Areas.Should().BeEmpty();

        MetricCharts.SmallMultiples(days, [Tile("Open", 3, [null, null, null, null, 3]), Tile("New", 1)]).Should().BeNull();
        MetricCharts.SmallMultiples([], [Tile("Open", 3, [3])]).Should().BeNull();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(9, 3)]
    [InlineData(10, 4)]
    [InlineData(16, 4)]
    public void GridColumns_FollowTheMetricCount(int count, int columns) => MetricCharts.GridColumns(count).Should().Be(columns);

    // --- mix over time --------------------------------------------------------------------------------------------

    [Fact]
    public void Stacked_SplitsEachDaysColumn_IntoParts_ThatAddUpToIt()
    {
        var days = MetricMath.WorkingDaysWithin(Day, 7);
        var chart = MetricCharts.Stacked(days, [Tile("Phishing", 30, [10, 20, 30, 20, 30]), Tile("Malware", 10, [10, 0, 10, 20, 10])]);

        chart.Empty.Should().BeFalse();
        chart.NoHistory.Should().BeFalse();
        chart.Columns.Should().HaveCount(5);
        chart.Ticks.Select(t => t.Label).Should().Equal("40", "20", "0");
        foreach (var c in chart.Columns)
        {
            c.Segments.Sum(s => s.Height).Should().BeApproximately(MetricCharts.PlotBottom - c.Top, 1e-9);
            (c.Segments[^1].Y).Should().BeApproximately(c.Top, 1e-9);
        }
        chart.Columns[2].Top.Should().Be(MetricCharts.PlotTop, "the biggest day reaches the top");
        chart.Columns[2].Total.Should().Be("40");
        chart.Columns[2].Segments.Select(s => s.Series).Should().Equal(0, 1);
        chart.Columns[2].Segments[0].Height.Should().BeApproximately((MetricCharts.PlotBottom - MetricCharts.PlotTop) * .75, 1e-9, "30 of 40, at the bottom");
        chart.Columns[1].Segments.Should().ContainSingle("a 0 has no part");
        chart.Columns.Select(c => c.Date).Should().Equal("Oct 29", "Oct 30", "Nov 2", "Nov 3", "Nov 4");
        chart.Columns.Should().OnlyContain(c => c.Width > 0 && c.X >= MetricCharts.PlotLeft && c.X + c.Width <= MetricCharts.PlotRight);
        chart.Key.Select(k => (k.Name, k.Value, k.Percent)).Should().Equal(("Phishing", "30", "75%"), ("Malware", "10", "25%"));
    }

    [Fact]
    public void Stacked_CountsAMissedMetricAs0_ButADayWithNoValuesIsAGap()
    {
        var days = MetricMath.WorkingDaysWithin(Day, 7);
        var chart = MetricCharts.Stacked(days, [Tile("Phishing", 30, [10, null, 30, null, 30]), Tile("Malware", 10, [10, null, null, 5, -4])]);

        chart.Columns.Select(c => c.Date).Should().Equal(["Oct 29", "Nov 2", "Nov 3", "Nov 4"], "Oct 30 had no value at all: a gap, not a 0");
        chart.Columns[1].Total.Should().Be("30", "Malware missed Nov 2, so it counts as 0 there");
        chart.Columns[2].Total.Should().Be("5");
        chart.Columns[3].Segments.Should().ContainSingle("a negative counts as 0");
    }

    [Fact]
    public void Stacked_OfZeros_IsEmpty_AndWithOneDayHasNoHistory()
    {
        var days = MetricMath.WorkingDaysWithin(Day, 7);
        var zeros = MetricCharts.Stacked(days, [Tile("Phishing", 0, [0, 0, 0, 0, 0]), Tile("Malware", 0, [0, null, 0, 0, 0])]);
        zeros.Empty.Should().BeTrue();
        zeros.Columns.Should().BeEmpty();
        zeros.Key.Should().OnlyContain(k => k.Percent == null);

        var once = MetricCharts.Stacked(days, [Tile("Phishing", 3, [null, null, null, null, 3]), Tile("Malware", 1, [null, null, null, null, 1])]);
        once.Empty.Should().BeFalse();
        once.NoHistory.Should().BeTrue();
        once.Key.Select(k => k.Percent).Should().Equal("75%", "25%");

        var today = MetricCharts.Stacked(days, [Tile("Phishing", 0, [4, 6, 0, 0, 0])]);
        today.Empty.Should().BeFalse("earlier days had something to share out");
        today.Columns.Should().HaveCount(5);
        MetricCharts.Stacked([], [Tile("Phishing", 3)]).Should().Match<StackedChart>(c => c.NoHistory && c.FirstDate == "");
    }

    // --- against threshold ----------------------------------------------------------------------------------------

    private static MetricTile Thresholded(string name, decimal? value, MetricDirection direction, decimal? warn, decimal? critical,
        MetricFormat format = MetricFormat.Count)
    {
        var metric = new MetricDefinition { Name = name, Key = name.ToLowerInvariant(), Format = format, Direction = direction, WarnThreshold = warn, CriticalThreshold = critical };
        return new MetricTile(metric, value, null, null, MetricMath.LevelOf(metric, value), []);
    }

    [Fact]
    public void Bullet_WhereLowerIsBetter_RunsNormalWarnCritical_FromZero()
    {
        var row = MetricCharts.Bullets([Thresholded("Waiting", 12, MetricDirection.LowerIsBetter, 10, 20)]).Single();

        var scale = 20 * 1.15;
        row.HasThresholds.Should().BeTrue();
        row.Level.Should().Be(MetricLevel.Warn);
        row.Value.Should().Be("12");
        row.Fraction.Should().BeApproximately(12 / scale, 1e-9);
        row.Bands.Select(b => b.Level).Should().Equal(MetricLevel.Normal, MetricLevel.Warn, MetricLevel.Critical);
        row.Bands[0].From.Should().Be(0);
        row.Bands[0].To.Should().BeApproximately(10 / scale, 1e-9);
        row.Bands[1].To.Should().BeApproximately(20 / scale, 1e-9);
        row.Bands[2].To.Should().Be(1);
        row.Edges.Select(e => (e.Level, e.Label)).Should().Equal((MetricLevel.Warn, "warning 10"), (MetricLevel.Critical, "critical 20"));
        row.Edges.Select(e => e.LabelBefore).Should().Equal([true, false], "the two labels lean apart");
    }

    [Fact]
    public void Bullet_WhereHigherIsBetter_PutsCriticalAtTheLowEnd()
    {
        var row = MetricCharts.Bullets([Thresholded("Coverage", 97, MetricDirection.HigherIsBetter, 95, 90, MetricFormat.Percent)]).Single();

        var scale = 97 * 1.15;
        row.Level.Should().Be(MetricLevel.Normal);
        row.Fraction.Should().BeApproximately(97 / scale, 1e-9, "the scale runs past today's value when it's the largest");
        row.Bands.Select(b => b.Level).Should().Equal(MetricLevel.Critical, MetricLevel.Warn, MetricLevel.Normal);
        row.Bands[0].To.Should().BeApproximately(90 / scale, 1e-9);
        row.Bands[1].To.Should().BeApproximately(95 / scale, 1e-9);
        row.Edges.Select(e => e.Label).Should().Equal("critical 90%", "warning 95%");
        row.Edges.Select(e => e.LabelBefore).Should().Equal([true, false], "however close, the two lean apart");
    }

    [Fact]
    public void Bullet_WithOneThreshold_HasTwoBands_AndWithNoneIsPlain()
    {
        var bullets = MetricCharts.Bullets([
            Thresholded("Queue", 3, MetricDirection.LowerIsBetter, 10, null),
            Thresholded("Patched", 3, MetricDirection.HigherIsBetter, null, 10),
            Thresholded("Hunts", 7, MetricDirection.Neutral, null, null),
        ]);

        bullets[0].Bands.Select(b => b.Level).Should().Equal(MetricLevel.Normal, MetricLevel.Warn);
        bullets[0].Edges.Single().LabelBefore.Should().BeFalse("one label starts at its edge");
        bullets[1].Bands.Select(b => b.Level).Should().Equal(MetricLevel.Critical, MetricLevel.Normal);
        bullets[1].Level.Should().Be(MetricLevel.Critical);
        bullets[2].HasThresholds.Should().BeFalse();
        bullets[2].Edges.Should().BeEmpty();
        bullets[2].Fraction.Should().BeApproximately(1 / 1.15, 1e-9);
        bullets.Select(b => b.Series).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void Bullet_OfZeroOrNothing_HasNoLength()
    {
        var bullets = MetricCharts.Bullets([
            Thresholded("Queue", 0, MetricDirection.LowerIsBetter, null, null),
            Thresholded("Late", null, MetricDirection.LowerIsBetter, 0, 0),
            Thresholded("Back", -2, MetricDirection.LowerIsBetter, 5, 10),
        ]);

        bullets.Should().OnlyContain(b => b.Fraction == 0);
        bullets[0].Value.Should().Be("0");
        bullets[1].Value.Should().Be("—");
        bullets[1].Bands.Should().ContainSingle().Which.Level.Should().Be(MetricLevel.Critical, "thresholds at 0 leave only the critical band");
        bullets[2].Bands.Should().HaveCount(3);
        MetricCharts.Bullets([]).Should().BeEmpty();
    }
}
