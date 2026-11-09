using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Content;
using Tide.Application.Org;
using Tide.Application.Scorecard;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Metrics;

/// <summary>One metric on one day: its value, the comparison with the previous business day that has one, its threshold
/// level, and the recent values a sparkline plots (oldest first, null where a day has none).</summary>
public sealed record MetricTile(MetricDefinition Metric, decimal? Value, string? Note, MetricComparison? Previous, MetricLevel Level,
    IReadOnlyList<decimal?> Spark)
{
    /// <summary>Where an imported value came from ("Import: queue.csv"); null for a typed one.</summary>
    public string? ImportedFrom { get; init; }

    /// <summary>
    /// The value <see cref="Previous"/> compares with, the most recent earlier business day's, kept whether or not the day
    /// has a value yet: the entry form shows it beside an empty box. Null when there's no earlier value.
    /// </summary>
    public (DateOnly Date, decimal Value)? Reference { get; init; }

    /// <summary>The values a group slide's trend chart plots, one per <see cref="MetricDay.TrendDays"/> (null where a day
    /// has none, so a gap stays a gap rather than reading as 0).</summary>
    public IReadOnlyList<decimal?> Trend { get; init; } = [];

    public string Display => Value is { } v ? MetricMath.Format(Metric.Format, v) : "—";

    /// <summary>
    /// The metric on <paramref name="day"/>, from its values (any order): the day's value and note, the comparison with
    /// the previous business day that has one, its level, and where an imported value came from. No sparkline or trend.
    /// </summary>
    public static MetricTile On(MetricDefinition m, IReadOnlyCollection<MetricValue> mine, DateOnly day)
    {
        var today = mine.FirstOrDefault(v => v.BriefDate == day);
        var previous = MetricMath.PreviousWithValue(mine.Select(v => (v.BriefDate, v.Value)), day);
        return new MetricTile(m, today?.Value, today?.Note, MetricMath.Compare(m.Direction, today?.Value, previous), MetricMath.LevelOf(m, today?.Value), [])
        {
            ImportedFrom = today?.Source == MetricSource.Imported ? today.EnteredByName : null,
            Reference = previous,
        };
    }
}

public sealed record MetricGroupDay(MetricGroup Group, IReadOnlyList<MetricTile> Tiles)
{
    /// <summary>The days in the <see cref="MetricsService.TrendDays"/> calendar days ending on the brief date that the
    /// group's trend charts plot, oldest first (<see cref="MetricMath.TrendDaysWithin"/>): the dates each tile's
    /// <see cref="MetricTile.Trend"/> has a value (or null) for.</summary>
    public IReadOnlyList<DateOnly> TrendDays { get; init; } = [];
}

/// <summary>The opening metrics for one brief date, by group, in order (retired metrics and groups left out).</summary>
public sealed record MetricDay(DateOnly Date, IReadOnlyList<MetricGroupDay> Groups)
{
    public bool Any => Groups.Count > 0;

    /// <summary>The working days the comparisons were made on, so a reader names the day compared with as the entry form does.</summary>
    public WorkingDays? Days { get; init; }

    /// <summary>The metrics that flag a missing value and have none on this date.</summary>
    public IReadOnlyList<MetricDefinition> Missing => Groups.SelectMany(g => g.Tiles)
        .Where(t => t.Value is null && t.Metric.FlagMissing).Select(t => t.Metric).ToList();
}

/// <summary>A metric's definition as the admin form edits it.</summary>
public sealed class MetricForm
{
    public Guid? Id { get; set; }
    public Guid GroupId { get; set; }
    public string Name { get; set; } = "";
    public MetricFormat Format { get; set; } = MetricFormat.Count;
    public MetricDirection Direction { get; set; } = MetricDirection.Neutral;
    public decimal? WarnThreshold { get; set; }
    public decimal? CriticalThreshold { get; set; }
    public bool FlagMissing { get; set; } = true;

    public static MetricForm From(MetricDefinition m) => new()
    {
        Id = m.Id, GroupId = m.GroupId, Name = m.Name, Format = m.Format, Direction = m.Direction,
        WarnThreshold = m.WarnThreshold, CriticalThreshold = m.CriticalThreshold, FlagMissing = m.FlagMissing,
    };
}

/// <summary>Every metric (retired ones too) and its values over a range: the metrics page and its CSV export.</summary>
public sealed record MetricHistory(DateOnly From, DateOnly To, IReadOnlyList<MetricDefinition> Metrics, IReadOnlyList<MetricValue> Values);

/// <summary>
/// The opening metrics (MET-01): administrators define groups and metrics, analysts type the day's values, and everyone
/// with the team edition sees them compared with the previous business day. Nothing is built in; an optional generic
/// example set can be imported, never seeded.
/// </summary>
public sealed class MetricsService(IAppDbContextFactory dbs, ICurrentUser user, OrgClock? clock = null, Briefs.IMeetingsInProgress? meetings = null)
{
    /// <summary>Business days a sparkline covers (about four weeks).</summary>
    public const int SparkDays = 20;

    /// <summary>Calendar days a group slide's trend chart covers.</summary>
    public const int TrendDays = 30;

    /// <summary>How far back a comparison looks for an earlier value.</summary>
    public const int LookBackDays = 120;

    /// <summary>The longest range the metrics page and export cover.</summary>
    public const int MaxRangeDays = 3 * 366;

    /// <summary>
    /// The example set an administrator may import: the kinds of metric the model handles (a status breakdown, a queue with
    /// an ageing count, an SLA split), naming no product or organization. Meant to be renamed or retired.
    /// </summary>
    public static readonly IReadOnlyList<(string Group, (string Name, MetricDirection Direction, decimal? Warn, decimal? Critical)[] Metrics)> ExampleSet =
    [
        ("User-reported email", [("Triaged", MetricDirection.Neutral, null, null), ("Awaiting triage", MetricDirection.LowerIsBetter, 10, 25)]),
        ("Alert queue", [("Open", MetricDirection.LowerIsBetter, null, null), ("Older than 24 hours", MetricDirection.LowerIsBetter, 1, 5)]),
        ("Vulnerability SLA", [("Within SLA", MetricDirection.HigherIsBetter, null, null), ("Past SLA", MetricDirection.LowerIsBetter, 1, 10)]),
    ];

    // --- reading -------------------------------------------------------------------------------------------------

    /// <summary>The active groups and metrics, in order; retired ones too when <paramref name="includeRetired"/>.</summary>
    public async Task<IReadOnlyList<MetricGroup>> ListAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        RequireRead();
        return await GroupsAsync(includeRetired, ct);
    }

    private async Task<List<MetricGroup>> GroupsAsync(bool includeRetired, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        var groups = await db.MetricGroups.AsNoTracking().Include(g => g.Metrics)
            .Where(g => includeRetired || !g.IsRetired).ToListAsync(ct);
        foreach (var g in groups)
            g.Metrics = g.Metrics.Where(m => includeRetired || !m.IsRetired).InOrder(m => m.SortOrder, m => m.Name).ToList();
        return groups.InOrder(g => g.SortOrder, g => g.Name).ToList();
    }

    /// <summary>The metrics for <paramref name="day"/>: each value, its comparison, threshold level and sparkline. A Visitor
    /// gets only the day of the live meeting they're in (its numbers slides), never the history.</summary>
    public async Task<MetricDay> GetDayAsync(DateOnly day, CancellationToken ct = default)
    {
        if (!Briefs.MeetingAccess.SeatedOn(user, meetings, day)) RequireRead();
        var groups = (await GroupsAsync(false, ct)).Where(g => g.Metrics.Count > 0).ToList();
        if (groups.Count == 0) return new MetricDay(day, []) { Days = clock?.Days };
        var ids = groups.SelectMany(g => g.Metrics).Select(m => m.Id).ToList();
        var from = day.AddDays(-LookBackDays);
        await using var db = await dbs.CreateAsync(ct);
        var values = (await db.MetricValues.AsNoTracking()
                .Where(v => ids.Contains(v.DefinitionId) && v.BriefDate >= from && v.BriefDate <= day)
                .ToListAsync(ct))
            .ToLookup(v => v.DefinitionId);
        var sparkDays = MetricMath.BusinessDaysEnding(day, SparkDays, clock?.Days);

        return new MetricDay(day, groups.Select(g =>
        {
            // A holiday counts as a trend day for the group once someone has entered one of its numbers on it.
            var trendDays = MetricMath.TrendDaysWithin(day, TrendDays, g.Metrics.SelectMany(m => values[m.Id]).Select(v => v.BriefDate), clock?.Days);
            return new MetricGroupDay(g, g.Metrics.Select(m => Tile(m, values[m.Id].ToList(), trendDays)).ToList()) { TrendDays = trendDays };
        }).ToList()) { Days = clock?.Days };

        MetricTile Tile(MetricDefinition m, List<MetricValue> mine, IReadOnlyList<DateOnly> trendDays)
        {
            var byDate = mine.ToDictionary(v => v.BriefDate, v => v.Value);
            return MetricTile.On(m, mine, day) with
            {
                Spark = sparkDays.Select(d => byDate.TryGetValue(d, out var v) ? v : (decimal?)null).ToList(),
                Trend = trendDays.Select(d => byDate.TryGetValue(d, out var v) ? v : (decimal?)null).ToList(),
            };
        }
    }

    /// <summary>Every metric, retired ones too, and its values from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public async Task<MetricHistory> HistoryAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        RequireRead();
        if (to < from) (from, to) = (to, from);
        if (to.DayNumber - from.DayNumber > MaxRangeDays) throw new ArgumentException("Choose a range of three years or less.");
        await using var db = await dbs.CreateAsync(ct);
        var metrics = await db.MetricDefinitions.AsNoTracking().Include(m => m.Group).ToListAsync(ct);
        var groupOrder = metrics.Select(m => m.Group!).DistinctBy(g => g.Id).InOrder(g => g.SortOrder, g => g.Name).Select((g, i) => (g.Id, i)).ToDictionary();
        var ordered = metrics.OrderBy(m => groupOrder[m.GroupId]).ThenBy(m => m.SortOrder).ThenByName(m => m.Name).ThenBy(m => m.Id).ToList();
        var values = await db.MetricValues.AsNoTracking().Where(v => v.BriefDate >= from && v.BriefDate <= to).ToListAsync(ct);
        return new MetricHistory(from, to, ordered, values.OrderBy(v => v.BriefDate).ToList());
    }

    /// <summary>
    /// The history as CSV, one row per value: date, group, metric key and name, value, format, direction, note and who
    /// entered it. Text cells that a spreadsheet would read as a formula are quoted with a leading apostrophe.
    /// </summary>
    public static string ToCsv(MetricHistory history)
    {
        var byId = history.Metrics.ToDictionary(m => m.Id);
        var sb = new StringBuilder("date,group,metric_key,metric,value,format,direction,retired,note,entered_by\r\n");
        foreach (var v in history.Values)
        {
            if (!byId.TryGetValue(v.DefinitionId, out var m)) continue;
            sb.AppendJoin(',',
                v.BriefDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Csv.Cell(m.Group?.Name), Csv.Cell(m.Key), Csv.Cell(m.Name),
                v.Value.ToString("0.####", CultureInfo.InvariantCulture),
                m.Format, m.Direction, m.IsRetired ? "yes" : "no",
                Csv.Cell(v.Note), Csv.Cell(v.EnteredByName));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    // --- entering values -----------------------------------------------------------------------------------------

    /// <summary>Sets (or, with a null value, clears) a metric's value for a brief date.</summary>
    public async Task SaveValueAsync(Guid metricId, DateOnly day, decimal? value, string? note, CancellationToken ct = default)
    {
        if (!user.Has(Permission.EnterMetrics)) throw new ForbiddenException("Only analysts can enter the day's metrics.");
        // A holiday's numbers are kept if someone enters them (they're just not expected); a weekend's never are.
        if (WorkingDays.IsWeekend(day)) throw new ArgumentException("Metrics are kept for business days.");
        if (value is { } v && !MetricMath.Fits(v)) throw new ArgumentException("That number is too large.");
        var cleanNote = Articles.ArticleService.CleanOrNull(note, 300);
        // Two people saving the day's first value at the same moment both found no row and both added one; the second
        // add hit the unique (metric, day) index. Read again on a fresh context and save once more: it then updates the
        // row the first made, recorded as a change of it (the failed add and its audit entry were never saved).
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await SaveValueOnceAsync(metricId, day, value, cleanNote, ct);
                return;
            }
            catch (DbUpdateException ex) when (attempt == 0 && ex is not DbUpdateConcurrencyException && ex.Entries.Any(e => e.State == EntityState.Added))
            {
                /* added by a colleague meanwhile: read it and save again */
            }
        }
    }

    private async Task SaveValueOnceAsync(Guid metricId, DateOnly day, decimal? value, string? cleanNote, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        if (!user.Has(Permission.Administer) && await IsLockedAsync(db, day, ct))
            throw new InvalidOperationException("The brief for this day has been sent, so its numbers are fixed. An administrator can correct them.");
        var metric = await db.MetricDefinitions.FirstOrDefaultAsync(m => m.Id == metricId, ct)
                     ?? throw new KeyNotFoundException("That metric no longer exists.");
        var row = await db.MetricValues.FirstOrDefaultAsync(x => x.DefinitionId == metricId && x.BriefDate == day, ct);
        if (value is null)
        {
            if (row is not null) db.MetricValues.Remove(row);
        }
        else
        {
            if (metric.IsRetired) throw new InvalidOperationException("That metric is retired; bring it back to enter values.");
            if (row is null) db.MetricValues.Add(row = new MetricValue { DefinitionId = metricId, BriefDate = day });
            var rounded = Math.Round(value.Value, 4);
            // Typing over an imported number makes it the analyst's: later imports leave it alone (MetricImportService).
            if (row.Source == MetricSource.Imported && row.Value != rounded) row.IsOverride = true;
            row.Source = MetricSource.Manual;
            row.ImportId = null;
            row.Value = rounded;
            row.Note = cleanNote;
            row.EnteredById = user.UserId;
            row.EnteredByName = user.DisplayName;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Whether the current user can no longer change <paramref name="day"/>'s numbers: its brief has been sent
    /// (what went out is the record), unless they're an administrator.</summary>
    public async Task<bool> IsLockedAsync(DateOnly day, CancellationToken ct = default)
    {
        if (user.Has(Permission.Administer)) return false;
        await using var db = await dbs.CreateAsync(ct);
        return await IsLockedAsync(db, day, ct);
    }

    /// <summary>Whether <paramref name="day"/>'s brief was finished without sending ("Not sent": no real send has gone
    /// since), so its fixed numbers never went out.</summary>
    public async Task<bool> FinishedNotSentAsync(DateOnly day, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        return await db.Briefs.AnyAsync(b => b.BriefDate == day && b.Status == BriefStatus.Sent && b.NotSentAtUtc != null
            && !db.BriefSends.Any(s => s.BriefId == b.Id && !s.IsTest && s.Status == SendStatus.Sent), ct);
    }

    private static Task<bool> IsLockedAsync(IAppDbContext db, DateOnly day, CancellationToken ct) =>
        db.Briefs.AnyAsync(b => b.BriefDate == day && b.Status == BriefStatus.Sent, ct);

    // --- administering -------------------------------------------------------------------------------------------

    public async Task<Guid> AddGroupAsync(string name, CancellationToken ct = default)
    {
        RequireAdmin();
        var clean = AdminLists.Name(name);
        await using var db = await dbs.CreateAsync(ct);
        var key = await AdminLists.UniqueKeyAsync(ScorecardService.KeyFor(clean), k => db.MetricGroups.AnyAsync(g => g.Key == k, ct));
        var order = await db.MetricGroups.Select(g => (int?)g.SortOrder).MaxAsync(ct) ?? -1;
        var group = new MetricGroup { Key = key, Name = clean, SortOrder = order + 1 };
        db.MetricGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return group.Id;
    }

    public async Task RenameGroupAsync(Guid id, string name, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var group = await db.MetricGroups.FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new KeyNotFoundException("That group no longer exists.");
        group.Name = AdminLists.Name(name);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Retires (or brings back) a group. One with no metric that has values is deleted instead, with its metrics.</summary>
    public async Task SetGroupRetiredAsync(Guid id, bool retired, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var group = await db.MetricGroups.Include(g => g.Metrics).FirstOrDefaultAsync(g => g.Id == id, ct)
                    ?? throw new KeyNotFoundException("That group no longer exists.");
        var ids = group.Metrics.Select(m => m.Id).ToList();
        if (retired && !await db.MetricValues.AnyAsync(v => ids.Contains(v.DefinitionId), ct))
        {
            db.MetricDefinitions.RemoveRange(group.Metrics);
            db.MetricGroups.Remove(group);
        }
        else group.IsRetired = retired;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sets the chart a group's slide draws under its numbers.</summary>
    public async Task SetGroupChartAsync(Guid id, MetricChart chart, CancellationToken ct = default)
    {
        RequireAdmin();
        if (!Enum.IsDefined(chart)) throw new ArgumentException("Choose a chart.");
        await using var db = await dbs.CreateAsync(ct);
        var group = await db.MetricGroups.FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new KeyNotFoundException("That group no longer exists.");
        group.Chart = chart;
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveGroupAsync(Guid id, int by, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var all = (await db.MetricGroups.ToListAsync(ct)).InOrder(g => g.SortOrder, g => g.Name).ToList();
        AdminLists.Move(all, all.FindIndex(g => g.Id == id), by, (g, i) => g.SortOrder = i);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Adds a metric (no <see cref="MetricForm.Id"/>) or changes one. A rename keeps its key and history.</summary>
    public async Task<Guid> SaveMetricAsync(MetricForm form, CancellationToken ct = default)
    {
        RequireAdmin();
        var name = AdminLists.Name(form.Name);
        if (!Enum.IsDefined(form.Format) || !Enum.IsDefined(form.Direction)) throw new ArgumentException("Choose a format and a direction.");
        if (MetricMath.ThresholdProblem(form.Direction, form.WarnThreshold, form.CriticalThreshold) is { } problem) throw new ArgumentException(problem);
        // Thresholds are kept in the same column as values: one too large (or too fine) would only fail on SQL Server.
        if ((form.WarnThreshold is { } warn && !MetricMath.Fits(warn)) || (form.CriticalThreshold is { } crit && !MetricMath.Fits(crit)))
            throw new ArgumentException("That threshold is too large.");
        await using var db = await dbs.CreateAsync(ct);
        if (!await db.MetricGroups.AnyAsync(g => g.Id == form.GroupId, ct)) throw new KeyNotFoundException("That group no longer exists.");
        MetricDefinition metric;
        if (form.Id is { } id)
        {
            metric = await db.MetricDefinitions.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new KeyNotFoundException("That metric no longer exists.");
            if (metric.GroupId != form.GroupId)
                metric.SortOrder = (await db.MetricDefinitions.Where(m => m.GroupId == form.GroupId).Select(m => (int?)m.SortOrder).MaxAsync(ct) ?? -1) + 1;
        }
        else
        {
            var key = await AdminLists.UniqueKeyAsync(ScorecardService.KeyFor(name), k => db.MetricDefinitions.AnyAsync(m => m.Key == k, ct));
            var order = await db.MetricDefinitions.Where(m => m.GroupId == form.GroupId).Select(m => (int?)m.SortOrder).MaxAsync(ct) ?? -1;
            metric = new MetricDefinition { Key = key, SortOrder = order + 1 };
            db.MetricDefinitions.Add(metric);
        }
        metric.GroupId = form.GroupId;
        metric.Name = name;
        metric.Format = form.Format;
        metric.Direction = form.Direction;
        metric.WarnThreshold = form.WarnThreshold is { } w ? Math.Round(w, 4) : null;
        metric.CriticalThreshold = form.CriticalThreshold is { } c ? Math.Round(c, 4) : null;
        metric.FlagMissing = form.FlagMissing;
        await db.SaveChangesAsync(ct);
        return metric.Id;
    }

    /// <summary>Retires (or brings back) a metric. One that never had a value is deleted instead of retired.</summary>
    public async Task SetMetricRetiredAsync(Guid id, bool retired, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var metric = await db.MetricDefinitions.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new KeyNotFoundException("That metric no longer exists.");
        if (retired && !await db.MetricValues.AnyAsync(v => v.DefinitionId == id, ct)) db.MetricDefinitions.Remove(metric);
        else metric.IsRetired = retired;
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveMetricAsync(Guid id, int by, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var metric = await db.MetricDefinitions.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new KeyNotFoundException("That metric no longer exists.");
        var siblings = (await db.MetricDefinitions.Where(m => m.GroupId == metric.GroupId).ToListAsync(ct)).InOrder(m => m.SortOrder, m => m.Name).ToList();
        AdminLists.Move(siblings, siblings.FindIndex(m => m.Id == id), by, (m, i) => m.SortOrder = i);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Adds the example set's groups that aren't there yet (by key). Returns how many groups were added.</summary>
    public async Task<int> ImportExampleSetAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var groupKeys = (await db.MetricGroups.Select(g => g.Key).ToListAsync(ct)).ToHashSet();
        var metricKeys = (await db.MetricDefinitions.Select(m => m.Key).ToListAsync(ct)).ToHashSet();
        var order = await db.MetricGroups.Select(g => (int?)g.SortOrder).MaxAsync(ct) ?? -1;
        var added = 0;
        foreach (var (groupName, metrics) in ExampleSet)
        {
            var key = ScorecardService.KeyFor(groupName);
            if (!groupKeys.Add(key)) continue;
            var group = new MetricGroup { Key = key, Name = groupName, SortOrder = ++order };
            var i = 0;
            foreach (var (name, direction, warn, critical) in metrics)
            {
                var metricKey = ScorecardService.KeyFor(groupName + " " + name);
                for (var n = 2; !metricKeys.Add(metricKey); n++) metricKey = $"{ScorecardService.KeyFor(groupName + " " + name)}-{n}";
                group.Metrics.Add(new MetricDefinition
                {
                    Key = metricKey, Name = name, Direction = direction, WarnThreshold = warn, CriticalThreshold = critical, SortOrder = i++,
                });
            }
            db.MetricGroups.Add(group);
            added++;
        }
        await db.SaveChangesAsync(ct);
        return added;
    }

    private void RequireRead()
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("You don't have access to the metrics.");
    }

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can define metrics.");
    }
}
