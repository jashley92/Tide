using System.Security.Cryptography;
using Microsoft.AspNetCore.Components;
using Tide.Application.Abstractions;

namespace Tide.Web.Services;

/// <summary>
/// Where a prerendered page's state waits for the same page's interactive render (<see cref="PrerenderHandoff"/>). Kept
/// in memory for <see cref="Lifetime"/>, for the user it was read for only, and taken once. A key that's gone (an expired
/// one, an evicted one, a farm's other server) just means the page loads as before.
/// </summary>
public sealed class PrerenderStore(TimeProvider time)
{
    /// <summary>How long a prerender's state waits for its circuit.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    /// <summary>The most states kept at once: past it the oldest goes, so a burst of prerenders whose circuits never
    /// come can't grow it without end.</summary>
    public const int Capacity = 1000;

    // Expired states are dropped this often at most; one taken late is refused whenever the sweep last ran.
    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string UserId, object State, DateTimeOffset Until)> _kept = [];
    // The keys in the order they were kept, which is the order they expire in (one lifetime for all): the oldest first.
    private readonly Queue<string> _order = new();
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;

    /// <summary>Keeps <paramref name="state"/> for <paramref name="userId"/>; the key to persist for the interactive render.</summary>
    public string Keep(string userId, object state)
    {
        var now = time.GetUtcNow();
        var fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            if (now >= _nextSweep)
            {
                // A prerender whose circuit never came. Only the front can have expired; a taken key there is just dropped.
                while (_order.TryPeek(out var oldest) && (!_kept.TryGetValue(oldest, out var kept) || kept.Until <= now))
                    _kept.Remove(_order.Dequeue());
                _nextSweep = now + SweepEvery;
            }
            while (_kept.Count >= Capacity && _order.TryDequeue(out var oldest)) _kept.Remove(oldest);
            _kept[fresh] = (userId, state, now + Lifetime);
            _order.Enqueue(fresh);
        }
        return fresh;
    }

    /// <summary>The state kept under <paramref name="key"/> for <paramref name="userId"/>, once; null when there's none.</summary>
    public T? Take<T>(string? key, string userId) where T : class
    {
        if (key is null) return null;
        (string UserId, object State, DateTimeOffset Until) kept;
        lock (_gate)
            if (!_kept.Remove(key, out kept)) return null;
        return kept.UserId == userId && kept.Until > time.GetUtcNow() ? kept.State as T : null;
    }

    /// <summary>How many prerenders' states are waiting (for tests).</summary>
    public int Count
    {
        get { lock (_gate) return _kept.Count; }
    }
}

/// <summary>
/// Hands what a page read while it was prerendered to the same page's interactive render, which otherwise starts empty:
/// it would draw its loading state over the prerendered page and then the page again, a flicker on every reload. The
/// prerender keeps its state (<see cref="Keep"/>) and persists the key it's given (<c>[PersistentState]</c>); the
/// interactive render takes it (<see cref="Take{T}(RendererInfo, string?, out bool)"/>) before its first draw, and reads
/// again only when something was saved since the prerender began reading. For the signed-in user of this scope only.
/// </summary>
public sealed class PrerenderHandoff(PrerenderStore store, ICurrentUser user, ChangeFeed feed)
{
    // The feed's count when this scope began, before its prerender read anything: kept with each state, so a save that
    // landed while the prerender was reading counts as a change since.
    private readonly long _since = feed.Version;

    private sealed record Kept(object State, long Version);

    /// <summary>What the prerender kept under <paramref name="key"/>, once the render is interactive; null before, or
    /// when there's none.</summary>
    public T? Take<T>(RendererInfo renderer, string? key) where T : class => Take<T>(renderer, key, out _);

    /// <summary>As <see cref="Take{T}(RendererInfo, string?)"/>; <paramref name="current"/> says nothing has been saved
    /// since the prerender began reading, so reading again would give what it read. A page follows the feed from its
    /// first draw, so anything saved after this is heard as ever.</summary>
    public T? Take<T>(RendererInfo renderer, string? key, out bool current) where T : class
    {
        current = false;
        if (!renderer.IsInteractive || store.Take<Kept>(key, user.UserId) is not { State: T state } kept) return null;
        current = kept.Version == feed.Version;
        return state;
    }

    /// <summary>The key to persist for <paramref name="state"/> while prerendering: <paramref name="key"/> when it's
    /// kept already (a prerender sets a component's parameters again when the page around it draws again), so it's kept
    /// once. Null once interactive: there's nothing to hand on.</summary>
    public string? Keep(RendererInfo renderer, object state, string? key) =>
        renderer.IsInteractive ? null : key ?? store.Keep(user.UserId, new Kept(state, _since));
}
