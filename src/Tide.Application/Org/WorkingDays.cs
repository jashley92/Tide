namespace Tide.Application.Org;

/// <summary>
/// The days the team works: Monday to Friday, less the organization's holidays (Administration › Organization
/// settings). A holiday is a day off as a weekend is: no brief is held or expected, nothing is sent, and it doesn't
/// count towards an action's deadline. The current calendar is <see cref="OrgClock.Days"/>; a value never changes.
/// </summary>
public sealed class WorkingDays
{
    /// <summary>Monday to Friday, with no holidays.</summary>
    public static readonly WorkingDays Weekdays = new(new Dictionary<DateOnly, string>());

    private readonly IReadOnlyDictionary<DateOnly, string> _holidays;

    /// <param name="holidays">Each holiday's date and name.</param>
    public WorkingDays(IReadOnlyDictionary<DateOnly, string> holidays) => _holidays = holidays;

    /// <summary>Each holiday's date and name.</summary>
    public IReadOnlyDictionary<DateOnly, string> Holidays => _holidays;

    public static bool IsWeekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public bool IsWorkingDay(DateOnly d) => !IsWeekend(d) && !_holidays.ContainsKey(d);

    /// <summary>The holiday's name on <paramref name="d"/>, or null when it isn't one.</summary>
    public string? HolidayOn(DateOnly d) => _holidays.GetValueOrDefault(d);

    /// <summary>The first working day after <paramref name="from"/>, or from it with <paramref name="includeToday"/>.</summary>
    public DateOnly Next(DateOnly from, bool includeToday = false)
    {
        var d = includeToday ? from : from.AddDays(1);
        while (!IsWorkingDay(d)) d = d.AddDays(1);
        return d;
    }

    /// <summary>The last working day before <paramref name="from"/>.</summary>
    public DateOnly Previous(DateOnly from)
    {
        var d = from.AddDays(-1);
        while (!IsWorkingDay(d)) d = d.AddDays(-1);
        return d;
    }

    /// <summary>Why <paramref name="d"/> isn't a working day ("Thanksgiving is a holiday", "It's a weekend"), or null.</summary>
    public string? DayOffReason(DateOnly d) =>
        HolidayOn(d) is { } name ? $"{name} is a holiday" : IsWeekend(d) ? "It's a weekend" : null;
}
