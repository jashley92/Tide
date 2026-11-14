using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http.Connections;
using Tide.Application.Abstractions;

namespace Tide.Web.Services;

/// <summary>Someone else at a place: who, and whether they have unsaved changes there.</summary>
public sealed record PresentPerson(string UserId, string Name, bool Editing);

/// <summary>A page's stay at a place. Disposing it leaves.</summary>
public interface IPresenceVisit : IDisposable
{
    /// <summary>Whether this page has unsaved changes, so the others see "editing" rather than "viewing".</summary>
    void SetEditing(bool editing);
}

/// <summary>
/// Who else has a page open: on a brief, an article, an action, a triage item, so two people
/// don't edit the same thing without knowing. In memory, per app (ADR 0001); an open page joins a place and leaves when
/// it closes. A tab that closes without saying so stops showing as soon as its connection drops, and is cleared when its
/// circuit ends (<see cref="PresenceCircuit"/>).
/// </summary>
public sealed class Presence
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Visit>> _places = new(StringComparer.Ordinal);

    // Circuits whose browser has gone for now (SetConnected).
    private readonly ConcurrentDictionary<string, byte> _away = new(StringComparer.Ordinal);

    // Joining and leaving change a place's set and whether the place is kept together; reads go without it.
    private readonly Lock _gate = new();

    /// <summary>Raised with the place whose company changed.</summary>
    public event Action<string>? Changed;

    /// <summary>Joins <paramref name="place"/> ("brief:2026-10-22"); disposing the result leaves it.</summary>
    public IPresenceVisit Join(string place, string userId, string name, string circuitId = "")
    {
        var visit = new Visit(this, place, userId, name, circuitId);
        lock (_gate) _places.GetOrAdd(place, _ => new())[visit.Key] = visit;
        Notify(place);
        return visit;
    }

    /// <summary>The other people at <paramref name="place"/>, each once however many tabs, editing if any tab is.</summary>
    public IReadOnlyList<PresentPerson> Others(string place, string userId) =>
        _places.TryGetValue(place, out var visits)
            ? visits.Values.Where(v => v.UserId != userId && !_away.ContainsKey(v.CircuitId))
                .GroupBy(v => v.UserId, StringComparer.Ordinal)
                .Select(g => new PresentPerson(g.Key, g.First().Name, g.Any(v => v.Editing)))
                .OrderBy(p => p.Name, StringComparer.CurrentCulture)
                .ToList()
            : [];

    /// <summary>Leaves every place a circuit's pages joined: the tab closed or dropped without its pages saying so.</summary>
    public void RemoveCircuit(string circuitId)
    {
        if (circuitId.Length == 0) return;
        _away.TryRemove(circuitId, out _);
        foreach (var visit in _places.Values.SelectMany(v => v.Values).Where(v => v.CircuitId == circuitId).ToList())
            visit.Dispose();
    }

    private void Leave(Visit visit)
    {
        lock (_gate)
        {
            if (!_places.TryGetValue(visit.Place, out var visits) || !visits.TryRemove(visit.Key, out _)) return;
            // The last one out removes the place, so places visited once (every article opened) don't pile up.
            if (visits.IsEmpty) _places.TryRemove(visit.Place, out _);
        }
        Notify(visit.Place);
    }

    /// <summary>
    /// A circuit whose browser has gone (closed, or its connection dropped) is kept for minutes in case it comes back; its
    /// pages aren't shown as here meanwhile, and are again if it reconnects.
    /// </summary>
    public void SetConnected(string circuitId, bool connected)
    {
        if (circuitId.Length == 0) return;
        if (!(connected ? _away.TryRemove(circuitId, out _) : _away.TryAdd(circuitId, 0))) return;
        foreach (var place in _places.Where(p => p.Value.Values.Any(v => v.CircuitId == circuitId)).Select(p => p.Key).ToList())
            Notify(place);
    }

    /// <summary>How many circuits are away (for tests: a closed one isn't kept).</summary>
    internal int AwayCount => _away.Count;

    /// <summary>How many places have someone at them (for tests: a place everyone left isn't kept).</summary>
    internal int PlaceCount => _places.Count;

    private void Notify(string place) => Together.Notify(Changed, place);

    private sealed class Visit(Presence owner, string place, string userId, string name, string circuitId) : IPresenceVisit
    {
        private int _left;
        private volatile bool _editing;

        public Guid Key { get; } = Guid.NewGuid();
        public string Place => place;
        public string UserId => userId;
        public string Name => name;
        public string CircuitId => circuitId;
        public bool Editing => _editing;

        public void SetEditing(bool editing)
        {
            if (_editing == editing || Volatile.Read(ref _left) == 1) return;
            _editing = editing;
            owner.Notify(place);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0) owner.Leave(this);
        }
    }
}

/// <summary>
/// The circuit a page belongs to, so its presence can be cleared when the tab closes or drops without the page being
/// disposed, along with a card it was dragging on the board, its seat in a live meeting and its claim on a first tour. One
/// per circuit (scoped), and the circuit's handler.
/// It also keeps the transport the circuit's connection is on, for Diagnostics' "Your connection".
/// </summary>
public sealed class PresenceCircuit(Presence presence, BoardDrags? drags = null, LiveMeetings? meetings = null, TourClaims? tours = null) : CircuitHandler
{
    /// <summary>This circuit's id once it has opened; empty while prerendering.</summary>
    public string CircuitId { get; private set; } = "";

    /// <summary>
    /// The transport this circuit last (re)connected over, or null outside a circuit (a prerender, a test's scope). Blazor
    /// calls <see cref="OnConnectionUpAsync"/> from inside the hub method that started or reconnected the circuit, where
    /// <see cref="TransportHubFilter"/> has made the transport ambient.
    /// </summary>
    public HttpTransportType? Transport { get; private set; }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Opened(circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Closed(circuit.Id);
        return Task.CompletedTask;
    }

    // A dropped connection (a closed lid, lost Wi-Fi, a closed browser) is away from a live meeting and its places at once;
    // the circuit itself is kept for minutes in case it comes back, which is when it's back.
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Down(CircuitId);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Connected(TransportHubFilter.Current);
        Up(CircuitId);
        return Task.CompletedTask;
    }

    /// <summary>The circuit's browser went away (closed, or its connection dropped): it isn't shown as here meanwhile.</summary>
    public void Down(string circuitId)
    {
        presence.SetConnected(circuitId, false);
        meetings?.SetConnected(circuitId, false);
        tours?.SetConnected(circuitId, false);
    }

    /// <summary>The circuit's browser is back.</summary>
    public void Up(string circuitId)
    {
        presence.SetConnected(circuitId, true);
        meetings?.SetConnected(circuitId, true);
        tours?.SetConnected(circuitId, true);
    }

    public void Opened(string circuitId) => CircuitId = circuitId;

    /// <summary>The circuit (re)connected over <paramref name="transport"/>; nothing when it isn't known.</summary>
    public void Connected(HttpTransportType? transport)
    {
        if (transport is { } t) Transport = t;
    }

    public void Closed(string circuitId)
    {
        presence.RemoveCircuit(circuitId);
        drags?.RemoveCircuit(circuitId);
        meetings?.RemoveCircuit(circuitId);
        tours?.RemoveCircuit(circuitId);
    }
}

/// <summary>
/// One page's ear on the <see cref="ChangeFeed"/>: keeps the changes it cares about, skips its own circuit's (its own
/// save already shows), and after a short pause hands the page everything heard, on the page's circuit, so a burst of
/// saves is one reload. One reload at a time: a change heard while the page is still reading waits for that read and
/// is then read, so a slow read from before it can't finish last and leave the page on the older state. Dispose with the page.
/// </summary>
public sealed class LiveChanges : IDisposable
{
    /// <summary>How long a burst may run before the page reloads.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(600);

    private readonly ChangeFeed _feed;
    private readonly Guid _origin;
    private readonly Func<Change, bool> _wanted;
    private readonly Func<Func<Task>, Task> _dispatch;
    private readonly Func<IReadOnlyList<Change>, Task> _apply;
    private readonly TimeSpan _pause;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private List<Change> _heard = [];
    private bool _disposed, _reloading, _dueMeanwhile;

    /// <param name="feed">The app's change feed.</param>
    /// <param name="origin">The page's own circuit, whose changes it skips.</param>
    /// <param name="wanted">Whether the page shows this change. Runs on the publisher's thread: read only what's safe.</param>
    /// <param name="dispatch">Runs work on the page's circuit (the component's InvokeAsync).</param>
    /// <param name="apply">Reloads for the changes heard, on the circuit.</param>
    /// <param name="pause">How long a burst may run; <see cref="Pause"/> unless a test says otherwise.</param>
    /// <param name="time">The clock the pause runs on.</param>
    public LiveChanges(ChangeFeed feed, ChangeOrigin origin, Func<Change, bool> wanted, Func<Func<Task>, Task> dispatch,
        Func<IReadOnlyList<Change>, Task> apply, TimeSpan? pause = null, TimeProvider? time = null)
    {
        _feed = feed;
        _origin = origin.Id;
        _wanted = wanted;
        _dispatch = dispatch;
        _apply = apply;
        _pause = pause ?? Pause;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        feed.Changed += OnChanged;
    }

    /// <summary>Who made the changes, for "Robin updated this brief": the names once each, or "Someone".</summary>
    public static string Who(IEnumerable<Change> changes)
    {
        var names = changes.Select(c => c.ActorName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.CurrentCulture).ToList();
        return names.Count switch
        {
            0 => "Someone",
            1 => names[0],
            _ => string.Join(", ", names[..^1]) + " and " + names[^1],
        };
    }

    private void OnChanged(Change change)
    {
        if (change.Origin == _origin || !_wanted(change)) return;
        lock (_gate)
        {
            if (_disposed) return;
            _heard.Add(change);
            _timer.Change(_pause, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        List<Change> heard;
        lock (_gate)
        {
            if (_disposed || _heard.Count == 0) return;
            // A reload is still reading: this one starts when it's done, with everything heard by then.
            if (_reloading) { _dueMeanwhile = true; return; }
            heard = _heard;
            _heard = [];
            _reloading = true;
        }
        _ = Run(heard);
    }

    private async Task Run(IReadOnlyList<Change> heard)
    {
        try { await _dispatch(() => _apply(heard)); }
        catch (Exception) { /* the page closed as it reloaded; nothing to tell */ }
        bool again;
        lock (_gate)
        {
            _reloading = false;
            again = _dueMeanwhile;
            _dueMeanwhile = false;
        }
        if (again) Fire();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _feed.Changed -= OnChanged;
        _timer.Dispose();
    }
}

internal static class Together
{
    // Each subscriber in turn, on the publisher's thread (another circuit): one that throws (a page closing at that
    // moment) mustn't fail the publisher's own action or keep the others from hearing. The publisher's ambient user is
    // cleared for each (the host's logs stay), so work a subscriber starts from here isn't logged as the publisher's.
    public static void Notify<T>(Action<T>? handlers, T topic)
    {
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                using (Security.AmbientUser.WithoutUser()) h(topic);
            }
            catch (Exception) { /* that subscriber is gone or broken; the rest still hear */ }
        }
    }
}
