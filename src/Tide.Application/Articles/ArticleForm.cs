using Tide.Application.Indicators;
using Tide.Application.Mitre;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Articles;

/// <summary>
/// An article as the editor holds it: every field the analyst edits, saved in one go by
/// <see cref="ArticleService.SaveAsync"/>. Screenshots are saved as they're added, so only their captions and markings
/// ride here.
/// </summary>
public sealed class ArticleForm
{
    public Guid Id { get; set; }
    public Guid EditVersion { get; set; }

    public string Title { get; set; } = "";
    public string? SourceName { get; set; }
    public string? SourceUrl { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public TlpLevel Tlp { get; set; } = TlpLevel.Clear;
    public Classification Classification { get; set; } = Classification.Informational;
    public Priority? Priority { get; set; }
    public string Bluf { get; set; } = "";
    public string? WhyItMatters { get; set; }
    public TlpLevel WhyItMattersTlp { get; set; } = TlpLevel.Clear;
    public string? RecommendedActions { get; set; }
    public string? Sectors { get; set; }

    /// <summary>The saved article's "Keep it" on "Why it matters" (<see cref="Article.WhyItMattersKeptHash"/>), so the
    /// preview answers the warning as the running order does until the text or marking is edited. A save never writes it.</summary>
    public string? WhyItMattersKeptHash { get; set; }

    public List<BulletForm> Bullets { get; set; } = [];
    public List<ScreenshotForm> Screenshots { get; set; } = [];
    public HashSet<Guid> TagIds { get; set; } = [];
    public List<string> TechniqueIds { get; set; } = [];
    public List<IndicatorForm> Indicators { get; set; } = [];
    public List<string> Cves { get; set; } = [];

    /// <summary>
    /// The editor's copy of <paramref name="a"/>. A part stored below the article's TLP (from before the pickers held the
    /// floor) comes in at the article's TLP, which is what it already counts as, so the next save stores that.
    /// </summary>
    public static ArticleForm From(Article a)
    {
        var form = Copy(a);
        form.PullUpParts();
        return form;
    }

    /// <summary>
    /// Sets the article's TLP. Raising it pulls any bullet, screenshot or "why it matters" below it up to match, since a
    /// part never counts lower than its article; lowering it leaves the parts where they are. Returns how many parts it
    /// pulled up (an empty "why it matters" isn't counted).
    /// </summary>
    public int SetTlp(TlpLevel tlp)
    {
        Tlp = tlp;
        return PullUpParts();
    }

    /// <summary>
    /// Sets the classification, as <see cref="Article.Classify"/> does: turning informational to threat while "why it
    /// matters" is still empty starts it at AMBER, or the article's TLP when higher. Returns whether it moved, so the
    /// editor can say why. Turning to informational drops the priority.
    /// </summary>
    public bool SetClassification(Classification classification)
    {
        var why = Article.WhyItMattersTlpOnClassify(Classification, classification, WhyItMatters, WhyItMattersTlp, Tlp);
        Classification = classification;
        if (classification == Classification.Informational) Priority = null;
        if (why is not { } t) return false;
        WhyItMattersTlp = t;
        return true;
    }

    /// <summary>
    /// Whether "why it matters" sits at the threat default and hasn't been written or moved since: what the editor
    /// shows the "starts at AMBER" hint for, on a draft promoted as a threat.
    /// </summary>
    public bool WhyItMattersAtThreatDefault =>
        Classification == Classification.Threat && string.IsNullOrWhiteSpace(WhyItMatters)
        && WhyItMattersTlp > Tlp && WhyItMattersTlp == Article.DefaultWhyItMattersTlp(Classification, Tlp);

    /// <summary>
    /// The article this form would save as, built in memory and never tracked or stored, so an unsaved draft can be cut
    /// for an edition (<see cref="Briefs.BriefEditions.BuildArticle"/>) exactly as the saved one will be. Fields are mapped
    /// as <see cref="ArticleService.SaveAsync"/> maps them: trimmed, blank bullets dropped, parts in the form's order,
    /// indicators normalised and de-duplicated, the priority kept for a threat only. What a save would refuse (an
    /// over-long field, a blank title, a link that isn't http or https, an unknown technique or CVE, too many bullets)
    /// doesn't throw here: the text is kept, and the link, technique or CVE is left out, so the save still reports it.
    /// Scorecard tags carry their ids only, and screenshots have no file loaded.
    /// </summary>
    public Article ToPreview()
    {
        var a = new Article
        {
            Id = Id,
            EditVersion = EditVersion,
            Title = ArticleService.Tidy(Title),
            SourceName = ArticleService.TidyOrNull(SourceName),
            SourceUrl = ArticleService.SafeUrl(SourceUrl),
            PublishedAtUtc = PublishedAtUtc,
            Tlp = Domain.Enums.Tlp.Checked(Tlp),
            Classification = Classification,
            Priority = Classification == Classification.Threat ? Priority : null,
            Bluf = ArticleService.Tidy(Bluf),
            WhyItMatters = ArticleService.TidyOrNull(WhyItMatters, multiline: true),
            WhyItMattersTlp = Domain.Enums.Tlp.Checked(WhyItMattersTlp),
            RecommendedActions = ArticleService.TidyOrNull(RecommendedActions, multiline: true),
            Sectors = ArticleService.TidyOrNull(Sectors),
            WhyItMattersKeptHash = WhyItMattersKeptHash,
        };
        a.Bullets = ArticleService.KeptBullets(Bullets)
            .Select((b, i) => new ArticleBullet { ArticleId = a.Id, SortOrder = i, Text = b.Text, Tlp = b.Tlp }).ToList();
        a.Screenshots = Screenshots.Select((s, i) => new ArticleScreenshot
        {
            Id = s.Id, ArticleId = a.Id, FileId = s.FileId, SortOrder = i,
            Caption = ArticleService.TidyOrNull(s.Caption), Tlp = Domain.Enums.Tlp.Checked(s.Tlp),
        }).ToList();
        a.Tags = TagIds.Select(id => new ArticleTag { ArticleId = a.Id, TagId = id }).ToList();
        a.Techniques = TechniqueIds.Select(t => AttackCatalog.Find(t)?.Id).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(id => new ArticleTechnique { ArticleId = a.Id, TechniqueId = id }).ToList();
        a.Indicators = ArticleService.WantedIndicators(Indicators, strict: false).Values
            .Select(f => new ArticleIndicator { ArticleId = a.Id, Type = f.Type, Value = f.Value, Note = f.Note }).ToList();
        a.Cves = Cves.Select(IocExtractor.NormalizeCve).OfType<string>().Distinct(StringComparer.Ordinal)
            .Select(id => new ArticleCve { ArticleId = a.Id, CveId = id }).ToList();
        return a;
    }

    private int PullUpParts()
    {
        var raised = 0;
        foreach (var b in Bullets.Where(b => b.Tlp < Tlp)) { b.Tlp = Tlp; raised++; }
        foreach (var s in Screenshots.Where(s => s.Tlp < Tlp)) { s.Tlp = Tlp; raised++; }
        if (WhyItMattersTlp < Tlp)
        {
            WhyItMattersTlp = Tlp;
            if (!string.IsNullOrWhiteSpace(WhyItMatters)) raised++;
        }
        return raised;
    }

    private static ArticleForm Copy(Article a) => new()
    {
        Id = a.Id,
        EditVersion = a.EditVersion,
        Title = a.Title,
        SourceName = a.SourceName,
        SourceUrl = a.SourceUrl,
        PublishedAtUtc = a.PublishedAtUtc,
        Tlp = a.Tlp,
        Classification = a.Classification,
        Priority = a.Priority,
        Bluf = a.Bluf,
        WhyItMatters = a.WhyItMatters,
        WhyItMattersTlp = a.WhyItMattersTlp,
        RecommendedActions = a.RecommendedActions,
        Sectors = a.Sectors,
        WhyItMattersKeptHash = a.WhyItMattersKeptHash,
        Bullets = a.Bullets.OrderBy(b => b.SortOrder).Select(b => new BulletForm { Text = b.Text, Tlp = b.Tlp }).ToList(),
        Screenshots = a.Screenshots.OrderBy(s => s.SortOrder)
            .Select(s => new ScreenshotForm { Id = s.Id, FileId = s.FileId, Caption = s.Caption, Tlp = s.Tlp }).ToList(),
        TagIds = a.Tags.Select(t => t.TagId).ToHashSet(),
        TechniqueIds = a.Techniques.Select(t => t.TechniqueId).Order(StringComparer.OrdinalIgnoreCase).ToList(),
        Indicators = a.Indicators.OrderBy(i => i.Type).ThenBy(i => i.Value, StringComparer.OrdinalIgnoreCase)
            .Select(i => new IndicatorForm { Type = i.Type, Value = i.Value, Note = i.Note }).ToList(),
        Cves = a.Cves.Select(c => c.CveId).Order(StringComparer.Ordinal).ToList(),
    };
}

public sealed class BulletForm
{
    public string Text { get; set; } = "";
    public TlpLevel Tlp { get; set; } = TlpLevel.Clear;
}

public sealed class ScreenshotForm
{
    public Guid Id { get; set; }
    public Guid FileId { get; set; }
    public string? Caption { get; set; }
    public TlpLevel Tlp { get; set; } = TlpLevel.Clear;
}

public sealed class IndicatorForm
{
    public IndicatorType Type { get; set; }
    public string Value { get; set; } = "";
    public string? Note { get; set; }
}

/// <summary>
/// The parts of an article the editor groups its fields into, in the order it shows them: what a readiness check points
/// at, so the editor can flag the section that needs work and take you to it.
/// </summary>
public enum ArticlePart
{
    Assessment,
    BottomLine,
    Bullets,
    Screenshots,
    WhyItMatters,
    Recommended,
    Indicators,
    Tags,
}

/// <summary>A change the editor can make for a readiness check in one click.</summary>
public enum ArticleFix
{
    /// <summary>Mark "Why it matters to us" TLP:AMBER, so the company-wide edition leaves it out.</summary>
    WhyItMattersAmber,

    /// <summary>Keep "Why it matters" at its marking: it is meant for the company-wide edition. Changes no marking.</summary>
    KeepWhyItMatters,
}

/// <summary>
/// A gap in an article that a brief shouldn't go out with. <see cref="Blocking"/> ones stop it being Ready; <see cref="Part"/>
/// is where it's fixed, and <see cref="Need"/> names what's missing in a word or two, for a row's "Needs: Bottom line".
/// <see cref="Fix"/>, when set, is a one-click change the editor offers for it. <see cref="ToCheck"/> marks one that asks
/// for a second look at something written (a TLP marking) rather than something missing.
/// </summary>
public sealed record ArticleCheck(string Message, bool Blocking, ArticlePart Part, string? Need = null, ArticleFix? Fix = null,
    bool ToCheck = false)
{
    /// <summary>What's missing in a word or two, or the whole message when there's no short name.</summary>
    public string Short => Need ?? Message;
}

public static class ArticleChecks
{
    /// <summary>
    /// What's missing or inconsistent, most important first. Empty when the article is ready. Given
    /// <paramref name="companyCeiling"/>, the company-wide edition's ceiling, it also warns when a threat's "Why it
    /// matters" will reach that edition (<see cref="WhyReachesCompany"/>).
    /// </summary>
    public static IReadOnlyList<ArticleCheck> For(Article a, TlpLevel? companyCeiling = null)
    {
        var checks = new List<ArticleCheck>();
        if (string.IsNullOrWhiteSpace(a.Bluf)) checks.Add(new("Add the bottom line (BLUF).", true, ArticlePart.BottomLine, "Bottom line"));
        if (!a.Bullets.Any(b => !string.IsNullOrWhiteSpace(b.Text))) checks.Add(new("Add at least one bullet.", true, ArticlePart.Bullets, "A bullet"));
        if (a.Classification == Classification.Threat && a.Priority is null) checks.Add(new("A threat needs a priority.", true, ArticlePart.Assessment, "Priority"));
        if (a.Classification == Classification.Threat && string.IsNullOrWhiteSpace(a.RecommendedActions))
            checks.Add(new("A threat should say what to do (recommended actions).", false, ArticlePart.Recommended, "Recommended actions"));
        if (a.Tags.Count == 0) checks.Add(new("Add a scorecard tag.", false, ArticlePart.Tags, "Scorecard tag"));
        if (string.IsNullOrWhiteSpace(a.SourceUrl) && string.IsNullOrWhiteSpace(a.SourceName))
            checks.Add(new("Say where it came from (source or link).", false, ArticlePart.Assessment, "Source"));
        var uncaptioned = a.Screenshots.Count(s => string.IsNullOrWhiteSpace(s.Caption));
        if (uncaptioned > 0) checks.Add(new($"{uncaptioned} screenshot{(uncaptioned == 1 ? " has" : "s have")} no caption.", false, ArticlePart.Screenshots, "Captions"));
        if (companyCeiling is { } ceiling && WhyReachesCompany(a, ceiling) is { } why) checks.Add(why);
        return checks;
    }

    /// <summary>
    /// The warning for a threat whose "Why it matters to us" goes to everyone: it has text, and its effective TLP is at or
    /// below <paramref name="companyCeiling"/>, so the company-wide edition carries it. That paragraph often names our own
    /// systems and exposure, so the writer is asked to check it, with a one-click fix that marks it AMBER (offered only
    /// when AMBER is above the ceiling, so that marking it does leave it out). Null otherwise, and while someone has kept
    /// the paragraph as it is (<see cref="Article.WhyItMattersKept"/>): they looked, and it is meant to go to everyone.
    /// Keeping it answers only this warning; the editions carry the paragraph exactly as they would anyway.
    /// </summary>
    public static ArticleCheck? WhyReachesCompany(Article a, TlpLevel companyCeiling)
    {
        if (a.Classification != Classification.Threat || string.IsNullOrWhiteSpace(a.WhyItMatters) || a.WhyItMattersKept) return null;
        var why = a.Effective(a.WhyItMattersTlp);
        return why > companyCeiling ? null
            : new($"Why it matters is {Tlp.Label(why)} and will reach the company-wide edition.", false, ArticlePart.WhyItMatters,
                "Why it matters TLP", TlpLevel.Amber > companyCeiling ? ArticleFix.WhyItMattersAmber : null, ToCheck: true);
    }
}
