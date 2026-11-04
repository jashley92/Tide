namespace Tide.Web.Services;

/// <summary>
/// What a live reload did to a list, by row key: the rows that arrived, the ones that left, and the ones still there
/// that changed. <see cref="LeftBefore"/> says where each row that left was: before the row (still shown) that followed
/// it, or, when none did, at the end; that's where its ghost goes.
/// </summary>
public sealed record LiveDiff<TKey>(IReadOnlyList<TKey> Arrived, IReadOnlyList<TKey> Left, IReadOnlyList<TKey> Changed,
    IReadOnlyDictionary<TKey, TKey> LeftBefore) where TKey : notnull
{
    public bool IsEmpty => Arrived.Count == 0 && Left.Count == 0 && Changed.Count == 0;
}

public static class LiveDiff
{
    /// <summary>
    /// The rows of <paramref name="after"/> that weren't in <paramref name="before"/>, those of before that aren't in
    /// after, and those in both whose <paramref name="signature"/> (what the row shows: a record, a tuple) differs.
    /// </summary>
    public static LiveDiff<TKey> Of<TRow, TKey>(IEnumerable<TRow> before, IEnumerable<TRow> after, Func<TRow, TKey> key,
        Func<TRow, object?> signature) where TKey : notnull
    {
        var was = new List<(TKey Key, object? Sign)>();
        foreach (var row in before) was.Add((key(row), signature(row)));
        var wasByKey = new Dictionary<TKey, object?>();
        foreach (var (k, s) in was) wasByKey.TryAdd(k, s);
        var now = new Dictionary<TKey, object?>();
        var arrived = new List<TKey>();
        var changed = new List<TKey>();
        foreach (var row in after)
        {
            var k = key(row);
            if (!now.TryAdd(k, signature(row))) continue;
            if (!wasByKey.TryGetValue(k, out var s)) arrived.Add(k);
            else if (!Equals(s, now[k])) changed.Add(k);
        }
        // Each row that left goes before the next row that stayed (walking back from the end, the last one seen).
        var left = new List<TKey>();
        var leftBefore = new Dictionary<TKey, TKey>();
        var next = default(TKey);
        var hasNext = false;
        for (var i = was.Count - 1; i >= 0; i--)
        {
            var k = was[i].Key;
            if (now.ContainsKey(k))
            {
                (next, hasNext) = (k, true);
                continue;
            }
            if (left.Contains(k)) continue;
            left.Insert(0, k);
            if (hasNext) leftBefore[k] = next!;
        }
        return new LiveDiff<TKey>(arrived, left, changed, leftBefore);
    }
}

/// <summary>How a row is marked after a live reload.</summary>
public enum LiveMark { Arrived, Changed }

/// <summary>A row that left, shown a moment as a line saying what happened to it ("Sam dismissed “…”") before it goes.
/// It stands before the row <see cref="Before"/>, or at the end of the list when <see cref="AtEnd"/>.</summary>
public sealed record LiveGhost<TKey>(TKey Key, string Text, bool AtEnd, TKey Before) where TKey : notnull;

/// <summary>
/// The marks a live list wears for a moment, so a colleague's change is seen without moving anything: rows that arrived
/// (tide-live-arrived: they slide in with a soft highlight), rows that changed (tide-live-changed: the highlight alone),
/// rows that left (a ghost line that fades and collapses), and arrivals not yet looked at (a "New" marker the page drops
/// when the row is first selected). The highlight and the ghost clear themselves; the page re-renders when they do.
/// Use on the page's circuit, as its state is; dispose with the page.
/// </summary>
public sealed class LiveMarks<TKey> : IDisposable where TKey : notnull
{
    /// <summary>How long a row keeps its highlight: a little longer than the CSS fade, so the class outlives it.</summary>
    public static readonly TimeSpan Glow = TimeSpan.FromSeconds(2.4);

    /// <summary>How long a ghost stays: its line reads a moment, then fades and collapses (the CSS times it to end here).</summary>
    public static readonly TimeSpan Ghost = TimeSpan.FromSeconds(1.6);

    private readonly Func<Func<Task>, Task> _dispatch;
    private readonly Action _changed;
    private readonly TimeProvider _time;
    private readonly TimeSpan _glow, _ghost;
    private readonly Dictionary<TKey, (LiveMark Mark, int Ticket)> _marks = [];
    private readonly HashSet<TKey> _unseen = [];
    private readonly List<(LiveGhost<TKey> Ghost, int Ticket)> _ghosts = [];
    private readonly Dictionary<int, ITimer> _timers = [];
    private int _ticket;
    private bool _disposed;

    /// <param name="dispatch">Runs work on the page's circuit (the component's InvokeAsync).</param>
    /// <param name="changed">Re-renders the page (its StateHasChanged), once a mark has cleared.</param>
    /// <param name="glow">How long a highlight lasts; <see cref="Glow"/> unless a test says otherwise.</param>
    /// <param name="ghost">How long a ghost lasts; <see cref="Ghost"/> unless a test says otherwise.</param>
    /// <param name="time">The clock the marks clear on.</param>
    public LiveMarks(Func<Func<Task>, Task> dispatch, Action changed, TimeSpan? glow = null, TimeSpan? ghost = null, TimeProvider? time = null)
    {
        _dispatch = dispatch;
        _changed = changed;
        _glow = glow ?? Glow;
        _ghost = ghost ?? Ghost;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The ghosts showing now, in the order their rows left.</summary>
    public IReadOnlyList<LiveGhost<TKey>> Ghosts => _ghosts.Select(g => g.Ghost).ToList();

    /// <summary>Marks what <paramref name="diff"/> found: arrivals and changes highlighted (arrivals also unseen when
    /// <paramref name="unseen"/>), and, with <paramref name="ghost"/> to say what happened to each, a ghost per row that left.</summary>
    public void Apply(LiveDiff<TKey> diff, Func<TKey, string>? ghost = null, bool unseen = false)
    {
        if (_disposed || diff.IsEmpty) return;
        if (diff.Arrived.Count + diff.Changed.Count > 0)
        {
            var glow = ++_ticket;
            Mark(diff.Arrived, LiveMark.Arrived, unseen, glow);
            Mark(diff.Changed, LiveMark.Changed, false, glow);
            Expire(glow, _glow);
        }
        if (ghost is null || diff.Left.Count == 0) return;
        var ticket = ++_ticket;
        foreach (var key in diff.Left)
        {
            _ghosts.RemoveAll(g => EqualityComparer<TKey>.Default.Equals(g.Ghost.Key, key));
            var before = diff.LeftBefore.TryGetValue(key, out var b);
            _ghosts.Add((new LiveGhost<TKey>(key, ghost(key), !before, b!), ticket));
            _unseen.Remove(key);
            _marks.Remove(key);
        }
        Expire(ticket, _ghost);
    }

    /// <summary>Highlights <paramref name="keys"/> for a moment; arrivals, with <paramref name="unseen"/>, keep a "New"
    /// marker until <see cref="Seen"/>.</summary>
    public void Mark(IEnumerable<TKey> keys, LiveMark mark, bool unseen = false)
    {
        if (_disposed) return;
        var list = keys.ToList();
        if (list.Count == 0) return;
        var ticket = ++_ticket;
        Mark(list, mark, unseen, ticket);
        Expire(ticket, _glow);
    }

    private void Mark(IEnumerable<TKey> keys, LiveMark mark, bool unseen, int ticket)
    {
        foreach (var key in keys)
        {
            _marks[key] = (mark, ticket);
            if (unseen && mark == LiveMark.Arrived) _unseen.Add(key);
        }
    }

    /// <summary>The row's class while it's marked: tide-live-arrived or tide-live-changed; empty otherwise.</summary>
    public string Css(TKey key) => _marks.TryGetValue(key, out var m)
        ? m.Mark == LiveMark.Arrived ? "tide-live-arrived" : "tide-live-changed"
        : "";

    public bool IsMarked(TKey key) => _marks.ContainsKey(key);

    /// <summary>Whether the row arrived live and hasn't been looked at since.</summary>
    public bool IsUnseen(TKey key) => _unseen.Contains(key);

    /// <summary>The row was looked at (selected, focused): its "New" marker goes. True when it had one.</summary>
    public bool Seen(TKey key) => _unseen.Remove(key);

    /// <summary>Drops every mark (the list now shows something else: another tab).</summary>
    public void Clear()
    {
        _marks.Clear();
        _unseen.Clear();
        _ghosts.Clear();
    }

    // After a while the marks of one batch go (unless a later batch marked the row again), and the page re-renders.
    private void Expire(int ticket, TimeSpan after)
    {
        var timer = _time.CreateTimer(_ => _ = Expired(ticket), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timers[ticket] = timer;
        timer.Change(after, Timeout.InfiniteTimeSpan);
    }

    private async Task Expired(int ticket)
    {
        try
        {
            await _dispatch(() =>
            {
                if (_disposed) return Task.CompletedTask;
                if (_timers.Remove(ticket, out var timer)) timer.Dispose();
                var gone = _marks.Where(kv => kv.Value.Ticket == ticket).Select(kv => kv.Key).ToList();
                foreach (var key in gone) _marks.Remove(key);
                var ghosts = _ghosts.RemoveAll(g => g.Ticket == ticket);
                if (gone.Count > 0 || ghosts > 0) _changed();
                return Task.CompletedTask;
            });
        }
        catch (Exception) { /* the page closed as the mark cleared; nothing to show */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var timer in _timers.Values) timer.Dispose();
        _timers.Clear();
    }
}
