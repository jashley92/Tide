using Tide.Application.Articles;

namespace Tide.Web.Components.Shared;

/// <summary>
/// Where the article editor's keys go (editor.js hears them, ArticleEditorPanel acts): G then a letter to a section's
/// first field, and N to the next readiness gap, in the order the page runs.
/// </summary>
public static class EditorKeys
{
    /// <summary>The editor's sections in the order its cards run, top to bottom (not <see cref="ArticlePart"/>'s order:
    /// Screenshots comes after Recommended on the page).</summary>
    public static IReadOnlyList<ArticlePart> PageOrder => Order;

    private static readonly ArticlePart[] Order =
    [
        ArticlePart.Assessment, ArticlePart.BottomLine, ArticlePart.Bullets, ArticlePart.WhyItMatters,
        ArticlePart.Recommended, ArticlePart.Screenshots, ArticlePart.Indicators, ArticlePart.Tags,
    ];

    /// <summary>The section the letter after G names: B bottom line, U bullets, W why it matters, R recommended actions,
    /// I indicators, T tags. Assessment and Screenshots have no letter; null for any other.</summary>
    public static ArticlePart? Section(char letter) => char.ToLowerInvariant(letter) switch
    {
        'b' => ArticlePart.BottomLine,
        'u' => ArticlePart.Bullets,
        'w' => ArticlePart.WhyItMatters,
        'r' => ArticlePart.Recommended,
        'i' => ArticlePart.Indicators,
        't' => ArticlePart.Tags,
        _ => null,
    };

    /// <summary>The first field of a section, as the editor draws it: the text box, the first of <paramref name="bullets"/>,
    /// the new-indicator box, the scorecard tag search. Null (its heading) for Bullets with none, and for a part with no
    /// letter.</summary>
    public static string? FieldId(ArticlePart part, int bullets) => part switch
    {
        ArticlePart.BottomLine => "f-bluf",
        ArticlePart.Bullets when bullets > 0 => "f-bullet-0",
        ArticlePart.WhyItMatters => "f-why",
        ArticlePart.Recommended => "f-rec",
        ArticlePart.Indicators => "f-indicator",
        ArticlePart.Tags => "tags-search",
        _ => null,
    };

    // The section cards' heading ids, as the editor draws them.
    private static readonly Dictionary<string, ArticlePart> Headings = new(StringComparer.Ordinal)
    {
        ["sec-assessment"] = ArticlePart.Assessment, ["sec-bluf"] = ArticlePart.BottomLine, ["sec-bullets"] = ArticlePart.Bullets,
        ["sec-why"] = ArticlePart.WhyItMatters, ["sec-rec"] = ArticlePart.Recommended, ["sec-shots"] = ArticlePart.Screenshots,
        ["sec-ind"] = ArticlePart.Indicators, ["sec-tags"] = ArticlePart.Tags,
    };

    /// <summary>The section whose card has the heading <paramref name="headingId"/>; null for none (focus above the
    /// first card, or outside them).</summary>
    public static ArticlePart? SectionOf(string? headingId) =>
        headingId is not null && Headings.TryGetValue(headingId, out var part) ? part : null;

    /// <summary>
    /// The gap N goes to: the first section among <paramref name="gaps"/> after <paramref name="from"/> in the order the
    /// page runs, or the first of them from the top (focus in no section, or past the last gap). Null when there's none.
    /// </summary>
    public static ArticlePart? NextGap(IEnumerable<ArticlePart> gaps, ArticlePart? from)
    {
        var flagged = gaps.ToHashSet();
        var after = from is { } f ? Array.IndexOf(Order, f) : -1;
        var inOrder = Order.Where(flagged.Contains).ToList();
        if (inOrder.Count == 0) return null;
        return inOrder.FirstOrDefault(p => Array.IndexOf(Order, p) > after, inOrder[0]);
    }
}
