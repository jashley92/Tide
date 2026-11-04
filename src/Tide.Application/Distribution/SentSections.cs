using System.Globalization;

namespace Tide.Application.Distribution;

/// <summary>
/// One article's part of a frozen semantic edition: its number and headline as they went, the article it names (from the
/// heading's <c>data-article</c>, null in a send made before headings named their article), and its markup.
/// </summary>
public sealed record SentSection(int Number, string Headline, Guid? ArticleId, string Html);

/// <summary>A line of a correction's diff: one the recipients had (<see cref="DiffKind.Removed"/>), one there is now
/// (<see cref="DiffKind.Added"/>), or one that's in both.</summary>
public sealed record DiffLine(DiffKind Kind, string Text);

public enum DiffKind { Same, Removed, Added }

/// <summary>
/// Reads a frozen edition back by article, and compares an article as sent with the article now, for a correction (ADR
/// 0015). <see cref="BriefRenderer.Semantic"/> writes the markup, so its blocks are simple: one per line, an article's
/// section running from its <c>&lt;h2&gt;</c> to the next. Both sides are compared as plain text, line by line.
/// </summary>
public static class SentSections
{
    /// <summary>The heading attribute that names the article (its id).</summary>
    public const string ArticleAttribute = "data-article";

    /// <summary>Each article's section of <paramref name="html"/>, in order. The last stops before the edition's closing
    /// lines (the count of articles left out, and the marking), which belong to no article.</summary>
    public static IReadOnlyList<SentSection> Parse(string? html)
    {
        if (string.IsNullOrEmpty(html)) return [];
        var heads = Headings(html).ToList();
        var list = new List<SentSection>(heads.Count);
        for (var i = 0; i < heads.Count; i++)
        {
            var (start, attrs, inner) = heads[i];
            var end = i + 1 < heads.Count ? heads[i + 1].Start : html.Length;
            var body = html[start..end];
            if (i + 1 == heads.Count) body = WithoutClosingLines(body);
            var text = PublishedContent.TextOf(inner);
            var (number, headline) = Numbered(text);
            list.Add(new SentSection(number ?? i + 1, headline, ArticleOf(attrs), body));
        }
        return list;
    }

    /// <summary>The <c>&lt;h2&gt;</c> headings of <paramref name="html"/>: where each starts, its attributes and its inner markup.</summary>
    internal static IEnumerable<(int Start, string Attributes, string Inner)> Headings(string html)
    {
        for (var at = html.IndexOf("<h2", StringComparison.Ordinal); at >= 0; at = html.IndexOf("<h2", at + 3, StringComparison.Ordinal))
        {
            var next = at + 3 < html.Length ? html[at + 3] : '\0';
            if (next is not ('>' or ' ')) continue;
            var open = html.IndexOf('>', at);
            if (open < 0) yield break;
            var close = html.IndexOf("</h2>", open, StringComparison.Ordinal);
            if (close < 0) yield break;
            yield return (at, html[(at + 3)..open], html[(open + 1)..close]);
        }
    }

    /// <summary>"3. Title" is the third article's "Title"; a heading without a number has none.</summary>
    internal static (int? Number, string Headline) Numbered(string heading)
    {
        var dot = heading.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0 && heading[..dot].All(char.IsAsciiDigit) && int.TryParse(heading[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return (n, heading[(dot + 2)..].Trim());
        return (null, heading.Trim());
    }

    private static Guid? ArticleOf(string attributes)
    {
        var key = ArticleAttribute + "=\"";
        var at = attributes.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;
        at += key.Length;
        var end = attributes.IndexOf('"', at);
        return end > at && Guid.TryParse(attributes[at..end], CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    // The edition's last lines: "N restricted items were briefed to the security team and left out of this edition." and
    // the closing marking ("TLP:CLEAR" alone).
    private static string WithoutClosingLines(string body)
    {
        var lines = body.Split('\n').ToList();
        while (lines.Count > 0 && (string.IsNullOrWhiteSpace(lines[^1]) || IsClosingLine(PublishedContent.TextOf(lines[^1]))))
            lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines) + "\n";
    }

    private static bool IsClosingLine(string text) =>
        (text.StartsWith("TLP:", StringComparison.Ordinal) && !text.Contains(' ', StringComparison.Ordinal))
        || text.EndsWith(BriefRenderer.WithheldEnd, StringComparison.Ordinal);

    /// <summary>
    /// A section as the lines a reader sees: each block's text, a list item marked "• ", the heading without its number,
    /// and a figure without its number (both can move when another article is added or left out, which isn't a change to
    /// this one).
    /// </summary>
    public static IReadOnlyList<string> TextLines(string sectionHtml)
    {
        var lines = new List<string>();
        foreach (var raw in sectionHtml.Split('\n'))
        {
            var text = PublishedContent.TextOf(raw);
            if (text.Length == 0) continue;
            var line = raw.TrimStart();
            if (line.StartsWith("<h2", StringComparison.Ordinal)) text = Numbered(text).Headline;
            else if (line.StartsWith("<li", StringComparison.Ordinal)) text = "• " + text;
            else if (line.StartsWith("<figure", StringComparison.Ordinal)) text = Unnumbered(text);
            lines.Add(text);
        }
        return lines;
    }

    // "Figure 2: the login page" is "Figure: the login page".
    private static string Unnumbered(string figure)
    {
        if (!figure.StartsWith("Figure ", StringComparison.Ordinal)) return figure;
        var i = 7;
        while (i < figure.Length && char.IsAsciiDigit(figure[i])) i++;
        return i > 7 ? "Figure" + figure[i..] : figure;
    }

    /// <summary>
    /// The lines of <paramref name="was"/> and <paramref name="now"/> in order, each marked as in both, only in the one
    /// sent, or only in the one now (a longest common subsequence: an article is a few dozen lines).
    /// </summary>
    public static IReadOnlyList<DiffLine> Diff(IReadOnlyList<string> was, IReadOnlyList<string> now)
    {
        var n = was.Count;
        var m = now.Count;
        var common = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                common[i, j] = string.Equals(was[i], now[j], StringComparison.Ordinal) ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
        var diff = new List<DiffLine>(n + m);
        int a = 0, b = 0;
        while (a < n && b < m)
        {
            if (string.Equals(was[a], now[b], StringComparison.Ordinal)) { diff.Add(new(DiffKind.Same, was[a])); a++; b++; }
            else if (common[a + 1, b] >= common[a, b + 1]) diff.Add(new(DiffKind.Removed, was[a++]));
            else diff.Add(new(DiffKind.Added, now[b++]));
        }
        while (a < n) diff.Add(new(DiffKind.Removed, was[a++]));
        while (b < m) diff.Add(new(DiffKind.Added, now[b++]));
        return diff;
    }
}
