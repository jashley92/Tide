using Microsoft.Extensions.Options;

namespace Tide.Application.Org;

/// <summary>Organization-wide settings (config section <c>Org</c>). The server's values are the defaults: an administrator
/// changes them in the app (Administration › Organization › Organization settings).</summary>
public sealed class OrgOptions
{
    /// <summary>The organization's time zone, as an IANA or Windows id. Brief dates and the meeting time are in it.</summary>
    public string TimeZone { get; set; } = "America/New_York";

    /// <summary>When the daily brief is presented, in <see cref="TimeZone"/> ("10:30").</summary>
    public string MeetingTime { get; set; } = "10:30";

    /// <summary>The name shown in the header and on slides ("Cyber Threat Intelligence").</summary>
    public string TeamName { get; set; } = "Cyber Threat Intelligence";

    /// <summary>Whether an action opened in a meeting shows beside the slide, which narrows for it, rather than over it.
    /// Off by default: it's switched on after a rehearsal in the meeting room.</summary>
    public bool ActionBesideSlide { get; set; }
}

/// <summary>
/// Today, in the organization's time zone, and the working days briefs are held on (<see cref="Days"/>: Monday to
/// Friday, less the holidays). Brief dates are local calendar days, so the brief for "today" doesn't change at UTC
/// midnight. The zone, meeting time and team name follow the settings as they change (one saved in Administration
/// takes effect at once), and the holidays as they're saved (<see cref="HolidayService"/>).
/// </summary>
public sealed class OrgClock : IDisposable
{
    private sealed record Snapshot(TimeZoneInfo Zone, TimeOnly MeetingTime, string TeamName, bool ActionBesideSlide);

    private readonly TimeProvider _time;
    private readonly IDisposable? _listening;
    private volatile Snapshot _now;
    private volatile WorkingDays _days = WorkingDays.Weekdays;

    /// <summary>Fixed settings (tests, tools).</summary>
    public OrgClock(IOptions<OrgOptions> options, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _now = Read(options.Value);
    }

    /// <summary>Settings that change: a new value is used from then on. One that can't be used (a zone this server
    /// doesn't know) leaves the last good one in force; at start-up it stops the app, as it always has.</summary>
    public OrgClock(IOptionsMonitor<OrgOptions> options, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _now = Read(options.CurrentValue);
        _listening = options.OnChange(o =>
        {
            try { _now = Read(o); }
            catch (InvalidOperationException) { return; /* keep the last good settings */ }
            Changed?.Invoke();
        });
    }

    /// <summary>The settings changed (one saved in Administration): what shows them now (a meeting's action beside the
    /// slide or over it) is drawn again. Raised on the thread that saved them.</summary>
    public event Action? Changed;

    public void Dispose() => _listening?.Dispose();

    public TimeZoneInfo Zone => _now.Zone;
    public TimeOnly MeetingTime => _now.MeetingTime;
    public string TeamName => _now.TeamName;

    /// <summary>Whether a meeting's action opens beside the slide (<see cref="OrgOptions.ActionBesideSlide"/>).</summary>
    public bool ActionBesideSlide => _now.ActionBesideSlide;

    /// <summary>The working days, with the holidays as last loaded or saved.</summary>
    public WorkingDays Days => _days;

    /// <summary>Puts <paramref name="holidays"/> in force (<see cref="HolidayService"/> calls it at start-up and on each change).</summary>
    public void UseHolidays(IReadOnlyDictionary<DateOnly, string> holidays) => _days = new WorkingDays(holidays);

    private static Snapshot Read(OrgOptions o) => new(
        FindZone(o.TimeZone),
        TimeOnly.TryParse(o.MeetingTime, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : new TimeOnly(10, 30),
        string.IsNullOrWhiteSpace(o.TeamName) ? "Cyber Threat Intelligence" : o.TeamName.Trim(), o.ActionBesideSlide);

    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(_time.GetUtcNow(), Zone);

    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    /// <summary>The brief day "today" means: today on a working day, the next working day on a weekend or holiday.</summary>
    public DateOnly CurrentBriefDate => Days.Next(Today, includeToday: true);

    /// <summary>A UTC instant as local time in the organization's zone.</summary>
    public DateTimeOffset ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);

    /// <summary>The day, in the organization's zone, a UTC instant falls on.</summary>
    public DateOnly DayOf(DateTimeOffset utc) => DateOnly.FromDateTime(ToLocal(utc).DateTime);

    /// <summary>
    /// The first instant of a local <paramref name="day"/>: the day an instant falls on (<see cref="ToLocal"/>) is
    /// <paramref name="day"/> from here until the next day's start. A midnight a clock change skips starts at the change.
    /// </summary>
    public DateTimeOffset StartOfDay(DateOnly day) => At(Zone, day, TimeOnly.MinValue);

    /// <summary>The first instant <paramref name="day"/> shows <paramref name="time"/> in <paramref name="zone"/>: a time
    /// the clocks skip that day is when they change, and one they show twice is its first.</summary>
    public static DateTimeOffset At(TimeZoneInfo zone, DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offsets = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
        return offsets.Select(o => new DateTimeOffset(local, o)).Min();
    }

    /// <summary>When the meeting for <paramref name="day"/> starts, as an instant (DST-correct).</summary>
    public DateTimeOffset MeetingStart(DateOnly day)
    {
        var now = _now;
        var local = day.ToDateTime(now.MeetingTime, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, now.Zone.GetUtcOffset(local));
    }

    private static TimeZoneInfo FindZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out var zone)) return zone;
        throw new InvalidOperationException(
            $"Org:TimeZone '{id}' isn't a time zone this server knows. Use an IANA id such as America/New_York.");
    }
}
