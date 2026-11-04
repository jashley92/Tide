using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Tide.Domain.Entities;

namespace Tide.Application.Distribution;

/// <summary>
/// What a sent company-wide edition said, read back from its frozen semantic HTML (<see cref="BriefSend.WebHtml"/>):
/// its article headlines, and its words for the published list's filter. Read from the send, never from the articles
/// as they are now, so it can only ever hold what the company was sent.
/// </summary>
public sealed record PublishedContent(IReadOnlyList<string> Headlines, string Text)
{
    public static readonly PublishedContent Empty = new([], "");

    /// <summary>
    /// The headlines (each article's <c>&lt;h2&gt;</c>, its "1. " number taken off) and the plain text of a frozen
    /// edition. <see cref="BriefRenderer.Semantic"/> writes the HTML, so the tags are simple and never nested in a
    /// heading; anything else just comes out as text.
    /// </summary>
    public static PublishedContent Parse(string? html)
    {
        if (string.IsNullOrEmpty(html)) return Empty;
        var headlines = SentSections.Headings(html).Select(h => SentSections.Numbered(TextOf(h.Inner)).Headline)
            .Where(t => t.Length > 0).ToList();
        return new(headlines, TextOf(html));
    }

    /// <summary>This edition with the words of the corrections sent of it, which a filter matches too.</summary>
    public PublishedContent With(IEnumerable<PublishedContent> corrections) =>
        corrections.Aggregate(this, (all, c) => all with { Text = all.Text + " " + c.Text });

    /// <summary>Whether every word of <paramref name="query"/> appears in the edition (case-insensitive); an empty query matches all.</summary>
    public bool Matches(string? query) =>
        Words(query).All(w => Text.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>The words of a filter: split on whitespace, quotes dropped.</summary>
    public static IReadOnlyList<string> Words(string? query) =>
        (query ?? "").Replace("\"", " ", StringComparison.Ordinal)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // The markup's text: tags dropped (a tag is a word break), entities decoded, runs of whitespace made one space.
    internal static string TextOf(string html)
    {
        var sb = new StringBuilder(html.Length);
        var inTag = false;
        foreach (var c in html)
        {
            if (c == '<') { inTag = true; sb.Append(' '); }
            else if (c == '>' && inTag) inTag = false;
            else if (!inTag) sb.Append(c);
        }
        var text = WebUtility.HtmlDecode(sb.ToString());
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>
/// Each send's <see cref="PublishedContent"/>, parsed once: a send is frozen when it's made, so what was parsed from it
/// never goes stale. One entry a published brief, so it stays small (a few hundred editions' text).
/// </summary>
public sealed class PublishedContentCache
{
    private readonly ConcurrentDictionary<Guid, PublishedContent> _bySend = new();

    public bool TryGet(Guid sendId, out PublishedContent content) => _bySend.TryGetValue(sendId, out content!);

    public PublishedContent Add(Guid sendId, string? webHtml) => _bySend.GetOrAdd(sendId, _ => PublishedContent.Parse(webHtml));

    /// <summary>Drops a send's entry: it was withdrawn by a TLP slip, so its words must stop matching (ADR 0015).</summary>
    public void Remove(Guid sendId) => _bySend.TryRemove(sendId, out _);
}

/// <summary>
/// A day in the published list: the sent company-wide edition shown for it, with the headlines it went out with, and the
/// corrections sent of the day's sends, newest first. <see cref="Withdrawn"/> is true when every send of the day was
/// withdrawn by a TLP slip: <see cref="Send"/> is then the withdrawn send's date and time only, and there are no headlines.
/// </summary>
public sealed record PublishedBrief(BriefSend Send, IReadOnlyList<string> Headlines, IReadOnlyList<BriefCorrection>? Corrections = null,
    bool Withdrawn = false)
{
    public IReadOnlyList<BriefCorrection> Corrections { get; init; } = Corrections ?? [];
}
