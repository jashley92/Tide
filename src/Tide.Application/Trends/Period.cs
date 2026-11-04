using System.Globalization;

namespace Tide.Application.Trends;

/// <summary>How trends are bucketed.</summary>
public enum PeriodKind
{
    Month = 0,
    Quarter = 1,
    /// <summary>An ISO week, Monday to Sunday (<see cref="Period.Index"/> is its week number in its ISO year).</summary>
    Week = 2,
}

/// <summary>
/// A calendar month or quarter, or an ISO week: <see cref="Index"/> is the month (1–12), the quarter (1–4) or the week
/// (1–53, of the ISO year). Written as a key in links and files ("2026-09", "2026-Q3", "2026-W44") and labelled for people
/// ("Sep 2026", "Q3 2026", "Week of Oct 26").
/// </summary>
public readonly record struct Period(PeriodKind Kind, int Year, int Index)
{
    private int Months => Kind == PeriodKind.Month ? 1 : 3;

    public DateOnly Start => Kind == PeriodKind.Week
        ? DateOnly.FromDateTime(ISOWeek.ToDateTime(Year, Index, DayOfWeek.Monday))
        : new(Year, Kind == PeriodKind.Month ? Index : (Index - 1) * 3 + 1, 1);

    /// <summary>The last day, inclusive.</summary>
    public DateOnly End => Kind == PeriodKind.Week ? Start.AddDays(6) : Start.AddMonths(Months).AddDays(-1);

    public bool Contains(DateOnly day) => day >= Start && day <= End;

    public Period Previous => Shift(-1);

    public Period Next => Shift(1);

    public Period Shift(int by)
    {
        if (Kind == PeriodKind.Week) return Of(PeriodKind.Week, Start.AddDays(7 * by));
        var perYear = Kind == PeriodKind.Month ? 12 : 4;
        var n = Year * perYear + (Index - 1) + by;
        return new Period(Kind, n / perYear, n % perYear + 1);
    }

    public string Key => Kind switch
    {
        PeriodKind.Month => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{Index:D2}"),
        PeriodKind.Week => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-W{Index:D2}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-Q{Index}"),
    };

    public string Label => Kind switch
    {
        PeriodKind.Month => Start.ToString("MMM yyyy", CultureInfo.InvariantCulture),
        PeriodKind.Week => "Week of " + Start.ToString("MMM d", CultureInfo.InvariantCulture),
        _ => string.Create(CultureInfo.InvariantCulture, $"Q{Index} {Year}"),
    };

    public override string ToString() => Key;

    /// <summary>The period of <paramref name="kind"/> that holds <paramref name="day"/>.</summary>
    public static Period Of(PeriodKind kind, DateOnly day)
    {
        if (kind != PeriodKind.Week) return new(kind, day.Year, kind == PeriodKind.Month ? day.Month : (day.Month - 1) / 3 + 1);
        var dt = day.ToDateTime(TimeOnly.MinValue);
        return new(PeriodKind.Week, ISOWeek.GetYear(dt), ISOWeek.GetWeekOfYear(dt));
    }

    /// <summary>Reads a key ("2026-09", "2026-Q3", "2026-W44"); its form decides the kind. Null when it's none of them.</summary>
    public static Period? Parse(string? key)
    {
        var k = (key ?? "").Trim();
        if (k.Length < 6 || k[4] != '-' || !int.TryParse(k.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year < 1900)
            return null;
        var rest = k.AsSpan(5);
        if (rest.Length == 2 && (rest[0] == 'Q' || rest[0] == 'q') && rest[1] is >= '1' and <= '4')
            return new Period(PeriodKind.Quarter, year, rest[1] - '0');
        if (rest.Length == 3 && (rest[0] == 'W' || rest[0] == 'w') && int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var week)
            && week >= 1 && week <= ISOWeek.GetWeeksInYear(year))
            return new Period(PeriodKind.Week, year, week);
        if (rest.Length == 2 && int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var month) && month is >= 1 and <= 12)
            return new Period(PeriodKind.Month, year, month);
        return null;
    }
}
