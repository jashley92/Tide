using Tide.Domain.Entities;

namespace Tide.Application.Metrics;

/// <summary>One group's run of columns in the metric history table: its heading spans them.</summary>
public sealed record MetricColumnGroup(Guid GroupId, string Key, string Name, IReadOnlyList<MetricDefinition> Metrics);

/// <summary>
/// The metric history table's columns (MetricsHistory): the metrics by group, in the history's order, with a heading per
/// group over its own columns, and the group filter kept in the page's address (<c>?groups=email-triage,cases</c>, by the
/// groups' keys, which never change) so a reload or a shared link shows the same groups.
/// </summary>
public static class MetricHistoryColumns
{
    /// <summary>The query parameter the filter is kept in.</summary>
    public const string Parameter = "groups";

    /// <summary>
    /// The metrics as runs of columns, one per group, in the order given (the history's: by group, then metric). A group's
    /// metrics stay together even when the order interleaves two groups (two with the same place and name), so no group
    /// gets two headings or two chips.
    /// </summary>
    public static IReadOnlyList<MetricColumnGroup> Group(IEnumerable<MetricDefinition> metrics)
    {
        var runs = new List<MetricColumnGroup>();
        var byGroup = new Dictionary<Guid, List<MetricDefinition>>();
        foreach (var m in metrics)
        {
            if (!byGroup.TryGetValue(m.GroupId, out var run))
            {
                byGroup[m.GroupId] = run = [];
                runs.Add(new MetricColumnGroup(m.GroupId, m.Group?.Key ?? "", m.Group?.Name ?? "", run));
            }
            run.Add(m);
        }
        return runs;
    }

    /// <summary>The group keys a query value names (empty for all groups).</summary>
    public static IReadOnlySet<string> Parse(string? query) =>
        (query ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The query value for the groups chosen; null (no parameter) for all of them.</summary>
    public static string? Query(IEnumerable<string> keys)
    {
        var list = keys.Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? null : string.Join(',', list);
    }

    /// <summary>
    /// The groups shown: those <paramref name="chosen"/> names that the history has, or every group when it names none
    /// of them (so a link naming a group since removed still shows something).
    /// </summary>
    public static IReadOnlyList<MetricColumnGroup> Shown(IReadOnlyList<MetricColumnGroup> all, IReadOnlySet<string> chosen)
    {
        var picked = all.Where(g => chosen.Contains(g.Key)).ToList();
        return picked.Count == 0 ? all : picked;
    }

    /// <summary>
    /// The choice after a group's chip is pressed: with every group showing it picks that one alone; otherwise it adds or
    /// takes away that group, and taking away the last goes back to all.
    /// </summary>
    public static IReadOnlySet<string> Toggle(IReadOnlyList<MetricColumnGroup> all, IReadOnlySet<string> chosen, string key)
    {
        var current = all.Where(g => chosen.Contains(g.Key)).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!current.Add(key)) current.Remove(key);
        return current.Count == all.Count ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : current;
    }
}
