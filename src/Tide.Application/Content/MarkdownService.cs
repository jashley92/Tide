using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Tide.Application.Content;

/// <summary>
/// Renders analyst-written Markdown (bullets, "why it matters") to safe HTML. Storage stays Markdown; rendering happens
/// at the edges. Raw HTML is disabled and dangerous link schemes are neutralised, so stored content can't inject script;
/// the app's strict CSP (<c>script-src 'self'</c>) is a second line of defence.
/// <para>
/// Bare addresses are never turned into links (no autolinks): an article's bullets quote malicious URLs and domains, and
/// a brief must not make them clickable. Only a link the analyst wrote as <c>[text](url)</c> is one.
/// </para>
/// </summary>
public interface IMarkdownService
{
    /// <summary>Markdown to sanitised HTML (raw HTML escaped, unsafe link schemes dropped).</summary>
    string ToHtml(string? markdown);

    /// <summary>One line of Markdown to sanitised inline HTML with no wrapping paragraph: bold, italics, code and
    /// links. Block syntax (lists, headings) isn't parsed and shows as typed.</summary>
    string ToInlineHtml(string? markdown);

    /// <summary>Markdown to readable plain text (formatting stripped).</summary>
    string ToPlainText(string? markdown);
}

public sealed partial class MarkdownService : IMarkdownService
{
    // Precise source locations, so a link's destination can be read as it was typed (TypedWithBackslash).
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().UsePreciseSourceLocation().Build();

    // One-line fields: inline syntax only. A paragraph is the only block, so "- x" or "# x" stays literal text.
    private static readonly MarkdownPipeline InlinePipeline = BuildInline();

    private static MarkdownPipeline BuildInline()
    {
        var b = new MarkdownPipelineBuilder().DisableHtml().UsePreciseSourceLocation();
        b.BlockParsers.RemoveAll(p => p is not Markdig.Parsers.ParagraphBlockParser);
        return b.Build();
    }

    // Only these URL schemes are allowed on links; everything else (javascript:, data:, vbscript:, file:) is rewritten
    // to an inert anchor so stored Markdown can't carry an executable payload.
    private static readonly string[] AllowedSchemes = ["http://", "https://", "mailto:"];

    /// <summary>A one-line field's text as stored: line breaks and runs of spaces become single spaces.</summary>
    public static string OneLine(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Pasted text as bullets, one per non-blank line. A list marker ("-", "*", "+", "•", "·" or "1.") is dropped only
    /// when a space follows it, so a line that starts with Markdown bold ("**CVE-…**") keeps its asterisks.
    /// </summary>
    public static IReadOnlyList<string> BulletLines(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => ListMarker().Replace(l, "").Trim()).Where(l => l.Length > 0).ToList();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:[-*+•·‣▪]|\d{1,2}[.)])\s+")]
    private static partial System.Text.RegularExpressions.Regex ListMarker();

    public string ToHtml(string? markdown) =>
        string.IsNullOrWhiteSpace(markdown) ? string.Empty : Render(markdown, Pipeline);

    public string ToInlineHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        var html = Render(OneLine(markdown), InlinePipeline).Trim();
        return html.StartsWith("<p>", StringComparison.Ordinal) && html.EndsWith("</p>", StringComparison.Ordinal) ? html[3..^4] : html;
    }

    public string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        try { return Markdown.ToPlainText(markdown, Pipeline).Trim(); }
        catch (ArgumentException) { return OneLine(markdown); }   // too deeply nested: see Render
    }

    /// <summary>Whether Markdig can render <paramref name="markdown"/>: a run of a few hundred brackets or quote markers,
    /// well within a field's length, nests deeper than it allows.</summary>
    public static bool IsRenderable(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return true;
        try { RenderOrThrow(markdown, Pipeline); return true; }
        catch (ArgumentException) { return false; }
    }

    // Markdig refuses input nested deeper than it allows (a few hundred '[' or '> ' in a row) with an exception, from
    // the parser or the renderer. That would fail every render of the brief the text is on (the pages, the email, the
    // send) until someone found the field, so such text is shown as it was typed, escaped, instead.
    private static string Render(string markdown, MarkdownPipeline pipeline)
    {
        try { return RenderOrThrow(markdown, pipeline); }
        catch (ArgumentException) { return "<p>" + System.Net.WebUtility.HtmlEncode(markdown.Trim()) + "</p>\n"; }
    }

    private static string RenderOrThrow(string markdown, MarkdownPipeline pipeline)
    {
        var document = Markdown.Parse(markdown, pipeline);
        foreach (var link in document.Descendants<LinkInline>())
            // The address as typed is checked too: Markdig unescapes a backslash before a slash ("\/evil.example" becomes
            // "/evil.example"), which a browser would read as it was typed, as another host.
            if (!IsSafeUrl(link.Url) || TypedWithBackslash(markdown, link))
                link.Url = "#";
        // An angle-bracket autolink (<javascript:…>) is its own inline, not a LinkInline. An unsafe one stays as its text.
        foreach (var auto in document.Descendants<AutolinkInline>().Where(a => !a.IsEmail && !IsSafeUrl(a.Url)).ToList())
            auto.ReplaceBy(new LiteralInline(auto.Url));

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<LinkInlineRenderer>(new SafeLinkRenderer());
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>Links open in a new tab without a referrer or opener. An image isn't loaded inline: its URL would be
    /// fetched by every reader's browser (a tracking pixel, or worse, from a malicious host the article is about).</summary>
    private sealed class SafeLinkRenderer : LinkInlineRenderer
    {
        protected override void Write(HtmlRenderer renderer, LinkInline link)
        {
            if (link.IsImage)
            {
                renderer.Write("<span class=\"tide-image-ref\" title=\"Images aren't shown inline. Add it as a screenshot.\">");
                renderer.Write("<span class=\"bi bi-image\" aria-hidden=\"true\"></span> ");
                if (link.FirstChild is null) renderer.WriteEscape(link.Url ?? "image");
                else renderer.WriteChildren(link);
                renderer.Write("</span>");
                return;
            }
            renderer.Write("<a href=\"").WriteEscapeUrl(link.Url ?? "#").Write("\" target=\"_blank\" rel=\"noopener noreferrer\">");
            renderer.WriteChildren(link);
            renderer.Write("</a>");
        }
    }

    // Whether an inline link's destination, as typed ("[x](\/evil.example)"), holds a backslash: Markdig has unescaped it
    // by the time the address is read, but a browser given the typed text would read it as a slash.
    private static bool TypedWithBackslash(string markdown, LinkInline link)
    {
        if (link.Span.IsEmpty || link.Span.Start < 0 || link.Span.End >= markdown.Length) return false;
        var raw = markdown.AsSpan(link.Span.Start, link.Span.Length);
        var destination = raw.LastIndexOf("](", StringComparison.Ordinal);
        return destination >= 0 && raw[destination..].Contains('\\');
    }

    private static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;   // empty/relative anchors are fine
        var u = url.TrimStart();
        // A protocol-relative address (//evil.example) is a link to another host that names no scheme, so it would slip
        // past the scheme check below; it's refused like any scheme that isn't allowed. Browsers read a backslash as a
        // slash there (/\evil.example, \\evil.example and \/evil.example are that address), and no web address needs
        // one, so any backslash is refused too.
        if (u.StartsWith("//", StringComparison.Ordinal) || u.Contains('\\')) return false;
        if (u.StartsWith('/') || u.StartsWith('#') || u.StartsWith('.')) return true;
        if (!u.Contains(':')) return true;
        return AllowedSchemes.Any(s => u.StartsWith(s, StringComparison.OrdinalIgnoreCase));
    }
}
