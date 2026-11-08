using AwesomeAssertions;
using Tide.Application.Trends;

namespace Tide.UnitTests;

public class TrendChartTests
{
    // Two periods; a line counts one article in the last when it's seen, none when it isn't.
    private static TrendRow Line(string key, bool seen = true, string group = "Techniques") =>
        new(key, key, group, [[], seen ? [Guid.NewGuid()] : []]);

    private static readonly TrendReport Report = new(
        [new Period(PeriodKind.Month, 2026, 8), new Period(PeriodKind.Month, 2026, 9)], true, [], [],
        Attack:
        [
            Line("tactic:TA0001", group: "Tactics"), Line("tactic:TA0002", group: "Tactics"), Line("tactic:TA0040", seen: false, group: "Tactics"),
            Line("technique:T1566"), Line("technique:T1059"), Line("technique:T1486", seen: false), Line("technique:none"),
        ],
        TacticTechniques: [Line("tactic:TA0001:T1566"), Line("tactic:TA0002:T1059"), Line("tactic:TA0002:T1204", seen: false)],
        Actions: [], CountedOn: new Dictionary<Guid, DateOnly>());

    private static string[] Keys(string chart) => [.. TrendChart.Resolve(Report, Report.Attack, chart).Select(r => r.Key)];

    [Fact]
    public void Keys_ResolveAgainstEveryTacticAndTechnique_InOrder_EachOnce()
    {
        Keys("tactic:TA0002:T1059,technique:T1566,tactic:TA0001,technique:T1566,tactic:TA0001:T1566")
            .Should().Equal("tactic:TA0002:T1059", "technique:T1566", "tactic:TA0001", "tactic:TA0001:T1566");
        Keys("technique:T1486").Should().Equal(["technique:T1486"], "a line picked on its own is charted, zeros and all");
        Keys("tactic:TA9999:T1,unknown, ,tactic:TA0002:T1204").Should().Equal("tactic:TA0002:T1204");
    }

    [Fact]
    public void Groups_StandForTheLinesThatCountedSomething()
    {
        Keys(TrendChart.All).Should().Equal("tactic:TA0001", "tactic:TA0002", "technique:T1566", "technique:T1059", "technique:none");
        Keys(TrendChart.AllTechniques).Should().Equal(["technique:T1566", "technique:T1059"], "not \"No technique\", nor one never seen");
        Keys("tactic:TA0002," + TrendChart.AllUnder("tactic:TA0002")).Should().Equal("tactic:TA0002", "tactic:TA0002:T1059");
        Keys(TrendChart.AllUnder("tactic:TA9999")).Should().BeEmpty();
        Keys(TrendChart.None).Should().BeEmpty();
        Keys("all-techniques,-technique:T1566").Should().Equal("technique:T1059");
        TrendChart.Resolve(Report, Report.Actions, TrendChart.AllTechniques).Should().BeEmpty("another view has no techniques");
    }

    [Fact]
    public void Toggle_KeepsAGroupOneItem()
    {
        string Toggle(string chart, string key) => TrendChart.Toggle(Report, Report.Attack, chart, key);

        Toggle("tactic:TA0001", "technique:T1059").Should().Be("tactic:TA0001,technique:T1059");
        Toggle("tactic:TA0001,technique:T1059", "tactic:TA0001").Should().Be("technique:T1059");
        Toggle("tactic:TA0001", "tactic:TA0001").Should().Be(TrendChart.None, "the last line off leaves the chart empty, not back to its default");
        Toggle(TrendChart.None, "tactic:TA0002:T1059").Should().Be("tactic:TA0002:T1059");

        Toggle(TrendChart.AllTechniques, "technique:T1566").Should().Be("all-techniques,-technique:T1566");
        Toggle("all-techniques,-technique:T1566", "technique:T1566").Should().Be(TrendChart.AllTechniques);
        Toggle("technique:T1566,all-techniques", "technique:T1566").Should().Be("all-techniques,-technique:T1566", "the group still has it");
    }

    [Fact]
    public void ATechnique_IsTheSameLine_UnderAnyTacticOrInTheFlatList()
    {
        TrendChart.Identity("tactic:TA0002:T1059").Should().Be("technique:T1059");
        TrendChart.Identity("technique:T1059").Should().Be("technique:T1059");
        TrendChart.Identity("tactic:TA0002").Should().Be("tactic:TA0002", "a tactic is itself");
        TrendChart.Identity("tag:ransomware").Should().Be("tag:ransomware");

        var charted = TrendChart.Resolve(Report, Report.Attack, "tactic:TA0001,tactic:TA0002:T1059,technique:T1566,tactic:TA0001:T1566");
        TrendChart.Slot(charted, Line("technique:T1059")).Should().Be(1, "charted from Execution, it's the flat list's T1059 too");
        TrendChart.Slot(charted, Line("tactic:TA0001:T1566")).Should().Be(3, "its own line first");
        TrendChart.Slot(charted, Line("tactic:TA0040:T1566")).Should().Be(2);
        TrendChart.Slot(charted, Line("technique:T1486")).Should().Be(-1);
        TrendChart.Slot(charted, Line("tactic:TA0002")).Should().Be(-1);
    }
}
