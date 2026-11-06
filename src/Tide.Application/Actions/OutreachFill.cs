using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tide.Application.Briefs;
using Tide.Application.Content;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Domain.Observables;

namespace Tide.Application.Actions;

/// <summary>A placeholder an outreach template may use, as the template page lists it.</summary>
/// <param name="Name">Its name, as written between the braces ("article.title").</param>
/// <param name="Group">What it's filled from, the page's heading over it.</param>
/// <param name="Description">What it becomes.</param>
public sealed record OutreachPlaceholder(string Name, string Group, string Description)
{
    /// <summary>As a template writes it ("{{article.title}}").</summary>
    public string Written => "{{" + Name + "}}";
}

/// <summary>
/// What an outreach email is filled from, already cut to the template's ceiling (<see cref="OutreachFill.Cut"/>):
/// <see cref="Article"/> holds only the parts the ceiling lets through, and <see cref="Indicators"/> only the indicators
/// it lets through, each once.
/// </summary>
public sealed record OutreachFacts
{
    public TlpLevel Ceiling { get; init; } = TlpLevel.Clear;

    /// <summary>Whether the action was raised from an article: with <see cref="Article"/> null, it was left out whole.</summary>
    public bool FromArticle { get; init; }

    public EditionArticle? Article { get; init; }

    /// <summary>The enrichment of the article's CVEs, where TIDE has it, by id.</summary>
    public IReadOnlyDictionary<string, CveRecord> Cves { get; init; } = new Dictionary<string, CveRecord>();

    public IReadOnlyList<ArticleIndicator> Indicators { get; init; } = [];

    /// <summary>The highest marking among <see cref="Indicators"/>: the article's for its own, AMBER for the action's.</summary>
    public TlpLevel IndicatorsTlp { get; init; } = TlpLevel.Clear;

    /// <summary>Whether the ceiling held back indicators (the article's, or the action's own).</summary>
    public bool IndicatorsHeldBack { get; init; }

    public StackProduct? Product { get; init; }
    public string? ProductTeam { get; init; }

    public int ActionNumber { get; init; }

    /// <summary>The action's address in TIDE, or null when the server's address (<c>App:BaseUrl</c>) isn't set.</summary>
    public string? ActionLink { get; init; }
    public DateOnly? Due { get; init; }
    public string SenderName { get; init; } = "";
    public string? SenderTeam { get; init; }
    public DateOnly Today { get; init; }

    /// <summary>What the ceiling held back, a sentence each, with the placeholders it concerns.</summary>
    public IReadOnlyList<OutreachLeftOut> LeftOut { get; init; } = [];
}

/// <summary>Something the ceiling held back, in a sentence, and the placeholders that would have carried it.</summary>
public sealed record OutreachLeftOut(string Text, IReadOnlyList<string> Placeholders);

/// <summary>
/// An outreach email as filled, for the analyst to check and edit before sending it from their own mail. A placeholder
/// with nothing to fill, or whose part was above the ceiling, is left in the text as a marker (<see cref="OutreachFill.Marker"/>)
/// and listed in <see cref="Unfilled"/>, so it can't go unnoticed. <see cref="Marking"/> is the highest TLP of what was
/// filled in, and the body carries a line saying so.
/// </summary>
public sealed record OutreachDraft(string Subject, string Body, TlpLevel Marking, TlpLevel Ceiling, IReadOnlyList<string> LeftOut,
    IReadOnlyList<string> Unfilled);

/// <summary>
/// Outreach templates' placeholders and how they're filled (ADR 0019). <c>{{name}}</c>, as hunt queries write theirs:
/// the article's parts, the action's indicators (all, or one kind: <c>{{indicators:domain}}</c>, by the names the hunt
/// queries' <c>{{values:domain}}</c> use), the Our stack product, and the action and its sender. A name TIDE doesn't
/// know is refused when the template is saved. Filling is cut to the template's TLP ceiling by the editions' rule
/// (<see cref="BriefEditions.BuildArticle"/>: an article above it is left out whole, a part above it alone), and says
/// what it left out.
/// </summary>
public static partial class OutreachFill
{
    public const string ArticleGroup = "The article";
    public const string IndicatorsGroup = "The indicators";
    public const string ProductGroup = "The Our stack product";
    public const string ActionGroup = "The action and you";

    /// <summary>The placeholder for the action's indicators; <c>{{indicators:kind}}</c> takes one kind.</summary>
    public const string IndicatorsName = "indicators";

    /// <summary>The placeholder for the email's TLP marking.</summary>
    public const string TlpName = "tlp";

    /// <summary>
    /// The action's own indicators (added on one with no article) carry no marking of their own, so they count as AMBER,
    /// as the API holds other unmarked internal text (a product's notes) to AMBER.
    /// </summary>
    public const TlpLevel UnmarkedTlp = TlpLevel.Amber;

    public static readonly IReadOnlyList<OutreachPlaceholder> All =
    [
        new("article.title", ArticleGroup, "The article's title."),
        new("article.bottom-line", ArticleGroup, "Its bottom line."),
        new("article.why-it-matters", ArticleGroup, "Why it matters to us, when its own TLP is within the ceiling."),
        new("article.key-points", ArticleGroup, "Its key points, one a line, each within the ceiling."),
        new("article.link", ArticleGroup, "Its source's address."),
        new("article.cves", ArticleGroup, "Its CVEs, one a line, with KEV and EPSS where TIDE knows them."),
        new("article.tlp", ArticleGroup, "The article's TLP, as TLP:GREEN."),
        new(IndicatorsName, IndicatorsGroup, "Every indicator on the article and the action, defanged, one a line."),
        new(IndicatorsName + ":domain", IndicatorsGroup, "One kind only (domain, url, ipv4, sha256…), as the saved hunt queries name them."),
        new("product.name", ProductGroup, "The product the action or its article names."),
        new("product.versions", ProductGroup, "The versions we run."),
        new("product.team", ProductGroup, "The team that owns it."),
        new("product.contact", ProductGroup, "Its vendor or support contact's name."),
        new("action.number", ActionGroup, "The action's number, as A-12."),
        new("action.link", ActionGroup, "Its address in TIDE."),
        new("action.due", ActionGroup, "When it's due."),
        new("sender.name", ActionGroup, "Your name."),
        new("sender.team", ActionGroup, "Your team: the action's, when you're on it."),
        new("today", ActionGroup, "Today's date."),
        new(TlpName, ActionGroup, "The email's TLP, as TLP:GREEN. Without it, the body starts with a TLP line."),
    ];

    // The names a template may use; {{indicators:kind}} is checked by kind.
    private static readonly HashSet<string> Names = All.Select(p => p.Name).Where(n => !n.Contains(':')).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9.\-]*)(?::([A-Za-z0-9]*))?\s*\}\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pattern();

    [GeneratedRegex(@"\[\[[a-z0-9.:\-]+: (?:nothing to fill|above TLP:[A-Z+]+, left out)\]\]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MarkerPattern();

    /// <summary>The placeholder for one kind of indicator ("{{indicators:sha256}}").</summary>
    public static string IndicatorsOf(HuntValueKind kind) => "{{" + IndicatorsName + ":" + kind.ToString().ToLowerInvariant() + "}}";

    /// <summary>
    /// Refuses <paramref name="text"/> (a subject or body) when it names a placeholder TIDE doesn't fill, or a kind of
    /// indicator it doesn't know, saying which and what it can use, so a typo is caught when the template is saved
    /// rather than when an email is written.
    /// </summary>
    public static void Check(string text)
    {
        foreach (Match m in Pattern().Matches(text ?? ""))
        {
            var name = m.Groups[1].Value;
            if (!Names.Contains(name))
                throw new ArgumentException($"{{{{{name}}}}} isn't a placeholder TIDE fills. Use one of: {string.Join(", ", All.Where(p => !p.Name.Contains(':')).Select(p => p.Written))}.");
            if (!m.Groups[2].Success) continue;
            if (!string.Equals(name, IndicatorsName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{{{{{name}}}}} takes no kind: only {{{{{IndicatorsName}:…}}}} does.");
            if (HuntQueryBuilder.KindNamed(m.Groups[2].Value) is null)
                throw new ArgumentException($"{{{{{IndicatorsName}:{m.Groups[2].Value}}}}} isn't a kind of indicator TIDE knows. Use {{{{{IndicatorsName}}}}}, or one of: {string.Join(", ", Enum.GetValues<HuntValueKind>().Select(IndicatorsOf))}.");
        }
    }

    /// <summary>What's left in the text for a placeholder with nothing to fill, or whose part the ceiling held back.</summary>
    public static string Marker(string placeholder, TlpLevel? above = null) =>
        above is { } ceiling ? $"[[{placeholder}: above {Tlp.Label(ceiling)}, left out]]" : $"[[{placeholder}: nothing to fill]]";

    /// <summary>Whether <paramref name="text"/> still holds a marker (<see cref="Marker"/>) the analyst hasn't dealt with.</summary>
    public static bool HasMarker(string? text) => MarkerPattern().IsMatch(text ?? "");

    /// <summary>
    /// <paramref name="article"/> (its bullets, indicators and CVEs loaded) as the ceiling lets it through, by the team
    /// edition's cut (<see cref="BriefEditions.BuildArticle"/>, which carries indicators), and whether the action's own
    /// <paramref name="ownIndicators"/> (unmarked, so <see cref="UnmarkedTlp"/>) are within it, with a sentence for each
    /// thing held back.
    /// </summary>
    public static (EditionArticle? Article, bool OwnIndicators, IReadOnlyList<OutreachLeftOut> LeftOut) Cut(Article? article, int ownIndicators, TlpLevel ceiling)
    {
        var leftOut = new List<OutreachLeftOut>();
        EditionArticle? kept = null;
        if (article is not null)
        {
            kept = BriefEditions.BuildArticle(article, Edition.Team, ceiling);
            if (kept is null)
                leftOut.Add(new($"The article is {Tlp.Label(article.Tlp)}, above this template's {Tlp.Label(ceiling)}: its title, bottom line, link, CVEs and indicators are left out.",
                    [.. All.Select(p => p.Name).Where(n => n.StartsWith("article.", StringComparison.Ordinal)), IndicatorsName]));
            else
            {
                var bullets = article.Bullets.Count - kept.Bullets.Count;
                if (bullets > 0) leftOut.Add(new($"{Words.Plural(bullets, "key point")} above {Tlp.Label(ceiling)} left out.", ["article.key-points"]));
                if (!string.IsNullOrWhiteSpace(article.WhyItMatters) && !kept.ShowWhy)
                    leftOut.Add(new($"Why it matters is {Tlp.Label(article.Effective(article.WhyItMattersTlp))}, above {Tlp.Label(ceiling)}: left out.", ["article.why-it-matters"]));
            }
        }
        var own = ownIndicators > 0 && UnmarkedTlp <= ceiling;
        if (ownIndicators > 0 && !own)
            leftOut.Add(new($"The action's own indicators ({ownIndicators}) carry no marking, so count as {Tlp.Label(UnmarkedTlp)}, above {Tlp.Label(ceiling)}: left out.", [IndicatorsName]));
        return (kept, own, leftOut);
    }

    /// <summary>
    /// Fills a template's subject and body from <paramref name="facts"/>. Each placeholder becomes its value; one with
    /// nothing to fill, or held back by the ceiling, becomes a marker and is listed. The marking is the highest TLP of
    /// what was filled in (CLEAR when nothing marked was), and the body starts with a TLP line unless it places
    /// <c>{{tlp}}</c> itself.
    /// </summary>
    public static OutreachDraft Fill(string subject, string body, OutreachFacts facts)
    {
        var marking = TlpLevel.Clear;
        var unfilled = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        string Replace(string text) => Pattern().Replace(text ?? "", m =>
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            used.Add(name);
            if (name == TlpName) return TlpSlot;   // filled once the marking is known
            var scope = m.Groups[2].Success ? HuntQueryBuilder.KindNamed(m.Groups[2].Value) : null;
            var written = scope is { } k ? IndicatorsOf(k) : "{{" + name + "}}";
            var (value, tlp, above) = ValueOf(name, scope, facts);
            if (string.IsNullOrWhiteSpace(value))
            {
                var label = written[2..^2];
                if (!unfilled.Contains(written)) unfilled.Add(written);
                return Marker(label, above ? facts.Ceiling : null);
            }
            if (tlp > marking) marking = tlp;
            return value;
        });
        var filledSubject = Replace(subject).ReplaceLineEndings(" ").Trim();
        var filledBody = Replace((body ?? "").Replace("\r\n", "\n"));
        var line = $"{Tlp.Label(marking)}: {Tlp.Meaning(marking)}";
        var placed = filledBody.Contains(TlpSlot, StringComparison.Ordinal);
        filledSubject = filledSubject.Replace(TlpSlot, Tlp.Label(marking), StringComparison.Ordinal);
        filledBody = filledBody.Replace(TlpSlot, line, StringComparison.Ordinal);
        if (!placed) filledBody = line + "\n\n" + filledBody.TrimStart('\n');
        // Only what the template would have carried: a key point held back is news only to a template that lists them.
        var leftOut = facts.LeftOut.Where(l => l.Placeholders.Any(used.Contains)).Select(l => l.Text).ToList();
        return new OutreachDraft(filledSubject, filledBody.TrimEnd() + "\n", marking, facts.Ceiling, leftOut, unfilled);
    }

    // Where {{tlp}} was, until the marking is known: a value filled in that happens to say "{{tlp}}" stays as it is.
    private const string TlpSlot = "\u0000tlp\u0000";

    // A placeholder's value, the TLP it carries, and whether the ceiling held it back (rather than there being nothing).
    private static (string? Value, TlpLevel Tlp, bool Above) ValueOf(string name, HuntValueKind? scope, OutreachFacts f)
    {
        if (name == IndicatorsName) return Indicators(f, scope);
        if (name.StartsWith("article.", StringComparison.Ordinal)) return ArticlePart(name, f);
        var inv = CultureInfo.InvariantCulture;
        string? value = name switch
        {
            "product.name" => f.Product?.Name,
            "product.versions" => f.Product?.Versions,
            "product.team" => f.Product is null ? null : f.ProductTeam,
            "product.contact" => f.Product?.ContactName,
            "action.number" => $"A-{f.ActionNumber}",
            "action.link" => f.ActionLink,
            "action.due" => f.Due?.ToString("dddd, MMMM d, yyyy", inv),
            "sender.name" => f.SenderName,
            "sender.team" => f.SenderTeam,
            "today" => f.Today.ToString("dddd, MMMM d, yyyy", inv),
            _ => null,
        };
        // Our stack's and the action's own facts carry no marking: the API gives a product's name, versions and team to
        // any ceiling, and the action's number and dates are TIDE's bookkeeping.
        return (value, TlpLevel.Clear, false);
    }

    private static (string? Value, TlpLevel Tlp, bool Above) ArticlePart(string name, OutreachFacts f)
    {
        if (f.Article is not { } cut) return (null, TlpLevel.Clear, f.FromArticle);
        var a = cut.Article;
        return name switch
        {
            "article.title" => (a.Title, a.Tlp, false),
            "article.bottom-line" => (a.Bluf, a.Tlp, false),
            "article.link" => (a.SourceUrl, a.Tlp, false),
            "article.tlp" => (Tlp.Label(a.Tlp), a.Tlp, false),
            "article.cves" => (Cves(a, f.Cves), a.Tlp, false),
            "article.why-it-matters" => cut.ShowWhy
                ? (a.WhyItMatters, a.Effective(a.WhyItMattersTlp), false)
                : (null, TlpLevel.Clear, !string.IsNullOrWhiteSpace(a.WhyItMatters)),
            "article.key-points" => cut.Bullets.Count > 0
                ? (string.Join("\n", cut.Bullets.Select(b => "- " + b.Text.Trim())), cut.Bullets.Max(b => a.Effective(b.Tlp)), false)
                : (null, TlpLevel.Clear, a.Bullets.Count > 0),
            _ => (null, TlpLevel.Clear, false),
        };
    }

    // One a line: "CVE-2026-1234 (on CISA's KEV list; EPSS 94.1%)", or the id alone when TIDE knows nothing more.
    private static string? Cves(Article a, IReadOnlyDictionary<string, CveRecord> records)
    {
        if (a.Cves.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var id in a.Cves.Select(c => c.CveId).Order(StringComparer.OrdinalIgnoreCase))
        {
            var notes = new List<string>();
            if (records.TryGetValue(id, out var r))
            {
                if (r.IsKev) notes.Add("on CISA's KEV list" + (r.KevRansomware ? ", used in ransomware" : ""));
                if (r.EpssScore is { } epss) notes.Add($"EPSS {(epss * 100).ToString("0.#", CultureInfo.InvariantCulture)}%");
            }
            sb.Append(id).Append(notes.Count > 0 ? $" ({string.Join("; ", notes)})" : "").Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    // Defanged, one a line, in the order a hunt groups them; one kind only when the placeholder names it.
    private static (string? Value, TlpLevel Tlp, bool Above) Indicators(OutreachFacts f, HuntValueKind? scope)
    {
        var shown = f.Indicators.Where(i => scope is not { } k || HuntValues.KindOf(i) == k)
            .OrderBy(HuntValues.KindOf).ThenBy(i => i.Value, StringComparer.OrdinalIgnoreCase)
            .Select(i => IocObservable.Defang(i.Type, i.Value)).ToList();
        if (shown.Count > 0) return (string.Join("\n", shown), f.IndicatorsTlp, false);
        // Nothing to fill: because the ceiling held them back, or because there are none (of that kind).
        return (null, TlpLevel.Clear, f.IndicatorsHeldBack);
    }
}
