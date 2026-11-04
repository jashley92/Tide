using System.Globalization;
using Tide.Domain.Enums;

namespace Tide.Application.Metrics;

/// <summary>A point on a chart, in its viewBox's units, and the threshold level of the value there.</summary>
public sealed record ChartPoint(double X, double Y, MetricLevel Level = MetricLevel.Normal);

/// <summary>A label on a chart's value axis, at <see cref="Y"/> in the viewBox.</summary>
public sealed record ChartTick(double Y, string Label);

/// <summary>
/// One metric's line on a trend chart: <see cref="Lines"/> are SVG polyline points, one run of days with values each
/// (a day without a value breaks the line, rather than drawing it down to 0); <see cref="Dots"/> are the days a run of
/// one leaves on their own; <see cref="Breaches"/> are the values past a threshold.
/// </summary>
public sealed record TrendLine(int Series, string Name, string Current, IReadOnlyList<string> Lines, IReadOnlyList<ChartPoint> Dots,
    IReadOnlyList<ChartPoint> Breaches);

/// <summary>A trend chart: a line per metric, the value axis's labels, and the first and last dates under it.</summary>
public sealed record TrendChart(IReadOnlyList<TrendLine> Lines, IReadOnlyList<ChartTick> Ticks, string FirstDate, string LastDate);

/// <summary>A donut slice: <see cref="Path"/> is its SVG path, null for a value of 0 or none (it's still in the key).</summary>
public sealed record DonutSlice(int Series, string Name, string Value, string? Percent, string? Path);

/// <summary>A donut of a group's current values: the slices, and the total for its middle. <see cref="Empty"/>: nothing
/// above 0 to share out, so no ring is drawn.</summary>
public sealed record DonutChart(IReadOnlyList<DonutSlice> Slices, string Total, bool Empty);

/// <summary>A bar of a group's current values: its length, from 0 to 1 of the longest.</summary>
public sealed record ChartBar(int Series, string Name, string Value, double Fraction);

/// <summary>
/// The geometry of the charts on present mode's metric slides, kept here (pure, and tested on its own) so the slide only
/// draws what this works out: the trend's lines and axis, the donut's arcs, the bars' lengths. The trend and the donut
/// are SVG in a fixed viewBox (<see cref="TrendWidth"/> by <see cref="TrendHeight"/>, <see cref="DonutSize"/> square)
/// that scales with the slide, so its labels stay large on a projector. Series are numbered by the metric's place in its
/// group, so a metric keeps its colour from tile to chart to key.
/// </summary>
public static class MetricCharts
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public const double TrendWidth = 1000, TrendHeight = 400;

    /// <summary>The trend's plot area within its viewBox: room on the left for the value labels, below for the dates.</summary>
    public const double PlotLeft = 150, PlotRight = 975, PlotTop = 24, PlotBottom = 330;

    public const double DonutSize = 400, DonutOuter = 190, DonutInner = 118;

    /// <summary>
    /// The trend over <paramref name="days"/> (oldest first) of each tile's <see cref="MetricTile.Trend"/>; null when
    /// there's no history to draw: no metric has values on two of the days.
    /// </summary>
    public static TrendChart? Trend(IReadOnlyList<DateOnly> days, IReadOnlyList<MetricTile> tiles)
    {
        var n = days.Count;
        if (n == 0 || !tiles.Any(t => Known(t, n).Count() >= 2)) return null;
        var known = tiles.SelectMany(t => Known(t, n)).Select(k => k.Value).ToList();
        var range = MetricMath.ChartRange(known, fromZero: known.All(v => v >= 0));
        var format = CommonFormat(tiles);
        double X(int i) => n > 1 ? PlotLeft + (PlotRight - PlotLeft) * i / (n - 1) : (PlotLeft + PlotRight) / 2;
        double Y(decimal v) => PlotBottom - (PlotBottom - PlotTop) * MetricMath.ChartHeight(v, range);

        var lines = tiles.Select((t, s) =>
        {
            var runs = Runs(t, n);
            var points = Known(t, n).ToList();
            return new TrendLine(s, t.Metric.Name, t.Display,
                runs.Where(r => r.Count > 1).Select(r => string.Join(' ', r.Select(i => Pt(X(i), Y(t.Trend[i]!.Value))))).ToList(),
                runs.Where(r => r.Count == 1).Select(r => new ChartPoint(X(r[0]), Y(t.Trend[r[0]]!.Value))).ToList(),
                points.Select(p => (p, Level: MetricMath.LevelOf(t.Metric, p.Value))).Where(p => p.Level != MetricLevel.Normal)
                    .Select(p => new ChartPoint(X(p.p.Index), Y(p.p.Value), p.Level)).ToList());
        }).ToList();
        var mid = (range.Min + range.Max) / 2;
        var ticks = new List<ChartTick>
        {
            new(PlotTop, MetricMath.Format(format, range.Max)),
            new((PlotTop + PlotBottom) / 2, MetricMath.Format(format == MetricFormat.Count && mid != Math.Floor(mid) ? MetricFormat.Number : format, mid)),
            new(PlotBottom, MetricMath.Format(format, range.Min)),
        };
        return new TrendChart(lines, ticks, Day(days[0]), Day(days[^1]));
    }

    /// <summary>
    /// A donut of the tiles' current values, a slice each in the group's order from the top, clockwise. A negative value
    /// counts as 0 (a share can't be less than nothing); a metric with no value is in the key with "—" and no slice.
    /// </summary>
    public static DonutChart Share(IReadOnlyList<MetricTile> tiles)
    {
        var parts = tiles.Select(t => Math.Max(0, t.Value ?? 0)).ToList();
        var total = parts.Sum();
        var format = CommonFormat(tiles);
        var slices = new List<DonutSlice>(tiles.Count);
        var start = 0m;
        for (var s = 0; s < tiles.Count; s++)
        {
            var t = tiles[s];
            var part = parts[s];
            string? percent = t.Value is null || total <= 0 ? null : Percent(part, total);
            string? path = part > 0 && total > 0 ? Arc((double)(start / total), (double)((start + part) / total)) : null;
            slices.Add(new DonutSlice(s, t.Metric.Name, t.Display, percent, path));
            start += part;
        }
        return new DonutChart(slices, MetricMath.Format(format, total), total <= 0);
    }

    /// <summary>The tiles' current values as bars, each a fraction of the largest. A value of 0, a negative one or none
    /// has no length; with nothing above 0 every bar is empty, which still reads (each shows its value beside it).</summary>
    public static IReadOnlyList<ChartBar> Bars(IReadOnlyList<MetricTile> tiles)
    {
        var max = tiles.Select(t => t.Value ?? 0).DefaultIfEmpty(0).Max();
        return tiles.Select((t, s) => new ChartBar(s, t.Metric.Name, t.Display,
            max > 0 && t.Value is { } v && v > 0 ? (double)(v / max) : 0)).ToList();
    }

    /// <summary>
    /// The SVG path of a donut slice from <paramref name="from"/> to <paramref name="to"/> (fractions of the whole, 0 at
    /// the top, clockwise). A slice of the whole is two halves, since one arc can't start and end at the same point.
    /// </summary>
    public static string Arc(double from, double to)
    {
        if (to - from >= 1 - 1e-9) return Arc(0, .5) + " " + Arc(.5, 1);
        var large = to - from > .5 ? 1 : 0;
        var (ox0, oy0) = Polar(DonutOuter, from);
        var (ox1, oy1) = Polar(DonutOuter, to);
        var (ix1, iy1) = Polar(DonutInner, to);
        var (ix0, iy0) = Polar(DonutInner, from);
        return string.Create(Invariant,
            $"M{ox0:0.##} {oy0:0.##} A{DonutOuter} {DonutOuter} 0 {large} 1 {ox1:0.##} {oy1:0.##} L{ix1:0.##} {iy1:0.##} A{DonutInner} {DonutInner} 0 {large} 0 {ix0:0.##} {iy0:0.##} Z");
    }

    /// <summary>A share as the key says it: "42%", or "&lt;1%" for a sliver that would round to nothing.</summary>
    public static string Percent(decimal part, decimal total)
    {
        if (total <= 0) return "0%";
        var pct = Math.Round(part / total * 100, MidpointRounding.AwayFromZero);
        return part > 0 && pct == 0 ? "<1%" : pct.ToString("0", Invariant) + "%";
    }

    // A point on a circle round the donut's centre, a fraction of the way round from the top.
    private static (double X, double Y) Polar(double radius, double fraction)
    {
        var angle = fraction * 2 * Math.PI - Math.PI / 2;
        return (DonutSize / 2 + radius * Math.Cos(angle), DonutSize / 2 + radius * Math.Sin(angle));
    }

    // The days (by index) a tile has a value, among the first n.
    private static IEnumerable<(int Index, decimal Value)> Known(MetricTile t, int n) =>
        t.Trend.Take(n).Select((v, i) => (v, i)).Where(x => x.v.HasValue).Select(x => (x.i, x.v!.Value));

    // The runs of consecutive days with a value, by index: a day without one ends a run.
    private static List<List<int>> Runs(MetricTile t, int n)
    {
        var runs = new List<List<int>>();
        List<int>? run = null;
        for (var i = 0; i < Math.Min(n, t.Trend.Count); i++)
        {
            if (t.Trend[i] is null) { run = null; continue; }
            if (run is null) runs.Add(run = []);
            run.Add(i);
        }
        return runs;
    }

    // The format the group's values share, for the axis and the total; a plain number when they differ.
    private static MetricFormat CommonFormat(IReadOnlyList<MetricTile> tiles) =>
        tiles.Select(t => t.Metric.Format).Distinct().Count() == 1 ? tiles[0].Metric.Format : MetricFormat.Number;

    private static string Pt(double x, double y) => string.Create(Invariant, $"{x:0.#},{y:0.#}");

    private static string Day(DateOnly d) => d.ToString("MMM d", Invariant);
}
