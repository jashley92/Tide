using AwesomeAssertions;
using Tide.Application.Trends;

namespace Tide.UnitTests;

/// <summary>What moved this period (P13), worked out from fixed trend reports.</summary>
public class TrendHighlightsTests
{
    private static readonly Period Oct = new(PeriodKind.Month, 2026, 10);

    // A line counting n made-up ids in each period, oldest first.
    private static TrendRow Line(string key, string name, string group, params int[] counts) =>
        new(key, name, group, counts.Select(n => (IReadOnlyList<Guid>)Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToList()).ToList());

    private static TrendReport Report(Period end, int span, TrendRow[]? articles = null, TrendRow[]? tags = null, TrendRow[]? attack = null, TrendRow[]? actions = null) =>
        new(Enumerable.Range(0, span).Select(i => end.Shift(i - span + 1)).ToList(), true, articles ?? [], tags ?? [], attack ?? [], [], actions ?? [],
            new Dictionary<Guid, DateOnly>());

    [Fact]
    public void Ranks_ByChangeWeightedByHowMuchItWas_BiggestFirst()
    {
        var report = Report(Oct, 6, tags:
        [
            Line("tag:big", "Ransomware", "Threats", 90, 95, 100, 100, 100, 110),    // +10, +10%
            Line("tag:tripled", "Phishing", "Threats", 2, 3, 4, 4, 5, 12),           // +7, +140%
            Line("tag:steady", "Fraud", "Threats", 6, 6, 6, 6, 6, 6),
        ], attack:
        [
            Line("technique:T1190", "Initial Access › T1190 Exploit Public-Facing Application", "Techniques", 1, 0, 2, 3, 4, 12),
        ]);

        var got = TrendHighlights.From(report);

        got.Select(h => h.Key).Should().Equal(["technique:T1190", "tag:tripled", "tag:big"], "4 to 12 and 5 to 12 outrank 100 to 110; an unchanged line isn't one");
        got[0].Text.Should().Be("T1190 Exploit Public-Facing Application: 12, the highest in 6 months", "the technique without the tactic the table names it under");
        got[2].Text.Should().Be("Ransomware tag: 110, the highest in 6 months");
        got.Should().AllSatisfy(h => h.At.Should().Be(Oct, "a line that counted something opens to what it counted now"));
    }

    [Fact]
    public void UpAndDown_FromThePeriodBefore_WhenNotTheHighest()
    {
        var report = Report(Oct, 6, tags: [Line("tag:a", "Phishing", "Threats", 20, 1, 1, 1, 4, 12), Line("tag:b", "Fraud", "Threats", 0, 0, 0, 0, 9, 4)]);

        TrendHighlights.From(report).Select(h => h.Text).Should().Equal("Phishing tag: 12, up from 4", "Fraud tag: 4, down from 9");
    }

    [Fact]
    public void BelowTheMinimumCount_InBothPeriods_IsNoHighlight()
    {
        var report = Report(Oct, 6, tags: [Line("tag:small", "Phishing", "Threats", 0, 0, 0, 0, 1, 2), Line("tag:gone", "Fraud", "Threats", 0, 0, 0, 0, 2, 0)],
            attack: [Line("technique:T1059", "Execution › T1059 Command and Scripting Interpreter", "Techniques", 0, 0, 0, 0, 0, 3)]);

        TrendHighlights.From(report).Select(h => h.Key).Should().Equal(["technique:T1059"],
            "1 to 2 and 2 to 0 are noise; 0 to 3 reaches the minimum of " + TrendHighlights.MinCount);
    }

    [Fact]
    public void NoneThisPeriod_AfterSome_OpensToThePeriodItCountedIn()
    {
        var report = Report(Oct, 6, tags: [Line("tag:phishing", "Phishing", "Threats", 3, 4, 2, 6, 5, 0)]);

        var h = TrendHighlights.From(report).Should().ContainSingle().Subject;
        h.Text.Should().Be("Phishing tag: none this month, after 5");
        h.At.Should().Be(Oct.Previous, "nothing counted in October: it opens to September's five");
        h.Up.Should().BeFalse();
    }

    [Fact]
    public void AYearBack_IsComparedWhenTheReportReachesIt()
    {
        var q3 = new Period(PeriodKind.Quarter, 2026, 3);
        // Q3 2025, Q4, Q1, Q2, Q3 2026: 15 a year ago, 6 last quarter, 6 now.
        var report = Report(q3, 5, tags: [Line("tag:phishing", "Phishing", "Threats", 15, 9, 7, 6, 6)]);

        var h = TrendHighlights.From(report).Should().ContainSingle().Subject;
        h.Text.Should().Be("Phishing tag: 6, down from 15 in Q3 2025");
        h.Against.Should().Be(q3.Shift(-4));
        TrendHighlights.YearBefore(Oct).Should().Be(new Period(PeriodKind.Month, 2025, 10));
    }

    [Fact]
    public void AYearBack_WithNoDataInTheReport_IsNotCompared()
    {
        // Six months reach back to May: last October isn't in the report, so only September is compared with, and a line
        // that's unchanged on September is no highlight however it stood a year ago.
        var report = Report(Oct, 6, tags: [Line("tag:same", "Phishing", "Threats", 0, 0, 0, 0, 7, 7)]);
        TrendHighlights.From(report).Should().BeEmpty();

        // Nothing at all counted: nothing moved.
        TrendHighlights.From(Report(Oct, 6, tags: [Line("tag:none-yet", "Fraud", "Threats", 0, 0, 0, 0, 0, 0)])).Should().BeEmpty();
    }

    [Fact]
    public void AtMostFive_TwoFromAPart_AndNoLineThatCountedTheSameAsOneShown()
    {
        var rising = Line("articles:all", "Articles", "Articles", 0, 0, 0, 0, 4, 12);
        var report = Report(Oct, 6,
            articles: [rising, rising with { Key = "articles:informational", Name = "Informational" }],
            tags: [Line("tag:a", "A", "T", 0, 0, 0, 0, 3, 9), Line("tag:b", "B", "T", 0, 0, 0, 0, 3, 8), Line("tag:c", "C", "T", 0, 0, 0, 0, 3, 7)],
            attack: [Line("tactic:TA0001", "Initial Access", "Tactics", 0, 0, 0, 0, 3, 9), Line("technique:T1190", "T1190", "Techniques", 0, 0, 0, 0, 3, 8)],
            actions: [Line("actions:overdue-at-end", "Overdue at period end", "Backlog", 1, 2, 3, 2, 3, 9), Line("actions:closed", "Closed", "Closed", 0, 0, 0, 0, 3, 30)]);

        var got = TrendHighlights.From(report);

        got.Should().HaveCount(TrendHighlights.Most);
        got.Select(h => h.Key).Should().NotContain("articles:informational", "it counted exactly what Articles did")
            .And.NotContain("tag:c", "two tags are already shown").And.NotContain("actions:closed", "Closed isn't a line highlights read");
        got.Select(h => h.Text).Should().Contain("Overdue at month end: 9, the highest in 6 months");
    }

    [Fact]
    public void APeriodUnderWay_OnlyShowsRises()
    {
        var report = Report(Oct, 6, tags: [Line("tag:down", "Fraud", "T", 0, 0, 0, 0, 9, 2), Line("tag:up", "Phishing", "T", 0, 0, 0, 1, 4, 8)]);

        TrendHighlights.From(report, partial: true).Select(h => h.Key).Should().Equal(["tag:up"], "a month to date is always short of a whole one");
    }
}
