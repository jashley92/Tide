using Tide.Application.Abstractions;
using Tide.Application.Mitre;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Web.Components.Shared;

/// <summary>An ATT&amp;CK tactic on an article's slide, with the article's techniques under it (by primary tactic).</summary>
public sealed record SlideTactic(MitreTactic Tactic, IReadOnlyList<AttackTechnique> Techniques)
{
    public string Name => AttackMatrix.TacticName(Tactic);

    /// <summary>"T1566.001 Spearphishing Attachment; T1190 Exploit Public-Facing Application": the chip's title.</summary>
    public string Detail => string.Join("; ", Techniques.Select(t => $"{t.Id} {t.Name}"));
}

/// <summary>A scorecard category's tags on an article's slide.</summary>
public sealed record SlideTagGroup(string Category, IReadOnlyList<string> Tags);

/// <summary>
/// The tag row on an article's slide in present mode: its ATT&amp;CK tactics in kill-chain order, and its board-scorecard
/// tags by category. What each edition shows follows the brief's own copies (BriefRenderer, EditionArticleView): both
/// editions list the ATT&amp;CK techniques, so both slides show the tactics; neither copy carries scorecard tags, which
/// are the team's own bookkeeping, so only the team edition's slide shows them (see <see cref="ShowsScorecard"/>).
/// </summary>
public static class SlideTags
{
    /// <summary>The most tactic chips the row draws; the rest are counted in a "+N more" chip.</summary>
    public const int MaxChips = 6;

    /// <summary>The most scorecard tags the row draws (their category names take room too); the rest are counted in a
    /// "+N more" chip. With <see cref="MaxChips"/>, the row keeps to two lines on a slide.</summary>
    public const int MaxTags = 5;

    /// <summary>Whether an edition's slide shows scorecard tags: the team edition's only.</summary>
    public static bool ShowsScorecard(Edition edition) => edition == Edition.Team;

    /// <summary>The article's tactics in matrix order, each with its techniques by id. Ids no longer in the catalogue are left out.</summary>
    public static IReadOnlyList<SlideTactic> Tactics(Article a) => a.Techniques
        .Select(t => AttackCatalog.Find(t.TechniqueId)).OfType<AttackTechnique>()
        .DistinctBy(t => t.Id)
        .GroupBy(t => t.PrimaryTactic)
        .OrderBy(g => AttackMatrix.TacticRank(g.Key))
        .Select(g => new SlideTactic(g.Key, g.OrderBy(t => t.Id, StringComparer.Ordinal).ToList()))
        .ToList();

    /// <summary>The article's scorecard tags by category, in the scorecard's order.</summary>
    public static IReadOnlyList<SlideTagGroup> Scorecard(Article a) => a.Tags
        .Select(t => t.Tag).OfType<ScorecardTag>()
        .DistinctBy(t => t.Id)
        .GroupBy(t => t.CategoryId)
        .OrderBy(g => g.First().Category?.SortOrder ?? int.MaxValue).ThenBy(g => g.First().Category?.Name, StringComparer.OrdinalIgnoreCase)
        .Select(g => new SlideTagGroup(g.First().Category?.Name ?? "Scorecard",
            g.OrderBy(t => t.SortOrder).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => t.Name).ToList()))
        .ToList();

    /// <summary>
    /// The groups cut to <paramref name="max"/> tags in all, so the row stays within its two lines; the names left out
    /// come back as <c>Left</c>, for a "+N more" chip.
    /// </summary>
    public static (IReadOnlyList<SlideTagGroup> Shown, IReadOnlyList<string> Left) Cap(IReadOnlyList<SlideTagGroup> groups, int max = MaxTags)
    {
        var shown = new List<SlideTagGroup>();
        var rest = new List<string>();
        var left = max;
        foreach (var g in groups)
        {
            var take = Math.Min(left, g.Tags.Count);
            if (take > 0) shown.Add(g with { Tags = g.Tags.Take(take).ToList() });
            rest.AddRange(g.Tags.Skip(take));
            left -= take;
        }
        return (shown, rest);
    }

    /// <summary>
    /// Whether <paramref name="changes"/> (heard on the change feed) only changed articles' ATT&amp;CK techniques or
    /// scorecard tags, between the brief as it was (<paramref name="before"/>) and as read again: each change is to an
    /// article on both, and everything else the slides draw of it is the same. Present mode updates those chips quietly.
    /// </summary>
    public static bool OnlyTagsChanged(Brief? before, Brief after, IReadOnlyList<Change> changes)
    {
        if (before is null || before.Id != after.Id || changes.Count == 0) return false;
        if (Shape(before) != Shape(after)) return false;
        foreach (var c in changes)
        {
            if (!c.Topic.StartsWith("article:", StringComparison.Ordinal) || !Guid.TryParse(c.Topic["article:".Length..], out var id)) return false;
            var was = before.Items.FirstOrDefault(i => i.ArticleId == id)?.Article;
            var now = after.Items.FirstOrDefault(i => i.ArticleId == id)?.Article;
            if (was is null || now is null || Content(was) != Content(now)) return false;
        }
        return true;
    }

    // The brief itself as the slides draw it: its running order, ticks, intro and status.
    private static string Shape(Brief b) => System.Text.Json.JsonSerializer.Serialize(new object?[]
    {
        b.Status, b.Intro, b.TeamNote, b.Items.OrderBy(i => i.SortOrder).Select(i => new object[] { i.ArticleId, i.Discussed }),
    });

    // An article as its slide draws it, leaving out the tags and techniques (and the edit version a save moves).
    private static string Content(Article a) => System.Text.Json.JsonSerializer.Serialize(new object?[]
    {
        a.Title, a.SourceName, a.SourceUrl, a.Tlp, a.Classification, a.Priority, a.Status, a.Bluf,
        a.WhyItMatters, a.WhyItMattersTlp, a.RecommendedActions,
        a.Bullets.OrderBy(b => b.SortOrder).Select(b => new object[] { b.Text, b.Tlp }),
        a.Screenshots.OrderBy(s => s.SortOrder).Select(s => new object?[] { s.FileId, s.Caption, s.Tlp }),
        a.Indicators.Select(i => $"{i.Type}:{i.Value}").Order(StringComparer.Ordinal),
        a.Cves.Select(c => c.CveId).Order(StringComparer.Ordinal),
    });
}
