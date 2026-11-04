using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Org;

/// <summary>What an import or a copy did: the holidays added, and why the others weren't.</summary>
public sealed record HolidayImport(IReadOnlyList<Holiday> Added, IReadOnlyList<string> Skipped);

/// <summary>
/// The organization's holidays (EP-82): days off besides weekends, kept by an administrator on Organization settings.
/// Each change is saved, audited, and put in force on the org clock at once (<see cref="OrgClock.UseHolidays"/>), so
/// the rota, deadlines, reminders and metrics follow it. A holiday on a weekend is refused: it's a day off already.
/// </summary>
public sealed class HolidayService(IAppDbContextFactory dbs, ICurrentUser user, OrgClock clock)
{
    // Reads and puts in force one at a time, so two changes at once can't leave the older list in force.
    private static readonly SemaphoreSlim Loading = new(1, 1);

    /// <summary>The most reasons listed for what an import skipped.</summary>
    public const int MaxSkippedShown = 30;

    /// <summary>The largest calendar file read (a year of a country's holidays is a few kilobytes).</summary>
    public const int MaxCalendarBytes = 1024 * 1024;

    /// <summary>The most days one import or copy adds; more is surely the wrong file.</summary>
    public const int MaxPerImport = 100;

    /// <summary>The holidays, earliest first.</summary>
    public async Task<IReadOnlyList<Holiday>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        return (await db.Holidays.AsNoTracking().ToListAsync(ct)).OrderBy(h => h.Date).ToList();
    }

    /// <summary>Adds a holiday. Throws <see cref="ArgumentException"/> saying why when it can't be.</summary>
    public async Task<Holiday> AddAsync(DateOnly date, string? name, CancellationToken ct = default)
    {
        RequireAdmin();
        var clean = Name(name);
        if (WorkingDays.IsWeekend(date)) throw new ArgumentException($"{Day(date)} is a weekend, so it's a day off already.");
        await using var db = await dbs.CreateAsync(ct);
        if (await db.Holidays.AsNoTracking().FirstOrDefaultAsync(h => h.Date == date, ct) is { } there)
            throw new ArgumentException($"{Day(date)} is already a holiday ({there.Name}).");
        if (await db.Briefs.AnyAsync(b => b.BriefDate == date, ct))
            throw new ArgumentException($"{Day(date)} has a brief already. Move its articles to another day's brief first, or leave it a working day.");
        var holiday = new Holiday { Date = date, Name = clean };
        db.Holidays.Add(holiday);
        await db.SaveChangesAsync(ct);
        await LoadAsync(db, clock, ct);
        return holiday;
    }

    /// <summary>Removes the holiday on <paramref name="date"/>; nothing when there's none.</summary>
    public async Task RemoveAsync(DateOnly date, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        if (await db.Holidays.FirstOrDefaultAsync(h => h.Date == date, ct) is not { } holiday) return;
        db.Holidays.Remove(holiday);
        await db.SaveChangesAsync(ct);
        await LoadAsync(db, clock, ct);
    }

    /// <summary>
    /// Adds the all-day events of a calendar file (.ics, as Outlook and most governments publish holidays). An event
    /// that lasts several days adds each; one already a holiday, on a weekend, or repeating (an RRULE: its dates
    /// aren't listed) is skipped and said so.
    /// </summary>
    public async Task<HolidayImport> ImportAsync(byte[] calendar, CancellationToken ct = default)
    {
        RequireAdmin();
        if (calendar.Length > MaxCalendarBytes) throw new ArgumentException(TooBig);
        var text = Encoding.UTF8.GetString(calendar);
        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("That isn't a calendar (.ics) file.");
        var (days, skipped) = ReadCalendar(text, clock.Zone);
        return await AddAllAsync(days, skipped, ct);
    }

    /// <summary>
    /// Copies <paramref name="fromYear"/>'s holidays to the same dates of the next year, for an administrator to check:
    /// a holiday that moves (the 4th Thursday of November) needs its date changed.
    /// </summary>
    public async Task<HolidayImport> CopyYearAsync(int fromYear, CancellationToken ct = default)
    {
        RequireAdmin();
        List<Holiday> from;
        await using (var db = await dbs.CreateAsync(ct))
            from = await db.Holidays.AsNoTracking().Where(h => h.Date >= new DateOnly(fromYear, 1, 1) && h.Date <= new DateOnly(fromYear, 12, 31)).ToListAsync(ct);
        if (from.Count == 0) throw new ArgumentException($"There are no holidays in {fromYear} to copy.");
        var days = new List<(DateOnly, string)>();
        var skipped = new List<string>();
        foreach (var h in from.OrderBy(h => h.Date))
        {
            if (h.Date.Month == 2 && h.Date.Day == 29) { skipped.Add($"{h.Name}: February 29 isn't in {fromYear + 1}."); continue; }
            days.Add((h.Date.AddYears(1), h.Name));
        }
        return await AddAllAsync(days, skipped, ct);
    }

    /// <summary>Whether to remind an administrator that next year has no holidays: in December, once any were kept.</summary>
    public async Task<bool> NextYearMissingAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        if (today.Month != 12) return false;
        await using var db = await dbs.CreateAsync(ct);
        var next = new DateOnly(today.Year + 1, 1, 1);
        return await db.Holidays.AnyAsync(ct) && !await db.Holidays.AnyAsync(h => h.Date >= next, ct);
    }

    /// <summary>What an import over <see cref="MaxCalendarBytes"/> is told.</summary>
    public const string TooBig = "That file is over 1 MB; a holiday calendar is a few kilobytes.";

    /// <summary>Reads the holidays into <paramref name="clock"/> (at start-up, and after each change).</summary>
    public static async Task LoadAsync(IAppDbContext db, OrgClock clock, CancellationToken ct = default)
    {
        await Loading.WaitAsync(ct);
        try { clock.UseHolidays(await db.Holidays.AsNoTracking().ToDictionaryAsync(h => h.Date, h => h.Name, ct)); }
        finally { Loading.Release(); }
    }

    // Adds each new day once; what's skipped says why. The clock is loaded again whatever was added.
    private async Task<HolidayImport> AddAllAsync(IReadOnlyList<(DateOnly Date, string Name)> days, List<string> skipped, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        var have = (await db.Holidays.AsNoTracking().ToListAsync(ct)).ToDictionary(h => h.Date, h => h.Name);
        var dates = days.Select(d => d.Date).ToList();
        var briefed = (await db.Briefs.AsNoTracking().Where(b => dates.Contains(b.BriefDate)).Select(b => b.BriefDate).ToListAsync(ct)).ToHashSet();
        var added = new List<Holiday>();
        foreach (var (date, name) in days.OrderBy(d => d.Date))
        {
            if (have.TryGetValue(date, out var there)) { skipped.Add($"{Day(date)}: already a holiday ({there})."); continue; }
            if (WorkingDays.IsWeekend(date)) { skipped.Add($"{Day(date)} ({name}): a weekend."); continue; }
            if (briefed.Contains(date)) { skipped.Add($"{Day(date)} ({name}): it has a brief already."); continue; }
            if (added.Count == MaxPerImport) { skipped.Add($"Only {MaxPerImport} days are added at once; the rest weren't."); break; }
            var holiday = new Holiday { Date = date, Name = Trimmed(name) };
            db.Holidays.Add(holiday);
            have[date] = holiday.Name;
            added.Add(holiday);
        }
        if (added.Count > 0) await db.SaveChangesAsync(ct);
        await LoadAsync(db, clock, ct);
        if (skipped.Count > MaxSkippedShown) skipped = [.. skipped.Take(MaxSkippedShown), $"…and {skipped.Count - MaxSkippedShown} more."];
        return new HolidayImport(added, skipped);
    }

    // The all-day events of an iCalendar text (RFC 5545): lines are unfolded, each VEVENT's properties gathered, and then
    // its DTSTART, DTEND and SUMMARY read. A start with a time takes its date, in the organization's zone when it's a UTC
    // time ("...T230000Z"); an end is exclusive, as the format has it. Any line ending is read (CRLF, LF or CR alone).
    internal static (List<(DateOnly Date, string Name)> Days, List<string> Skipped) ReadCalendar(string text, TimeZoneInfo? zone = null)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n ", "").Replace("\n\t", "").Split('\n');
        var days = new List<(DateOnly, string)>();
        var skipped = new List<string>();
        Dictionary<string, string>? props = null;
        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) props = new(StringComparer.Ordinal);
            else if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase) && props is not null)
            {
                ReadEvent(props, days, skipped, zone);
                props = null;
            }
            else if (props is not null && ValueStart(line) is > 0 and var colon)
                props.TryAdd(line[..colon].Split(';')[0].ToUpperInvariant(), line[(colon + 1)..].Trim());
        }
        return (days, skipped);
    }

    // Where a property's value starts: the first colon outside a quoted parameter (TZID="Europe/Paris:x" has one inside).
    private static int ValueStart(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) return i;
        }
        return -1;
    }

    // One event's days, or why it was skipped.
    private static void ReadEvent(Dictionary<string, string> props, List<(DateOnly, string)> days, List<string> skipped, TimeZoneInfo? zone)
    {
        DateOnly? DateOf(string value) => CalendarDate(value, zone);
        var name = props.TryGetValue("SUMMARY", out var summary) && Unescape(summary) is { Length: > 0 } clean ? clean : "Holiday";
        if (!props.TryGetValue("DTSTART", out var startText) || DateOf(startText) is not { } start)
        {
            skipped.Add($"{name}: no start date.");
            return;
        }
        if (props.ContainsKey("RRULE") || props.ContainsKey("RDATE"))
        {
            skipped.Add($"{name}: it repeats, and a repeating event's dates aren't listed. Add them by hand.");
            return;
        }
        if (start.Year is < 2000 or > 2100)
        {
            skipped.Add($"{name}: {start.Year} isn't a year TIDE keeps holidays for.");
            return;
        }
        var last = props.TryGetValue("DTEND", out var endText) && DateOf(endText) is { } end && end > start ? end.AddDays(-1) : start;
        if (last.DayNumber - start.DayNumber > 31)
        {
            skipped.Add($"{name}: longer than a month.");
            return;
        }
        for (var d = start; d <= last; d = d.AddDays(1)) days.Add((d, name));
    }

    // A DATE (20261126), or a DATE-TIME's date: a UTC one (20261125T230000Z) as the day it is in the organization's zone.
    private static DateOnly? CalendarDate(string value, TimeZoneInfo? zone)
    {
        if (zone is not null && value.Length == 16 && value.EndsWith('Z')
            && DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));
        return value.Length >= 8 && DateOnly.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static string Unescape(string v) =>
        v.Replace("\\n", " ", StringComparison.OrdinalIgnoreCase).Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    private static string Name(string? name)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) throw new ArgumentException("Give the holiday a name, such as Thanksgiving.");
        if (clean.Length > Holiday.MaxName) throw new ArgumentException($"A holiday's name is at most {Holiday.MaxName} characters.");
        return clean;
    }

    private static string Trimmed(string name) => name.Length > Holiday.MaxName ? name[..Holiday.MaxName].TrimEnd() : name;

    private static string Day(DateOnly d) => d.ToString("ddd MMM d, yyyy", CultureInfo.InvariantCulture);

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can change the holidays.");
    }
}
