using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Metrics;

/// <summary>A stretch of a metric's value axis and the level a value in it reads at: from <see cref="From"/> to
/// <see cref="To"/>, low to high, where null is open-ended (below everything, or above it).</summary>
public sealed record MetricBand(decimal? From, decimal? To, MetricLevel Level);

/// <summary>
/// The bands a metric's thresholds cut its value axis into, as <see cref="MetricMath.LevelOf"/> reads them: the
/// present-mode bullet bars draw them (MetricCharts), and the metric history's charts shade them. One rule, so the two
/// can't disagree.
/// </summary>
public static class MetricBands
{
    /// <summary>The metric's thresholds, low to high, with the level each one starts.</summary>
    public static IReadOnlyList<(decimal At, MetricLevel Level)> Stops(MetricDefinition metric)
    {
        var stops = new List<(decimal At, MetricLevel Level)>();
        if (metric.WarnThreshold is { } w) stops.Add((w, MetricLevel.Warn));
        if (metric.CriticalThreshold is { } c) stops.Add((c, MetricLevel.Critical));
        stops.Sort((a, b) => a.At.CompareTo(b.At));
        return stops;
    }

    /// <summary>
    /// The bands from the bottom of the axis up, normal included; none when the metric has no thresholds. Higher is
    /// better: the low end is the bad one, so the band below each threshold is that threshold's level, and normal is above
    /// them all. Otherwise (lower is better, or neutral) normal is below the first threshold, and the band above each is
    /// that threshold's level.
    /// </summary>
    public static IReadOnlyList<MetricBand> Of(MetricDefinition metric)
    {
        var stops = Stops(metric);
        if (stops.Count == 0) return [];
        var edges = stops.Select(s => (decimal?)s.At).Prepend(null).Append(null).ToList();
        var levels = metric.Direction == MetricDirection.HigherIsBetter
            ? stops.Select(s => s.Level).Append(MetricLevel.Normal)
            : stops.Select(s => s.Level).Prepend(MetricLevel.Normal);
        return [.. levels.Select((level, i) => new MetricBand(edges[i], edges[i + 1], level))];
    }
}
