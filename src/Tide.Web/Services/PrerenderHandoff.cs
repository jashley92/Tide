using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Tide.Web.Services;

/// <summary>
/// Hands what a page read while it was prerendered to the same page's interactive render, which otherwise starts empty:
/// it would draw its loading state over the prerendered page and then the page again, a flicker on every reload. The
/// prerender keeps its state here and persists the key it's given (<c>[PersistentState]</c>); the interactive render
/// takes it before its first draw. Kept in memory for <see cref="Lifetime"/>, for the user it was read for only, and
/// taken once. A key that's gone (an expired one, a farm's other server) just means the page loads as before.
/// </summary>
public sealed class PrerenderHandoff(TimeProvider time)
{
    /// <summary>How long a prerender's state waits for its circuit.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (string UserId, object State, DateTimeOffset Until)> _kept = new();

    /// <summary>Keeps <paramref name="state"/> for <paramref name="userId"/>; the key to persist for the interactive render.</summary>
    public string Keep(string userId, object state)
    {
        var now = time.GetUtcNow();
        foreach (var (key, kept) in _kept)
            if (kept.Until <= now) _kept.TryRemove(key, out _);   // a prerender whose circuit never came
        var fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _kept[fresh] = (userId, state, now + Lifetime);
        return fresh;
    }

    /// <summary>The state kept under <paramref name="key"/> for <paramref name="userId"/>, once; null when there's none.</summary>
    public T? Take<T>(string? key, string userId) where T : class =>
        key is not null && _kept.TryRemove(key, out var kept) && kept.UserId == userId && kept.Until > time.GetUtcNow()
            ? kept.State as T
            : null;

    /// <summary>How many prerenders' states are waiting (for tests).</summary>
    public int Count => _kept.Count;
}
