using System.Collections.Concurrent;
using Tide.Domain.Enums;

namespace Tide.Web.Services;

/// <summary>A card someone has picked up on the actions board: whose, who holds it, and the column it's over (none while
/// over its own).</summary>
public sealed record BoardDrag(Guid ActionId, string UserId, string Name, ActionStatus? Over, DateTimeOffset At);

/// <summary>
/// The cards being dragged on the actions board right now, so everyone else's board shows "Robin is moving this" and the
/// column it's over while the drag lasts. A board reports when a drag starts, when it crosses into another column, and
/// when it ends: a few messages a drag, not the pointer's path. In memory, per app (ADR 0001); one drag per tab, keyed by
/// its <see cref="ChangeOrigin"/>, and a tab that closes mid-drag is cleared with its circuit (<see cref="PresenceCircuit"/>).
/// </summary>
public sealed class BoardDrags(TimeProvider time)
{
    /// <summary>How long a drag shows without word from its board, in case its end was lost.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, (BoardDrag Drag, string CircuitId)> _drags = new();

    /// <summary>Raised when a drag starts, moves to another column or ends.</summary>
    public event Action<string>? Changed;

    /// <summary>The drags under way by people other than <paramref name="userId"/>.</summary>
    public IReadOnlyList<BoardDrag> Others(string userId)
    {
        var now = time.GetUtcNow();
        return _drags.Values.Select(d => d.Drag).Where(d => d.UserId != userId && now - d.At < Stale).ToList();
    }

    /// <summary>The tab at <paramref name="origin"/> holds <paramref name="actionId"/>, over <paramref name="over"/>.</summary>
    public void Report(Guid origin, string circuitId, Guid actionId, string userId, string name, ActionStatus? over)
    {
        var drag = new BoardDrag(actionId, userId, name, over, time.GetUtcNow());
        var changed = true;
        _drags.AddOrUpdate(origin, (drag, circuitId), (_, was) =>
        {
            changed = was.Drag.ActionId != actionId || was.Drag.Over != over;
            return (drag, circuitId);
        });
        if (changed) Notify();
    }

    /// <summary>The tab at <paramref name="origin"/> let go (dropped the card, or the drag was cancelled).</summary>
    public void End(Guid origin)
    {
        if (_drags.TryRemove(origin, out _)) Notify();
    }

    /// <summary>Ends a closed tab's drag: it closed or dropped while holding a card.</summary>
    public void RemoveCircuit(string circuitId)
    {
        if (circuitId.Length == 0) return;
        var gone = false;
        foreach (var (origin, d) in _drags)
            if (d.CircuitId == circuitId && _drags.TryRemove(origin, out _)) gone = true;
        if (gone) Notify();
    }

    private void Notify() => Together.Notify(Changed, "board");
}
