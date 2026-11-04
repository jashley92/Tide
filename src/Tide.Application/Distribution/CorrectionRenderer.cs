using System.Globalization;
using System.Text;
using Tide.Domain.Enums;
using static Tide.Application.Distribution.EmailComposer;

namespace Tide.Application.Distribution;

/// <summary>What became of an article a correction is about.</summary>
public enum CorrectedState
{
    /// <summary>Its text differs from what was sent; the diff says how.</summary>
    Changed,

    /// <summary>Its text is as it was sent (the note says what changed: a screenshot, say).</summary>
    Unchanged,

    /// <summary>It isn't in the edition now (taken off the brief, or raised above the ceiling), so nothing of it is repeated.</summary>
    LeftTheEdition,
}

/// <summary>
/// An article in a correction: its number and headline as sent (no headline when it has left the edition, or for a slip
/// whose headline is what slipped), and its diff. <see cref="RestrictedNow"/> counts the lines that went but are above the
/// ceiling now, which the diff leaves out rather than repeat.
/// </summary>
public sealed record ArticleDiff(int Number, Guid ArticleId, string? Headline, CorrectedState State, IReadOnlyList<DiffLine> Lines, int RestrictedNow = 0)
{
    /// <summary>"Article 2: Phishing kit targets payroll", or "Article 2".</summary>
    public string Label => Headline is null ? $"Article {Number}" : $"Article {Number}: {Headline}";
}

/// <summary>What a correction says: everything in it is already cut to its ceiling.</summary>
public sealed record CorrectionContent(string TeamName, string? AppUrl, Edition Edition, DateOnly BriefDate, TlpLevel Marking, bool IsTlpSlip,
    string? Note, IReadOnlyList<ArticleDiff> Articles);

/// <summary>A correction rendered for sending: the email (HTML and text) and the plain web copy for the published page.</summary>
public sealed record RenderedCorrection(string EmailHtml, string Text, string WebHtml);

/// <summary>
/// Renders a correction (ADR 0015) as the brief's email is rendered: tables and inline styles from <see cref="EmailComposer"/>,
/// so Outlook shows it as intended, and plain semantic HTML for the archive. It's handed content already cut by the
/// correction's ceiling and renders only that. A TLP slip has fixed wording, and never repeats what leaked.
/// </summary>
public static class CorrectionRenderer
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The fixed words of a slip that can't be taken back: the email wasn't recalled, and deleting it is the reader's job.</summary>
    public const string NotRecalled = "The email was not and cannot be recalled: deleting it is up to you.";

    /// <summary>The original's subject with "Correction:" in front, its marking the correction's when that's different.</summary>
    public static string Subject(string original, TlpLevel originalMarking, TlpLevel marking)
    {
        var tag = $"[{Tlp.Label(originalMarking)}]";
        var subject = marking != originalMarking && original.EndsWith(tag, StringComparison.Ordinal)
            ? original[..^tag.Length] + $"[{Tlp.Label(marking)}]"
            : original;
        return SingleLine("Correction: " + subject);
    }

    public static RenderedCorrection Render(CorrectionContent c)
    {
        var html = Email(c);
        return new RenderedCorrection(html, ToText(html), Web(c));
    }

    /// <summary>"the company-wide edition of Monday, October 26, 2026".</summary>
    private static string Which(CorrectionContent c) => $"the {BriefRenderer.EditionName(c.Edition).ToLowerInvariant()} of {BriefRenderer.DayText(c.BriefDate)}";

    /// <summary>The slip's first lines: what went wrong, and where, by number (and headline when the writer says it's safe).</summary>
    public static string SlipText(CorrectionContent c)
    {
        var where = c.Articles.Count switch
        {
            0 => "",
            1 => $" It was in {Where(c.Articles[0])}.",
            _ => $" It was in {string.Join(", ", c.Articles.Take(c.Articles.Count - 1).Select(Where))} and {Where(c.Articles[^1])}.",
        };
        return $"Something in {Which(c)} was sent above its audience's marking.{where} Please delete that email, and any copies or forwards of it.";
    }

    private static string Where(ArticleDiff a) => a.Headline is null ? $"article {a.Number}" : $"article {a.Number} (“{a.Headline}”)";

    // --- email ---------------------------------------------------------------------------------------------------

    private static string Email(CorrectionContent c)
    {
        var title = c.IsTlpSlip ? "TLP slip" : "Correction";
        var sb = new StringBuilder();
        sb.Append(Invariant, $"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta name="x-apple-disable-message-reformatting"><title>{Esc(title)}</title></head>
            <body style="margin:0;padding:0;background:{Palette.Page};">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:{Palette.Page};">
            <tr><td align="center" style="padding:20px 10px;">
            <table role="presentation" width="{BriefRenderer.EmailWidth}" cellpadding="0" cellspacing="0" border="0" style="width:{BriefRenderer.EmailWidth}px;max-width:100%;background:{Palette.Card};border:1px solid {Palette.Border};font-family:{FontStack};">
            <tr><td style="background:{Palette.Header};padding:18px 24px;">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"><tr>
                <td valign="top" style="color:{Palette.HeaderText};font-family:{FontStack};">
                  <div style="font-size:19px;font-weight:700;">{Esc(c.TeamName)} daily brief: {Esc(title.ToLowerInvariant())}</div>
                  <div style="font-size:13px;color:{Palette.HeaderMuted};margin-top:3px;">{Esc(BriefRenderer.DayText(c.BriefDate))} &middot; {Esc(BriefRenderer.EditionName(c.Edition))}</div>
                </td>
                <td valign="top" align="right" style="font-family:{FontStack};">{TlpLabel(c.Marking, 13)}</td>
              </tr></table>
            </td></tr>
            <tr><td style="height:4px;line-height:4px;font-size:4px;background:{(c.IsTlpSlip ? Palette.Threat : Palette.Warn)};">&nbsp;</td></tr>
            <tr><td style="padding:10px 24px;background:{Palette.Surface2};font-size:13px;line-height:1.5;color:{Palette.Muted};">This email is {TlpLabel(c.Marking, 11)} {Esc(Tlp.Meaning(c.Marking))}</td></tr>
            """);

        if (c.IsTlpSlip)
        {
            sb.Append(Invariant, $"""
                <tr><td style="padding:18px 24px 6px;">
                  <p style="margin:0 0 10px;font-size:15px;line-height:1.55;font-weight:600;color:{Palette.Text};">{Esc(SlipText(c))}</p>
                  <p style="margin:0 0 10px;font-size:15px;line-height:1.55;color:{Palette.Threat};font-weight:600;">{Esc(NotRecalled)}</p>
                </td></tr>
                """);
            if (!string.IsNullOrWhiteSpace(c.Note))
                sb.Append(Invariant, $"<tr><td style=\"padding:4px 24px 14px;\">{Label("From the sender")}<p style=\"margin:0;font-size:15px;line-height:1.55;color:{Palette.Text};\">{Lines(c.Note)}</p></td></tr>");
        }
        else
        {
            sb.Append(Invariant, $"""
                <tr><td style="padding:18px 24px 6px;">
                  <p style="margin:0 0 12px;font-size:15px;line-height:1.55;color:{Palette.Text};">This corrects {Esc(Which(c))}, which you were sent.</p>
                  {Label("What changed")}
                  <p style="margin:0 0 6px;font-size:15px;line-height:1.55;font-weight:600;color:{Palette.Text};">{Lines(c.Note)}</p>
                </td></tr>
                """);
            foreach (var a in c.Articles) sb.Append(ArticleBlock(a));
        }

        var link = string.IsNullOrWhiteSpace(c.AppUrl) || c.IsTlpSlip ? "" :
            $" &middot; <a style=\"color:{Palette.Link};\" href=\"{Esc(c.AppUrl!.TrimEnd('/') + (c.Edition == Edition.Public ? "/published/" : "/briefs/") + c.BriefDate.ToString("yyyy-MM-dd", Invariant))}\">Open in TIDE</a>";
        sb.Append(Invariant, $"""
            <tr><td style="padding:14px 24px;background:{Palette.Page};border-top:1px solid {Palette.Border};font-size:12px;line-height:1.6;color:{Palette.Muted};">
              {TlpLabel(c.Marking, 11)} {Esc(Tlp.Meaning(c.Marking))}<br>
              {Esc(c.TeamName)} &middot; {Esc(BriefRenderer.EditionName(c.Edition))}{link}
            </td></tr>
            </table>
            </td></tr>
            </table>
            </body></html>
            """);
        return sb.ToString();
    }

    private static string Label(string text) =>
        $"<div style=\"margin:0 0 4px;font-size:12px;font-weight:700;letter-spacing:.06em;text-transform:uppercase;color:{Palette.Muted};\">{Esc(text)}</div>";

    private static string ArticleBlock(ArticleDiff a)
    {
        var sb = new StringBuilder();
        sb.Append(Invariant, $"""
            <tr><td style="padding:14px 24px;border-top:1px solid {Palette.Border};">
              <h2 style="margin:0 0 8px;font-size:17px;line-height:1.3;font-weight:700;color:{Palette.Text};">{Esc(a.Label)}</h2>
            """);
        var muted = $"margin:0 0 6px;font-size:14px;line-height:1.5;color:{Palette.Muted};";
        if (a.State == CorrectedState.LeftTheEdition)
            sb.Append(Invariant, $"<p style=\"{muted}\">{Esc(LeftText)}</p>");
        else if (a.State == CorrectedState.Unchanged)
            sb.Append(Invariant, $"<p style=\"{muted}\">{Esc(UnchangedText)}</p>");
        else
        {
            sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:0 0 6px;\">");
            foreach (var line in a.Lines.Where(l => l.Kind != DiffKind.Same))
            {
                var (label, back) = line.Kind == DiffKind.Removed ? ("Was", Palette.ThreatSoft) : ("Now", Palette.OkSoft);
                sb.Append(Invariant, $"<tr><td valign=\"top\" style=\"padding:4px 10px;width:44px;background:{back};font-size:12px;font-weight:700;color:{Palette.Text};white-space:nowrap;\">{label}</td>" +
                          $"<td style=\"padding:4px 10px;background:{back};font-size:14px;line-height:1.5;color:{Palette.Text};\">{Esc(line.Text)}</td></tr>");
            }
            sb.Append("</table>");
        }
        if (a.RestrictedNow > 0) sb.Append(Invariant, $"<p style=\"{muted}font-style:italic;\">{Esc(Restricted(a.RestrictedNow))}</p>");
        sb.Append("</td></tr>");
        return sb.ToString();
    }

    private const string LeftText = "This article is no longer in this edition, so none of it is repeated here.";
    private const string UnchangedText = "Its text is unchanged; the note above says what changed.";

    private static string Restricted(int n) =>
        $"{n} {(n == 1 ? "line that went is" : "lines that went are")} restricted now, so {(n == 1 ? "it isn't" : "they aren't")} repeated here.";

    // A note is plain text: escaped, its line breaks kept.
    private static string Lines(string? text) =>
        string.Join("<br>", (text ?? "").Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(Esc));

    // --- web copy ------------------------------------------------------------------------------------------------

    private static string Web(CorrectionContent c)
    {
        var sb = new StringBuilder();
        sb.Append(Invariant, $"<p><strong>{Esc(Tlp.Label(c.Marking))}</strong> {Esc(Tlp.Meaning(c.Marking))}</p>\n");
        if (c.IsTlpSlip)
        {
            sb.Append(Invariant, $"<p><strong>{Esc(SlipText(c))}</strong></p>\n<p>{Esc(NotRecalled)}</p>\n");
            if (!string.IsNullOrWhiteSpace(c.Note)) sb.Append(Invariant, $"<h3>From the sender</h3>\n<p>{Lines(c.Note)}</p>\n");
            return sb.ToString();
        }
        sb.Append(Invariant, $"<p>This corrects {Esc(Which(c))}.</p>\n<h3>What changed</h3>\n<p>{Lines(c.Note)}</p>\n");
        foreach (var a in c.Articles)
        {
            sb.Append(Invariant, $"<h3>{Esc(a.Label)}</h3>\n");
            if (a.State == CorrectedState.LeftTheEdition) sb.Append(Invariant, $"<p><em>{Esc(LeftText)}</em></p>\n");
            else if (a.State == CorrectedState.Unchanged) sb.Append(Invariant, $"<p><em>{Esc(UnchangedText)}</em></p>\n");
            else
            {
                sb.Append("<ul>\n");
                foreach (var line in a.Lines.Where(l => l.Kind != DiffKind.Same))
                    sb.Append(line.Kind == DiffKind.Removed
                        ? $"<li><del>Was: {Esc(line.Text)}</del></li>\n"
                        : $"<li><ins>Now: {Esc(line.Text)}</ins></li>\n");
                sb.Append("</ul>\n");
            }
            if (a.RestrictedNow > 0) sb.Append(Invariant, $"<p><em>{Esc(Restricted(a.RestrictedNow))}</em></p>\n");
        }
        return sb.ToString();
    }
}
