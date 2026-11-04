using Tide.Domain.Entities;

namespace Tide.Application.Intake;

/// <summary>
/// A feed source's filter (ADR 0009): which items are taken in, by the feed's own tags (<see cref="CategoryFilter"/>) and
/// by words in the title (<see cref="Mentions"/>). An item passes the "only" side when it has one of the only-tags OR
/// its title mentions one of the only-words (either side may be empty; both empty takes everything); it's then left out
/// if it has a skip-tag OR its title mentions a skip-word. Skips win.
/// </summary>
public sealed record FeedFilter(IReadOnlyList<string> OnlyTags, IReadOnlyList<string> SkipTags, IReadOnlyList<string> OnlyWords,
    IReadOnlyList<string> SkipWords)
{
    public static FeedFilter For(Source s) => new(CategoryFilter.Parse(s.OnlyCategories), CategoryFilter.Parse(s.SkipCategories),
        CategoryFilter.Parse(s.OnlyTitleWords), CategoryFilter.Parse(s.SkipTitleWords));

    public bool Keeps(ParsedItem item)
    {
        bool HasTag(IReadOnlyList<string> tags) => item.Categories.Any(c => tags.Contains(c.Trim(), StringComparer.OrdinalIgnoreCase));
        var hasOnly = OnlyTags.Count > 0 || OnlyWords.Count > 0;
        var passes = !hasOnly || HasTag(OnlyTags) || OnlyWords.Any(w => Mentions(item.Title, w));
        return passes && !HasTag(SkipTags) && !SkipWords.Any(w => Mentions(item.Title, w));
    }

    /// <summary>
    /// Whether <paramref name="title"/> mentions <paramref name="phrase"/> as whole words, ignoring case and spacing:
    /// "ransomware" matches "New Ransomware gang" and "anti-ransomware" (a hyphen ends a word) but not "ransomwares";
    /// "zero-day" matches "Zero-Day flaw"; "patch tuesday" matches "Patch  Tuesday". A word ends at anything that isn't a
    /// letter or digit.
    /// </summary>
    public static bool Mentions(string title, string phrase)
    {
        var p = string.Join(' ', phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (p.Length == 0) return false;
        var t = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        for (var at = t.IndexOf(p, StringComparison.OrdinalIgnoreCase); at >= 0; at = t.IndexOf(p, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var startsWord = at == 0 || !char.IsLetterOrDigit(t[at - 1]) || !char.IsLetterOrDigit(p[0]);
            var end = at + p.Length;
            var endsWord = end == t.Length || !char.IsLetterOrDigit(t[end]) || !char.IsLetterOrDigit(p[^1]);
            if (startsWord && endsWord) return true;
        }
        return false;
    }
}

/// <summary>
/// The tag half of a feed's filter (Source.OnlyCategories and SkipCategories): which items are taken in by the feed's own tags
/// (RSS &lt;category&gt;, Atom category). Tags match whole, ignoring case and surrounding space: "Security" matches
/// "security" but not "Security Week". With an "only" list, an item needs one of its tags, so an item with no tags is
/// left out; a "skip" tag leaves an item out whatever else it carries.
/// </summary>
public static class CategoryFilter
{
    public const int MaxTags = 50;
    public const int MaxTagLength = 100;

    /// <summary>The tags (or title words) in what an administrator typed: separated by commas, semicolons or lines, each once.</summary>
    public static IReadOnlyList<string> Parse(string? text) =>
        (text ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Length > MaxTagLength ? t[..MaxTagLength] : t)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxTags).ToList();

    /// <summary>The tags as stored (one per line), or null for none.</summary>
    public static string? Store(string? text) => Parse(text) is { Count: > 0 } tags ? string.Join('\n', tags) : null;

    /// <summary>The stored tags as the form shows them ("Security, Microsoft").</summary>
    public static string Show(string? stored) => string.Join(", ", Parse(stored));

    /// <summary>The tags a read of a feed carried, most used first (ties by name), at most <paramref name="max"/>, one per
    /// line; null when it carried none.</summary>
    public static string? Seen(IEnumerable<ParsedItem> items, int max = 40)
    {
        var tags = items.SelectMany(i => i.Categories)
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key).Take(max).ToList();
        if (tags.Count == 0) return null;
        var text = string.Join('\n', tags);
        return text.Length <= 2000 ? text : text[..text.LastIndexOf('\n', 1999)];
    }
}
