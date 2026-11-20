using System.Globalization;

namespace Tide.Application.Metrics;

/// <summary>
/// The days the metric history covers (MetricsHistory), kept in its address (<c>?from=2026-10-01&amp;to=2026-10-30</c>)
/// so a reload or a shared link shows the same range. A range of N days covers N days counting both ends: "30 days" to
/// Friday starts on the Saturday 29 days before it.
/// </summary>
public static class MetricHistoryRange
{
    /// <summary>The query parameters the range is kept in.</summary>
    public const string FromParameter = "from", ToParameter = "to";

    /// <summary>How many days the page shows with no range in its address.</summary>
    public const int DefaultDays = 30;

    /// <summary>The <paramref name="days"/> days to <paramref name="to"/>, both ends counted.</summary>
    public static (DateOnly From, DateOnly To) Last(int days, DateOnly to) => (to.AddDays(-(Math.Max(1, days) - 1)), to);

    /// <summary>
    /// The range an address asks for, as of <paramref name="today"/> (the current brief date). A value that isn't a date
    /// is ignored: no end is today, and no start is the default span before the end. The two are put in order, neither is
    /// later than today (there are no numbers yet), and a range longer than the page reads is cut to its last
    /// <see cref="MetricsService.MaxRangeDays"/> days.
    /// </summary>
    public static (DateOnly From, DateOnly To) Parse(string? from, string? to, DateOnly today)
    {
        var end = Day(to) ?? today;
        var start = Day(from) ?? Last(DefaultDays, end < today ? end : today).From;
        if (start > end) (start, end) = (end, start);
        if (end > today) end = today;
        if (start > end) start = end;
        if (end.DayNumber - start.DayNumber > MetricsService.MaxRangeDays) start = end.AddDays(-MetricsService.MaxRangeDays);
        return (start, end);
    }

    /// <summary>A day as the address holds it ("2026-10-01").</summary>
    public static string Query(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? Day(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
