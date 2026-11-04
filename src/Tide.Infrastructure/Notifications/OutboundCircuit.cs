namespace Tide.Infrastructure.Notifications;

/// <summary>
/// A per-target back-off for sends to other hosts (the mail relay; later, metric connectors). A
/// target that didn't answer <see cref="FailuresToOpen"/> sends in a row within <see cref="FailureWindow"/> (one dropped
/// connection isn't a relay that's down) is backed off for <see cref="FirstBackOff"/>, doubling on each further failure
/// up to <see cref="MaxBackOff"/>; meanwhile <see cref="TryEnter"/> says no and the caller fails fast instead of waiting
/// out a timeout. When the back-off ends one send goes through as a probe while the others still fail fast; a success
/// ends the back-off.
/// </summary>
public sealed class OutboundCircuit
{
    public static readonly TimeSpan FirstBackOff = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxBackOff = TimeSpan.FromMinutes(10);

    /// <summary>Failures in a row, within <see cref="FailureWindow"/>, that back a target off.</summary>
    public const int FailuresToOpen = 3;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _targets = new(StringComparer.OrdinalIgnoreCase);

    public OutboundCircuit(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Whether a send to <paramref name="target"/> may go now; false while it's backed off.</summary>
    public bool TryEnter(string target)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_targets.TryGetValue(target, out var state) || state.BackOffs == 0) return true;   // not backed off
            if (now < state.OpenUntil) return false;
            // One probe at a time; a probe that never reports holds the turn for at most MaxBackOff.
            if (state.ProbeUntil is { } probing && now < probing) return false;
            state.ProbeUntil = now + MaxBackOff;
            return true;
        }
    }

    /// <summary>How long until the target's back-off ends (zero when it isn't backed off, or it's over).</summary>
    public TimeSpan RetryIn(string target)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
            return _targets.TryGetValue(target, out var state) && state.BackOffs > 0 && now < state.OpenUntil
                ? state.OpenUntil - now : TimeSpan.Zero;
    }

    /// <summary>The target answered: any back-off ends.</summary>
    public void Succeeded(string target)
    {
        lock (_gate) _targets.Remove(target);
    }

    /// <summary>
    /// A send that was let in ended without learning whether the target answers (the caller cancelled, or it failed
    /// before anything was sent): the probe's turn is handed back and the back-off stays as it was.
    /// </summary>
    public void Release(string target)
    {
        lock (_gate) if (_targets.TryGetValue(target, out var state)) state.ProbeUntil = null;
    }

    /// <summary>The target didn't answer (timed out, couldn't be reached, or a server error): it's backed off.</summary>
    public void Failed(string target)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_targets.TryGetValue(target, out var state)) _targets[target] = state = new State();
            if (state.BackOffs == 0)
            {
                // Not backed off yet: counted, and backed off on the third within the window.
                state.Recent.Enqueue(now);
                while (state.Recent.Count > 0 && now - state.Recent.Peek() > FailureWindow) state.Recent.Dequeue();
                if (state.Recent.Count < FailuresToOpen) return;
                state.Recent.Clear();
            }
            else if (now < state.OpenUntil) return;   // a send started before the back-off: it doesn't lengthen it
            state.BackOffs = Math.Min(state.BackOffs + 1, 16);
            var backOff = TimeSpan.FromTicks(Math.Min(FirstBackOff.Ticks << (state.BackOffs - 1), MaxBackOff.Ticks));
            state.OpenUntil = now + backOff;
            state.ProbeUntil = null;
        }
    }

    private sealed class State
    {
        public int BackOffs;   // 0 while it's only counting failures
        public readonly Queue<DateTimeOffset> Recent = new();
        public DateTimeOffset OpenUntil;
        public DateTimeOffset? ProbeUntil;
    }
}
