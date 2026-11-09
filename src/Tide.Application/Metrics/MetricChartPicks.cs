using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Metrics;

/// <summary>How the metric history page draws the metrics charted: on one chart, or a small chart each.</summary>
public enum MetricChartView
{
    /// <summary>Every line on one chart, on one value axis: only for a few metrics in the same unit.</summary>
    Overlay = 0,

    /// <summary>A small chart per metric, each on its own value axis, over the same days.</summary>
    Grid = 1,
}

/// <summary>
/// The metrics charted on the metric history page (MetricsHistory), kept in its address so a reload or a shared link
/// charts the same ones: <c>?chart=group:email-triage,mttr</c> names a group (its current metrics) or a metric, by keys
/// that never change. In the history's order, so a metric keeps its colour as others come and go.
/// </summary>
public static class MetricChartPicks
{
    /// <summary>The query parameters: the metrics charted, and the view asked for.</summary>
    public const string Parameter = "chart";
    public const string ViewParameter = "view";

    /// <summary>The most lines one chart takes before they can't be told apart; more go to a grid.</summary>
    public const int MaxOverlay = 6;

    private const string GroupPrefix = "group:";

    /// <summary>
    /// The metrics a query value names, in the history's order: a group's key stands for its current metrics, and a
    /// retired metric is charted only when named. When it names none the history has, the first current metric (or the
    /// first of any), so the page always charts something.
    /// </summary>
    public static IReadOnlyList<MetricDefinition> Resolve(IReadOnlyList<MetricDefinition> metrics, string? query)
    {
        var items = (query ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var picked = metrics.Where(m => items.Contains(m.Key) || (!m.IsRetired && m.Group is { } g && items.Contains(GroupPrefix + g.Key))).ToList();
        if (picked.Count > 0 || metrics.Count == 0) return picked;
        return [metrics.FirstOrDefault(m => !m.IsRetired) ?? metrics[0]];
    }

    /// <summary>
    /// The query value for <paramref name="picked"/>: a group whose current metrics are all picked is written as the
    /// group, so the address stays short and a metric added to it later is charted too. Null for nothing picked.
    /// </summary>
    public static string? Query(IReadOnlyList<MetricDefinition> metrics, IEnumerable<MetricDefinition> picked)
    {
        var ids = picked.Select(m => m.Id).ToHashSet();
        var items = new List<string>();
        foreach (var group in metrics.GroupBy(m => m.GroupId))
        {
            var current = group.Where(m => !m.IsRetired).ToList();
            var whole = current.Count > 0 && current.All(m => ids.Contains(m.Id)) && current[0].Group is not null;
            if (whole) items.Add(GroupPrefix + current[0].Group!.Key);
            items.AddRange(group.Where(m => ids.Contains(m.Id) && !(whole && !m.IsRetired)).Select(m => m.Key));
        }
        return items.Count == 0 ? null : string.Join(',', items);
    }

    /// <summary>The metrics picked after one is ticked or unticked, in the history's order.</summary>
    public static IReadOnlyList<MetricDefinition> Toggle(IReadOnlyList<MetricDefinition> metrics, IReadOnlyList<MetricDefinition> picked, MetricDefinition metric)
    {
        var ids = picked.Select(m => m.Id).ToHashSet();
        if (!ids.Add(metric.Id)) ids.Remove(metric.Id);
        return [.. metrics.Where(m => ids.Contains(m.Id))];
    }

    /// <summary>
    /// The metrics picked after a group's box is pressed: with all its current metrics charted it takes the whole group
    /// off (retired ones too); otherwise it adds its current metrics (or, in a group all retired, all of them).
    /// </summary>
    public static IReadOnlyList<MetricDefinition> ToggleGroup(IReadOnlyList<MetricDefinition> metrics, IReadOnlyList<MetricDefinition> picked, Guid groupId)
    {
        var ids = picked.Select(m => m.Id).ToHashSet();
        var group = metrics.Where(m => m.GroupId == groupId).ToList();
        var current = Current(group);
        if (current.All(m => ids.Contains(m.Id))) ids.ExceptWith(group.Select(m => m.Id));
        else ids.UnionWith(current.Select(m => m.Id));
        return [.. metrics.Where(m => ids.Contains(m.Id))];
    }

    /// <summary>Whether a group's box reads ticked (every current metric charted), mixed (some of it) or not.</summary>
    public static bool? GroupState(IEnumerable<MetricDefinition> group, IReadOnlyList<MetricDefinition> picked)
    {
        var ids = picked.Select(m => m.Id).ToHashSet();
        var list = group.ToList();
        if (Current(list).All(m => ids.Contains(m.Id))) return true;
        return list.Any(m => ids.Contains(m.Id)) ? null : false;
    }

    // A group's current metrics, or all of them when every one is retired (so its box still does something).
    private static List<MetricDefinition> Current(List<MetricDefinition> group) =>
        group.Any(m => !m.IsRetired) ? [.. group.Where(m => !m.IsRetired)] : group;

    /// <summary>Whether the metrics can share one chart: a few of them, all in one unit (a count and a percentage on one
    /// axis would read as comparable).</summary>
    public static bool CanOverlay(IReadOnlyList<MetricDefinition> picked) =>
        picked.Count <= MaxOverlay && picked.Select(m => m.Format).Distinct().Count() <= 1;

    /// <summary>The view drawn: the one asked for (overlay by default) when the metrics can share a chart, else a grid.</summary>
    public static MetricChartView ViewFor(IReadOnlyList<MetricDefinition> picked, string? asked) =>
        CanOverlay(picked) && !string.Equals(asked, "grid", StringComparison.OrdinalIgnoreCase) ? MetricChartView.Overlay : MetricChartView.Grid;

    /// <summary>The query value for a view: none for overlay, the default.</summary>
    public static string? ViewQuery(MetricChartView view) => view == MetricChartView.Grid ? "grid" : null;
}

/// <summary>A stretch of a metric's value axis that trips one of its thresholds, from <see cref="From"/> to
/// <see cref="To"/> (low to high, clipped to the chart).</summary>
public sealed record MetricBand(decimal From, decimal To, MetricLevel Level);

/// <summary>The threshold bands a metric's chart shades.</summary>
public static class MetricBands
{
    /// <summary>
    /// The bands of <paramref name="range"/> where a value would trip the metric's thresholds, as
    /// <see cref="MetricMath.LevelOf"/> reads them: below the thresholds when higher is better, above them otherwise.
    /// Critical wins where they overlap; a band wholly outside the range is left out.
    /// </summary>
    public static IReadOnlyList<MetricBand> Of(MetricDefinition metric, (decimal Min, decimal Max) range)
    {
        var bands = new List<MetricBand>();
        var low = metric.Direction == MetricDirection.HigherIsBetter;
        void Add(decimal from, decimal to, MetricLevel level)
        {
            (from, to) = (Math.Max(from, range.Min), Math.Min(to, range.Max));
            if (to > from) bands.Add(new MetricBand(from, to, level));
        }
        if (metric.CriticalThreshold is { } c)
        {
            if (low) Add(range.Min, c, MetricLevel.Critical);
            else Add(c, range.Max, MetricLevel.Critical);
        }
        if (metric.WarnThreshold is { } w)
        {
            if (low) Add(metric.CriticalThreshold is { } c2 && c2 < w ? c2 : range.Min, w, MetricLevel.Warn);
            else Add(w, metric.CriticalThreshold is { } c2 && c2 > w ? c2 : range.Max, MetricLevel.Warn);
        }
        return bands;
    }
}
