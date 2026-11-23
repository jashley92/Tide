using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Content;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

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
    IReadOnlyList<ReportMetric> Metrics, IReadOnlyList<ReportTop> Topics, IReadOnlyList<ReportTop> Techniques, IReadOnlyList<ReportTop> ByTeam)
{
    /// <summary>The lines that moved most (<see cref="TrendHighlights"/>), from the same trend report as the rest.</summary>
    public IReadOnlyList<TrendHighlight> Highlights { get; init; } = [];

    /// <summary>The articles counted in the period ("Articles briefed"), by id: what a drafted assessment may read the
    /// titles and bottom lines of, under the AI limit.</summary>
    public IReadOnlyList<Guid> Counted { get; init; } = [];
}

public sealed class ReportService(TrendsService trends, MetricsService metrics, OrgClock clock, IAppDbContextFactory dbs, ICurrentUser user)
{
    /// <summary>How many topics and techniques a report lists.</summary>
    public const int TopCount = 5;

    /// <summary>The report's numbers' marking, as its page shows it: they're the team's own counts.</summary>
    public const TlpLevel NumbersTlp = TlpLevel.Amber;

    /// <summary>The sections that take an assessment, by key, in the report's order, with their names.</summary>
    public static readonly IReadOnlyList<(string Key, string Name)> Sections =
    [
        ("headlines", "Headlines"), ("outcomes", "Outcomes"), ("metrics", "Opening metrics"),
        ("topics", "Topics and techniques"), ("teams", "Actions raised by team"),
    ];

    /// <summary>The report's name: "Monthly report · October 2026", "Weekly report · Week of Oct 26", "Quarterly report · Q4 2026".</summary>
    public static string Name(Period p) => p.Kind switch
    {
        PeriodKind.Week => $"Weekly report · {p.Label}",
        PeriodKind.Month => $"Monthly report · {p.Start.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}",
        _ => $"Quarterly report · {p.Label}",
    };

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

        var partial = trends.IsPartial(period);
        return new PeriodReport(period, partial, headlines, outcomes, reportMetrics, topics, techniques, byTeam)
            { Highlights = TrendHighlights.From(report, partial), Counted = report.Find("articles:all")?.Ids[^1] ?? [] };
    }

    // --- assessments ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the current user may write the report's assessments: whoever enters the day's numbers or writes articles
    /// (the analysts, and administrators). The report is the analysts' numbers and articles, so whoever makes those says
    /// what they mean. A manager reads the report and doesn't write it.
    /// </summary>
    public bool CanAssess => user.Has(Permission.EnterMetrics) || user.Has(Permission.WriteArticles);

    /// <summary>The saved assessments of <paramref name="period"/>'s sections (only those written). For the team, as the
    /// report is.</summary>
    public async Task<IReadOnlyList<ReportAssessment>> AssessmentsAsync(Period period, CancellationToken ct = default)
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("Reports are open to the team.");
        await using var db = await dbs.CreateAsync(ct);
        var key = period.Key;
        var rows = await db.ReportAssessments.AsNoTracking().Where(a => a.PeriodKey == key).ToListAsync(ct);
        return rows.OrderBy(a => SectionIndex(a.Section)).ToList();
    }

    /// <summary>
    /// Saves <paramref name="section"/>'s assessment for <paramref name="period"/> with its TLP; blank text clears it.
    /// <paramref name="editVersion"/> is the assessment's <see cref="ReportAssessment.EditVersion"/> when the page read it,
    /// or null when there was none: if a colleague has saved (or cleared) it since, the save is refused
    /// (<see cref="StaleEditException"/>) rather than replacing theirs unseen. Returns the assessment as saved, or null
    /// once cleared. Who saved it and when are kept on it, and the audit trail keeps every version.
    /// </summary>
    public async Task<ReportAssessment?> SaveAssessmentAsync(Period period, string section, string? text, TlpLevel tlp, Guid? editVersion,
        CancellationToken ct = default)
    {
        if (!CanAssess) throw new ForbiddenException("Only analysts can write the report's assessments.");
        if (SectionIndex(section) < 0) throw new ArgumentException("That isn't a section of the report.");
        var clean = ArticleService.CleanOrNull(text, ReportAssessment.MaxText, multiline: true);
        tlp = Tlp.Checked(tlp);
        await using var db = await dbs.CreateAsync(ct);
        var key = period.Key;
        var row = await db.ReportAssessments.FirstOrDefaultAsync(a => a.PeriodKey == key && a.Section == section, ct);
        if (row?.EditVersion != editVersion) throw new StaleEditException(AssessmentSavedMeanwhile);
        if (clean is null)
        {
            if (row is null) return null;
            db.ReportAssessments.Remove(row);
        }
        else
        {
            if (row is not null && row.Text == clean && row.Tlp == tlp) return row;   // nothing changed: no new "edited by"
            if (row is null) db.ReportAssessments.Add(row = new ReportAssessment { PeriodKey = key, Section = section });
            else row.EditVersion = Guid.NewGuid();
            row.Text = clean;
            row.Tlp = tlp;
            row.UpdatedById = user.UserId;
            row.UpdatedByName = user.DisplayName;
            row.UpdatedAtUtc = clock.Now.ToUniversalTime();
        }
        // A colleague's save between the read and this one: a changed or deleted row fails the edit version, and a first
        // save at the same moment as theirs fails the unique (period, section) index.
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new StaleEditException(AssessmentSavedMeanwhile); }
        return clean is null ? null : row;
    }

    /// <summary>Why a save of an assessment was refused as stale.</summary>
    public const string AssessmentSavedMeanwhile =
        "Someone else saved this assessment since you opened the report. Reload to see theirs, then save yours again.";

    private static int SectionIndex(string section)
    {
        for (var i = 0; i < Sections.Count; i++)
            if (string.Equals(Sections[i].Key, section, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>The marking a report with <paramref name="assessments"/> carries (ADR 0012): the highest of the numbers'
    /// (<see cref="NumbersTlp"/>) and each assessment's.</summary>
    public static TlpLevel Marking(IEnumerable<ReportAssessment> assessments) =>
        assessments.Select(a => a.Tlp).Append(NumbersTlp).Max();

    /// <summary>
    /// The report as one CSV: a row per number, with its section, this period and the one before; after each section's
    /// numbers, its assessment, if one was written (its text in <c>text</c>, who last edited it in <c>name</c>). Every row
    /// carries its TLP: the numbers <see cref="NumbersTlp"/>, an assessment its own. The file as a whole is marked at the
    /// highest (<see cref="Marking"/>), in its name.
    /// </summary>
    public static string ToCsv(PeriodReport r, IReadOnlyList<ReportAssessment>? assessments = null)
    {
        var numbers = Tlp.Label(NumbersTlp);
        var sb = new StringBuilder("section,name,current,previous,tlp,text").Append('\n');
        void Row(string section, string name, object? current, object? previous) =>
            sb.Append(Csv.NumberOrCell(section)).Append(',').Append(Csv.NumberOrCell(name)).Append(',').Append(Csv.NumberOrCell(Num(current))).Append(',')
                .Append(Csv.NumberOrCell(Num(previous))).Append(',').Append(numbers).Append(",\n");
        void Assessment(string key)
        {
            if (assessments?.FirstOrDefault(a => a.Section == key) is not { } a) return;
            var by = $"Assessment (edited by {a.UpdatedByName}, {Words.MonthDayYear(DateOnly.FromDateTime(a.UpdatedAtUtc.UtcDateTime))})";
            sb.Append(Csv.Cell(Sections[SectionIndex(key)].Name)).Append(',').Append(Csv.Cell(by)).Append(",,,").Append(Tlp.Label(a.Tlp)).Append(',')
                .Append(Csv.Cell(a.Text)).Append('\n');
        }
        foreach (var f in r.Headlines) Row("Headlines", f.Name, f.Current, f.Previous);
        Assessment("headlines");
        foreach (var f in r.Outcomes) Row("Outcomes", f.Name, f.Current, f.Previous);
        Assessment("outcomes");
        foreach (var m in r.Metrics) Row("Opening metrics", $"{m.Group}: {m.Metric.Name} (latest; average {Num(m.Average)}; {m.Missing} days missing)", m.Latest, null);
        Assessment("metrics");
        foreach (var t in r.Topics) Row("Topics", t.Name, t.Current, t.Current - t.Change);
        foreach (var t in r.Techniques) Row("Techniques", t.Name, t.Current, t.Current - t.Change);
        Assessment("topics");
        foreach (var t in r.ByTeam) Row("Actions raised by team", t.Name, t.Current, t.Current - t.Change);
        Assessment("teams");
        return sb.ToString();
    }

    private static string Num(object? v) => v switch
    {
        null => "",
        double d => Words.Num(d),
        decimal m => m.ToString("0.##", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}
