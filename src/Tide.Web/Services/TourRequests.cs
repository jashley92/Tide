using System.Collections.Concurrent;

namespace Tide.Web.Services;

/// <summary>Help's Take the tour and its guides, and a page's Show me around, to the layout's OnboardingHost in the same
/// circuit (one per circuit).</summary>
public sealed class TourRequests
{
    /// <summary>Raised with whether it was asked from Help.</summary>
    public event Action<bool>? Asked;

    /// <summary>Starts the tour; <paramref name="fromHelp"/> when Help's button asks, so focus can go back to it.</summary>
    public void Ask(bool fromHelp = false) => Asked?.Invoke(fromHelp);

    /// <summary>Raised with a guide's key, and whether it was asked from Help rather than its page's Show me around.</summary>
    public event Action<string, bool>? GuideAsked;

    /// <summary>Starts the guide <paramref name="key"/>: on its page, which Help's opens first.</summary>
    public void AskGuide(string key, bool fromHelp = false) => GuideAsked?.Invoke(key, fromHelp);
}

/// <summary>
/// Which circuit is showing a person their tour, so a second tab of theirs doesn't start a second one (one per app;
/// in memory, as a tour is only ever on screen in this process). A claim lasts while its circuit's browser is there: a tab
/// closed (or a browser gone) keeps its circuit for minutes in case it comes back, and meanwhile a new tab may take the
/// claim and show the tour that's still due (<see cref="SetConnected"/>, from <see cref="PresenceCircuit"/>).
/// </summary>
public sealed class TourClaims
{
    private sealed record Claim(object Owner, string CircuitId);

    private readonly ConcurrentDictionary<string, Claim> _showing = new(StringComparer.Ordinal);

    // Circuits whose browser has gone for now.
    private readonly ConcurrentDictionary<string, byte> _away = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="owner"/>, on the circuit <paramref name="circuitId"/> (empty outside one), may show
    /// <paramref name="userId"/> the tour: no one else is, but a page whose browser has gone.</summary>
    public bool TryClaim(string userId, object owner, string circuitId)
    {
        var mine = new Claim(owner, circuitId);
        return _showing.AddOrUpdate(userId, mine, (_, held) => held.Owner == owner || _away.ContainsKey(held.CircuitId) ? mine : held).Owner == owner;
    }

    /// <summary><paramref name="owner"/> is no longer showing it.</summary>
    public void Release(string userId, object owner)
    {
        if (_showing.TryGetValue(userId, out var held) && held.Owner == owner) _showing.TryRemove(new KeyValuePair<string, Claim>(userId, held));
    }

    /// <summary>A circuit's browser went away, or came back.</summary>
    public void SetConnected(string circuitId, bool connected)
    {
        if (circuitId.Length == 0) return;
        if (connected) _away.TryRemove(circuitId, out _);
        else _away[circuitId] = 0;
    }

    /// <summary>A circuit given up on: it won't be back.</summary>
    public void RemoveCircuit(string circuitId) => _away.TryRemove(circuitId, out _);
}
