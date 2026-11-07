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
/// One metric's panel in a grid of small trends: its <see cref="Line"/> on a scale of its own, the <see cref="Areas"/>
/// under each run of it (SVG polygon points), a dot on its <see cref="Latest"/> value, and the <see cref="Max"/> and
/// <see cref="Min"/> of its axis. <see cref="Level"/> is today's value's, for the corner.
/// </summary>
public sealed record TrendPanel(TrendLine Line, MetricLevel Level, IReadOnlyList<string> Areas, ChartPoint? Latest, ChartTick Max, ChartTick Min);

/// <summary>A small trend per metric, laid out <see cref="Columns"/> across, over the days from the first date to the last.</summary>
public sealed record SmallMultiplesChart(IReadOnlyList<TrendPanel> Panels, int Columns, string FirstDate, string LastDate);

/// <summary>One metric's part of a day's column: from <see cref="Y"/> down by <see cref="Height"/>, in the viewBox.</summary>
public sealed record StackSegment(int Series, double Y, double Height);

/// <summary>A day's column on the stacked chart: where it stands, its <see cref="Top"/>, its total, and its parts from
/// the bottom up, in the group's order.</summary>
public sealed record StackColumn(string Date, double X, double Width, double Top, string Total, IReadOnlyList<StackSegment> Segments);

/// <summary>
/// The mix over time: a column per working day that has any value (a day with none is a gap), the value axis's labels,
/// the first and last dates, and a key of today's values and shares. <see cref="Empty"/>: nothing above 0 to share out
/// on any day; <see cref="NoHistory"/>: fewer than two days have values, so there's no "over time" to draw.
/// </summary>
public sealed record StackedChart(IReadOnlyList<StackColumn> Columns, IReadOnlyList<ChartTick> Ticks, IReadOnlyList<DonutSlice> Key,
    string FirstDate, string LastDate, bool Empty, bool NoHistory);

/// <summary>A shaded band behind a bullet bar, from 0 to 1 of its scale: a value in it is at <see cref="Level"/>.</summary>
public sealed record BulletBand(MetricLevel Level, double From, double To);

/// <summary>A threshold's edge on a bullet bar, at 0 to 1 of its scale, and its label ("warning 10"): ending at the edge
/// when <see cref="LabelBefore"/>, starting at it otherwise.</summary>
public sealed record BulletEdge(MetricLevel Level, double At, string Label, bool LabelBefore = false);

/// <summary>
/// A metric's bullet bar: today's value as a <see cref="Fraction"/> of its row's scale, over the bands its thresholds
/// make. No bands: the metric has no threshold set, so the bar is plain.
/// </summary>
public sealed record BulletRow(int Series, string Name, string Value, MetricLevel Level, double Fraction, IReadOnlyList<BulletBand> Bands,
    IReadOnlyList<BulletEdge> Edges)
{
    public bool HasThresholds => Bands.Count > 0;
}

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

    /// <summary>A small trend's viewBox, and its plot area within it: room on the left for its max and min.</summary>
    public const double PanelWidth = 600, PanelHeight = 150;

    public const double PanelLeft = 110, PanelRight = 588, PanelTop = 12, PanelBottom = 138;

    /// <summary>How far past the larger of today's value and the thresholds a bullet's scale runs, so neither sits on its end.</summary>
    public const decimal BulletHeadroom = 1.15m;

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

        var lines = tiles.Select((t, s) => LineOf(t, s, n, X, Y)).ToList();
        return new TrendChart(lines, Ticks(range, format), Day(days[0]), Day(days[^1]));
    }

    /// <summary>
    /// A small trend per tile over <paramref name="days"/> (oldest first), each on its own scale, so a metric in the
    /// thousands doesn't flatten one in single figures as it does on <see cref="Trend"/>. Null when there's no history to
    /// draw, as for <see cref="Trend"/>: no metric has values on two of the days.
    /// </summary>
    public static SmallMultiplesChart? SmallMultiples(IReadOnlyList<DateOnly> days, IReadOnlyList<MetricTile> tiles)
    {
        var n = days.Count;
        if (n == 0 || !tiles.Any(t => Known(t, n).Count() >= 2)) return null;
        return new SmallMultiplesChart(tiles.Select((t, s) => Panel(t, s, n)).ToList(), GridColumns(tiles.Count), Day(days[0]), Day(days[^1]));
    }

    /// <summary>How many small trends go across: two for up to four metrics, three up to nine, four beyond (and one for a
    /// group of one, which would otherwise sit in half the slide). Few enough that each stays readable on a projector.</summary>
    public static int GridColumns(int count) => count switch
    {
        <= 1 => 1,
        <= 4 => 2,
        <= 9 => 3,
        _ => 4,
    };

    // A tile's small trend, scaled to its own values.
    private static TrendPanel Panel(MetricTile t, int s, int n)
    {
        var known = Known(t, n).ToList();
        var range = MetricMath.ChartRange(known.Select(k => k.Value), fromZero: known.TrueForAll(k => k.Value >= 0));
        double X(int i) => n > 1 ? PanelLeft + (PanelRight - PanelLeft) * i / (n - 1) : (PanelLeft + PanelRight) / 2;
        double Y(decimal v) => PanelBottom - (PanelBottom - PanelTop) * MetricMath.ChartHeight(v, range);
        var areas = Runs(t, n).Where(r => r.Count > 1).Select(r => string.Join(' ',
            r.Select(i => Pt(X(i), Y(t.Trend[i]!.Value))).Prepend(Pt(X(r[0]), PanelBottom)).Append(Pt(X(r[^1]), PanelBottom)))).ToList();
        ChartPoint? latest = known.Count > 0 ? new ChartPoint(X(known[^1].Index), Y(known[^1].Value), MetricMath.LevelOf(t.Metric, known[^1].Value)) : null;
        return new TrendPanel(LineOf(t, s, n, X, Y), t.Level, areas, latest,
            new ChartTick(PanelTop, MetricMath.Format(t.Metric.Format, range.Max)), new ChartTick(PanelBottom, MetricMath.Format(t.Metric.Format, range.Min)));
    }

    /// <summary>
    /// The mix over <paramref name="days"/> (oldest first): a column per day of the tiles' <see cref="MetricTile.Trend"/>
    /// values stacked in the group's order from the bottom, so both the day's total and its mix read. A negative value
    /// counts as 0, as on <see cref="Share"/>, whose key (today's value and share of each) this shares.
    /// </summary>
    public static StackedChart Stacked(IReadOnlyList<DateOnly> days, IReadOnlyList<MetricTile> tiles)
    {
        var n = days.Count;
        // A metric missed on a day the others have values counts as 0 within that day's column (the column is what was
        // entered). A day with no value for any metric is a gap, not a column of 0: nothing was entered, which isn't the
        // same as nothing to count.
        var parts = Enumerable.Range(0, n)
            .Select(i => tiles.Any(t => At(t, i) is not null) ? tiles.Select(t => Math.Max(0, At(t, i) ?? 0)).ToList() : null).ToList();
        var totals = parts.Where(p => p is not null).Select(p => p!.Sum()).ToList();
        var key = Share(tiles);
        var empty = key.Empty && totals.TrueForAll(t => t <= 0);
        var noHistory = totals.Count < 2;
        if (empty || noHistory) return new StackedChart([], [], key.Slices, n == 0 ? "" : Day(days[0]), n == 0 ? "" : Day(days[^1]), empty, noHistory);

        var range = MetricMath.ChartRange(totals, fromZero: true);
        var format = CommonFormat(tiles);
        double Y(decimal v) => PlotBottom - (PlotBottom - PlotTop) * MetricMath.ChartHeight(v, range);
        var slot = (PlotRight - PlotLeft) / n;
        var width = slot * .7;
        var columns = new List<StackColumn>();
        for (var i = 0; i < n; i++)
        {
            if (parts[i] is not { } day) continue;
            var segments = new List<StackSegment>();
            var below = 0m;
            for (var s = 0; s < day.Count; s++)
            {
                if (day[s] <= 0) continue;
                var top = Y(below + day[s]);
                segments.Add(new StackSegment(s, top, Y(below) - top));
                below += day[s];
            }
            columns.Add(new StackColumn(Day(days[i]), PlotLeft + slot * i + (slot - width) / 2, width, Y(below), MetricMath.Format(format, below), segments));
        }
        return new StackedChart(columns, Ticks(range, format), key.Slices, Day(days[0]), Day(days[^1]), false, false);
    }

    /// <summary>
    /// A bullet bar per tile: today's value over the bands its metric's thresholds make, applied in its direction as
    /// <see cref="MetricMath.LevelOf"/> applies them. Each row has its own scale, from 0 to the largest of the value and
    /// the thresholds, with <see cref="BulletHeadroom"/>. A negative value or none has no length.
    /// </summary>
    public static IReadOnlyList<BulletRow> Bullets(IReadOnlyList<MetricTile> tiles) => tiles.Select(Bullet).ToList();

    private static BulletRow Bullet(MetricTile t, int s)
    {
        var m = t.Metric;
        var value = Math.Max(0, t.Value ?? 0);
        var scale = Math.Max(value, Math.Max(m.WarnThreshold ?? 0, m.CriticalThreshold ?? 0)) * BulletHeadroom;
        if (scale <= 0) scale = 1;
        double F(decimal v) => (double)Math.Clamp(v / scale, 0, 1);

        var stops = new List<(decimal At, MetricLevel Level)>();
        if (m.WarnThreshold is { } w) stops.Add((w, MetricLevel.Warn));
        if (m.CriticalThreshold is { } c) stops.Add((c, MetricLevel.Critical));
        stops.Sort((a, b) => a.At.CompareTo(b.At));
        // The band edges from 0 up: 0, each threshold, the end of the scale. Higher is better: the low end is the bad one,
        // so the band below each threshold is that threshold's level, and normal is above them all. Otherwise (lower is
        // better, or neutral) normal is below the first threshold, and the band above each is that threshold's level.
        var bands = new List<BulletBand>();
        if (stops.Count > 0)
        {
            var at = stops.Select(x => F(x.At)).Prepend(0).Append(1).ToList();
            var levels = m.Direction == MetricDirection.HigherIsBetter
                ? stops.Select(x => x.Level).Append(MetricLevel.Normal)
                : stops.Select(x => x.Level).Prepend(MetricLevel.Normal);
            bands.AddRange(levels.Select((level, i) => new BulletBand(level, at[i], at[i + 1])).Where(b => b.To > b.From));
        }
        // A label starts at its edge (the headroom leaves room after the last one), but of two, the lower one ends at its
        // edge instead, so they lean apart and don't overlap, unless it's too near the start of the bar to.
        var edges = stops.Select((x, i) =>
        {
            var edgeAt = F(x.At);
            return new BulletEdge(x.Level, edgeAt, (x.Level == MetricLevel.Critical ? "critical " : "warning ") + MetricMath.Format(m.Format, x.At),
                LabelBefore: stops.Count == 2 && i == 0 && edgeAt >= .2);
        }).ToList();
        return new BulletRow(s, m.Name, t.Display, t.Level, F(value), bands, edges);
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

    // A tile's line on a trend: its runs, the days on their own, and the values past a threshold.
    private static TrendLine LineOf(MetricTile t, int s, int n, Func<int, double> x, Func<decimal, double> y)
    {
        var runs = Runs(t, n);
        return new TrendLine(s, t.Metric.Name, t.Display,
            runs.Where(r => r.Count > 1).Select(r => string.Join(' ', r.Select(i => Pt(x(i), y(t.Trend[i]!.Value))))).ToList(),
            runs.Where(r => r.Count == 1).Select(r => new ChartPoint(x(r[0]), y(t.Trend[r[0]]!.Value))).ToList(),
            Known(t, n).Select(p => (p, Level: MetricMath.LevelOf(t.Metric, p.Value))).Where(p => p.Level != MetricLevel.Normal)
                .Select(p => new ChartPoint(x(p.p.Index), y(p.p.Value), p.Level)).ToList());
    }

    // The value axis's labels on the full-size plot: the top, the middle (a count's half-way with its decimals), the bottom.
    private static List<ChartTick> Ticks((decimal Min, decimal Max) range, MetricFormat format)
    {
        var mid = (range.Min + range.Max) / 2;
        return
        [
            new(PlotTop, MetricMath.Format(format, range.Max)),
            new((PlotTop + PlotBottom) / 2, MetricMath.Format(format == MetricFormat.Count && mid != Math.Floor(mid) ? MetricFormat.Number : format, mid)),
            new(PlotBottom, MetricMath.Format(format, range.Min)),
        ];
    }

    // A tile's value on day i, or null where it has none (or its trend doesn't reach that far).
    private static decimal? At(MetricTile t, int i) => i < t.Trend.Count ? t.Trend[i] : null;

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

    private static string Day(DateOnly d) => Content.Words.MonthDay(d);
}
