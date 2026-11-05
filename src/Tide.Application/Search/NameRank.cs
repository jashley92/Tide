namespace Tide.Application.Search;

/// <summary>
/// How well something a picker lists matches what was typed into it, alike for the adversary picker and the "We run"
/// picker: its name, an optional code (a MITRE id) and its other names, each exact, starting with the words, holding
/// them, or not at all, ignoring case. The name wins a tie, then the code, then the first other name.
/// </summary>
public static class NameRank
{
    public const int Exact = 0, Starts = 1, Holds = 2, None = 3;

    /// <summary>How one name matches <paramref name="text"/>.</summary>
    public static int Of(string term, string text) =>
        string.Equals(term, text, StringComparison.OrdinalIgnoreCase) ? Exact
        : term.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? Starts
        : term.Contains(text, StringComparison.OrdinalIgnoreCase) ? Holds : None;

    /// <summary>The best match among the name, the code and the other names, and the other name that made it when the
    /// name and code matched less well (shown "Name (also Other)").</summary>
    public static (int Rank, string? Other) Best(string text, string name, IEnumerable<string> others, string? code = null)
    {
        var best = (Rank: Of(name, text), Other: (string?)null);
        if (code is not null && Of(code, text) < best.Rank) best = (Of(code, text), null);
        foreach (var other in others)
            if (Of(other, text) < best.Rank) best = (Of(other, text), other);
        return best;
    }

    /// <summary>
    /// The LIKE patterns a bounded database search uses for <paramref name="text"/>, escaped with
    /// <see cref="SearchText.Escape"/>. Other names are kept "one; two; three", so an exact other name is the whole field,
    /// or sits first (before ";"), last (after "; ") or between; one starting with the words is first, or after "; ".
    /// </summary>
    public sealed record Patterns(string Any, string Exact, string Starts, string OtherFirst, string OtherLast, string OtherMiddle, string OtherStarts)
    {
        public static Patterns For(string text)
        {
            var exact = SearchText.Literal(text);
            var starts = exact + "%";
            return new Patterns(SearchText.Contains(text), exact, starts, exact + ";%", "%; " + exact, "%; " + exact + ";%", "%; " + starts);
        }
    }
}
