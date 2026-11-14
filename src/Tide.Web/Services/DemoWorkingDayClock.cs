namespace Tide.Web.Services;

/// <summary>
/// Development only, for the layout checks (tools/screenshots, <c>Demo:WorkingDay</c>): the system clock, moved on whole
/// days to Monday when it's started on a weekend in the organization's zone, the time of day kept. Today's brief is then
/// today's, so present mode's meeting on it can start (a meeting isn't held before its brief's day) whatever day the
/// checks run. On a working day it is the system clock. Holidays aren't known when it's made, so only weekends move.
/// </summary>
public sealed class DemoWorkingDayClock : TimeProvider
{
    private readonly TimeProvider _system;

    public DemoWorkingDayClock(TimeZoneInfo zone, TimeProvider? system = null)
    {
        _system = system ?? System;
        var day = TimeZoneInfo.ConvertTime(_system.GetUtcNow(), zone).DayOfWeek;
        Days = day switch { DayOfWeek.Saturday => 2, DayOfWeek.Sunday => 1, _ => 0 };
    }

    /// <summary>How many days on from the system clock this one is (0 on a working day).</summary>
    public int Days { get; }

    public override DateTimeOffset GetUtcNow() => _system.GetUtcNow().AddDays(Days);
}
