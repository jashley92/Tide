namespace Tide.TestSupport;

/// <summary>A clock that reads <see cref="Now"/>, which stays put until the test moves it.</summary>
public sealed class TestClock(DateTimeOffset now = default) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// A working day's clock for a test that mustn't depend on the day it runs: it starts at 11:00 New York time (the
/// organization's default zone, after the 10:30 meeting and well clear of midnight in UTC) on the coming Wednesday (today,
/// when today is one) and runs on from there in real time. Whatever day the suite runs, "today" is a Wednesday with a
/// working day either side, so today's brief is today's (not Monday's, as on a weekend), its meeting can start, and
/// business days counted back from it fall the same way every run. What the app writes with the real clock is up to six
/// days older than this one's "now", as if written earlier in the week.
/// </summary>
public sealed class WeekdayClock : TimeProvider
{
    private readonly TimeSpan _by;

    public WeekdayClock()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var now = System.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var wednesday = today.AddDays(((int)DayOfWeek.Wednesday - (int)today.DayOfWeek + 7) % 7);
        var local = wednesday.ToDateTime(new TimeOnly(11, 0));
        _by = new DateTimeOffset(local, zone.GetUtcOffset(local)) - now;
    }

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + _by;
}

/// <summary>The system clock, a fixed span ahead: "now", that much later.</summary>
public sealed class ShiftedClock(TimeSpan by) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + by;
}

/// <summary>
/// A clock whose timers fire only when the test moves it on (<see cref="Advance"/>). Its time of day is the system's.
/// Safe to hand an app: pages make and drop timers from their own threads.
/// </summary>
public sealed class ManualClock : TimeProvider
{
    private readonly List<Timer> _timers = [];
    private TimeSpan _now;

    /// <summary>How many timers are waiting to fire.</summary>
    public int Pending { get { lock (_timers) return _timers.Count(t => t.Due is not null); } }

    /// <summary>Moves the clock on by <paramref name="by"/>, firing each timer that falls due on the way.</summary>
    public void Advance(TimeSpan by)
    {
        List<Timer> due;
        lock (_timers)
        {
            _now += by;
            due = _timers.Where(t => t.Due <= _now).ToList();
            foreach (var t in due) t.Due = null;
        }
        foreach (var t in due) t.Callback(t.State);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        lock (_timers) _timers.Add(timer);
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
            lock (owner._timers) Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Dispose()
        {
            lock (owner._timers) owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The system's time, with timers that fire at once: each wait the code asks for is recorded in <see cref="Waits"/>
/// rather than waited out.</summary>
public sealed class InstantClock : TimeProvider
{
    private readonly List<TimeSpan> _waits = [];

    public IReadOnlyList<TimeSpan> Waits { get { lock (_waits) return [.. _waits]; } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_waits) _waits.Add(dueTime);
        return System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }
}
