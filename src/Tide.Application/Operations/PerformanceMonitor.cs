namespace Tide.Application.Operations;

/// <summary>What a timing is of: a page or file the browser asked for, an event on an open page, a database command.</summary>
public enum TimedKind { Request, PageEvent, DbCommand }

/// <summary>One kind's or one name's timings over a window: how many, the median and the 95th percentile, the slowest.</summary>
public sealed record TimingSummary(string Name, long Count, double P50Ms, double P95Ms, double MaxMs);

/// <summary>A name's timings over the last day, and over the last hour (null when none fell in it).</summary>
public sealed record TimingRow(TimingSummary Day, TimingSummary? Hour);

/// <summary>A database command that took longer than the slow-SQL threshold: when, how long, and its text (no values).</summary>
public sealed record SlowCommand(DateTimeOffset AtUtc, double Ms, string Sql);

/// <summary>A page event that took longer than the slow-event threshold: when, how long, and the page it was on.</summary>
public sealed record SlowEvent(DateTimeOffset AtUtc, double Ms, string Page);

/// <summary>
/// The thresholds past which a database command (<c>Diagnostics:SlowSqlMs</c>) or a page event
/// (<c>Diagnostics:SlowEventMs</c>) is logged as a Warning and listed on Diagnostics; 0 turns that warning off.
/// </summary>
public sealed class DiagnosticsOptions
{
    public int SlowSqlMs { get; set; } = 500;
    public int SlowEventMs { get; set; } = 1000;
}

/// <summary>
/// How fast this process has been, kept in memory for the last day: the durations of requests (by route), of events on
/// open pages (by page) and of database commands, each in hourly buckets of a log-scaled histogram, so a percentile over
/// the last hour or day is read without keeping every timing. Plus the last slow commands and events. Fed by the request
/// meter, the command interceptor and the circuit handler; read by Diagnostics' Performance panel. Nothing leaves the
/// server, and no search term, parameter value or query string is kept.
/// </summary>
public sealed class PerformanceMonitor(TimeProvider time)
{
    /// <summary>The event id a slow database command is logged under.</summary>
    public const int SlowSqlEventId = 7201;

    /// <summary>The event id a slow page event is logged under.</summary>
    public const int SlowPageEventId = 7202;

    /// <summary>Hours kept.</summary>
    public const int Hours = 24;

    /// <summary>The most names (routes, pages) kept apart per kind; past it, timings go under "(other)".</summary>
    public const int MaxNames = 200;

    /// <summary>The slow commands and events kept, newest first.</summary>
    public const int MaxSlow = 20;

    public const string Other = "(other)";

    /// <summary>The name a kind's timings are kept under all together.</summary>
    public const string All = "*";

    // Bin i holds durations up to Edge(i) ms: 0.25 ms doubling by √2, to about 2 minutes past the last edge.
    private const int Bins = 40;
    private static readonly double[] Edges = Enumerable.Range(0, Bins).Select(i => 0.25 * Math.Pow(Math.Sqrt(2), i)).ToArray();

    private readonly object _gate = new();
    private readonly Dictionary<(TimedKind, string), Series> _series = [];
    private readonly int[] _names = new int[Enum.GetValues<TimedKind>().Length];
    private readonly LinkedList<SlowCommand> _slowCommands = new();
    private readonly LinkedList<SlowEvent> _slowEvents = new();

    /// <summary>Records one timing.</summary>
    public void Record(TimedKind kind, string? name, double ms)
    {
        var hour = Hour(time.GetUtcNow());
        var bin = Array.BinarySearch(Edges, ms);
        bin = bin >= 0 ? bin : Math.Min(~bin, Bins - 1);
        lock (_gate)
        {
            Get(kind, All).Add(hour, bin, ms);
            if (name is not null) Get(kind, name).Add(hour, bin, ms);
        }
    }

    public void SlowCommand(double ms, string sql)
    {
        lock (_gate) Push(_slowCommands, new SlowCommand(time.GetUtcNow(), ms, sql));
    }

    public void SlowEvent(double ms, string page)
    {
        lock (_gate) Push(_slowEvents, new SlowEvent(time.GetUtcNow(), ms, page));
    }

    public IReadOnlyList<SlowCommand> SlowCommands() { lock (_gate) return [.. _slowCommands]; }

    public IReadOnlyList<SlowEvent> SlowEvents() { lock (_gate) return [.. _slowEvents]; }

    /// <summary>A kind's timings over the last <paramref name="hours"/> hours: all of them first (named <see cref="All"/>),
    /// then each name's, the most often timed first; at most <paramref name="take"/> names.</summary>
    public IReadOnlyList<TimingSummary> Summary(TimedKind kind, int hours, int take = 15) =>
        Rows(kind, take, hours).Select(r => r.Day).ToList();

    /// <summary>
    /// A kind's rows for the panel, in one pass: all of them first, then the <paramref name="take"/> names most often
    /// timed over the last <paramref name="hours"/> (a day by default), each with its last hour beside it.
    /// </summary>
    public IReadOnlyList<TimingRow> Rows(TimedKind kind, int take, int hours = Hours)
    {
        var now = Hour(time.GetUtcNow());
        lock (_gate)
        {
            var rows = _series.Where(s => s.Key.Item1 == kind)
                .Select(s => new TimingRow(s.Value.Summarize(s.Key.Item2, now, hours), s.Value.Summarize(s.Key.Item2, now, 1)))
                .Where(r => r.Day.Count > 0)
                .Select(r => r with { Hour = r.Hour is { Count: > 0 } ? r.Hour : null })
                .ToList();
            return [.. rows.Where(r => r.Day.Name == All),
                .. rows.Where(r => r.Day.Name != All).OrderByDescending(r => r.Day.Count).ThenBy(r => r.Day.Name, StringComparer.Ordinal).Take(take)];
        }
    }

    private static long Hour(DateTimeOffset t) => t.ToUnixTimeSeconds() / 3600;

    private Series Get(TimedKind kind, string name)
    {
        if (_series.TryGetValue((kind, name), out var s)) return s;
        if (name is not (All or Other) && _names[(int)kind] >= MaxNames) return Get(kind, Other);
        if (name is not (All or Other)) _names[(int)kind]++;
        return _series[(kind, name)] = new Series();
    }

    private static void Push<T>(LinkedList<T> list, T item)
    {
        list.AddFirst(item);
        while (list.Count > MaxSlow) list.RemoveLast();
    }

    // One name's histogram per hour, in a ring of Hours slots, each stamped with the hour it holds.
    private sealed class Series
    {
        private readonly long[] _hourOf = Enumerable.Repeat(-1L, Hours).ToArray();
        private readonly long[] _counts = new long[Hours * Bins];
        private readonly double[] _max = new double[Hours];

        public void Add(long hour, int bin, double ms)
        {
            var i = (int)(hour % Hours);
            if (_hourOf[i] != hour)
            {
                _hourOf[i] = hour;
                Array.Clear(_counts, i * Bins, Bins);
                _max[i] = 0;
            }
            _counts[i * Bins + bin]++;
            _max[i] = Math.Max(_max[i], ms);
        }

        public TimingSummary Summarize(string name, long now, int hours)
        {
            var merged = new long[Bins];
            double max = 0;
            for (var i = 0; i < Hours; i++)
            {
                if (_hourOf[i] is var h && (h > now || now - h >= hours)) continue;
                for (var b = 0; b < Bins; b++) merged[b] += _counts[i * Bins + b];
                max = Math.Max(max, _max[i]);
            }
            var count = merged.Sum();
            return new TimingSummary(name, count, Percentile(merged, count, 0.5, max), Percentile(merged, count, 0.95, max), max);
        }

        // The upper edge of the bin the percentile falls in (never past the slowest seen): an estimate within a factor of √2.
        private static double Percentile(long[] bins, long count, double p, double max)
        {
            if (count == 0) return 0;
            var rank = (long)Math.Ceiling(p * count);
            long seen = 0;
            for (var b = 0; b < Bins; b++)
                if ((seen += bins[b]) >= rank) return Math.Min(Edges[b], max);
            return max;
        }
    }
}
