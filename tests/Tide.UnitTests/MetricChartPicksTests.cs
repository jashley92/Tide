using AwesomeAssertions;
using Tide.Application.Metrics;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>Which metrics the metric history page charts (MetricChartPicks), kept in its address, how it draws them, and
/// the threshold bands it shades (MetricBands, which the bullet bars draw too).</summary>
public class MetricChartPicksTests
{
    private static readonly MetricGroup Email = new() { Id = Guid.NewGuid(), Key = "email", Name = "Email triage" };
    private static readonly MetricGroup Cases = new() { Id = Guid.NewGuid(), Key = "cases", Name = "Cases" };

    private static MetricDefinition M(MetricGroup g, string key, MetricFormat format = MetricFormat.Count, bool retired = false) =>
        new() { Id = Guid.NewGuid(), GroupId = g.Id, Group = g, Key = key, Name = key, Format = format, IsRetired = retired };

    // In the history's order: by group, then metric.
    private static readonly MetricDefinition Reported = M(Email, "reported"), Clicked = M(Email, "clicked"), OldRule = M(Email, "old-rule", retired: true),
        Opened = M(Cases, "opened"), Mttr = M(Cases, "mttr", MetricFormat.Hours);
    private static readonly IReadOnlyList<MetricDefinition> All = [Reported, Clicked, OldRule, Opened, Mttr];

    [Fact]
    public void AGroupInTheLink_StandsForItsCurrentMetrics_AndAMetricForItself_InTheHistorysOrder()
    {
        MetricChartPicks.Resolve(All, "mttr,group:email").Should().Equal(Reported, Clicked, Mttr);
        MetricChartPicks.Resolve(All, "old-rule").Should().Equal(OldRule);   // a retired metric is charted when named
    }

    [Fact]
    public void ALinkNamingNothingTheHistoryHas_ChartsTheFirstCurrentMetric()
    {
        foreach (var query in new[] { null, "", "gone,group:gone" })
            MetricChartPicks.Resolve(All, query).Should().Equal(Reported);
        MetricChartPicks.Resolve([OldRule], null).Should().Equal(OldRule);
        MetricChartPicks.Resolve([], "x").Should().BeEmpty();
    }

    [Fact]
    public void TheLink_NamesAWholeGroupAsTheGroup_AndAnythingElseByMetric()
    {
        MetricChartPicks.Query(All, [Reported, Clicked, Mttr]).Should().Be("group:email,mttr");
        MetricChartPicks.Query(All, [Reported, Clicked, OldRule]).Should().Be("group:email,old-rule");
        MetricChartPicks.Query(All, [Clicked, Opened]).Should().Be("clicked,opened");
        MetricChartPicks.Query(All, []).Should().BeNull();
        MetricChartPicks.Resolve(All, MetricChartPicks.Query(All, [Reported, Clicked, OldRule, Mttr])).Should().Equal(Reported, Clicked, OldRule, Mttr);   // the link reads back as what was picked
    }

    [Fact]
    public void AGroupsBox_AddsItsCurrentMetrics_OrWhenAllAreCharted_TakesTheGroupOff()
    {
        MetricChartPicks.ToggleGroup(All, [Clicked, Mttr], Email.Id).Should().Equal(Reported, Clicked, Mttr);
        MetricChartPicks.ToggleGroup(All, [Reported, Clicked, OldRule, Mttr], Email.Id).Should().Equal(Mttr);
        MetricChartPicks.Toggle(All, [Reported, Mttr], Opened).Should().Equal(Reported, Opened, Mttr);
        MetricChartPicks.Toggle(All, [Reported, Mttr], Reported).Should().Equal(Mttr);
    }

    [Fact]
    public void AGroupsBox_ReadsTicked_Mixed_OrClear()
    {
        var email = All.Where(m => m.GroupId == Email.Id).ToList();
        MetricChartPicks.GroupState(email, [Reported, Clicked]).Should().BeTrue("its current metrics are all charted");
        MetricChartPicks.GroupState(email, [Clicked]).Should().BeNull();
        MetricChartPicks.GroupState(email, [OldRule]).Should().BeNull();
        MetricChartPicks.GroupState(email, [Mttr]).Should().BeFalse();
    }

    [Fact]
    public void OneChart_TakesAFewMetricsInOneUnit_AndAnythingElseIsAGrid()
    {
        MetricChartPicks.ViewFor([Reported, Opened], null).Should().Be(MetricChartView.Overlay);
        MetricChartPicks.ViewFor([Reported, Opened], "grid").Should().Be(MetricChartView.Grid);
        MetricChartPicks.ViewFor([Reported, Mttr], null).Should().Be(MetricChartView.Grid, "counts and hours on one axis would read as comparable");
        var seven = Enumerable.Range(0, MetricChartPicks.MaxOverlay + 1).Select(i => M(Email, "m" + i)).ToList();
        MetricChartPicks.ViewFor(seven, null).Should().Be(MetricChartView.Grid);
    }

    [Fact]
    public void Bands_RunUpTheAxis_InTheMetricsDirection_OpenAtEachEnd()
    {
        var lower = new MetricDefinition { Direction = MetricDirection.LowerIsBetter, WarnThreshold = 10, CriticalThreshold = 20 };
        MetricBands.Of(lower).Should().Equal(new MetricBand(null, 10, MetricLevel.Normal), new MetricBand(10, 20, MetricLevel.Warn), new MetricBand(20, null, MetricLevel.Critical));

        var higher = new MetricDefinition { Direction = MetricDirection.HigherIsBetter, WarnThreshold = 90, CriticalThreshold = 80 };
        MetricBands.Of(higher).Should().Equal(new MetricBand(null, 80, MetricLevel.Critical), new MetricBand(80, 90, MetricLevel.Warn), new MetricBand(90, null, MetricLevel.Normal));

        var warnOnly = new MetricDefinition { Direction = MetricDirection.HigherIsBetter, WarnThreshold = 90 };
        MetricBands.Of(warnOnly).Should().Equal(new MetricBand(null, 90, MetricLevel.Warn), new MetricBand(90, null, MetricLevel.Normal));
        MetricBands.Of(new MetricDefinition()).Should().BeEmpty();
    }
}
