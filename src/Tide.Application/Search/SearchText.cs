using System.Text.RegularExpressions;
using Tide.Domain.Enums;
using Tide.Domain.Observables;

namespace Tide.Application.Search;

/// <summary>A run of text in a snippet, marked when it matched a search term.</summary>
public readonly record struct TextRun(string Text, bool Hit);

/// <summary>
/// The text side of search: splitting a query into terms, the forms each term is looked for in, LIKE patterns, and the
/// snippet shown under a hit with its matches marked.
/// </summary>
public static partial class SearchText
{
    public const int MaxTerms = 8;
    public const int MaxTermLength = 200;
    private const int SnippetLength = 180;

    /// <summary>
    /// The terms of <paramref name="query"/>: words, or "quoted phrases", each at least two characters, without
    /// repeats. Every term must match somewhere for a hit.
    /// </summary>
    public static IReadOnlyList<string> Terms(string? query)
    {
        var terms = new List<string>();
        foreach (Match m in TermRx().Matches(query ?? ""))
        {
            var t = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            if (t.Length < 2) continue;
            if (t.Length > MaxTermLength) t = t[..MaxTermLength];
            if (terms.Contains(t, StringComparer.OrdinalIgnoreCase)) continue;
            terms.Add(t);
            if (terms.Count == MaxTerms) break;
        }
        return terms;
    }

    /// <summary>
    /// The forms a term is looked for in. Indicators are stored live but often written defanged in prose, so a network
    /// indicator is looked for both ways: "evil[.]com" finds "evil.com" and the other way round.
    /// </summary>
    public static IReadOnlyList<string> Forms(string term)
    {
        var forms = new List<string> { term };
        var live = IocObservable.Refang(term);
        if (IocObservable.IsRefangable(IocObservable.DetectType(live)))
        {
            // An IP address is written defanged at its last dot or at every dot; both are looked for.
            var all = IocObservable.DetectType(live) == IndicatorType.IpAddress ? live.Replace(".", "[.]", StringComparison.Ordinal) : live;
            foreach (var f in new[] { live, IocObservable.Defang(live), all })
                if (!forms.Contains(f, StringComparer.OrdinalIgnoreCase)) forms.Add(f);
        }
        return forms;
    }

    /// <summary>The escape character used in every LIKE pattern built here.</summary>
    public const string Escape = "\\";

    /// <summary>A LIKE pattern matching <paramref name="text"/> anywhere, its wildcards taken literally.</summary>
    public static string Contains(string text) => "%" + Literal(text) + "%";

    /// <summary><paramref name="text"/> as a LIKE pattern that matches only itself.</summary>
    public static string Literal(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    /// <summary>
    /// A short extract of the first of <paramref name="texts"/> that holds one of <paramref name="forms"/>, centred on
    /// the match, with Markdown markup taken out; or the start of <paramref name="fallback"/> when none does.
    /// </summary>
    public static string? Snippet(IEnumerable<string?> texts, IReadOnlyList<string> forms, string? fallback = null)
    {
        foreach (var raw in texts)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var text = Plain(raw);
            var (at, length) = FirstMatch(text, forms);
            if (at < 0) continue;
            var start = Math.Max(0, at - 60);
            var end = Math.Min(text.Length, Math.Max(at + length, start + SnippetLength));
            if (start > 0 && text.IndexOf(' ', start, Math.Min(15, at - start)) is var sp and >= 0) start = sp + 1;
            return (start > 0 ? "…" : "") + text[start..end].Trim() + (end < text.Length ? "…" : "");
        }
        if (string.IsNullOrWhiteSpace(fallback)) return null;
        var f = Plain(fallback);
        return f.Length <= SnippetLength ? f : f[..SnippetLength].TrimEnd() + "…";
    }

    /// <summary><paramref name="text"/> split into runs, the ones matching any of <paramref name="forms"/> marked.</summary>
    public static IReadOnlyList<TextRun> Mark(string? text, IReadOnlyList<string> forms)
    {
        var runs = new List<TextRun>();
        if (string.IsNullOrEmpty(text)) return runs;
        var i = 0;
        while (i < text.Length)
        {
            var (at, length) = FirstMatch(text, forms, i);
            if (at < 0) break;
            if (at > i) runs.Add(new TextRun(text[i..at], false));
            runs.Add(new TextRun(text.Substring(at, length), true));
            i = at + length;
        }
        if (i < text.Length) runs.Add(new TextRun(text[i..], false));
        return runs;
    }

    /// <summary>Every form of every term, longest first (so a phrase is marked whole, not word by word).</summary>
    public static IReadOnlyList<string> AllForms(IEnumerable<string> terms) =>
        terms.SelectMany(Forms).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f.Length).ToList();

    private static (int At, int Length) FirstMatch(string text, IReadOnlyList<string> forms, int from = 0)
    {
        var best = (At: -1, Length: 0);
        foreach (var f in forms)
        {
            if (f.Length == 0) continue;
            var at = text.IndexOf(f, from, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (best.At < 0 || at < best.At || (at == best.At && f.Length > best.Length))) best = (at, f.Length);
        }
        return best;
    }

    // Markdown to the words a reader sees: links keep their text, emphasis and code marks and heading hashes go, and
    // runs of whitespace become one space.
    private static string Plain(string markdown)
    {
        var s = LinkRx().Replace(markdown, "$1");
        s = MarkupRx().Replace(s, "");
        s = HeadingRx().Replace(s, "");
        return SpaceRx().Replace(s, " ").Trim();
    }

    /// <summary>A whole query that is one action number ("A-12", "12"), or null.</summary>
    public static int? ActionNumber(string term) =>
        ActionNumberRx().Match(term.Trim()) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : null;

    /// <summary>The host an indicator names (a domain, the host of a URL or email address, an IP address), lower-cased; or null.</summary>
    public static string? HostOf(IndicatorType type, string value)
    {
        var v = value.Trim();
        var host = type switch
        {
            IndicatorType.Domain or IndicatorType.IpAddress => v,
            IndicatorType.EmailAddress => v.LastIndexOf('@') is var at and >= 0 ? v[(at + 1)..] : null,
            IndicatorType.Url => Uri.TryCreate(v, UriKind.Absolute, out var uri) ? uri.Host.Trim('[', ']') : null,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(host) ? null : host.TrimEnd('.').ToLowerInvariant();
    }

    /// <summary>Whether <paramref name="host"/> is <paramref name="target"/> or, for a name, one of its subdomains.</summary>
    public static bool SameOrUnder(string host, string target) =>
        string.Equals(host, target, StringComparison.OrdinalIgnoreCase)
        || (!System.Net.IPAddress.TryParse(target, out _) && host.EndsWith("." + target, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("\"([^\"]*)\"|(\\S+)")] private static partial Regex TermRx();
    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")] private static partial Regex LinkRx();
    [GeneratedRegex(@"\*\*|__|`|(?<!\w)[*_](?=\S)|(?<=\S)[*_](?!\w)")] private static partial Regex MarkupRx();
    [GeneratedRegex(@"(?m)^\s{0,3}#{1,6}\s+|^\s*[-*+]\s+|^\s*>\s?")] private static partial Regex HeadingRx();
    [GeneratedRegex(@"\s+")] private static partial Regex SpaceRx();
    [GeneratedRegex(@"^(?:A-)?(\d{1,9})$", RegexOptions.IgnoreCase)] private static partial Regex ActionNumberRx();
}
