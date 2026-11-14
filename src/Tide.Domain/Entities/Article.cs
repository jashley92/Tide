using System.Security.Cryptography;
using System.Text;
using Tide.Domain.Common;
using Tide.Domain.Enums;

namespace Tide.Domain.Entities;

/// <summary>
/// One analyst write-up of a news item or intel report: the bottom line, the bullets, screenshots, and how it's tagged
/// (Threat or Informational, scorecard tags, ATT&amp;CK techniques, indicators, CVEs). Briefs are built from these.
/// <para>
/// TLP is set on the article and can be raised on a bullet or screenshot (a part never counts lower than its article),
/// so one article can show in the public edition with its restricted parts left out.
/// </para>
/// </summary>
public class Article : AuditableEntity
{
    public const int MaxScreenshots = 10;
    public const int MaxBullets = 30;

    public string Title { get; set; } = string.Empty;

    /// <summary>Where it came from, as the analyst names it ("BleepingComputer", "FS-ISAC").</summary>
    public string? SourceName { get; set; }

    /// <summary>The article's address: a citation, kept live (an indicator is never put here).</summary>
    public string? SourceUrl { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }

    public TlpLevel Tlp { get; set; } = TlpLevel.Clear;
    public Classification Classification { get; set; } = Classification.Informational;

    /// <summary>Set for a <see cref="Classification.Threat"/>; null for an informational article.</summary>
    public Priority? Priority { get; set; }

    /// <summary>Bottom line up front: one sentence on what happened and why it matters.</summary>
    public string Bluf { get; set; } = string.Empty;

    /// <summary>"Why it matters to us": the organization's own angle (Markdown).</summary>
    public string? WhyItMatters { get; set; }

    /// <summary>The TLP of <see cref="WhyItMatters"/>: it often holds internal context, so it can be raised above the
    /// article's like a bullet can.</summary>
    public TlpLevel WhyItMattersTlp { get; set; } = TlpLevel.Clear;

    /// <summary>
    /// "Keep it": someone looked at "Why it matters" going to the company-wide edition and meant it to. It changes no
    /// marking and no edition; it only answers the warning, and only while the paragraph and the marking it counts as are
    /// what was kept (<see cref="WhyItMattersKept"/>). The marking kept, a fingerprint of that marking and the text
    /// (<see cref="WhyItMattersFingerprint"/>), who and when.
    /// </summary>
    public TlpLevel? WhyItMattersKeptTlp { get; set; }

    /// <inheritdoc cref="WhyItMattersKeptTlp"/>
    public string? WhyItMattersKeptHash { get; set; }

    /// <inheritdoc cref="WhyItMattersKeptTlp"/>
    public string? WhyItMattersKeptById { get; set; }

    /// <inheritdoc cref="WhyItMattersKeptTlp"/>
    public string? WhyItMattersKeptByName { get; set; }

    /// <inheritdoc cref="WhyItMattersKeptTlp"/>
    public DateTimeOffset? WhyItMattersKeptAtUtc { get; set; }

    /// <summary>Whether "Why it matters" is kept as it is now: the text and the marking it counts as haven't changed since.</summary>
    public bool WhyItMattersKept =>
        WhyItMattersKeptHash is { } kept && kept == WhyItMattersFingerprint(WhyItMatters, Effective(WhyItMattersTlp));

    /// <summary>A fingerprint of "Why it matters" at <paramref name="marking"/>: SHA-256 of both, in hex.</summary>
    public static string WhyItMattersFingerprint(string? text, TlpLevel marking) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{marking}\n{text}")));

    /// <summary>Keeps "Why it matters" as it is now, for <paramref name="byId"/> at <paramref name="at"/>. Returns the
    /// marking it counts as, which is the one kept. Nothing else changes.</summary>
    public TlpLevel KeepWhyItMatters(string byId, string byName, DateTimeOffset at)
    {
        var marking = Effective(WhyItMattersTlp);
        WhyItMattersKeptTlp = marking;
        WhyItMattersKeptHash = WhyItMattersFingerprint(WhyItMatters, marking);
        WhyItMattersKeptById = byId;
        WhyItMattersKeptByName = byName;
        WhyItMattersKeptAtUtc = at;
        return marking;
    }

    /// <summary>Clears a keep that no longer holds (the text or its marking changed), so the record says what is kept
    /// and keeping it again records the marking afresh.</summary>
    public void ForgetLapsedKeep()
    {
        if (WhyItMattersKeptHash is null || WhyItMattersKept) return;
        WhyItMattersKeptTlp = null;
        WhyItMattersKeptHash = WhyItMattersKeptById = WhyItMattersKeptByName = null;
        WhyItMattersKeptAtUtc = null;
    }

    /// <summary>What the analyst recommends be done (Markdown). Becomes action items once those ship.</summary>
    public string? RecommendedActions { get; set; }

    /// <summary>
    /// The earlier free-text "Threat actors", comma-separated as typed ("Scattered Spider, UNC3944"). Retired in 1.4.0 for
    /// the adversary picker (ArticleAdversary tags): kept, never written from the editor again. Its names a profile goes
    /// by are offered as tags; the rest show as the "Earlier note" until cleared (AdversaryService).
    /// </summary>
    public string? Actors { get; set; }

    /// <summary>Sectors affected, comma-separated as typed.</summary>
    public string? Sectors { get; set; }

    public ArticleStatus Status { get; set; } = ArticleStatus.Draft;

    /// <summary>The threat story it's part of, if any.</summary>
    public Guid? StoryId { get; set; }

    /// <summary>The analyst who wrote it (user id), and their name then.</summary>
    public string AuthorId { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;

    /// <summary>Changes on every save; a save made from an older copy is refused.</summary>
    public Guid EditVersion { get; set; } = Guid.NewGuid();

    /// <summary>
    /// When a screenshot was last added to it or removed from it, which leaves <see cref="EditVersion"/> alone. A review
    /// decided on 0.1.0-alpha.9 recorded the edit version, so it's out of date once this is after its decision.
    /// </summary>
    public DateTimeOffset? ScreenshotsChangedAtUtc { get; set; }

    public List<ArticleBullet> Bullets { get; set; } = [];
    public List<ArticleScreenshot> Screenshots { get; set; } = [];
    public List<ArticleTag> Tags { get; set; } = [];

    /// <summary>The products from Our stack the article is about (optional).</summary>
    public List<ArticleProduct> Products { get; set; } = [];
    public List<ArticleTechnique> Techniques { get; set; } = [];
    public List<ArticleIndicator> Indicators { get; set; } = [];
    public List<ArticleCve> Cves { get; set; } = [];

    /// <summary>The TLP a part of this article actually carries: its own, or the article's when that's higher.</summary>
    public TlpLevel Effective(TlpLevel partTlp) => partTlp > Tlp ? partTlp : Tlp;

    /// <summary>The highest TLP anything in the article carries: the article's own, or a raised bullet, screenshot or
    /// "why it matters".</summary>
    public TlpLevel HighestTlp => new[] { Tlp, string.IsNullOrWhiteSpace(WhyItMatters) ? Tlp : Effective(WhyItMattersTlp) }
        .Concat(Bullets.Select(b => Effective(b.Tlp))).Concat(Screenshots.Select(s => Effective(s.Tlp))).Max();

    /// <summary>The lowest TLP a threat's "why it matters" starts at: on a threat it usually names our own systems and
    /// gaps, which shouldn't reach all-staff unless the writer lowers it on purpose.</summary>
    public const TlpLevel ThreatWhyItMattersFloor = TlpLevel.Amber;

    /// <summary>
    /// The TLP "why it matters" starts at on an article of <paramref name="classification"/> marked
    /// <paramref name="articleTlp"/>: AMBER for a threat, or the article's TLP when that's higher; CLEAR (so the
    /// article's own) for an informational one.
    /// </summary>
    public static TlpLevel DefaultWhyItMattersTlp(Classification classification, TlpLevel articleTlp) =>
        classification != Classification.Threat ? TlpLevel.Clear
        : articleTlp > ThreatWhyItMattersFloor ? articleTlp : ThreatWhyItMattersFloor;

    /// <summary>
    /// The TLP "why it matters" moves to when an article turns from <paramref name="from"/> to <paramref name="to"/>:
    /// the threat default (<see cref="DefaultWhyItMattersTlp"/>) when it turns from informational to threat while
    /// "why it matters" is still empty and below that default; otherwise null, and the saved value stays. It only
    /// ever raises, and never touches a "why it matters" someone has written.
    /// </summary>
    public static TlpLevel? WhyItMattersTlpOnClassify(Classification from, Classification to, string? whyItMatters,
        TlpLevel whyItMattersTlp, TlpLevel articleTlp)
    {
        if (from != Classification.Informational || to != Classification.Threat) return null;
        if (!string.IsNullOrWhiteSpace(whyItMatters)) return null;
        var start = DefaultWhyItMattersTlp(to, articleTlp);
        return whyItMattersTlp < start ? start : null;
    }

    /// <summary>Sets the classification, starting an empty "why it matters" at the threat default when it turns
    /// informational to threat (<see cref="WhyItMattersTlpOnClassify"/>). Returns whether it moved.</summary>
    public bool Classify(Classification classification)
    {
        var why = WhyItMattersTlpOnClassify(Classification, classification, WhyItMatters, WhyItMattersTlp, Tlp);
        Classification = classification;
        if (why is not { } t) return false;
        WhyItMattersTlp = t;
        return true;
    }
}

/// <summary>One bullet of an article (Markdown, a line or a short paragraph), in order.</summary>
public class ArticleBullet : Entity
{
    public Guid ArticleId { get; set; }
    public int SortOrder { get; set; }
    public string Text { get; set; } = string.Empty;
    public TlpLevel Tlp { get; set; } = TlpLevel.Clear;
}

/// <summary>A screenshot on an article, in order, with a caption and its own TLP.</summary>
public class ArticleScreenshot : Entity
{
    public Guid ArticleId { get; set; }
    public Guid FileId { get; set; }
    public StoredFile? File { get; set; }
    public int SortOrder { get; set; }
    public string? Caption { get; set; }
    public TlpLevel Tlp { get; set; } = TlpLevel.Clear;

    /// <summary>The lowest TLP a newly added screenshot starts at; the article's, through <see cref="Article.Effective"/>,
    /// when that's higher. A screenshot is often a capture of our own systems, so it goes to the company-wide edition
    /// only once someone lowers its marking on purpose.</summary>
    public const TlpLevel NewFloor = TlpLevel.Amber;
}

/// <summary>A scorecard tag on an article.</summary>
public class ArticleTag
{
    public Guid ArticleId { get; set; }
    public Guid TagId { get; set; }
    public ScorecardTag? Tag { get; set; }
}

/// <summary>An ATT&amp;CK technique on an article, by catalog id ("T1566.001").</summary>
public class ArticleTechnique
{
    public Guid ArticleId { get; set; }
    public string TechniqueId { get; set; } = string.Empty;
}

/// <summary>An indicator on an article, stored live (refanged, canonical) and shown defanged.</summary>
public class ArticleIndicator : Entity
{
    public Guid ArticleId { get; set; }
    public IndicatorType Type { get; set; }
    public string Value { get; set; } = string.Empty;
    public string? Note { get; set; }
}

/// <summary>
/// Another report of what an article covers (a follow-up, another outlet), added from triage so its link isn't lost
/// when the item is set aside. The article's own source stays <see cref="Article.SourceUrl"/>.
/// </summary>
public class ArticleLink : Entity
{
    public Guid ArticleId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? SourceName { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }
    public string AddedByName { get; set; } = string.Empty;
}

/// <summary>A CVE an article is about ("CVE-2026-12345"). Enrichment (CVSS, EPSS, KEV) comes later.</summary>
public class ArticleCve
{
    public Guid ArticleId { get; set; }
    public string CveId { get; set; } = string.Empty;
}
