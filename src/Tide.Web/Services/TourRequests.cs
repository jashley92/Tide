using System.Collections.Concurrent;

namespace Tide.Web.Services;

/// <summary>Help's Take the tour, to the layout's OnboardingHost in the same circuit (one per circuit).</summary>
public sealed class TourRequests
{
    /// <summary>Raised with whether it was asked from Help.</summary>
    public event Action<bool>? Asked;

    /// <summary>Starts the tour; <paramref name="fromHelp"/> when Help's button asks, so focus can go back to it.</summary>
    public void Ask(bool fromHelp = false) => Asked?.Invoke(fromHelp);
}

/// <summary>
/// Which circuit is showing a person their tour, so a second tab of theirs doesn't start a second one (one per app;
/// in memory, as a tour is only ever on screen in this process).
/// </summary>
public sealed class TourClaims
{
    private readonly ConcurrentDictionary<string, object> _showing = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="owner"/> may show <paramref name="userId"/> the tour: no one else is.</summary>
    public bool TryClaim(string userId, object owner) => _showing.GetOrAdd(userId, owner) == owner;

    /// <summary><paramref name="owner"/> is no longer showing it.</summary>
    public void Release(string userId, object owner) => _showing.TryRemove(new KeyValuePair<string, object>(userId, owner));
}
