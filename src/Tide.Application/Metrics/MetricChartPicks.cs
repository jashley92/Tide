using Tide.Domain.Entities;

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
        var items = MetricHistoryColumns.Parse(query);
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
            // Written as the group only when it has current metrics: a group's key stands for those (Resolve).
            var whole = group.Any(m => !m.IsRetired) && Whole([.. group], ids) && group.First().Group is not null;
            if (whole) items.Add(GroupPrefix + group.First().Group!.Key);
            items.AddRange(group.Where(m => ids.Contains(m.Id) && !(whole && !m.IsRetired)).Select(m => m.Key));
        }
        return items.Count == 0 ? null : string.Join(',', items);
    }

    /// <summary>The metrics picked after one is ticked or unticked, in the history's order.</summary>
    public static IReadOnlyList<MetricDefinition> Toggle(IReadOnlyList<MetricDefinition> metrics, IReadOnlyList<MetricDefinition> picked, MetricDefinition metric)
    {
        var ids = Ids(picked);
        if (!ids.Add(metric.Id)) ids.Remove(metric.Id);
        return InOrder(metrics, ids);
    }

    /// <summary>
    /// The metrics picked after a group's box is pressed: with all its current metrics charted it takes the whole group
    /// off (retired ones too); otherwise it adds its current metrics (or, in a group all retired, all of them).
    /// </summary>
    public static IReadOnlyList<MetricDefinition> ToggleGroup(IReadOnlyList<MetricDefinition> metrics, IReadOnlyList<MetricDefinition> picked, Guid groupId)
    {
        var ids = Ids(picked);
        var group = metrics.Where(m => m.GroupId == groupId).ToList();
        if (Whole(group, ids)) ids.ExceptWith(group.Select(m => m.Id));
        else ids.UnionWith(Current(group).Select(m => m.Id));
        return InOrder(metrics, ids);
    }

    /// <summary>Whether a group's box reads ticked (every current metric charted), mixed (some of it) or not.</summary>
    public static bool? GroupState(IEnumerable<MetricDefinition> group, IReadOnlyList<MetricDefinition> picked)
    {
        var ids = Ids(picked);
        var list = group.ToList();
        return Whole(list, ids) ? true : list.Any(m => ids.Contains(m.Id)) ? null : false;
    }

    // Whether every one of a group's current metrics is among those picked.
    private static bool Whole(List<MetricDefinition> group, HashSet<Guid> ids) => Current(group).All(m => ids.Contains(m.Id));

    private static HashSet<Guid> Ids(IEnumerable<MetricDefinition> picked) => picked.Select(m => m.Id).ToHashSet();

    private static List<MetricDefinition> InOrder(IReadOnlyList<MetricDefinition> metrics, HashSet<Guid> ids) => [.. metrics.Where(m => ids.Contains(m.Id))];

    // A group's current metrics, or all of them when every one is retired (so its box still does something).
    private static List<MetricDefinition> Current(List<MetricDefinition> group) =>
        group.Any(m => !m.IsRetired) ? [.. group.Where(m => !m.IsRetired)] : group;

    /// <summary>Whether the metrics can share one chart: a few of them, all in one unit (a count and a percentage on one
    /// axis would read as comparable).</summary>
    public static bool CanOverlay(IReadOnlyList<MetricDefinition> picked) =>
        picked.Count <= MaxOverlay && picked.Select(m => m.Format).Distinct().Count() <= 1;

    /// <summary>The view drawn: the one asked for (overlay by default) when the metrics can share a chart, else a grid.</summary>
    public static MetricChartView ViewFor(IReadOnlyList<MetricDefinition> picked, string? asked) =>
        CanOverlay(picked) && !string.Equals(asked, GridValue, StringComparison.OrdinalIgnoreCase) ? MetricChartView.Overlay : MetricChartView.Grid;

    /// <summary>The view parameter's value for a grid (overlay, the default, has none).</summary>
    public const string GridValue = "grid";
}
