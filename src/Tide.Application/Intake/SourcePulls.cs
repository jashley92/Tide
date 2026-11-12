using System.Collections.Concurrent;
using Tide.Domain.Enums;

namespace Tide.Application.Intake;

/// <summary>
/// The sources being polled right now, so one source is polled once at a time: two analysts pressing Pull now together,
/// or a pull and the scheduled poll at the same moment, would each add the same new items, and the second save would fail
/// on their addresses. One process (ADR 0001), so held in memory, as the CVE refresh's guard is (<see cref="CveRunLog"/>).
/// </summary>
public sealed class SourcePollGuard
{
    private readonly ConcurrentDictionary<Guid, byte> _running = new();

    /// <summary>Starts polling the source unless it's being polled; true when this caller may go ahead.</summary>
    public bool TryStart(Guid sourceId) => _running.TryAdd(sourceId, 0);

    public void Finished(Guid sourceId) => _running.TryRemove(sourceId, out _);

    public bool IsRunning(Guid sourceId) => _running.ContainsKey(sourceId);

    private int _all;

    /// <summary>Starts a Pull all unless one is running, whoever started it; true when this caller may go ahead.</summary>
    public bool TryStartAll() => Interlocked.CompareExchange(ref _all, 1, 0) == 0;

    public void AllFinished() => Interlocked.Exchange(ref _all, 0);

    /// <summary>Whether a Pull all is running.</summary>
    public bool AllRunning => Volatile.Read(ref _all) == 1;
}

/// <summary>
/// A polled source as Triage's Sources tab shows it: when it was last pulled (on schedule or by hand), when it's next
/// due, how the last pull went, and from when an analyst may pull it again.
/// </summary>
/// <param name="NextDueUtc">When the scheduled poll next takes it (its interval after the last pull, backed off after
/// failures); null when it's never been polled, so it's due now.</param>
/// <param name="LastNewItems">Items the last good pull added; null before any pull worked.</param>
/// <param name="PullableAtUtc">From when Pull now is allowed again (the analyst pull-now window after the last pull);
/// null when it's never been polled.</param>
/// <param name="Pulling">Whether it's being polled at this moment.</param>
public sealed record PolledSource(Guid Id, string Name, SourceKind Kind, DateTimeOffset? LastPolledAtUtc, DateTimeOffset? NextDueUtc,
    int? LastNewItems, string? LastError, DateTimeOffset? PullableAtUtc, bool Pulling = false)
{
    /// <summary>Whether its last poll failed.</summary>
    public bool Failing => LastError is not null;

    /// <summary>Whether Pull now is allowed at <paramref name="now"/>.</summary>
    public bool Pullable(DateTimeOffset now) => !Pulling && (PullableAtUtc is not { } at || now >= at);
}

/// <summary>Pull now refused: the source was pulled within the analyst pull-now window.</summary>
public sealed class PullTooSoonException(string message, DateTimeOffset pullableAtUtc) : InvalidOperationException(message)
{
    /// <summary>From when the source may be pulled again.</summary>
    public DateTimeOffset PullableAtUtc { get; } = pullableAtUtc;
}

/// <summary>Short spans of time as Triage's Sources tab says them ("12 min ago", "in 1 h 5 min").</summary>
public static class PullTimes
{
    /// <summary>How long ago something happened: "just now", "4 min ago", "3 h ago", "2 days ago" (rounded down).</summary>
    public static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes} min ago";
        if (elapsed < TimeSpan.FromDays(2)) return $"{(int)elapsed.TotalHours} h ago";
        return $"{(int)elapsed.TotalDays} days ago";
    }

    /// <summary>How long until something: "1 min", "11 min", "1 h", "1 h 5 min", "3 days" (rounded up to the minute, so a
    /// wait is never said shorter than it is, and never "0 min").</summary>
    public static string Wait(TimeSpan remaining)
    {
        var minutes = Math.Max(1, (long)Math.Ceiling(remaining.TotalMinutes));
        if (minutes < 60) return $"{minutes} min";
        if (minutes < 48 * 60) return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
        return $"{minutes / (24 * 60)} days";
    }

    /// <summary>What a pull brought: "no new items", "1 new item", "12 new items".</summary>
    public static string NewItems(int n) => n == 0 ? "no new items" : Content.Words.Plural(n, "new item");

    /// <summary>Why Pull now is refused at <paramref name="now"/>: "Pulled 4 min ago. You can pull it again in 11 min."</summary>
    public static string TooSoon(DateTimeOffset lastPolledAtUtc, DateTimeOffset pullableAtUtc, DateTimeOffset now) =>
        $"Pulled {Ago(now - lastPolledAtUtc)}. You can pull it again in {Wait(pullableAtUtc - now)}.";
}
