using System.Net;
using System.Text.RegularExpressions;
using Tide.Domain.Enums;

namespace Tide.Application.Distribution;

/// <summary>
/// The pieces every TIDE email is made from. Email clients (Outlook above all) ignore
/// stylesheets and CSS variables, so styles are written inline on each tag and colours are literal hex: the
/// <see cref="Palette"/> mirrors the light theme's <c>--tide-*</c> tokens, and is the one place outside
/// <c>app.css</c> that names a colour.
/// </summary>
public static partial class EmailComposer
{
    /// <summary>The light theme's tokens as literal colours, for email (which can't read <c>var(--tide-*)</c>).</summary>
    public static class Palette
    {
        public const string Page = "#f5f7f9";        // --tide-bg
        public const string Card = "#ffffff";        // --tide-surface
        public const string Surface2 = "#eef2f5";    // --tide-surface-2
        public const string Border = "#dbe2e8";      // --tide-border
        public const string Text = "#17212b";        // --tide-text
        public const string Muted = "#5b6875";       // --tide-text-muted
        public const string Link = "#0b6a78";        // --tide-accent-text
        public const string Accent = "#0f7c8c";      // --tide-accent
        public const string Header = "#12232f";      // --tide-side-bg
        public const string HeaderText = "#ffffff";
        public const string HeaderMuted = "#c9d6df";  // --tide-side-text
        public const string Threat = "#b4232c";      // --tide-threat
        public const string ThreatSoft = "#fbe3e4";  // --tide-threat-soft
        public const string Info = "#2d5f8b";        // --tide-info
        public const string InfoSoft = "#e1ecf6";    // --tide-info-soft
        public const string Ok = "#1d7a46";          // --tide-ok
        public const string OkSoft = "#dff2e7";      // --tide-ok-soft
        public const string Warn = "#8a5a00";        // --tide-warn
        public const string WarnSoft = "#fbefd6";    // --tide-warn-soft
        public const string TlpBack = "#000000";     // --tide-tlp-bg
    }

    public const string FontStack = "'Segoe UI',-apple-system,Roboto,Helvetica,Arial,sans-serif";

    [GeneratedRegex(@"\s*[\p{Cc}\u2028\u2029]+\s*", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LineBreakRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"<head\b.*?</head>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeadRegex();

    [GeneratedRegex(@"[ \t]+\n",RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrailingSpaceRegex();

    [GeneratedRegex(@"\n{3,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BlankLinesRegex();

    /// <summary>A subject is one header line: line breaks and control characters collapse to one space (a title typed
    /// with a break would otherwise make the mail library throw).</summary>
    public static string SingleLine(string subject) => LineBreakRegex().Replace(subject, " ").Trim();

    /// <summary>HTML-encodes text for an email body or attribute.</summary>
    public static string Esc(string? text) => WebUtility.HtmlEncode(text ?? "");

    /// <summary>
    /// Styles rendered Markdown (the safe HTML from the Markdown service) for email: the few tags it produces get their
    /// styles inline. Links keep their address and lose the new-tab attributes, which mail clients ignore anyway.
    /// </summary>
    public static string InlineStyles(string html)
    {
        var para = $"margin:0 0 10px;font-size:15px;line-height:1.55;color:{Palette.Text};";
        var list = "margin:0 0 10px;padding-left:22px;";
        var item = $"margin:0 0 5px;font-size:15px;line-height:1.5;color:{Palette.Text};";
        var code = $"font-family:Consolas,'Courier New',monospace;font-size:13px;background:{Palette.Surface2};padding:1px 4px;";
        return html
            .Replace("<p>", $"<p style=\"{para}\">")
            .Replace("<ul>", $"<ul style=\"{list}\">")
            .Replace("<ol>", $"<ol style=\"{list}\">")
            .Replace("<li>", $"<li style=\"{item}\">")
            .Replace("<code>", $"<code style=\"{code}\">")
            .Replace("<blockquote>", $"<blockquote style=\"margin:0 0 10px;padding-left:12px;border-left:3px solid {Palette.Border};color:{Palette.Muted};\">")
            .Replace(" target=\"_blank\" rel=\"noopener noreferrer\"", "")
            .Replace("<a href", $"<a style=\"color:{Palette.Link};\" href")
            .Replace("<span class=\"bi bi-image\" aria-hidden=\"true\"></span> ", "[image] ");
    }

    /// <summary>
    /// The plain-text part of an email from its HTML: block ends become line breaks, list items get a dash, tags go,
    /// entities are decoded. Clients that can't show HTML (and some filters) read this.
    /// </summary>
    public static string ToText(string html)
    {
        // The head's <title> repeats the heading, and a list's end is a block end like a paragraph's.
        var t = HeadRegex().Replace(html, "")
            .Replace("<li", "\n- <li", StringComparison.OrdinalIgnoreCase)
            .Replace("</ul>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</ol>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</p>", "\n\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</h1>", "\n\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</h2>", "\n\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</h3>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</tr>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</td>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("</div>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase);
        t = WebUtility.HtmlDecode(TagRegex().Replace(t, ""));
        t = string.Join('\n', t.Split('\n').Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries))));
        t = TrailingSpaceRegex().Replace(t, "\n");
        return BlankLinesRegex().Replace(t, "\n\n").Trim();
    }

    /// <summary>A TLP marking as email can show it: the standard colour on black, bold, monospaced.</summary>
    public static string TlpLabel(TlpLevel level, int fontPx = 12) =>
        $"<span style=\"background:{Palette.TlpBack};color:#{Tlp.Color(level)};font-family:Consolas,'Courier New',monospace;" +
        $"font-weight:700;font-size:{fontPx}px;padding:2px 6px;white-space:nowrap;\">{Esc(Tlp.Label(level))}</span>";

    /// <summary>
    /// A plain notification email (a reminder, a digest): a header with the title, the body, and a footer. The body is
    /// HTML the caller built with <see cref="Esc"/>; it's shown as is.
    /// </summary>
    public static string Shell(string title, string subtitle, string bodyHtml, string footer) => $"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{Esc(title)}</title></head>
        <body style="margin:0;padding:0;background:{Palette.Page};">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:{Palette.Page};">
        <tr><td align="center" style="padding:20px 10px;">
        <table role="presentation" width="640" cellpadding="0" cellspacing="0" border="0" style="width:640px;max-width:100%;background:{Palette.Card};border:1px solid {Palette.Border};font-family:{FontStack};">
        <tr><td style="background:{Palette.Header};padding:16px 24px;color:{Palette.HeaderText};">
          <div style="font-size:18px;font-weight:700;">{Esc(title)}</div>
          <div style="font-size:13px;color:{Palette.HeaderMuted};margin-top:3px;">{Esc(subtitle)}</div>
        </td></tr>
        <tr><td style="height:4px;line-height:4px;font-size:4px;background:{Palette.Accent};">&nbsp;</td></tr>
        <tr><td style="padding:18px 24px;font-size:14px;line-height:1.5;color:{Palette.Text};">{bodyHtml}</td></tr>
        <tr><td style="padding:12px 24px;background:{Palette.Page};border-top:1px solid {Palette.Border};font-size:12px;color:{Palette.Muted};">{Esc(footer)}</td></tr>
        </table>
        </td></tr>
        </table>
        </body></html>
        """;
}
