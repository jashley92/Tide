using System.Globalization;
using System.Text;
using Tide.Application.Briefs;
using Tide.Application.Actions;
using Tide.Application.Content;
using Tide.Application.Indicators;
using Tide.Application.Metrics;
using Tide.Application.Mitre;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using static Tide.Application.Distribution.EmailComposer;

namespace Tide.Application.Distribution;

/// <summary>A screenshot in a rendering: its Content-ID (for email), its pixel size when known, and its place.</summary>
public sealed record RenderImage(ArticleScreenshot Shot, string ContentId, int Number, (int Width, int Height)? Size)
{
    // The extensions a stored screenshot can have (ScreenshotService.SafeName); anything else in a zip entry's name is
    // replaced, so a stored name can never carry a path or an odd character into the archive.
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".gif", ".webp" };

    /// <summary>A file name for the image in the SharePoint zip: its number and caption ("02-login-page.png"). Only
    /// its number, letters, digits, hyphens and a known image extension: never a path.</summary>
    public string FileName
    {
        get
        {
            var ext = Path.GetExtension(Shot.File?.FileName ?? "") is { Length: > 0 } e && ImageExtensions.Contains(e) ? e.ToLowerInvariant() : ".png";
            var words = new string((Shot.Caption ?? "screenshot").ToLowerInvariant()
                .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
            var slug = string.Join('-', words.Split('-', StringSplitOptions.RemoveEmptyEntries));
            if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
            return $"{Number:00}-{(slug.Length == 0 ? "screenshot" : slug)}{ext}";
        }
    }
}

/// <summary>What a rendering needs besides the edition: names, the day's metrics (team edition only), the app's
/// address for links, how an image is referenced, and the images' pixel sizes.</summary>
public sealed record BriefRenderContext(
    string TeamName,
    MetricDay? Metrics,
    string? AppUrl,
    Func<RenderImage, string> ImageSrc,
    IReadOnlyDictionary<Guid, (int Width, int Height)>? Sizes = null,
    Actions.OpenActionsReview? Open = null)
{
    /// <summary>Where a root-relative link in the brief's Markdown ("/actions/12") points: at <see cref="AppUrl"/>, or
    /// nowhere (its text kept) when that isn't set. The copy leaves TIDE, so it has no page to be relative to. Worked out
    /// once, from the AppUrl the context is made with.</summary>
    public LinkRoot Links { get; } = LinkRoot.Email(AppUrl);
}

/// <summary>A brief edition rendered for sending: the subject, the email (HTML and text), and the plain web copy.</summary>
public sealed record RenderedBrief(string Subject, string EmailHtml, string Text, string WebHtml, IReadOnlyList<RenderImage> Images);

/// <summary>
/// Turns a <see cref="BriefEdition"/> into what leaves TIDE: an email Outlook shows as intended (tables, inline styles,
/// images by Content-ID at their real width), and clean semantic HTML for the SharePoint copy and the archive. It is
/// handed an edition already cut by <see cref="BriefEditions.Build"/> and renders only what's in it, so there's no second
/// filter to drift: nothing withheld can appear. Metrics, indicators and follow-up actions are drawn only on the team edition
/// (the edition carries them only there).
/// </summary>
public sealed class BriefRenderer(IMarkdownService markdown)
{
    /// <summary>The most indicators an article lists in an edition; the rest are in the export.</summary>
    public const int MaxIndicatorsShown = 15;

    /// <summary>The email's width, and the widest an image is drawn inside it.</summary>
    public const int EmailWidth = 640;
    public const int ImageMaxWidth = EmailWidth - 48;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string DayText(DateOnly d) => d.ToString("dddd, MMMM d, yyyy", Invariant);

    public static string EditionName(Edition e) => e == Edition.Public ? "Company-wide edition" : "Security team edition";

    /// <summary>The subject line: team, day, edition and marking.</summary>
    public static string Subject(BriefEdition edition, string teamName) =>
        SingleLine($"{teamName} daily brief, {edition.Brief.BriefDate.ToString("ddd MMM d", Invariant)}" +
                   $"{(edition.Edition == Edition.Team ? " (security team)" : "")} [{Tlp.Label(edition.Marking)}]");

    /// <summary>The screenshots the edition shows, numbered in order, each with a Content-ID. <paramref name="include"/>,
    /// when given, keeps only the screenshots it accepts (an email leaves out one whose file is gone).</summary>
    public static IReadOnlyList<RenderImage> ImagesOf(BriefEdition edition, IReadOnlyDictionary<Guid, (int Width, int Height)>? sizes = null,
        Func<ArticleScreenshot, bool>? include = null)
    {
        var n = 0;
        return edition.Articles.SelectMany(a => a.Screenshots).Where(s => include is null || include(s)).Select(s =>
        {
            n++;
            (int, int)? size = sizes is not null && sizes.TryGetValue(s.FileId, out var z) ? z : null;
            return new RenderImage(s, $"shot{n}@tide", n, size);
        }).ToList();
    }

    /// <summary>The edition rendered for sending, with <paramref name="images"/> (by default every screenshot it shows).</summary>
    public RenderedBrief Render(BriefEdition edition, BriefRenderContext ctx, IReadOnlyList<RenderImage>? images = null)
    {
        images ??= ImagesOf(edition, ctx.Sizes);
        var html = Email(edition, ctx, images);
        return new RenderedBrief(Subject(edition, ctx.TeamName), html, ToText(html), Semantic(edition, ctx, images, null), images);
    }

    // --- email ---------------------------------------------------------------------------------------------------

    /// <summary>The edition as an email document.</summary>
    public string Email(BriefEdition edition, BriefRenderContext ctx, IReadOnlyList<RenderImage>? images = null)
    {
        images ??= ImagesOf(edition, ctx.Sizes);
        var imageFor = images.ToDictionary(i => i.Shot.Id);
        var brief = edition.Brief;
        var subject = Subject(edition, ctx.TeamName);
        var sb = new StringBuilder();
        sb.Append(Invariant, $"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta name="x-apple-disable-message-reformatting"><title>{Esc(subject)}</title></head>
            <body style="margin:0;padding:0;background:{Palette.Page};">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:{Palette.Page};">
            <tr><td align="center" style="padding:20px 10px;">
            <table role="presentation" width="{EmailWidth}" cellpadding="0" cellspacing="0" border="0" style="width:{EmailWidth}px;max-width:100%;background:{Palette.Card};border:1px solid {Palette.Border};font-family:{FontStack};">
            <tr><td style="background:{Palette.Header};padding:18px 24px;">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"><tr>
                <td valign="top" style="color:{Palette.HeaderText};font-family:{FontStack};">
                  <div style="font-size:19px;font-weight:700;">{Esc(ctx.TeamName)} daily brief</div>
                  <div style="font-size:13px;color:{Palette.HeaderMuted};margin-top:3px;">{Esc(DayText(brief.BriefDate))} &middot; presented by {Esc(brief.AnalystName)}</div>
                </td>
                <td valign="top" align="right" style="font-family:{FontStack};">
                  {TlpLabel(edition.Marking, 13)}
                  <div style="font-size:12px;color:{Palette.HeaderMuted};margin-top:6px;">{Esc(EditionName(edition.Edition))}</div>
                </td>
              </tr></table>
            </td></tr>
            <tr><td style="height:4px;line-height:4px;font-size:4px;background:{Palette.Accent};">&nbsp;</td></tr>
            <tr><td style="padding:10px 24px;background:{Palette.Surface2};font-size:13px;line-height:1.5;color:{Palette.Muted};">This email is {TlpLabel(edition.Marking, 11)} {Esc(Tlp.Meaning(edition.Marking))}</td></tr>
            """);

        // The team's opening note, before the numbers: the team edition only (the edition carries it nowhere else).
        if (edition.TeamNote is { } note) sb.Append(TeamNoteBlock(note, ctx.Links));
        if (edition.Edition == Edition.Team && ctx.Metrics is { Any: true } metrics) sb.Append(MetricsBlock(metrics));
        // Overdue actions, after the numbers: the team edition only, never the company-wide one (ADR 0005).
        if (edition.Edition == Edition.Team && ctx.Open is { Any: true } open) sb.Append(OpenActionsBlock(open, ctx.AppUrl));

        if (edition.ShowIntro)
            sb.Append(Invariant, $"<tr><td style=\"padding:18px 24px 4px;\">{InlineStyles(markdown.ToHtml(brief.Intro, ctx.Links))}</td></tr>");

        if (edition.Articles.Count == 0)
            sb.Append(Invariant, $"<tr><td style=\"padding:18px 24px;font-size:15px;color:{Palette.Muted};\">Nothing in this edition today.</td></tr>");

        for (var i = 0; i < edition.Articles.Count; i++)
            sb.Append(ArticleBlock(edition.Articles[i], i + 1, ctx, imageFor));

        if (edition.WithheldArticles > 0)
            sb.Append(Invariant, $"<tr><td style=\"padding:12px 24px;font-size:13px;font-style:italic;color:{Palette.Muted};border-top:1px solid {Palette.Border};\">" +
                      $"{Withheld(edition.WithheldArticles)}</td></tr>");

        var link = string.IsNullOrWhiteSpace(ctx.AppUrl) ? "" :
            $" &middot; <a style=\"color:{Palette.Link};\" href=\"{Esc(ctx.AppUrl!.TrimEnd('/') + (edition.Edition == Edition.Public ? "/published/" : "/briefs/") + brief.BriefDate.ToString("yyyy-MM-dd", Invariant))}\">Open in TIDE</a>";
        sb.Append(Invariant, $"""
            <tr><td style="padding:14px 24px;background:{Palette.Page};border-top:1px solid {Palette.Border};font-size:12px;line-height:1.6;color:{Palette.Muted};">
              {TlpLabel(edition.Marking, 11)} {Esc(Tlp.Meaning(edition.Marking))}<br>
              {Esc(ctx.TeamName)} &middot; {Esc(EditionName(edition.Edition))}{link}
            </td></tr>
            </table>
            </td></tr>
            </table>
            </body></html>
            """);
        return sb.ToString();
    }

    private static string Withheld(int articles) =>
        $"{articles} restricted {(articles == 1 ? "item was" : "items were")}{WithheldEnd}";

    /// <summary>How the edition's count of articles left out ends, for reading a frozen copy back by article.</summary>
    internal const string WithheldEnd = " briefed to the security team and left out of this edition.";

    /// <summary>The heading of the team's opening note, in the email and the plain copy.</summary>
    public const string TeamNoteHeading = "Opening note";

    private string TeamNoteBlock(string note, LinkRoot links) =>
        $"<tr><td style=\"padding:16px 24px 6px;\"><div style=\"font-size:12px;font-weight:700;letter-spacing:.06em;text-transform:uppercase;color:{Palette.Muted};margin-bottom:6px;\">{TeamNoteHeading}</div>" +
        $"{InlineStyles(markdown.ToHtml(note, links))}</td></tr>";

    private static string MetricsBlock(MetricDay day)
    {
        var sb = new StringBuilder();
        sb.Append(Invariant, $"<tr><td style=\"padding:16px 24px 6px;\"><div style=\"font-size:12px;font-weight:700;letter-spacing:.06em;text-transform:uppercase;color:{Palette.Muted};\">The day's numbers</div>");
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\">");
        foreach (var g in day.Groups)
        {
            sb.Append(Invariant, $"<tr><td colspan=\"4\" style=\"padding:10px 0 3px;font-size:13px;font-weight:700;color:{Palette.Text};border-bottom:1px solid {Palette.Border};\">{Esc(g.Group.Name)}</td></tr>");
            foreach (var t in g.Tiles)
            {
                var valueColor = t.Level switch { MetricLevel.Critical => Palette.Threat, MetricLevel.Warn => Palette.Warn, _ => Palette.Text };
                var change = t.Previous is { } p
                    ? $"<span style=\"color:{TrendColor(p.Trend)};\">{MetricMath.Arrow(p.Delta)} {Esc(MetricMath.FormatDelta(t.Metric.Format, p.Delta))}</span>" +
                      $" <span style=\"color:{Palette.Muted};\">vs {Esc(MetricMath.ReferenceLabel(p.Date, day.Date, day.Days))}</span>"
                    : $"<span style=\"color:{Palette.Muted};\">{(t.Value is null ? "no value" : "no earlier value")}</span>";
                sb.Append(Invariant, $"""
                    <tr>
                      <td style="padding:5px 0;font-size:14px;color:{Palette.Text};">{Esc(t.Metric.Name)}</td>
                      <td align="right" style="padding:5px 10px 5px 6px;font-size:16px;font-weight:700;color:{valueColor};white-space:nowrap;">{Esc(t.Display)}{LevelWords(t.Level)}</td>
                      <td style="padding:5px 6px;font-size:13px;white-space:nowrap;">{change}</td>
                      <td align="right" style="padding:5px 0;font-family:Consolas,'Courier New',monospace;font-size:13px;color:{Palette.Accent};white-space:nowrap;">{Esc(MetricMath.SparkText(t.Spark))}</td>
                    </tr>
                    """);
            }
        }
        sb.Append("</table></td></tr>");
        return sb.ToString();
    }

    // A value past a threshold says so in words too, not by its colour alone (a plain-text or colour-blind reader).
    private static string LevelWords(MetricLevel level) => level switch
    {
        MetricLevel.Critical => $" <span style=\"font-size:12px;font-weight:600;color:{Palette.Threat};\">&#9888; critical</span>",
        MetricLevel.Warn => $" <span style=\"font-size:12px;font-weight:600;color:{Palette.Warn};\">&#9888; warning</span>",
        _ => "",
    };

    private static string OpenActionsBlock(Actions.OpenActionsReview open, string? appUrl)
    {
        var baseUrl = appUrl?.TrimEnd('/');
        var sb = new StringBuilder();
        sb.Append(Invariant, $"<tr><td style=\"padding:16px 24px 6px;\"><div style=\"font-size:12px;font-weight:700;letter-spacing:.06em;text-transform:uppercase;color:{Palette.Muted};\">Still open from earlier briefs</div>");
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\">");
        foreach (var o in open.Rows)
        {
            var a = o.Row;
            var label = $"A-{a.Number}";
            var number = baseUrl is null ? Esc(label) : $"<a style=\"color:{Palette.Link};\" href=\"{Esc($"{baseUrl}/actions/{a.Number}")}\">{Esc(label)}</a>";
            var holder = o.NoOneToOwn ? "no one: the team has no members" : a.AssigneeName ?? "unassigned";
            var escalated = a.Escalated ? $" <strong style=\"color:{Palette.Threat};\">Escalated</strong>" : "";
            sb.Append(Invariant, $"""
                <tr>
                  <td style="padding:5px 6px 5px 0;font-size:12px;font-weight:700;white-space:nowrap;color:{(a.Priority <= Priority.P2 ? Palette.Threat : Palette.Muted)};">{a.Priority}</td>
                  <td style="padding:5px 0;font-size:14px;color:{Palette.Text};">{number} {Esc(a.Title)}{escalated} <span style="color:{Palette.Muted};">&middot; {Esc(a.TeamName)} &middot; {Esc(holder)}</span></td>
                  <td align="right" style="padding:5px 0 5px 8px;font-size:13px;font-weight:600;white-space:nowrap;color:{Palette.Threat};">{Esc(o.Late)}</td>
                </tr>
                """);
        }
        if (open.Overdue > open.Rows.Count)
            sb.Append(Invariant, $"<tr><td colspan=\"3\" style=\"padding:4px 0;font-size:13px;color:{Palette.Muted};\">{open.Overdue} overdue in all; the rest are under Actions &gt; Overdue.</td></tr>");
        sb.Append("</table></td></tr>");
        return sb.ToString();
    }

    private static string TrendColor(MetricTrend trend) => trend switch
    {
        MetricTrend.Better => Palette.Ok,
        MetricTrend.Worse => Palette.Threat,
        _ => Palette.Muted,
    };

    private string ArticleBlock(EditionArticle ea, int number, BriefRenderContext ctx, IReadOnlyDictionary<Guid, RenderImage> imageFor)
    {
        var a = ea.Article;
        var sb = new StringBuilder();
        var threat = a.Classification == Classification.Threat;
        var badge = threat
            ? $"<span style=\"background:{Palette.ThreatSoft};color:{Palette.Threat};font-size:12px;font-weight:700;padding:2px 8px;\">Threat{(a.Priority is { } p ? " " + p : "")}</span>"
            : $"<span style=\"background:{Palette.InfoSoft};color:{Palette.Info};font-size:12px;font-weight:700;padding:2px 8px;\">Informational</span>";
        var source = string.IsNullOrWhiteSpace(a.SourceName) ? "" : $" &nbsp;<span style=\"font-size:12px;color:{Palette.Muted};\">{Esc(a.SourceName)}</span>";
        sb.Append(Invariant, $"""
            <tr><td style="padding:18px 24px;border-top:1px solid {Palette.Border};">
              <div>{badge} &nbsp;{TlpLabel(a.Tlp, 11)}{source}</div>
              <h2 style="margin:8px 0 6px;font-size:19px;line-height:1.3;font-weight:700;color:{Palette.Text};">{number}. {Esc(a.Title)}</h2>
              <p style="margin:0 0 10px;font-size:15px;line-height:1.55;font-weight:600;color:{Palette.Text};">{Esc(a.Bluf)}</p>
            """);
        if (ea.Bullets.Count > 0)
        {
            sb.Append(Invariant, $"<ul style=\"margin:0 0 10px;padding-left:22px;\">");
            foreach (var b in ea.Bullets)
            {
                var mark = a.Effective(b.Tlp) > a.Tlp ? " " + TlpLabel(a.Effective(b.Tlp), 10) : "";
                sb.Append(Invariant, $"<li style=\"margin:0 0 5px;font-size:15px;line-height:1.5;color:{Palette.Text};\">{InlineStyles(markdown.ToInlineHtml(b.Text, ctx.Links))}{mark}</li>");
            }
            sb.Append("</ul>");
        }
        if (ea.ShowWhy) Section("Why it matters to us", a.WhyItMatters, RaisedMark(a, a.WhyItMattersTlp));
        Section("Recommended actions", a.RecommendedActions);
        foreach (var s in ea.Screenshots)
        {
            if (!imageFor.TryGetValue(s.Id, out var img)) continue;
            var (w, h) = img.Size is { } z && z.Width > 0 ? Fit(z.Width, z.Height) : (ImageMaxWidth, 0);
            var height = h > 0 ? $" height=\"{h}\"" : "";
            sb.Append(Invariant, $"""
                <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:6px 0 12px;"><tr><td>
                  <img src="{Esc(ctx.ImageSrc(img))}" width="{w}"{height} alt="{Esc(s.Caption ?? "Screenshot")}" style="display:block;border:1px solid {Palette.Border};width:{w}px;max-width:100%;height:auto;">
                </td></tr>
                """);
            if (!string.IsNullOrWhiteSpace(s.Caption))
                sb.Append(Invariant, $"<tr><td style=\"padding-top:4px;font-size:13px;color:{Palette.Muted};\">{Esc(s.Caption)}</td></tr>");
            sb.Append("</table>");
        }
        var techniques = Techniques(a);
        if (techniques.Count > 0)
            sb.Append(Invariant, $"<p style=\"margin:0 0 6px;font-size:13px;line-height:1.5;color:{Palette.Muted};\">ATT&amp;CK: {Esc(string.Join(" · ", techniques))}</p>");
        if (a.Cves.Count > 0)
            sb.Append(Invariant, $"<p style=\"margin:0 0 6px;font-size:13px;color:{Palette.Muted};\">CVEs: <span style=\"font-family:Consolas,'Courier New',monospace;\">{Esc(string.Join(", ", a.Cves.Select(c => c.CveId).Order()))}</span></p>");
        if (ea.Indicators.Count > 0)
        {
            sb.Append(Invariant, $"<div style=\"margin:10px 0 4px;font-size:13px;font-weight:700;color:{Palette.Text};\">Indicators ({ea.Indicators.Count})</div>");
            sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:0 0 6px;\">");
            foreach (var i in ea.Indicators.Take(MaxIndicatorsShown))
                sb.Append(Invariant, $"<tr><td valign=\"top\" style=\"padding:2px 12px 2px 0;font-size:13px;color:{Palette.Muted};white-space:nowrap;\">{Esc(IndicatorLabels.For(i.Type))}</td>" +
                          $"<td style=\"padding:2px 0;font-family:Consolas,'Courier New',monospace;font-size:13px;color:{Palette.Text};word-break:break-all;\">{Esc(IndicatorLabels.Shown(i.Type, i.Value))}</td></tr>");
            sb.Append("</table>");
            if (ea.Indicators.Count > MaxIndicatorsShown)
                sb.Append(Invariant, $"<p style=\"margin:0 0 6px;font-size:13px;color:{Palette.Muted};\">{MoreIndicators(ea.Indicators.Count)}</p>");
        }
        if (ea.Actions.Count > 0)
        {
            sb.Append(Invariant, $"<div style=\"margin:10px 0 4px;font-size:13px;font-weight:700;color:{Palette.Text};\">Follow-up actions ({ea.Actions.Count})</div>");
            sb.Append("<ul style=\"margin:0 0 8px;padding-left:22px;\">");
            foreach (var x in ea.Actions)
                sb.Append(Invariant, $"<li style=\"margin:0 0 3px;font-size:14px;line-height:1.45;color:{Palette.Text};\"><strong>A-{x.Number}</strong> {Esc(x.Title)} " +
                          $"<span style=\"color:{Palette.Muted};\">{Esc(ActionMeta(x))}</span></li>");
            sb.Append("</ul>");
        }
        if (!string.IsNullOrWhiteSpace(a.SourceUrl))
            sb.Append(Invariant, $"<p style=\"margin:0 0 6px;font-size:13px;\"><a style=\"color:{Palette.Link};\" href=\"{Esc(a.SourceUrl)}\">Read the source</a></p>");
        if (ea.WithheldParts > 0)
            sb.Append(Invariant, $"<p style=\"margin:6px 0 0;font-size:13px;font-style:italic;color:{Palette.Muted};\">{WithheldParts(ea.WithheldParts)}</p>");
        sb.Append("</td></tr>");
        return sb.ToString();

        void Section(string label, string? md, string mark = "")
        {
            if (string.IsNullOrWhiteSpace(md)) return;
            sb.Append(Invariant, $"<div style=\"margin:10px 0 4px;font-size:13px;font-weight:700;color:{Palette.Text};\">{Esc(label)}{mark}</div>");
            sb.Append(InlineStyles(markdown.ToHtml(md, ctx.Links)));
        }
    }

    // A part's TLP label, when it's raised above its article's.
    private static string RaisedMark(Article a, TlpLevel part) => a.Effective(part) > a.Tlp ? " " + TlpLabel(a.Effective(part), 10) : "";

    private static string MoreIndicators(int total) =>
        $"Showing {MaxIndicatorsShown} of {total}. Export the brief's indicators from TIDE for the full list.";

    /// <summary>"Cyber Defense · due Oct 28 · Open": an action's team, due date and status.</summary>
    public static string ActionMeta(EditionAction x) =>
        $"{x.TeamName}{(x.DueDate is { } d ? " · due " + d.ToString("MMM d", Invariant) : "")} · {ActionService.StatusName(x.Status)}";

    private static string WithheldParts(int n) => $"{n} restricted {(n == 1 ? "part was" : "parts were")} left out of this edition.";

    private static (int Width, int Height) Fit(int w, int h) =>
        w <= ImageMaxWidth ? (w, h) : (ImageMaxWidth, (int)Math.Round((double)h * ImageMaxWidth / w));

    private static List<string> Techniques(Article a) => a.Techniques
        .Select(t => AttackCatalog.Find(t.TechniqueId)).OfType<AttackTechnique>().OrderBy(t => t.Id)
        .Select(t => $"{t.Id} {t.Name}").ToList();

    // --- semantic (SharePoint and the archive) -------------------------------------------------------------------

    /// <summary>
    /// The edition as plain semantic HTML: headings, paragraphs, lists and figures, no styles or tables, so it pastes
    /// cleanly into a SharePoint page. <paramref name="figureName"/>, when given, names each image's file in the zip so
    /// the person pasting knows which to insert where.
    /// </summary>
    public string Semantic(BriefEdition edition, BriefRenderContext ctx, IReadOnlyList<RenderImage>? images = null,
        Func<RenderImage, string>? figureName = null)
    {
        images ??= ImagesOf(edition, ctx.Sizes);
        var imageFor = images.ToDictionary(i => i.Shot.Id);
        var brief = edition.Brief;
        var sb = new StringBuilder();
        sb.Append(Invariant, $"<h1>{Esc(ctx.TeamName)} daily brief: {Esc(DayText(brief.BriefDate))}</h1>\n");
        sb.Append(Invariant, $"<p><strong>{Esc(Tlp.Label(edition.Marking))}</strong> {Esc(Tlp.Meaning(edition.Marking))} Presented by {Esc(brief.AnalystName)}.</p>\n");
        // An h3, not an h2: an h2 starts an article's section when a correction reads the frozen copy back (SentSections).
        if (edition.TeamNote is { } note) sb.Append(Invariant, $"<h3>{TeamNoteHeading}</h3>\n").Append(Plain(markdown.ToHtml(note, ctx.Links)));
        if (edition.ShowIntro) sb.Append(Plain(markdown.ToHtml(brief.Intro, ctx.Links)));
        if (edition.Articles.Count == 0) sb.Append("<p>Nothing in this edition today.</p>\n");
        for (var i = 0; i < edition.Articles.Count; i++)
        {
            var ea = edition.Articles[i];
            var a = ea.Article;
            var kind = a.Classification == Classification.Threat ? "Threat" + (a.Priority is { } p ? " " + p : "") : "Informational";
            // Each heading names its article, so a correction finds the article's section in the frozen copy (ADR 0015).
            sb.Append(Invariant, $"<h2 {SentSections.ArticleAttribute}=\"{a.Id}\">{i + 1}. {Esc(a.Title)}</h2>\n");
            sb.Append(Invariant, $"<p><em>{Esc(kind)} &middot; {Esc(Tlp.Label(a.Tlp))}{(string.IsNullOrWhiteSpace(a.SourceName) ? "" : " &middot; " + Esc(a.SourceName))}</em></p>\n");
            sb.Append(Invariant, $"<p><strong>Bottom line:</strong> {Esc(a.Bluf)}</p>\n");
            if (ea.Bullets.Count > 0)
            {
                sb.Append("<ul>\n");
                foreach (var b in ea.Bullets)
                {
                    var mark = a.Effective(b.Tlp) > a.Tlp ? $" <strong>({Esc(Tlp.Label(a.Effective(b.Tlp)))})</strong>" : "";
                    sb.Append(Invariant, $"<li>{Plain(markdown.ToInlineHtml(b.Text, ctx.Links))}{mark}</li>\n");
                }
                sb.Append("</ul>\n");
            }
            if (ea.ShowWhy)
            {
                var mark = a.Effective(a.WhyItMattersTlp) > a.Tlp ? $" ({Esc(Tlp.Label(a.Effective(a.WhyItMattersTlp)))})" : "";
                sb.Append(Invariant, $"<h3>Why it matters to us{mark}</h3>\n").Append(Plain(markdown.ToHtml(a.WhyItMatters, ctx.Links)));
            }
            if (!string.IsNullOrWhiteSpace(a.RecommendedActions)) sb.Append("<h3>Recommended actions</h3>\n").Append(Plain(markdown.ToHtml(a.RecommendedActions, ctx.Links)));
            foreach (var s in ea.Screenshots)
            {
                if (!imageFor.TryGetValue(s.Id, out var img)) continue;
                var name = figureName is null ? "" : $" ({Esc(figureName(img))})";
                var caption = string.IsNullOrWhiteSpace(s.Caption) ? "" : ": " + Esc(s.Caption);
                sb.Append(Invariant, $"<figure><img src=\"{Esc(ctx.ImageSrc(img))}\" alt=\"{Esc(s.Caption ?? "Screenshot")}\"><figcaption>Figure {img.Number}{name}{caption}</figcaption></figure>\n");
            }
            var techniques = Techniques(a);
            if (techniques.Count > 0) sb.Append(Invariant, $"<p>ATT&amp;CK: {Esc(string.Join(" · ", techniques))}</p>\n");
            if (a.Cves.Count > 0) sb.Append(Invariant, $"<p>CVEs: {Esc(string.Join(", ", a.Cves.Select(c => c.CveId).Order()))}</p>\n");
            if (ea.Indicators.Count > 0)
            {
                sb.Append(Invariant, $"<h3>Indicators ({ea.Indicators.Count})</h3>\n<ul>\n");
                foreach (var ind in ea.Indicators.Take(MaxIndicatorsShown))
                    sb.Append(Invariant, $"<li>{Esc(IndicatorLabels.For(ind.Type))}: <code>{Esc(IndicatorLabels.Shown(ind.Type, ind.Value))}</code></li>\n");
                sb.Append("</ul>\n");
                if (ea.Indicators.Count > MaxIndicatorsShown) sb.Append(Invariant, $"<p>{MoreIndicators(ea.Indicators.Count)}</p>\n");
            }
            if (ea.Actions.Count > 0)
            {
                sb.Append(Invariant, $"<h3>Follow-up actions ({ea.Actions.Count})</h3>\n<ul>\n");
                foreach (var x in ea.Actions) sb.Append(Invariant, $"<li><strong>A-{x.Number}</strong> {Esc(x.Title)} ({Esc(ActionMeta(x))})</li>\n");
                sb.Append("</ul>\n");
            }
            if (!string.IsNullOrWhiteSpace(a.SourceUrl)) sb.Append(Invariant, $"<p><a href=\"{Esc(a.SourceUrl)}\">Read the source</a></p>\n");
            if (ea.WithheldParts > 0) sb.Append(Invariant, $"<p><em>{WithheldParts(ea.WithheldParts)}</em></p>\n");
        }
        if (edition.WithheldArticles > 0) sb.Append(Invariant, $"<p><em>{Withheld(edition.WithheldArticles)}</em></p>\n");
        sb.Append(Invariant, $"<p><strong>{Esc(Tlp.Label(edition.Marking))}</strong></p>\n");
        return sb.ToString();
    }

    // Rendered Markdown without the app's link attributes and icon markup, for a paste target.
    private static string Plain(string html) => html
        .Replace(" target=\"_blank\" rel=\"noopener noreferrer\"", "")
        .Replace("<span class=\"tide-image-ref\" title=\"Images aren't shown inline. Add it as a screenshot.\">", "<span>")
        .Replace("<span class=\"bi bi-image\" aria-hidden=\"true\"></span> ", "[image] ");
}
