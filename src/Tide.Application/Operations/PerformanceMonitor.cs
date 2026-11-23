namespace Tide.Application.Operations;

/// <summary>What a timing is of: a page or file the browser asked for, an event on an open page, a database command.</summary>
public enum TimedKind { Request, PageEvent, DbCommand }

/// <summary>One kind's or one name's timings over a window: how many, the median and the 95th percentile, the slowest.</summary>
public sealed record TimingSummary(string Name, long Count, double P50Ms, double P95Ms, double MaxMs);

/// <summary>A database command that took longer than the slow-SQL threshold: when, how long, and its text (no values).</summary>
public sealed record SlowCommand(DateTimeOffset AtUtc, double Ms, string Sql);

/// <summary>A page event that took longer than the slow-event threshold: when, how long, and the page it was on.</summary>
public sealed record SlowEvent(DateTimeOffset AtUtc, double Ms, string Page);

/// <summary>
/// How fast this process has been, kept in memory for the last day: the durations of requests (by route), of events on
/// open pages (by page) and of database commands, each in hourly buckets of a log-scaled histogram, so a percentile over
/// the last hour or day is read without keeping every timing. Plus the last slow commands and events, and the most
/// circuits open at once. Fed by the request meter, the command interceptor and the circuit handler; read by Diagnostics'
/// Performance panel. Nothing leaves the server, and no search term, parameter value or query string is kept.
/// </summary>
public sealed class PerformanceMonitor(TimeProvider time)
{
    /// <summary>Hours kept.</summary>
    public const int Hours = 24;

    /// <summary>The most names (routes, pages) kept apart per kind; past it, timings go under "(other)".</summary>
    public const int MaxNames = 200;

    /// <summary>The slow commands and events kept, newest first.</summary>
    public const int MaxSlow = 20;

    public const string Other = "(other)";

    // Bin i holds durations up to Edge(i) ms: 0.25 ms doubling by √2, to about 2 minutes past the last edge.
    private const int Bins = 40;
    private static readonly double[] Edges = Enumerable.Range(0, Bins).Select(i => 0.25 * Math.Pow(Math.Sqrt(2), i)).ToArray();

    private readonly object _gate = new();
    private readonly Dictionary<(TimedKind, string), Series> _series = [];
    private readonly LinkedList<SlowCommand> _slowCommands = new();
    private readonly LinkedList<SlowEvent> _slowEvents = new();
    private int _circuits;
    private readonly int[] _peakCircuits = new int[Hours];
    private readonly long[] _peakHour = new long[Hours];

    /// <summary>When the process started keeping timings.</summary>
    public DateTimeOffset Since { get; } = time.GetUtcNow();

    /// <summary>Circuits (open pages) now.</summary>
    public int CircuitsOpen => Volatile.Read(ref _circuits);

    /// <summary>Records one timing.</summary>
    public void Record(TimedKind kind, string? name, double ms)
    {
        var hour = Hour(time.GetUtcNow());
        var bin = Array.BinarySearch(Edges, ms);
        bin = bin >= 0 ? bin : Math.Min(~bin, Bins - 1);
        lock (_gate)
        {
            Add(Get(kind, "*"), hour, bin, ms);
            if (name is not null) Add(Get(kind, name), hour, bin, ms);
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

    public void CircuitOpened()
    {
        var open = Interlocked.Increment(ref _circuits);
        var hour = Hour(time.GetUtcNow());
        lock (_gate)
        {
            var i = (int)(hour % Hours);
            if (_peakHour[i] != hour) (_peakHour[i], _peakCircuits[i]) = (hour, 0);
            _peakCircuits[i] = Math.Max(_peakCircuits[i], open);
        }
    }

    public void CircuitClosed() => Interlocked.Decrement(ref _circuits);

    /// <summary>The most circuits open at once in the last day.</summary>
    public int PeakCircuits()
    {
        var now = Hour(time.GetUtcNow());
        lock (_gate) return Enumerable.Range(0, Hours).Where(i => now - _peakHour[i] < Hours).Select(i => _peakCircuits[i]).DefaultIfEmpty(0).Max();
    }

    public IReadOnlyList<SlowCommand> SlowCommands() { lock (_gate) return [.. _slowCommands]; }

    public IReadOnlyList<SlowEvent> SlowEvents() { lock (_gate) return [.. _slowEvents]; }

    /// <summary>A kind's timings over the last <paramref name="hours"/> hours: all of them first (named "*"), then each
    /// name's, the most often timed first; at most <paramref name="take"/> names.</summary>
    public IReadOnlyList<TimingSummary> Summary(TimedKind kind, int hours, int take = 15)
    {
        var now = Hour(time.GetUtcNow());
        lock (_gate)
        {
            var all = _series.Where(s => s.Key.Item1 == kind)
                .Select(s => Summarize(s.Key.Item2, s.Value, now, hours)).Where(s => s.Count > 0).ToList();
            return [.. all.Where(s => s.Name == "*"), .. all.Where(s => s.Name != "*").OrderByDescending(s => s.Count).ThenBy(s => s.Name, StringComparer.Ordinal).Take(take)];
        }
    }

    private static long Hour(DateTimeOffset t) => t.ToUnixTimeSeconds() / 3600;

    private Series Get(TimedKind kind, string name)
    {
        if (_series.TryGetValue((kind, name), out var s)) return s;
        if (name is not ("*" or Other) && _series.Count(k => k.Key.Item1 == kind) > MaxNames) return Get(kind, Other);
        return _series[(kind, name)] = new Series();
    }

    private static void Add(Series s, long hour, int bin, double ms)
    {
        var i = (int)(hour % Hours);
        if (s.HourOf[i] != hour)
        {
            s.HourOf[i] = hour;
            Array.Clear(s.Counts[i]);
            s.Max[i] = 0;
        }
        s.Counts[i][bin]++;
        s.Max[i] = Math.Max(s.Max[i], ms);
    }

    private static TimingSummary Summarize(string name, Series s, long now, int hours)
    {
        var merged = new long[Bins];
        double max = 0;
        for (var i = 0; i < Hours; i++)
        {
            if (s.HourOf[i] is var h && (h > now || now - h >= hours)) continue;
            for (var b = 0; b < Bins; b++) merged[b] += s.Counts[i][b];
            max = Math.Max(max, s.Max[i]);
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

    private static void Push<T>(LinkedList<T> list, T item)
    {
        list.AddFirst(item);
        while (list.Count > MaxSlow) list.RemoveLast();
    }

    private sealed class Series
    {
        public readonly long[] HourOf = Enumerable.Repeat(-1L, Hours).ToArray();
        public readonly long[][] Counts = Enumerable.Range(0, Hours).Select(_ => new long[Bins]).ToArray();
        public readonly double[] Max = new double[Hours];
    }
}
