using AwesomeAssertions;
using Tide.Application.Metrics;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>The day's numbers form's progress and folding (MetricEntryProgress), and the history table's columns and
/// group filter (MetricHistoryColumns).</summary>
public class MetricEntryProgressTests
{
    private static readonly DateOnly Day = new(2026, 11, 4);
    private static readonly HashSet<Guid> Nothing = [];

    private static MetricGroup Group(string name) => new() { Id = Guid.NewGuid(), Name = name, Key = name.ToLowerInvariant().Replace(' ', '-') };

    private static MetricDefinition Metric(MetricGroup g, string name) => new() { Id = Guid.NewGuid(), Name = name, GroupId = g.Id, Group = g };

    private static MetricTile Tile(MetricDefinition m, decimal? value) => new(m, value, null, null, MetricLevel.Normal, []);

    private sealed class Form
    {
        public readonly MetricGroup Email = Group("Email triage"), Cases = Group("Cases");
        public readonly MetricDefinition[] EmailMetrics, CaseMetrics;
        public readonly Dictionary<Guid, decimal?> Values = [];

        public Form()
        {
            EmailMetrics = [Metric(Email, "Reported"), Metric(Email, "Clicked"), Metric(Email, "Blocked"), Metric(Email, "Open")];
            CaseMetrics = [Metric(Cases, "New"), Metric(Cases, "Closed")];
        }

        public MetricDay Day() => new(MetricEntryProgressTests.Day,
        [
            new MetricGroupDay(Email, [.. EmailMetrics.Select(m => Tile(m, Values.GetValueOrDefault(m.Id)))]),
            new MetricGroupDay(Cases, [.. CaseMetrics.Select(m => Tile(m, Values.GetValueOrDefault(m.Id)))]),
        ]);
    }

    [Fact]
    public void Counts_EachGroup_AndTheWholeDay()
    {
        var f = new Form();
        f.Values[f.EmailMetrics[0].Id] = 3;
        f.Values[f.EmailMetrics[1].Id] = 0;   // a zero is a value
        f.Values[f.CaseMetrics[0].Id] = 1;
        f.Values[f.CaseMetrics[1].Id] = 2;
        var p = new MetricEntryProgress();
        p.Update(f.Day(), Nothing);

        p.Summary.Should().Be("4 of 6 entered");
        p.Groups.Select(g => (g.Name, g.Entered, g.Total, g.Complete)).Should().Equal(("Email triage", 2, 4, false), ("Cases", 2, 2, true));
        p.Groups[0].Count.Should().Be("2 of 4");
        p.Groups[0].AccessibleName.Should().Be("Email triage, 2 of 4 entered");
    }

    [Fact]
    public void AGroupWithNoMetrics_IsNeverComplete() =>
        new MetricGroupProgress(Guid.NewGuid(), "Empty", 0, 0).Complete.Should().BeFalse();

    [Fact]
    public void TheFirstRead_FoldsWhatsFinished_LaterOnesDontFoldAnything()
    {
        var f = new Form();
        foreach (var m in f.CaseMetrics) f.Values[m.Id] = 1;
        var p = new MetricEntryProgress();
        p.Update(f.Day(), Nothing);
        p.IsFolded(f.Cases.Id).Should().BeTrue("it was finished when the form opened");
        p.IsFolded(f.Email.Id).Should().BeFalse();

        foreach (var m in f.EmailMetrics) f.Values[m.Id] = 1;
        p.Update(f.Day(), Nothing);
        p.IsFolded(f.Email.Id).Should().BeFalse("it folds only when it's left, never while being typed in");
    }

    [Fact]
    public void AFinishedGroup_FoldsWhenLeft_NotWhileFocusIsStillInIt()
    {
        var f = new Form();
        foreach (var m in f.EmailMetrics) f.Values[m.Id] = 1;
        var p = new MetricEntryProgress();
        p.Update(new Form().Day(), Nothing);   // nothing entered when it opened
        p.Update(f.Day(), Nothing);

        p.Left(f.Email.Id, f.Email.Id).Should().BeFalse("focus is still in the group");
        p.IsFolded(f.Email.Id).Should().BeFalse();
        p.Left(f.Email.Id, f.Cases.Id).Should().BeTrue();
        p.IsFolded(f.Email.Id).Should().BeTrue();
        p.Left(f.Email.Id, null).Should().BeFalse("it's folded already");
    }

    [Fact]
    public void AnUnfinishedGroup_DoesntFoldWhenLeft()
    {
        var f = new Form();
        f.Values[f.EmailMetrics[0].Id] = 1;
        var p = new MetricEntryProgress();
        p.Update(f.Day(), Nothing);
        p.Left(f.Email.Id, null).Should().BeFalse();
        p.Fold(f.Email.Id).Should().BeFalse();
    }

    [Fact]
    public void AGroupHoldingAnInvalidBox_OrAColleaguesChange_NeverFolds_AndUnfoldsIfFolded()
    {
        var f = new Form();
        foreach (var m in f.EmailMetrics.Concat(f.CaseMetrics)) f.Values[m.Id] = 1;
        var p = new MetricEntryProgress();
        p.Update(f.Day(), Nothing);
        p.IsFolded(f.Email.Id).Should().BeTrue();

        p.Update(f.Day(), new HashSet<Guid> { f.EmailMetrics[2].Id });
        p.IsFolded(f.Email.Id).Should().BeFalse("a box in it holds what isn't a number (or a colleague's change)");
        p.Foldable(f.Email.Id).Should().BeFalse();
        p.Left(f.Email.Id, f.Cases.Id).Should().BeFalse();
        p.IsFolded(f.Cases.Id).Should().BeTrue("the other group isn't held");

        p.Update(f.Day(), Nothing);
        p.Left(f.Email.Id, null).Should().BeTrue("fixed, it folds when left");
    }

    [Fact]
    public void ClearingAValue_UnfoldsItsGroup()
    {
        var f = new Form();
        foreach (var m in f.CaseMetrics) f.Values[m.Id] = 1;
        var p = new MetricEntryProgress();
        p.Update(f.Day(), Nothing);
        p.IsFolded(f.Cases.Id).Should().BeTrue();

        f.Values[f.CaseMetrics[1].Id] = null;
        p.Update(f.Day(), Nothing);
        p.IsFolded(f.Cases.Id).Should().BeFalse();
        p.Groups[1].Count.Should().Be("1 of 2");
    }

    [Fact]
    public void UnfoldingAndFolding_ByHand()
    {
        var f = new Form();
        foreach (var m in f.CaseMetrics) f.Values[m.Id] = 1;
        var p = new MetricEntryProgress();
        p.Update(f.Day(), Nothing);

        p.Unfold(f.Cases.Id).Should().BeTrue();
        p.Unfold(f.Cases.Id).Should().BeFalse("it's open already");
        p.Fold(f.Cases.Id).Should().BeTrue();
        p.IsFolded(f.Cases.Id).Should().BeTrue();
    }

    [Fact]
    public void AChipJumps_ToTheFirstEmptyBox_OrTheFirstBoxWhenAllHaveValues()
    {
        var f = new Form();
        f.Values[f.EmailMetrics[0].Id] = 1;
        f.Values[f.EmailMetrics[2].Id] = 1;
        foreach (var m in f.CaseMetrics) f.Values[m.Id] = 1;
        var p = new MetricEntryProgress();
        p.Update(f.Day(), Nothing);

        p.JumpTarget(f.Email.Id).Should().Be(f.EmailMetrics[1].Id);
        p.JumpTarget(f.Cases.Id).Should().Be(f.CaseMetrics[0].Id);
        p.JumpTarget(Guid.NewGuid()).Should().BeNull();
        p.GroupOf(f.CaseMetrics[1].Id).Should().Be(f.Cases.Id);
        p.GroupOf(Guid.NewGuid()).Should().BeNull();
        p.MetricsOf(Guid.NewGuid()).Should().BeEmpty();
    }

    [Fact]
    public void HistoryColumns_RunByGroup_InTheHistorysOrder()
    {
        var f = new Form();
        var runs = MetricHistoryColumns.Group([.. f.EmailMetrics, .. f.CaseMetrics]);
        runs.Select(r => (r.Name, r.Key, r.Metrics.Count)).Should().Equal(("Email triage", "email-triage", 4), ("Cases", "cases", 2));
        MetricHistoryColumns.Group([]).Should().BeEmpty();
    }

    [Fact]
    public void HistoryFilter_IsKeptInTheQuery_ByGroupKey()
    {
        MetricHistoryColumns.Query([]).Should().BeNull("all groups need no parameter");
        MetricHistoryColumns.Query(["email-triage", "cases", "Cases", ""]).Should().Be("cases,email-triage");
        MetricHistoryColumns.Parse("cases, email-triage,,").Should().BeEquivalentTo(["cases", "email-triage"]);
        MetricHistoryColumns.Parse(null).Should().BeEmpty();
    }

    [Fact]
    public void HistoryFilter_PicksGroups_AndFallsBackToAll()
    {
        var f = new Form();
        var all = MetricHistoryColumns.Group([.. f.EmailMetrics, .. f.CaseMetrics]);
        var none = new HashSet<string>();

        MetricHistoryColumns.Shown(all, none).Should().HaveCount(2);
        MetricHistoryColumns.Shown(all, new HashSet<string> { "gone" }).Should().HaveCount(2, "a group since removed shows them all");
        MetricHistoryColumns.Shown(all, new HashSet<string> { "cases" }).Single().Name.Should().Be("Cases");

        var one = MetricHistoryColumns.Toggle(all, none, "cases");
        one.Should().BeEquivalentTo(["cases"], "with all showing, a chip picks that group alone");
        MetricHistoryColumns.Toggle(all, one, "email-triage").Should().BeEmpty("both picked is all of them");
        MetricHistoryColumns.Toggle(all, one, "cases").Should().BeEmpty("taking away the last goes back to all");
    }

    [Fact]
    public void HistoryFilter_ShowsAndCountsOnlyTheShownGroupsValues_SoADayWithOnlyHiddenOnesHasNoRow()
    {
        var f = new Form();
        var all = MetricHistoryColumns.Group([.. f.EmailMetrics, .. f.CaseMetrics]);
        static MetricValue Value(MetricDefinition m, DateOnly d) => new() { DefinitionId = m.Id, BriefDate = d, Value = 1 };
        var earlier = Day.AddDays(-1);
        IReadOnlyList<MetricValue> values = [Value(f.EmailMetrics[0], Day), Value(f.CaseMetrics[0], Day), Value(f.EmailMetrics[1], earlier)];

        var cases = MetricHistoryColumns.ShownValues(values, all, MetricHistoryColumns.Shown(all, new HashSet<string> { "cases" }));
        cases.Should().ContainSingle().Which.DefinitionId.Should().Be(f.CaseMetrics[0].Id);
        cases.Select(v => v.BriefDate).Distinct().Should().Equal([Day], "the earlier day had only email values");
        MetricHistoryColumns.ShownValues(values, all, all).Should().BeSameAs(values, "with all groups nothing changes");
    }
}
