using System.Globalization;

namespace Tide.Application.Content;

/// <summary>Small wording helpers shared by the pages and the views behind them.</summary>
public static class Words
{
    /// <summary>"1 tag", "3 tags": <paramref name="n"/> and the noun, <paramref name="many"/> (or the noun with an s)
    /// unless there's exactly one.</summary>
    public static string Plural(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";

    /// <summary>"An" before <paramref name="word"/> when it starts with a vowel, else "A", to start a sentence ("An Outreach
    /// action", "A Block action"). By the letter, not the sound, which is right for the names it's used with.</summary>
    public static string AOrAn(string word) => word.Length > 0 && "AEIOU".Contains(char.ToUpperInvariant(word[0])) ? "An" : "A";

    /// <summary><paramref name="text"/> cut to at most <paramref name="max"/> characters, unmarked: for a column's length.</summary>
    public static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>A report's number: "7", "2.5", one decimal at most, whatever the server's culture; <paramref name="none"/>
    /// when there's none.</summary>
    public static string Num(double? n, string none = "") => n is { } v ? v.ToString("0.#", CultureInfo.InvariantCulture) : none;

    /// <summary>"1 day", "2.5 days": the number as <see cref="Num"/> shows it decides, so no floating-point comparison.</summary>
    public static string Days(double n)
    {
        var shown = Num(n);
        return shown + (shown == "1" ? " day" : " days");
    }

    /// <summary>"Nov 20": a day in the short form lists, scales and rollups use.</summary>
    public static string MonthDay(DateOnly d) => d.ToString("MMM d", CultureInfo.InvariantCulture);

    /// <summary>"Nov 20, 2026": <see cref="MonthDay"/> with its year.</summary>
    public static string MonthDayYear(DateOnly d) => d.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    /// <summary><paramref name="text"/> cut to at most <paramref name="max"/> characters, the last of them "…" when it
    /// was cut: for text shown or stored where a cut should be visible.</summary>
    public static string Ellipsis(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>How many pages of <paramref name="size"/> rows <paramref name="total"/> rows fill: at least 1, and never
    /// a division by zero.</summary>
    public static int Pages(int total, int size) => Math.Max(1, (total + size - 1) / Math.Max(1, size));
}
