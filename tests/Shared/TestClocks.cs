namespace Tide.TestSupport;

/// <summary>A clock that reads <see cref="Now"/>, which stays put until the test moves it.</summary>
public sealed class TestClock(DateTimeOffset now = default) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>The system clock, a fixed span ahead: "now", that much later.</summary>
public sealed class ShiftedClock(TimeSpan by) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + by;
}

/// <summary>A clock whose timers fire only when the test moves it on (<see cref="Advance"/>).</summary>
public sealed class ManualClock : TimeProvider
{
    private readonly List<Timer> _timers = [];
    private TimeSpan _now;

    /// <summary>How many timers are waiting to fire.</summary>
    public int Pending => _timers.Count(t => t.Due is not null);

    /// <summary>Moves the clock on by <paramref name="by"/>, firing each timer that falls due on the way.</summary>
    public void Advance(TimeSpan by)
    {
        _now += by;
        foreach (var t in _timers.Where(t => t.Due <= _now).ToList())
        {
            t.Due = null;
            t.Callback(t.State);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class Timer(ManualClock owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback => callback;
        public object? State => state;
        public TimeSpan? Due { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Dispose() => owner._timers.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
