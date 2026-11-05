namespace Tide.Application.Content;

/// <summary>Small wording helpers shared by the pages and the views behind them.</summary>
public static class Words
{
    /// <summary>"1 tag", "3 tags": <paramref name="n"/> and the noun, <paramref name="many"/> (or the noun with an s)
    /// unless there's exactly one.</summary>
    public static string Plural(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";
}
