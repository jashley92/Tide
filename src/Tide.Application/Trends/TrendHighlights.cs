using System.Globalization;

namespace Tide.Application.Trends;

/// <summary>
/// One line that moved: what it counts (<see cref="Key"/>, the trend line), as people read it (<see cref="Name"/>,
/// "Phishing tag"), and how it moved (<see cref="Detail"/>, "none this month, after 5"). <see cref="At"/> is the period
/// whose ids it opens to: the current one, or the one compared with when nothing counted now (so it opens to what
/// there was). <see cref="Against"/> is the period it was compared with: the one before, or the same one a year back.
/// </summary>
public sealed record TrendHighlight(string Key, string Name, string Detail, int Current, int Before, Period At, Period Against, double Score)
{
    public bool Up => Current > Before;

    public string Text => $"{Name}: {Detail}";
}

/// <summary>
/// What moved this period (P13): the few trend lines that changed most against the period before, or against the same
/// period a year back when the report reaches that far. Worked out from a <see cref="TrendReport"/> the page has
/// already read, so it costs no further reads.
/// </summary>
public static class TrendHighlights
{
    /// <summary>The most lines a strip shows.</summary>
    public const int Most = 5;

    /// <summary>The count one side of a comparison must reach: from 1 to 2 is +100%, and means nothing.</summary>
    public const int MinCount = 3;

    /// <summary>The most lines from one part of the report (articles, tags, ATT&amp;CK, actions), so a tactic and its
    /// technique, or a category and its tag, don't fill the strip saying the same thing twice.</summary>
    public const int MostPerSection = 2;

    // A rise from nothing has no ratio; it counts as tripling, so 0 to 3 doesn't outrank 4 to 12.
    private const double RatioCap = 2;

    /// <summary>
    /// The lines that moved most, biggest first. A line's score is its change in count weighted by its change as a
    /// share of what it was (capped), so 4 to 12 outranks 100 to 110, and 100 to 110 outranks 3 to 4. A line under
    /// <see cref="MinCount"/> in both periods doesn't count. For a period still under way (<paramref name="partial"/>) only
    /// rises count: a month to date is always "down" on a whole one.
    /// </summary>
    public static IReadOnlyList<TrendHighlight> From(TrendReport report, bool partial = false)
    {
        if (report.Periods.Count < 2) return [];
        var previous = report.Periods.Count - 2;
        var yearAgo = report.Periods.ToList().IndexOf(YearBefore(report.Current));

        var moved = new List<(TrendHighlight Highlight, TrendRow Row, string Section)>();
        foreach (var (section, row) in Candidates(report))
        {
            var best = Compare(report, row, previous, partial);
            if (yearAgo >= 0 && Compare(report, row, yearAgo, partial) is { } year && (best is null || year.Score > best.Score)) best = year;
            if (best is not null) moved.Add((best, row, section));
        }

        var picked = new List<(TrendHighlight Highlight, TrendRow Row, string Section)>();
        foreach (var m in moved.OrderByDescending(m => m.Highlight.Score).ThenBy(m => m.Row.Key, StringComparer.Ordinal))
        {
            if (picked.Count == Most) break;
            if (picked.Count(p => p.Section == m.Section) == MostPerSection) continue;
            // A line that counted exactly what one already picked did (a category with one tag in use, "Articles" and
            // "Informational" in a month of nothing else) says the same thing again.
            if (picked.Any(p => SameIds(p.Row, m.Row))) continue;
            picked.Add(m);
        }
        return picked.Select(p => p.Highlight).ToList();
    }

    /// <summary>The same period a year earlier: twelve months, four quarters or 52 weeks back.</summary>
    public static Period YearBefore(Period p) => p.Shift(p.Kind switch { PeriodKind.Month => -12, PeriodKind.Quarter => -4, _ => -52 });

    // The lines worth a highlight: what was briefed, by tag and by ATT&CK, and the work it made. Not "No scorecard tag" or
    // "No technique" (housekeeping, not a threat), the techniques again under each tactic, or the action lines that
    // split another (closed, cancelled, by outcome, open), which say less than the ones kept.
    private static IEnumerable<(string Section, TrendRow Row)> Candidates(TrendReport report)
    {
        foreach (var r in report.Articles) yield return ("articles", r);
        foreach (var r in report.Tags.Where(r => r.Key != "tag:none")) yield return ("tags", r);
        foreach (var r in report.Attack.Where(r => r.Key != "technique:none")) yield return ("attack", r);
        foreach (var r in report.Actions.Where(r => IsActionCandidate(r.Key))) yield return ("actions", r);
    }

    private static bool IsActionCandidate(string key) =>
        key is "actions:raised" or TrendsService.DoneKey or TrendsService.LateKey or "actions:overdue-at-end" or "actions:hits"
        || key.StartsWith("actions:" + TrendReport.RaisedKind, StringComparison.Ordinal)
        || key.StartsWith("actions:raised-team:", StringComparison.Ordinal);

    private static TrendHighlight? Compare(TrendReport report, TrendRow row, int against, bool partial)
    {
        var (now, before) = (row.Current, row.Count(against));
        if (now == before || Math.Max(now, before) < MinCount || (partial && now < before)) return null;
        var change = Math.Abs(now - before);
        var ratio = before == 0 ? RatioCap : Math.Min(RatioCap, (double)change / before);
        var period = report.Periods[against];
        var isPrevious = against == report.Periods.Count - 2;
        var unit = Unit(report.Current.Kind);
        var when = isPrevious ? "" : " in " + period.Label;

        string detail;
        if (now == 0) detail = $"none this {unit}, after {Num(before)}{when}";
        else if (now > before && isPrevious && report.Periods.Count >= 3 && Enumerable.Range(0, report.Periods.Count - 1).All(i => row.Count(i) < now))
            detail = $"{Num(now)}, the highest in {report.Periods.Count} {unit}s";
        else detail = $"{Num(now)}, {(now > before ? "up" : "down")} from {(before == 0 ? "none" : Num(before))}{when}";

        return new TrendHighlight(row.Key, NameOf(row, unit), detail, now, before, now == 0 ? period : report.Current, period, change * (1 + ratio));
    }

    private static bool SameIds(TrendRow a, TrendRow b) =>
        a.Ids.Count == b.Ids.Count && a.Ids.Zip(b.Ids).All(p => p.First.Count == p.Second.Count && p.First.ToHashSet().SetEquals(p.Second));

    /// <summary>A line's name on its own, out of the table that gives it context: "Phishing tag", "Block actions raised".</summary>
    private static string NameOf(TrendRow row, string unit)
    {
        var key = row.Key;
        if (key.StartsWith("tag:", StringComparison.Ordinal)) return row.Name + " tag";
        // A technique is named under its tactic in the table ("Initial Access › T1190 …"); here the technique is enough.
        if (key.StartsWith("technique:", StringComparison.Ordinal) && row.Name.LastIndexOf(" › ", StringComparison.Ordinal) is >= 0 and var at)
            return row.Name[(at + 3)..];
        if (key.StartsWith("actions:" + TrendReport.RaisedKind, StringComparison.Ordinal)) return row.Name + " actions raised";
        if (key.StartsWith("actions:raised-team:", StringComparison.Ordinal)) return "Actions raised by " + row.Name;
        return key switch
        {
            "articles:informational" => "Informational articles",
            "actions:raised" => "Actions raised",
            TrendsService.DoneKey => "Actions done",
            TrendsService.LateKey => "Actions done late",
            "actions:overdue-at-end" => $"Overdue at {unit} end",
            "actions:hits" => "Hunts with hits",
            _ => row.Name,
        };
    }

    private static string Unit(PeriodKind kind) => kind switch { PeriodKind.Month => "month", PeriodKind.Quarter => "quarter", _ => "week" };

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}
