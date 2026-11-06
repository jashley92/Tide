using System.Globalization;
using System.Text;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Domain.Entities;

namespace Tide.Application.Trends;

/// <summary>A number a report gives, this period and the one before; <see cref="Line"/> is the trend line it comes from.</summary>
public sealed record ReportFigure(string Name, double? Current, double? Previous, string? Line = null, string Unit = "")
{
    public double? Change => Current is { } c && Previous is { } p ? c - p : null;
}

/// <summary>One opening metric over the period: its last value, average, lowest and highest, how many business days had
/// no value, and how many were past a threshold.</summary>
public sealed record ReportMetric(MetricDefinition Metric, string Group, decimal? Latest, decimal? Average, decimal? Lowest, decimal? Highest,
    int Missing, int Breaches);

/// <summary>A topic or technique the period's articles carried most, with its count and change.</summary>
public sealed record ReportTop(string Name, int Current, int Change, string Line);

/// <summary>
/// The report a team lead owes for a week, a month or a quarter, assembled from what TIDE already counts: the headline
/// numbers against the period before, the outcomes, the opening metrics, the topics and techniques briefed most, and
/// the actions raised by team. Every number comes from <see cref="TrendsService"/> or <see cref="MetricsService"/>, so it
/// can't differ from Trends or Metrics.
/// </summary>
public sealed record PeriodReport(Period Period, bool Partial, IReadOnlyList<ReportFigure> Headlines, IReadOnlyList<ReportFigure> Outcomes,
    IReadOnlyList<ReportMetric> Metrics, IReadOnlyList<ReportTop> Topics, IReadOnlyList<ReportTop> Techniques, IReadOnlyList<ReportTop> ByTeam);

public sealed class ReportService(TrendsService trends, MetricsService metrics, OrgClock clock)
{
    /// <summary>How many topics and techniques a report lists.</summary>
    public const int TopCount = 5;

    /// <summary>The report for <paramref name="period"/>. Readable by whoever reads Trends (the team edition).</summary>
    public async Task<PeriodReport> BuildAsync(Period period, CancellationToken ct = default)
    {
        var report = await trends.ReportAsync(period, ct: ct);
        int Now(string key) => report.Find(key)?.Current ?? 0;
        int Before(string key) => report.Find(key)?.Previous ?? 0;
        ReportFigure Count(string name, string key) => new(name, Now(key), Before(key), key);

        var headlines = new List<ReportFigure>
        {
            Count("Articles briefed", "articles:all"),
            Count("Threats", "articles:threat"),
            Count("P1 and P2 threats", "articles:p1-p2"),
            Count("Actions raised", "actions:raised"),
            Count("Actions done", TrendsService.DoneKey),
            new("Done on time", report.OnTimeRatio * 100, report.PreviousOnTimeRatio * 100, "actions:on-time", "%"),
        };
        var outcomes = new List<ReportFigure> { Count("Overdue at period end", "actions:overdue-at-end") };
        outcomes.AddRange(report.Outcomes.Select(o => new ReportFigure(o.Name, o.Current, o.Previous, null, " days")));
        outcomes.Add(Count("Hunts with hits", "actions:hits"));
        outcomes.Add(Count("Hunted", "actions:hunted"));

        ReportTop Top(TrendRow r) => new(r.Name, r.Current, r.Change, r.Key);
        var topics = report.Tags.Where(r => !r.IsRollup && r.Key != "tag:none" && r.Current > 0)
            .OrderByDescending(r => r.Current).ThenBy(r => r.Name, StringComparer.Ordinal).Take(TopCount).Select(Top).ToList();
        var techniques = report.Attack.Where(r => r.Group == "Techniques" && r.Current > 0)
            .OrderByDescending(r => r.Current).ThenBy(r => r.Name, StringComparer.Ordinal).Take(TopCount).Select(Top).ToList();
        var byTeam = report.Actions.Where(r => r.Group == "Raised by team" && (r.Current > 0 || r.Previous > 0)).Select(Top).ToList();

        var end = period.End < clock.Today ? period.End : clock.Today;
        // A period that hasn't started yet has no values. HistoryAsync reads a range given backwards the right way round,
        // so asking it for this one would read the days from today to the period's start, which aren't in it.
        var started = end >= period.Start;
        var history = await metrics.HistoryAsync(started ? period.Start : clock.Today, end, ct);
        IReadOnlyList<MetricValue> inPeriod = started ? history.Values : [];
        var businessDays = Enumerable.Range(0, Math.Max(0, end.DayNumber - period.Start.DayNumber + 1))
            .Select(i => period.Start.AddDays(i)).Count(clock.Days.IsWorkingDay);
        var reportMetrics = history.Metrics.Where(m => !m.IsRetired).Select(m =>
        {
            var values = inPeriod.Where(v => v.DefinitionId == m.Id).OrderBy(v => v.BriefDate).ToList();
            var numbers = values.Select(v => v.Value).ToList();
            return new ReportMetric(m, m.Group?.Name ?? "", numbers.Count > 0 ? numbers[^1] : null,
                numbers.Count > 0 ? Math.Round(numbers.Average(), 1) : null, numbers.Count > 0 ? numbers.Min() : null, numbers.Count > 0 ? numbers.Max() : null,
                Math.Max(0, businessDays - values.Select(v => v.BriefDate).Distinct().Count(clock.Days.IsWorkingDay)),
                numbers.Count(n => MetricMath.LevelOf(m, n) != MetricLevel.Normal));
        }).ToList();

        return new PeriodReport(period, trends.IsPartial(period), headlines, outcomes, reportMetrics, topics, techniques, byTeam);
    }

    /// <summary>The report as one CSV: a row per number, with its section, this period and the one before.</summary>
    public static string ToCsv(PeriodReport r)
    {
        var sb = new StringBuilder("section,name,current,previous").Append('\n');
        void Row(string section, string name, object? current, object? previous) =>
            sb.Append(Csv(section)).Append(',').Append(Csv(name)).Append(',').Append(Csv(Num(current))).Append(',').Append(Csv(Num(previous))).Append('\n');
        foreach (var f in r.Headlines) Row("Headlines", f.Name, f.Current, f.Previous);
        foreach (var f in r.Outcomes) Row("Outcomes", f.Name, f.Current, f.Previous);
        foreach (var m in r.Metrics) Row("Opening metrics", $"{m.Group}: {m.Metric.Name} (latest; average {Num(m.Average)}; {m.Missing} days missing)", m.Latest, null);
        foreach (var t in r.Topics) Row("Topics", t.Name, t.Current, t.Current - t.Change);
        foreach (var t in r.Techniques) Row("Techniques", t.Name, t.Current, t.Current - t.Change);
        foreach (var t in r.ByTeam) Row("Actions raised by team", t.Name, t.Current, t.Current - t.Change);
        return sb.ToString();
    }

    private static string Num(object? v) => v switch
    {
        null => "",
        double d => d.ToString("0.#", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.##", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    // A field as CSV: quoted when it holds a comma, a quote or a line break, and never read as a formula by a spreadsheet
    // (a number, which a leading minus or plus is fine on, is left as it is).
    private static string Csv(string value)
    {
        var v = value.Length > 0 && (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? "'" + value : value;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }
}
