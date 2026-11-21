using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tide.Application.Briefs;
using Tide.Application.Content;
using Tide.Application.Org;
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

/// <summary>What a piece of a template between double braces is.</summary>
public enum OutreachTokenKind
{
    /// <summary>A placeholder: <c>{{name}}</c>, <c>{{name:modifier}}</c>, <c>{{name?}}</c> or <c>{{name|text}}</c>.</summary>
    Placeholder,

    /// <summary>A section's start: <c>{{#name}}</c>.</summary>
    Open,

    /// <summary>A section's other branch: <c>{{else}}</c>.</summary>
    Else,

    /// <summary>A section's end: <c>{{/name}}</c>.</summary>
    Close,
}

/// <summary>
/// A piece of an outreach template between double braces, as <see cref="OutreachFill.Tokens"/> reads it: the one reading
/// the check on save, the fill and the Library rail's "Fills in" share.
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="Written">As the template writes it.</param>
/// <param name="Index">Where it starts in the text.</param>
/// <param name="Name">The placeholder it's about, lower case ("article.cves"); <c>else</c> for <see cref="OutreachTokenKind.Else"/>.</param>
/// <param name="Modifier">What follows a colon ("domain" in <c>{{indicators:domain}}</c>), or null.</param>
/// <param name="Optional">Whether it ends in <c>?</c>: dropped when there's nothing to fill.</param>
/// <param name="Fallback">The text after a <c>|</c>, as written (not trimmed), or null when there's no <c>|</c>.</param>
public sealed record OutreachToken(OutreachTokenKind Kind, string Written, int Index, string Name, string? Modifier, bool Optional, string? Fallback)
{
    /// <summary>Where it ends in the text.</summary>
    public int End => Index + Written.Length;
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

    /// <summary>What TIDE knows of the article's CVEs, by id; read only for <see cref="OutreachFill.CvesDetailedName"/>.</summary>
    public IReadOnlyDictionary<string, CveRecord> Cves { get; init; } = new Dictionary<string, CveRecord>();

    /// <summary>The addresses of the other reports added to the article (<see cref="ArticleLink"/>), oldest first, which
    /// <c>{{article.other-links}}</c> lists. They carry the article's marking.</summary>
    public IReadOnlyList<string> ArticleLinks { get; init; } = [];

    public IReadOnlyList<ArticleIndicator> Indicators { get; init; } = [];

    /// <summary>The highest marking among <see cref="Indicators"/>: the article's for its own, AMBER for the action's.</summary>
    public TlpLevel IndicatorsTlp { get; init; } = TlpLevel.Clear;

    /// <summary>Whether the ceiling held back indicators (the article's, or the action's own).</summary>
    public bool IndicatorsHeldBack { get; init; }

    /// <summary>The product, with its <see cref="StackProduct.Team"/> loaded: <c>{{product.team}}</c> is its name.</summary>
    public StackProduct? Product { get; init; }

    public int ActionNumber { get; init; }

    /// <summary>The action's address in TIDE, or null when the server's address (<c>App:BaseUrl</c>) isn't set.</summary>
    public string? ActionLink { get; init; }
    public DateOnly? Due { get; init; }
    public string SenderName { get; init; } = "";
    public string? SenderTeam { get; init; }
    public DateOnly Today { get; init; }

    /// <summary>The organization's working days, which <c>{{today:+2}}</c> and <c>{{today:-2}}</c> count in (weekends and
    /// holidays skipped).</summary>
    public WorkingDays Days { get; init; } = WorkingDays.Weekdays;

    /// <summary>What the ceiling held back, a sentence each, with the placeholders it concerns.</summary>
    public IReadOnlyList<OutreachLeftOut> LeftOut { get; init; } = [];
}

/// <summary>Something the ceiling held back, in a sentence, and the placeholders that would have carried it.</summary>
public sealed record OutreachLeftOut(string Text, IReadOnlyList<string> Placeholders);

/// <summary>
/// What a template's ceiling lets through of an action (<see cref="OutreachFill.Cut"/>): its article as cut (null when
/// left out whole), whether the action's own indicators are within it, what was held back, the marking of the indicators
/// let through, and whether the ceiling held any back.
/// </summary>
public sealed record OutreachCut(EditionArticle? Article, bool OwnIndicators, IReadOnlyList<OutreachLeftOut> LeftOut, TlpLevel IndicatorsTlp,
    bool IndicatorsHeldBack);

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

    /// <summary>The placeholder for the article's CVE numbers, one a line.</summary>
    public const string CvesName = "article.cves";

    /// <summary>The placeholder for what TIDE knows of each of the article's CVEs, a paragraph a CVE.</summary>
    public const string CvesDetailedName = "article.cves-detailed";

    /// <summary>The placeholder for the other reports added to the article.</summary>
    public const string OtherLinksName = "article.other-links";

    /// <summary>A CVE's page on the NVD, as the app links it.</summary>
    public static string CveLink(string cveId) => "https://nvd.nist.gov/vuln/detail/" + cveId;

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
        new(OtherLinksName, ArticleGroup, "The other reports added to it, one a line."),
        new(CvesName, ArticleGroup, "Its CVE numbers, one a line."),
        new("article.cve-links", ArticleGroup, "A link to each of its CVEs on the NVD, one a line."),
        new(CvesDetailedName, ArticleGroup, "Each of its CVEs in a short paragraph: CVSS, KEV and EPSS, the vendor and product, the NVD's description and a link, as far as TIDE knows them."),
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
        new(TodayName + ":+2", ActionGroup, $"A date that many business days from today (up to {MaxDaysAhead}), skipping weekends and holidays, as in \"action required by {{{{today:+2}}}}\"."),
        new(TodayName + ":-2", ActionGroup, $"A date that many business days before today (up to {MaxDaysAhead}), as in \"since {{{{today:-2}}}}\"."),
        new(TlpName, ActionGroup, "The email's TLP, as TLP:GREEN. Without it, the body starts with a TLP line."),
    ];

    // The names a template may use; {{indicators:kind}} is checked by kind.
    private static readonly HashSet<string> Names = All.Select(p => p.Name).Where(n => !n.Contains(':')).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Every piece between double braces a template may use: {{else}}; or a name with a section's # or / before it, a
    // modifier after a colon, then a closing ? that makes it optional ("{{article.other-links?}}") or | and the text
    // written when it's empty ("{{product.contact|there}}"). The check on save says which mixes a template can't use.
    // Spaces are read one way only (after the # or / only when there is one, and the text after | given nothing back), so
    // a long run of them can't hold the server up.
    [GeneratedRegex(@"\{\{\s*(?:(?<else>else)|(?:(?<sign>[#/])\s*)?(?<name>[A-Za-z][A-Za-z0-9.\-]*)(?::(?<mod>[A-Za-z0-9+\-]*))?(?<opt>\?)?(?:\|(?<alt>(?>[^{}\r\n]*)))?)\s*\}\}",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TokenPattern();

    /// <summary>The pieces of <paramref name="text"/> between double braces that a template may use, in order.</summary>
    public static IReadOnlyList<OutreachToken> Tokens(string? text) => [.. TokenPattern().Matches(text ?? "").Select(TokenOf)];

    private static OutreachToken TokenOf(Match m)
    {
        if (m.Groups["else"].Success) return new(OutreachTokenKind.Else, m.Value, m.Index, ElseName, null, false, null);
        var kind = m.Groups["sign"].Value switch { "#" => OutreachTokenKind.Open, "/" => OutreachTokenKind.Close, _ => OutreachTokenKind.Placeholder };
        return new(kind, m.Value, m.Index, m.Groups["name"].Value.ToLowerInvariant(), m.Groups["mod"].Success ? m.Groups["mod"].Value : null,
            m.Groups["opt"].Success, m.Groups["alt"].Success ? m.Groups["alt"].Value : null);
    }

    /// <summary>The word that starts a section's other branch: <c>{{else}}</c>.</summary>
    public const string ElseName = "else";

    // A section tag as written plainly ("{{#article.cves}}"), for what's said about it.
    private static string Plain(OutreachToken t) => t.Kind == OutreachTokenKind.Else ? "{{" + ElseName + "}}"
        : "{{" + (t.Kind == OutreachTokenKind.Open ? "#" : "/") + t.Name + (t.Modifier is null ? "" : ":" + t.Modifier) + "}}";

    // Whether a section's close is about the placeholder its open is.
    private static bool Closes(OutreachToken close, OutreachToken open) =>
        close.Name == open.Name && string.Equals(close.Modifier ?? "", open.Modifier ?? "", StringComparison.OrdinalIgnoreCase);

    // The section tags among the tokens, in order.
    private static List<OutreachToken> Tags(IEnumerable<OutreachToken> tokens) => [.. tokens.Where(t => t.Kind != OutreachTokenKind.Placeholder)];

    // Refuses sections that don't pair up, or are about a placeholder TIDE doesn't fill.
    private static void CheckSections(List<OutreachToken> tags)
    {
        OutreachToken? open = null;
        var sawElse = false;
        foreach (var t in tags)
        {
            if (t.Optional || t.Fallback is not null)
                throw new ArgumentException($"{t.Written} is a section's tag: it takes no ? or |. Write it as {Plain(t)}.");
            switch (t.Kind)
            {
                case OutreachTokenKind.Open:
                    if (open is not null)
                        throw new ArgumentException($"{Plain(t)} is inside {Plain(open)}: close it with {{{{/{open.Name}}}}} first. A section can't hold another.");
                    if (string.Equals(t.Name, TlpName, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException($"{Plain(t)} can't start a section: the email always carries its TLP.");
                    CheckPlaceholder(t.Name, t.Modifier);
                    (open, sawElse) = (t, false);
                    break;
                case OutreachTokenKind.Else:
                    if (open is null) throw new ArgumentException($"{{{{{ElseName}}}}} only goes inside a section, between {{{{#name}}}} and {{{{/name}}}}.");
                    if (sawElse) throw new ArgumentException($"{Plain(open)} has two {{{{{ElseName}}}}}s: a section takes one at most.");
                    sawElse = true;
                    break;
                default:
                    if (open is null) throw new ArgumentException($"{Plain(t)} closes a section that isn't open: start it with {{{{#{t.Name}}}}}.");
                    if (!Closes(t, open)) throw new ArgumentException($"{Plain(t)} doesn't close {Plain(open)}: write {{{{/{open.Name}{(open.Modifier is null ? "" : ":" + open.Modifier)}}}}}.");
                    open = null;
                    break;
            }
        }
        if (open is not null) throw new ArgumentException($"{Plain(open)} is never closed: end the section with {{{{/{open.Name}}}}}.");
    }

    /// <summary>The placeholder for today's date; with <c>:+N</c> a date N business days on, with <c>:-N</c> one back.</summary>
    public const string TodayName = "today";

    /// <summary>The most business days <c>{{today:+N}}</c> looks ahead, or <c>{{today:-N}}</c> back (about three months).</summary>
    public const int MaxDaysAhead = 60;

    [GeneratedRegex(@"^([+-])(\d{1,3})$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DaysAhead();

    // The business days in "+2" (or -2 for "-2"), or null when it isn't a number of days TIDE counts.
    private static int? DaysAheadOf(string modifier) =>
        DaysAhead().Match(modifier) is { Success: true } m && int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) is var n and >= 1 and <= MaxDaysAhead
            ? m.Groups[1].Value == "-" ? -n : n : null;

    // Today moved by that many business days, forwards or back.
    private static DateOnly Moved(DateOnly today, int days, WorkingDays w)
    {
        var d = today;
        for (var i = 0; i < Math.Abs(days); i++) d = days > 0 ? w.Next(d) : w.Previous(d);
        return d;
    }

    [GeneratedRegex(@"\[\[[a-z0-9.:\-]+: (?:nothing to fill|above TLP:[A-Z+]+, left out)\]\]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MarkerPattern();

    /// <summary>The placeholder for one kind of indicator ("{{indicators:sha256}}").</summary>
    public static string IndicatorsOf(HuntValueKind kind) => "{{" + IndicatorsNameOf(kind) + "}}";

    // Its name between the braces ("indicators:sha256").
    private static string IndicatorsNameOf(HuntValueKind kind) => IndicatorsName + ":" + kind.ToString().ToLowerInvariant();

    /// <summary>
    /// Refuses <paramref name="text"/> (a subject or body) when it names a placeholder TIDE doesn't fill, or a kind of
    /// indicator it doesn't know, saying which and what it can use, so a typo is caught when the template is saved
    /// rather than when an email is written.
    /// </summary>
    public static void Check(string text)
    {
        text ??= "";
        var tokens = Tokens(text);
        CheckSections(Tags(tokens));
        foreach (var t in tokens.Where(t => t.Kind == OutreachTokenKind.Placeholder))
        {
            var fallback = t.Fallback is not null;
            if (t.Name == ElseName)
                throw new ArgumentException($"{t.Written} isn't written as a section's {{{{{ElseName}}}}}: it takes nothing else between the braces.");
            CheckPlaceholder(t.Name, t.Modifier);
            if ((t.Optional || fallback) && t.Name == TlpName)
                throw new ArgumentException($"{t.Written} can't be optional: the email always carries its TLP. Write it as {{{{{TlpName}}}}}.");
            if (t.Optional && fallback)
                throw new ArgumentException($"{t.Written} is both optional and has text for when it's empty: end it with ? to drop it, or with |text to write that instead.");
            if (fallback && string.IsNullOrWhiteSpace(t.Fallback))
                throw new ArgumentException($"{t.Written} has nothing after the |: write the text to use when it's empty, or end it with ? to drop it.");
        }
        // Braces round a name that doesn't parse ("{{today: +2}}", "{{article_title}}") would go out in every email as
        // typed: Fill leaves them, and they aren't a marker that holds the send back.
        var rest = TokenPattern().Replace(text, "\n");
        if (LooksLikePlaceholder().Match(rest) is { Success: true } typo)
            throw new ArgumentException($"{typo.Value} isn't written as a placeholder TIDE fills: no spaces round the colon or before a closing ?, and a name of letters, dots and hyphens, as in {{{{article.title}}}}, {{{{{TodayName}:+2}}}} or {{{{article.other-links?}}}}.");
    }

    // Refuses a name TIDE doesn't fill, or a modifier it doesn't take, saying what it can use.
    private static void CheckPlaceholder(string name, string? modifier)
    {
        if (!Names.Contains(name))
            throw new ArgumentException($"{{{{{name}}}}} isn't a placeholder TIDE fills. Use one of: {string.Join(", ", All.Where(p => !p.Name.Contains(':')).Select(p => p.Written))}.");
        if (modifier is null) return;
        if (string.Equals(name, TodayName, StringComparison.OrdinalIgnoreCase))
        {
            if (DaysAheadOf(modifier) is null)
                throw new ArgumentException($"{{{{{TodayName}:{modifier}}}}} isn't a number of business days. Write it as {{{{{TodayName}:+2}}}} or {{{{{TodayName}:-2}}}}, up to {MaxDaysAhead}.");
            return;
        }
        if (!string.Equals(name, IndicatorsName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{{{{{name}}}}} takes nothing after a colon: only {{{{{IndicatorsName}:…}}}} and {{{{{TodayName}:+2}}}} do.");
        if (HuntQueryBuilder.KindNamed(modifier) is null)
            throw new ArgumentException($"{{{{{IndicatorsName}:{modifier}}}}} isn't a kind of indicator TIDE knows. Use {{{{{IndicatorsName}}}}}, or one of: {string.Join(", ", Enum.GetValues<HuntValueKind>().Select(IndicatorsOf))}.");
    }

    // Double braces round something with a letter in it, on one line: what a placeholder with a typo looks like.
    [GeneratedRegex(@"\{\{[^{}\r\n]*[A-Za-z][^{}\r\n]*\}\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LooksLikePlaceholder();

    /// <summary>What's left in the text for a placeholder with nothing to fill, or whose part the ceiling held back.</summary>
    public static string Marker(string placeholder, TlpLevel? above = null) =>
        above is { } ceiling ? $"[[{placeholder}: above {Tlp.Label(ceiling)}, left out]]" : $"[[{placeholder}: nothing to fill]]";

    /// <summary>Whether <paramref name="text"/> still holds a marker (<see cref="Marker"/>) the analyst hasn't dealt with.</summary>
    public static bool HasMarker(string? text) => MarkerPattern().IsMatch(text ?? "");

    /// <summary>
    /// <paramref name="article"/> (its bullets, indicators and CVEs loaded) as the ceiling lets it through, by the team
    /// edition's cut (<see cref="BriefEditions.BuildArticle"/>, which carries indicators), and whether the action's own
    /// <paramref name="ownIndicators"/> (unmarked, so <see cref="UnmarkedTlp"/>) are within it, with a sentence for each
    /// thing held back, the marking of the indicators let through, and whether any were held back.
    /// </summary>
    public static OutreachCut Cut(Article? article, int ownIndicators, TlpLevel ceiling)
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
        // The article's indicators carry its marking, the action's own count as unmarked.
        var indicatorsTlp = new[] { kept is { Indicators.Count: > 0 } ? kept.Article.Tlp : TlpLevel.Clear, own ? UnmarkedTlp : TlpLevel.Clear }.Max();
        var heldBack = (article is { Indicators.Count: > 0 } && kept is null) || (ownIndicators > 0 && !own);
        return new OutreachCut(kept, own, leftOut, indicatorsTlp, heldBack);
    }

    /// <summary>A subject as it's kept, a template's or an email's: on one line, trimmed. Empty, or longer than
    /// <paramref name="max"/>, is refused in the caller's words.</summary>
    internal static string CleanSubject(string? text, int max, string empty, string tooLong) =>
        Checked((text ?? "").ReplaceLineEndings(" ").Trim(), max, empty, tooLong);

    /// <summary>A body as it's kept, a template's or an email's: \n line endings, trimmed. Empty, or longer than
    /// <paramref name="max"/>, is refused in the caller's words.</summary>
    internal static string CleanBody(string? text, int max, string empty, string tooLong) =>
        Checked((text ?? "").Replace("\r\n", "\n").Trim(), max, empty, tooLong);

    private static string Checked(string clean, int max, string empty, string tooLong) =>
        clean.Length == 0 ? throw new ArgumentException(empty)
        : clean.Length > max ? throw new ArgumentException(tooLong)
        : clean;

    /// <summary>
    /// Fills a template's subject and body from <paramref name="facts"/>. A section (<c>{{#name}}…{{else}}…{{/name}}</c>)
    /// keeps its first branch when its placeholder has a value, else the other (or nothing). Each placeholder becomes its
    /// value; one with nothing to fill, or held back by the ceiling, is dropped when it ends in <c>?</c>, becomes the
    /// text after its <c>|</c> when it has one, and otherwise becomes a marker and is listed. The marking is the highest TLP of
    /// what was filled in (CLEAR when nothing marked was), and the body starts with a TLP line unless it places
    /// <c>{{tlp}}</c> itself.
    /// </summary>
    public static OutreachDraft Fill(string subject, string body, OutreachFacts facts)
    {
        var marking = TlpLevel.Clear;
        var unfilled = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        // A placeholder's label as markers write it, and its value, TLP and whether the ceiling held it back; worked out
        // once, though a section's tag and the placeholder inside it both ask.
        var looked = new Dictionary<(string, string?), (string, string?, TlpLevel, bool)>();
        (string Label, string? Value, TlpLevel Tlp, bool Above) Look(string name, string? modifier)
        {
            used.Add(name);
            var key = (name, modifier?.ToLowerInvariant());
            if (looked.TryGetValue(key, out var seen)) return seen;
            var scope = modifier is not null && name == IndicatorsName ? HuntQueryBuilder.KindNamed(modifier) : null;
            var ahead = modifier is not null && name == TodayName ? DaysAheadOf(modifier) : null;
            var label = scope is { } k ? IndicatorsNameOf(k) : ahead is { } n ? $"{TodayName}:{n:+0;-0}" : name;
            var (value, tlp, above) = ValueOf(name, scope, ahead, facts);
            return looked[key] = (label, value, tlp, above);
        }

        // Each section kept as its first branch when its placeholder has a value, else as what follows {{else}} (or
        // nothing). Choosing says something about the part, so a kept first branch carries its marking. Sections that
        // don't pair up are refused when a template is saved; an older one that slipped through is left as it is.
        string Sections(string text)
        {
            var tags = Tags(Tokens(text));
            if (tags.Count == 0) return text;
            try { CheckSections(tags); }
            catch (ArgumentException) { return text; }
            var sb = new StringBuilder();
            var pos = 0;
            var (inside, has, inElse) = (false, false, false);
            foreach (var t in tags)
            {
                if (!inside || has != inElse) sb.Append(text, pos, t.Index - pos);
                // The tag goes as an empty optional placeholder does: the same pass drops its line when it's alone on it,
                // with a blank line that would then follow another, and closes the gap when it isn't.
                sb.Append(GoneSlot);
                pos = t.End;
                switch (t.Kind)
                {
                    case OutreachTokenKind.Open:
                        var (_, value, tlp, _) = Look(t.Name, t.Modifier);
                        (inside, has, inElse) = (true, !string.IsNullOrWhiteSpace(value), false);
                        if (has && tlp > marking) marking = tlp;
                        break;
                    case OutreachTokenKind.Else: inElse = true; break;
                    default: inside = false; break;
                }
            }
            return sb.Append(text, pos, text.Length - pos).ToString();
        }

        // Section tags still there belong to sections that don't pair up, and are left as they are.
        string Replace(string text) => TokenPattern().Replace(Sections(text ?? ""), m =>
        {
            var t = TokenOf(m);
            if (t.Kind != OutreachTokenKind.Placeholder) return m.Value;
            if (t.Name == TlpName) { used.Add(t.Name); return TlpSlot; }   // filled once the marking is known
            var (label, value, tlp, above) = Look(t.Name, t.Modifier);
            if (string.IsNullOrWhiteSpace(value))
            {
                // Optional: dropped, with its line when nothing else is on it. What the ceiling held back is still said.
                if (t.Optional) return GoneSlot;
                // With text for when it's empty: that text, which carries no marking.
                if (t.Fallback?.Trim() is { Length: > 0 } instead) return instead;
                var written = "{{" + label + "}}";
                if (!unfilled.Contains(written)) unfilled.Add(written);
                return Marker(label, above ? facts.Ceiling : null);
            }
            if (tlp > marking) marking = tlp;
            return value;
        });
        var filledSubject = Gone(Replace(subject).ReplaceLineEndings(" ")).Trim();
        var filledBody = Lines(Replace((body ?? "").Replace("\r\n", "\n")));
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

    // The body's lines with each line only an empty optional placeholder or a section's tags were on dropped, and the
    // gaps in the rest closed. A blank line that would then follow another blank line goes too: "a\n\n{{x?}}\n\nb" reads "a\n\nb", not "a\n\n\nb".
    // Blank lines the template has side by side of its own are kept.
    private static string Lines(string text)
    {
        if (!text.Contains(GoneSlot, StringComparison.Ordinal)) return text;
        var kept = new List<string>();
        // Whether lines were just dropped, and the line before them was blank (or there was none).
        var (dropped, blankBefore) = (false, false);
        foreach (var l in text.Split('\n'))
        {
            var g = Gone(l);
            if (g != l && string.IsNullOrWhiteSpace(g))
            {
                if (!dropped) (dropped, blankBefore) = (true, kept.Count == 0 || string.IsNullOrWhiteSpace(kept[^1]));
                continue;
            }
            var skip = dropped && blankBefore && string.IsNullOrWhiteSpace(l);
            dropped = false;
            if (!skip) kept.Add(g);
        }
        return string.Join("\n", kept);
    }

    // Where an optional placeholder with nothing to fill, or a section's tag, was, until its line is dropped or the gap closed.
    private const string GoneSlot = "\u0000gone\u0000";

    // The text with optional placeholders' gaps closed: "A, {{x?}} and B" reads "A, and B", not "A,  and B".
    private static string Gone(string text) => !text.Contains(GoneSlot, StringComparison.Ordinal) ? text
        : GapPattern().Replace(text, m => m.Value.Contains(' ') && m.Index > 0 && m.Index + m.Length < text.Length ? " " : "");

    [GeneratedRegex("[ \\t]*(?:\u0000gone\u0000[ \\t]*)+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GapPattern();

    // A placeholder's value, the TLP it carries, and whether the ceiling held it back (rather than there being nothing).
    private static (string? Value, TlpLevel Tlp, bool Above) ValueOf(string name, HuntValueKind? scope, int? ahead, OutreachFacts f)
    {
        if (name == IndicatorsName) return Indicators(f, scope);
        if (name.StartsWith("article.", StringComparison.Ordinal)) return ArticlePart(name, f);
        var inv = CultureInfo.InvariantCulture;
        string? value = name switch
        {
            "product.name" => f.Product?.Name,
            "product.versions" => f.Product?.Versions,
            "product.team" => f.Product?.Team?.Name,
            "product.contact" => f.Product?.ContactName,
            "action.number" => $"A-{f.ActionNumber}",
            "action.link" => f.ActionLink,
            "action.due" => f.Due?.ToString("dddd, MMMM d, yyyy", inv),
            "sender.name" => f.SenderName,
            "sender.team" => f.SenderTeam,
            TodayName => (ahead is { } n ? Moved(f.Today, n, f.Days) : f.Today).ToString("dddd, MMMM d, yyyy", inv),
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
            OtherLinksName => (OtherLinks(a, f.ArticleLinks), a.Tlp, false),
            "article.tlp" => (Tlp.Label(a.Tlp), a.Tlp, false),
            CvesName => (CveIds(a) is { Count: > 0 } ids ? string.Join("\n", ids) : null, a.Tlp, false),
            CvesDetailedName => (CvesDetailed(a, f.Cves), a.Tlp, false),
            "article.cve-links" => (CveIds(a) is { Count: > 0 } ids ? string.Join("\n", ids.Select(CveLink)) : null, a.Tlp, false),
            "article.why-it-matters" => cut.ShowWhy
                ? (a.WhyItMatters, a.Effective(a.WhyItMattersTlp), false)
                : (null, TlpLevel.Clear, !string.IsNullOrWhiteSpace(a.WhyItMatters)),
            "article.key-points" => cut.Bullets.Count > 0
                ? (string.Join("\n", cut.Bullets.Select(b => "- " + b.Text.Trim())), cut.Bullets.Max(b => a.Effective(b.Tlp)), false)
                : (null, TlpLevel.Clear, a.Bullets.Count > 0),
            _ => (null, TlpLevel.Clear, false),
        };
    }

    // The other reports' addresses, one a line, each once, leaving out one that is the source's own.
    private static string? OtherLinks(Article a, IReadOnlyList<string> others)
    {
        var source = a.SourceUrl?.Trim();
        var shown = others.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim())
            .Where(u => !string.Equals(u, source, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return shown.Count == 0 ? null : string.Join("\n", shown);
    }

    // The article's CVE ids, in the order every CVE placeholder lists them.
    private static List<string> CveIds(Article a) => [.. a.Cves.Select(c => c.CveId).Order(StringComparer.OrdinalIgnoreCase)];

    // "on CISA's KEV list[ since Nov 1, 2026][, due Nov 22, 2026][, used in ransomware]", for a CVE on it.
    private static string? KevNote(CveRecord? r) => r is not { IsKev: true } ? null
        : "on CISA's KEV list"
          + (r.KevAddedDate is { } added ? " since " + Words.MonthDayYear(added) : "")
          + (r.KevDueDate is { } due ? ", due " + Words.MonthDayYear(due) : "")
          + (r.KevRansomware ? ", used in ransomware" : "");

    // "EPSS 94.1%", when TIDE knows it.
    private static string? EpssNote(CveRecord? r) =>
        r?.EpssScore is { } epss ? $"EPSS {(epss * 100).ToString("0.#", CultureInfo.InvariantCulture)}%" : null;

    // A paragraph a CVE, a blank line between:
    //   CVE-2026-1234: CVSS 9.8 critical; on CISA's KEV list since Nov 1, 2026, due Nov 22, 2026, used in ransomware; EPSS 94.1%
    //   Ivanti Connect Secure
    //   The NVD's description.
    //   https://nvd.nist.gov/vuln/detail/CVE-2026-1234
    // A line TIDE has nothing for is left out; a CVE it knows nothing about is its id and link.
    private static string? CvesDetailed(Article a, IReadOnlyDictionary<string, CveRecord> records)
    {
        var ids = CveIds(a);
        if (ids.Count == 0) return null;
        var paragraphs = new List<string>();
        foreach (var id in ids)
        {
            var lines = new List<string>();
            records.TryGetValue(id, out var r);
            var cvss = r?.CvssScore is { } score
                ? $"CVSS {score.ToString("0.0", CultureInfo.InvariantCulture)}" + (string.IsNullOrWhiteSpace(r.CvssSeverity) ? "" : " " + r.CvssSeverity.Trim().ToLowerInvariant())
                : null;
            var notes = new[] { cvss, KevNote(r), EpssNote(r) }.OfType<string>().ToList();
            lines.Add(id + (notes.Count > 0 ? ": " + string.Join("; ", notes) : ""));
            var made = string.Join(" ", new[] { r?.Vendor, r?.Product }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
            if (made.Length > 0) lines.Add(made);
            if (!string.IsNullOrWhiteSpace(r?.Description)) lines.Add(r.Description.ReplaceLineEndings(" ").Trim());
            lines.Add(CveLink(id));
            paragraphs.Add(string.Join("\n", lines));
        }
        return string.Join("\n\n", paragraphs);
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
