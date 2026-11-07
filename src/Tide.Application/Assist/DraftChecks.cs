using System.Text.Json;
using System.Text.RegularExpressions;
using Tide.Application.Indicators;
using Tide.Application.Mitre;
using Tide.Domain.Common;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Domain.Observables;

namespace Tide.Application.Assist;

public sealed record SuggestedTechnique(string Id, string Name, string? Reason);
public sealed record SuggestedTag(Guid Id, string Name);
public sealed record SuggestedIndicator(IndicatorType Type, string Value, bool InSource);
public sealed record SuggestedCve(string Id, bool InSource);

/// <summary>
/// A bullet with the sources it cites (numbers as the prompt gave them, only ones that exist) and the phrase quoted from
/// the first. <see cref="QuoteFound"/> says whether that phrase really is in a cited source.
/// </summary>
public sealed record SuggestedBullet(string Text, IReadOnlyList<int> Sources, string? Quote = null, bool QuoteFound = false)
{
    /// <summary>Whether it cites a source and its quote checks out: one that doesn't is flagged.</summary>
    public bool Supported => Sources.Count > 0 && QuoteFound;
}

/// <summary>A fact the sources disagree on, left out of the bullets for the analyst to settle.</summary>
public sealed record SuggestedDisagreement(string Point, IReadOnlyList<int> Sources);

/// <summary>
/// The suggestions that passed <see cref="DraftChecks"/>, each for the analyst to accept or reject. What was left out
/// is counted, so the editor can say so ("1 technique dropped: not in the ATT&amp;CK catalog").
/// </summary>
public sealed record Suggestions(
    string? Bluf,
    IReadOnlyList<SuggestedBullet> Bullets,
    string? WhyItMatters,
    string? RecommendedActions,
    IReadOnlyList<SuggestedTechnique> Techniques,
    IReadOnlyList<SuggestedTag> Tags,
    IReadOnlyList<SuggestedIndicator> Indicators,
    IReadOnlyList<SuggestedCve> Cves,
    int DroppedTechniques = 0,
    int DroppedTags = 0,
    int DroppedIndicators = 0,
    IReadOnlyList<int>? BlufSources = null,
    IReadOnlyList<SuggestedDisagreement>? Disagreements = null)
{
    public IReadOnlyList<int> BlufSources { get; init; } = BlufSources ?? [];
    public IReadOnlyList<SuggestedDisagreement> Disagreements { get; init; } = Disagreements ?? [];

    /// <summary>How many separate things there are to decide on (each bullet, technique, tag, indicator and CVE counts).</summary>
    public int Count => (Bluf is null ? 0 : 1) + Bullets.Count + (WhyItMatters is null ? 0 : 1) + (RecommendedActions is null ? 0 : 1)
        + Techniques.Count + Tags.Count + Indicators.Count + Cves.Count;
}

/// <summary>What an answer is checked against: the texts it was drafted from (numbered from 1, as the prompt gave them),
/// whether our internal context went with it, the configured tags, and what the article already has (not offered again).</summary>
public sealed record DraftContext(
    IReadOnlyList<string> Sources,
    bool HadOurContext,
    IReadOnlyList<(Guid Id, string Name)> Tags,
    IReadOnlySet<string> HaveTechniques,
    IReadOnlySet<Guid> HaveTags,
    IReadOnlySet<string> HaveIndicators,
    IReadOnlySet<string> HaveCves)
{
    /// <summary>All the texts together, for whether an indicator or CVE is in any of them.</summary>
    public string AllText => string.Join("\n", Sources);
}

/// <summary>
/// Every suggestion is checked before anyone sees it (ADR 0004). Text is trimmed and cut to the editor's limits, and
/// Markdown links are reduced to their text, so a page can't plant a link in a brief. Techniques must be in the ATT&amp;CK
/// catalog (a retired one becomes its replacement), tags must be configured ones, and indicators are normalised and
/// marked by whether they're in the source text. "Why it matters" is kept only when our internal context was sent.
/// Each bullet keeps only the source numbers that exist, and its quote is looked for in the sources it cites, so a claim
/// that cites nothing, or misquotes, is flagged. What the article already has isn't offered again.
/// </summary>
public static partial class DraftChecks
{
    public const int MaxIndicators = 100;

    /// <summary>The checked suggestions in <paramref name="json"/>; an answer that isn't the JSON asked for is refused.</summary>
    public static Suggestions Check(string json, DraftContext context)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(Unwrapped(json), new JsonDocumentOptions { AllowTrailingCommas = true }); }
        catch (JsonException) { throw new ArgumentException("The answer isn't the JSON TIDE asked for. Run the prompt again, or check it was pasted whole."); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("The answer isn't the JSON TIDE asked for.");

            var bluf = Text(Str(root, "bottomLine"), 600);
            var bullets = Bullets(root, context);
            var why = context.HadOurContext ? Text(Str(root, "whyItMatters"), 8000) : null;
            var recommended = Text(Str(root, "recommendedActions"), 8000);

            var (techniques, droppedTechniques) = Techniques(root, context);
            var (tags, droppedTags) = Tags(root, context);
            var (indicators, droppedIndicators) = Indicators(root, context);
            var source = IocObservable.Refang(context.AllText);
            var cves = Strings(root, "cves").Select(IocExtractor.NormalizeCve).OfType<string>().Distinct(StringComparer.Ordinal)
                .Where(c => !context.HaveCves.Contains(c)).Select(c => new SuggestedCve(c, source.Contains(c, StringComparison.OrdinalIgnoreCase))).ToList();

            var disagreements = Items(root, "disagreements").Select(d => (Point: Text(Str(d, "point"), 600), Sources: Cited(d, "sources", context)))
                .Where(d => d.Point is not null).Take(10).Select(d => new SuggestedDisagreement(d.Point!, d.Sources)).ToList();
            return new Suggestions(bluf, bullets, why, recommended, techniques, tags, indicators, cves, droppedTechniques, droppedTags, droppedIndicators,
                bluf is null ? [] : Cited(root, "bottomLineSources", context), disagreements);
        }
    }

    /// <summary>The key an indicator is compared by (its type and value, ignoring case), as the editor's form holds them.</summary>
    public static string IndicatorKey(IndicatorType type, string value) => $"{(int)type}|{value.ToUpperInvariant()}";

    // Each bullet as an object (text, sources, quote) or, from an older prompt, a plain string that cites nothing.
    private static List<SuggestedBullet> Bullets(JsonElement root, DraftContext context)
    {
        var kept = new List<SuggestedBullet>();
        foreach (var b in Items(root, "bullets"))
        {
            if (kept.Count >= Article.MaxBullets) break;
            var text = Text(Bulleted((b.ValueKind == JsonValueKind.String ? b.GetString() : Str(b, "text")) ?? ""), 2000);
            if (text is null || kept.Exists(k => string.Equals(k.Text, text, StringComparison.OrdinalIgnoreCase))) continue;
            var sources = b.ValueKind == JsonValueKind.Object ? Cited(b, "sources", context) : [];
            var quote = b.ValueKind == JsonValueKind.Object ? Text(Str(b, "quote"), 400) : null;
            var found = quote is not null && sources.Any(n => Squashed(context.Sources[n - 1]).Contains(Squashed(quote), StringComparison.OrdinalIgnoreCase));
            kept.Add(new SuggestedBullet(text, sources, quote, found));
        }
        return kept;
    }

    // The source numbers in a list that name a source given (1 to the count), in order, once each.
    private static IReadOnlyList<int> Cited(JsonElement e, string name, DraftContext context) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _)).Select(x => x.GetInt32())
                .Where(n => n >= 1 && n <= context.Sources.Count).Distinct().Order().ToList()
            : [];

    private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    // Words separated by single spaces and quotes made straight, so a quote matches across line breaks and typography.
    private static string Squashed(string text) =>
        string.Join(' ', text.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u201C', '"').Replace('\u201D', '"')
            .Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ', '"', '\'');

    private static (List<SuggestedTechnique>, int) Techniques(JsonElement root, DraftContext context)
    {
        var kept = new List<SuggestedTechnique>();
        var dropped = 0;
        if (!root.TryGetProperty("techniques", out var list) || list.ValueKind != JsonValueKind.Array) return (kept, 0);
        foreach (var t in list.EnumerateArray())
        {
            var id = (t.ValueKind == JsonValueKind.Object ? Str(t, "id") : t.ValueKind == JsonValueKind.String ? t.GetString() : null)?.Trim();
            var found = AttackCatalog.Find(id) ?? AttackCatalog.Retired(id)?.ReplacedBy;
            if (found is null) { dropped++; continue; }
            if (context.HaveTechniques.Contains(found.Id) || kept.Exists(k => k.Id == found.Id)) continue;
            kept.Add(new SuggestedTechnique(found.Id, found.Name, t.ValueKind == JsonValueKind.Object ? Text(Str(t, "reason"), 300) : null));
        }
        return (kept, dropped);
    }

    private static (List<SuggestedTag>, int) Tags(JsonElement root, DraftContext context)
    {
        var kept = new List<SuggestedTag>();
        var dropped = 0;
        foreach (var name in Strings(root, "tags"))
        {
            var tag = context.Tags.FirstOrDefault(t => string.Equals(t.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (tag.Id == Guid.Empty) { dropped++; continue; }
            if (context.HaveTags.Contains(tag.Id) || kept.Exists(k => k.Id == tag.Id)) continue;
            kept.Add(new SuggestedTag(tag.Id, tag.Name));
        }
        return (kept, dropped);
    }

    // Each refanged and typed as the editor would type it (the model's type stands only where TIDE can't tell), then
    // normalised; marked by whether the source text has it. Ones that can't be read as an indicator are dropped.
    private static (List<SuggestedIndicator>, int) Indicators(JsonElement root, DraftContext context)
    {
        var kept = new List<SuggestedIndicator>();
        var dropped = 0;
        var source = IocObservable.Refang(context.AllText);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("indicators", out var list) || list.ValueKind != JsonValueKind.Array) return (kept, 0);
        foreach (var i in list.EnumerateArray())
        {
            if (kept.Count >= MaxIndicators) break;
            var raw = i.ValueKind == JsonValueKind.Object ? Str(i, "value") : null;
            var live = IocObservable.Refang((raw ?? "").Trim()).TrimEnd('.', ',', ';');
            if (live.Length is < 3 or > 2000) { dropped++; continue; }
            var type = IocObservable.DetectType(live);
            if (type == IndicatorType.Other && EnumNames.TryParse<IndicatorType>(Str(i, "type"), out var said)) type = said;
            var value = IocObservable.Normalize(type, live);
            var key = IndicatorKey(type, value);
            if (context.HaveIndicators.Contains(key) || !seen.Add(key)) continue;
            kept.Add(new SuggestedIndicator(type, value, source.Contains(value, StringComparison.OrdinalIgnoreCase)));
        }
        return (kept, dropped);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s))
            : [];

    // Trimmed, Markdown links reduced to their text, cut to the field's limit; null when nothing's left.
    private static string? Text(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = Content.MarkdownService.WithoutLinks(text.Replace("\r\n", "\n").Trim());
        return clean.Length == 0 ? null : clean.Length > max ? clean[..(max - 1)].TrimEnd() + "…" : clean;
    }

    private static string Bulleted(string text) => text.TrimStart('-', '*', '•', ' ', '\t');

    // A pasted answer may come wrapped in a Markdown code fence, or with a line of chat before it.
    private static string Unwrapped(string json)
    {
        var s = json.Trim();
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : s;
    }
}
