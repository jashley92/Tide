using System.Globalization;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Metrics;

/// <summary>How a change reads for a metric. <see cref="Unchanged"/> and <see cref="Neutral"/> are both grey.</summary>
public enum MetricTrend
{
    /// <summary>The value didn't move.</summary>
    Unchanged = 0,
    Better = 1,
    Worse = 2,

    /// <summary>It moved, but the metric has no good direction.</summary>
    Neutral = 3,
}

/// <summary>Where a value sits against its metric's thresholds.</summary>
public enum MetricLevel
{
    Normal = 0,
    Warn = 1,
    Critical = 2,
}

/// <summary>A value compared with the one before it: which day that was, what it was, and how the change reads.</summary>
public sealed record MetricComparison(DateOnly Date, decimal Value, decimal Delta, MetricTrend Trend);

/// <summary>
/// The rules the opening metrics are read by, kept pure so they're tested on their own: which earlier value a day is
/// compared with, how a change reads in the metric's direction, how thresholds apply, and how numbers are written.
/// </summary>
public static class MetricMath
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// The value to compare <paramref name="day"/> with: the most recent earlier business day that has one. Monday compares
    /// with Friday, and a holiday with no value is skipped (the comparison names the date it used). Null when there's none.
    /// </summary>
    public static (DateOnly Date, decimal Value)? PreviousWithValue(IEnumerable<(DateOnly Date, decimal Value)> history, DateOnly day)
    {
        (DateOnly Date, decimal Value)? best = null;
        foreach (var v in history)
            if (v.Date < day && !WorkingDays.IsWeekend(v.Date) && (best is null || v.Date > best.Value.Date))
                best = v;
        return best;
    }

    /// <summary>
    /// The day a value is compared with, as the entry form names it beside the value: the weekday ("Fri") when it's the
    /// business day before <paramref name="day"/>, so the usual case stays short, and otherwise the date ("Oct 23"), so a
    /// comparison that skipped a missed day or a holiday doesn't pass for last Friday.
    /// </summary>
    public static string ReferenceLabel(DateOnly reference, DateOnly day, WorkingDays? workingDays = null) =>
        reference.ToString(reference == (workingDays ?? WorkingDays.Weekdays).Previous(day) ? "ddd" : "MMM d", Invariant);

    /// <summary><paramref name="value"/> compared with <paramref name="previous"/>, or null when there's nothing to compare.</summary>
    public static MetricComparison? Compare(MetricDirection direction, decimal? value, (DateOnly Date, decimal Value)? previous)
    {
        if (value is not { } v || previous is not { } p) return null;
        var delta = v - p.Value;
        return new MetricComparison(p.Date, p.Value, delta, TrendOf(direction, delta));
    }

    /// <summary>How a change reads: no change is <see cref="MetricTrend.Unchanged"/> whatever the direction.</summary>
    public static MetricTrend TrendOf(MetricDirection direction, decimal delta) => delta == 0
        ? MetricTrend.Unchanged
        : direction switch
        {
            MetricDirection.HigherIsBetter => delta > 0 ? MetricTrend.Better : MetricTrend.Worse,
            MetricDirection.LowerIsBetter => delta < 0 ? MetricTrend.Better : MetricTrend.Worse,
            _ => MetricTrend.Neutral,
        };

    /// <summary>
    /// Where <paramref name="value"/> sits against the metric's thresholds, applied in its direction: for HigherIsBetter a
    /// value at or below a threshold trips it; for LowerIsBetter and Neutral, at or above. Critical wins over warn.
    /// </summary>
    public static MetricLevel LevelOf(MetricDefinition metric, decimal? value)
    {
        if (value is not { } v) return MetricLevel.Normal;
        bool Trips(decimal? threshold) => threshold is { } t && (metric.Direction == MetricDirection.HigherIsBetter ? v <= t : v >= t);
        return Trips(metric.CriticalThreshold) ? MetricLevel.Critical : Trips(metric.WarnThreshold) ? MetricLevel.Warn : MetricLevel.Normal;
    }

    /// <summary>Why a pair of thresholds can't be used together, or null. Critical must be past warn in the metric's
    /// direction, or warn would never show.</summary>
    public static string? ThresholdProblem(MetricDirection direction, decimal? warn, decimal? critical)
    {
        if (warn is not { } w || critical is not { } c) return null;
        return direction == MetricDirection.HigherIsBetter
            ? c > w ? "For a metric where higher is better, the critical threshold must be at or below the warning one." : null
            : c < w ? "The critical threshold must be at or above the warning one." : null;
    }

    /// <summary>The largest value kept (beyond it a number is almost certainly a mistake, and the column's precision).</summary>
    public const decimal MaxValue = 100_000_000_000_000m;

    /// <summary>
    /// Reads a value as typed or imported: "1,204", "97.5%", "3.5h" and "-2" all read; thousands separators, a percent
    /// sign and an hours suffix are ignored, and the decimal point is a full stop.
    /// </summary>
    public static bool TryParseValue(string? text, out decimal value)
    {
        var clean = (text ?? "").Replace(",", "").Replace("%", "").Replace("h", "").Trim();
        return decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out value) && Math.Abs(value) < MaxValue;
    }

    /// <summary>A value as the metric shows it: "1,204", "97.5%", "3.5 h".</summary>
    public static string Format(MetricFormat format, decimal value)
    {
        var n = value.ToString(format == MetricFormat.Count ? "#,0" : "#,0.##", Invariant);
        return format switch
        {
            MetricFormat.Percent => n + "%",
            MetricFormat.Hours => n + " h",
            _ => n,
        };
    }

    /// <summary>A change with its sign: "+3", "−1.5%", "0".</summary>
    public static string FormatDelta(MetricFormat format, decimal delta)
    {
        if (delta == 0) return "0";
        var text = Format(format, Math.Abs(delta));
        return (delta > 0 ? "+" : "−") + text;
    }

    /// <summary>The arrow for a change: up, down, or level.</summary>
    public static string Arrow(decimal delta) => delta > 0 ? "▲" : delta < 0 ? "▼" : "▬";

    /// <summary>
    /// The working days ending at <paramref name="day"/> (inclusive), oldest first: the dates a sparkline plots.
    /// </summary>
    public static IReadOnlyList<DateOnly> BusinessDaysEnding(DateOnly day, int count, WorkingDays? workingDays = null)
    {
        var w = workingDays ?? WorkingDays.Weekdays;
        var days = new List<DateOnly>(count);
        var d = w.IsWorkingDay(day) ? day : w.Previous(day);
        while (days.Count < count)
        {
            days.Add(d);
            d = w.Previous(d);
        }
        days.Reverse();
        return days;
    }

    /// <summary>
    /// The working days among the <paramref name="calendarDays"/> calendar days ending at <paramref name="day"/>
    /// (inclusive), oldest first: the dates a group slide's trend chart plots. Weekends and holidays aren't days a value
    /// is expected, so they're left out rather than drawn as gaps.
    /// </summary>
    public static IReadOnlyList<DateOnly> WorkingDaysWithin(DateOnly day, int calendarDays, WorkingDays? workingDays = null)
    {
        var w = workingDays ?? WorkingDays.Weekdays;
        return Enumerable.Range(0, Math.Max(0, calendarDays)).Select(i => day.AddDays(i - calendarDays + 1)).Where(w.IsWorkingDay).ToList();
    }

    /// <summary>
    /// The days a group's trend chart plots: the working days among the <paramref name="calendarDays"/> calendar days
    /// ending at <paramref name="day"/>, plus <paramref name="day"/> itself and any holiday in the window that has a value
    /// in <paramref name="valueDays"/> (a holiday's numbers are kept if someone enters them). Weekends stay out, and so
    /// does a holiday with no values. Oldest first.
    /// </summary>
    public static IReadOnlyList<DateOnly> TrendDaysWithin(DateOnly day, int calendarDays, IEnumerable<DateOnly> valueDays, WorkingDays? workingDays = null)
    {
        var w = workingDays ?? WorkingDays.Weekdays;
        var entered = valueDays.ToHashSet();
        return Enumerable.Range(0, Math.Max(0, calendarDays)).Select(i => day.AddDays(i - calendarDays + 1))
            .Where(d => d == day || w.IsWorkingDay(d) || (!WorkingDays.IsWeekend(d) && entered.Contains(d))).ToList();
    }

    /// <summary>
    /// A sparkline as SVG polyline points in a <paramref name="width"/> by <paramref name="height"/> box, one x step per
    /// day, gaps (days without a value) skipped. Empty when fewer than two values.
    /// </summary>
    public static string SparkPoints(IReadOnlyList<decimal?> values, double width, double height)
    {
        var known = values.Select((v, i) => (v, i)).Where(x => x.v.HasValue).Select(x => (Value: (double)x.v!.Value, Index: x.i)).ToList();
        if (known.Count < 2) return "";
        var min = known.Min(k => k.Value);
        var max = known.Max(k => k.Value);
        var range = max - min;
        var step = values.Count > 1 ? width / (values.Count - 1) : width;
        return string.Join(' ', known.Select(k =>
        {
            var x = k.Index * step;
            var y = range == 0 ? height / 2 : height - (k.Value - min) / range * height;
            return string.Create(Invariant, $"{x:0.#},{y:0.#}");
        }));
    }

    /// <summary>
    /// The span a chart's value axis covers: the lowest to the highest value, or from zero when <paramref name="fromZero"/>
    /// (counts, where a line's height should read as its size). Flat data gets room around it, so the line sits mid-way.
    /// </summary>
    public static (decimal Min, decimal Max) ChartRange(IEnumerable<decimal> values, bool fromZero = false)
    {
        var known = values.ToList();
        if (known.Count == 0) return (0, 1);
        var min = fromZero ? Math.Min(0, known.Min()) : known.Min();
        var max = known.Max();
        if (max > min) return (min, max);
        return fromZero ? (min, min + 1) : (min - 1, max + 1);
    }

    /// <summary>How far up a chart a value sits: 0 at the bottom of <paramref name="range"/>, 1 at the top.</summary>
    public static double ChartHeight(decimal value, (decimal Min, decimal Max) range) =>
        range.Max == range.Min ? .5 : (double)((value - range.Min) / (range.Max - range.Min));

    /// <summary>A sparkline as block characters ("▁▃▅▇"), for places SVG can't go (Outlook). Days without a value are
    /// a thin space.</summary>
    public static string SparkText(IReadOnlyList<decimal?> values)
    {
        const string blocks = "▁▂▃▄▅▆▇█";
        var known = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (known.Count < 2) return "";
        var min = known.Min();
        var range = known.Max() - min;
        return string.Concat(values.Select(v => v is not { } x ? " "
            : blocks[range == 0 ? 3 : (int)Math.Round((x - min) / range * (blocks.Length - 1))].ToString()));
    }
}
