using Tide.Application.Abstractions;
using Tide.Application.Mitre;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Web.Components.Shared;

/// <summary>An ATT&amp;CK tactic on an article's slide, with the article's techniques under it (by primary tactic).</summary>
public sealed record SlideTactic(MitreTactic Tactic, IReadOnlyList<AttackTechnique> Techniques)
{
    public string Name => AttackMatrix.TacticName(Tactic);

    /// <summary>The techniques its row lists, <see cref="SlideTags.MaxTechniques"/> at most.</summary>
    public IReadOnlyList<AttackTechnique> Shown { get; } = Techniques.Take(SlideTags.MaxTechniques).ToList();

    /// <summary>The rest, named in a "+N more" after them.</summary>
    public IReadOnlyList<AttackTechnique> Left { get; } = Techniques.Skip(SlideTags.MaxTechniques).ToList();
}

/// <summary>A scorecard category's tags on an article's slide.</summary>
public sealed record SlideTagGroup(string Category, IReadOnlyList<string> Tags);

/// <summary>
/// The "Tags and ATT&amp;CK" section of an article's slide in present mode: its board-scorecard tags by category, and its
/// ATT&amp;CK techniques by tactic in kill-chain order. What each edition shows follows the brief's own copies (BriefRenderer, EditionArticleView): both
/// editions list the ATT&amp;CK techniques, so both slides show the tactics; neither copy carries scorecard tags, which
/// are the team's own bookkeeping, so only the team edition's slide shows them (see <see cref="ShowsScorecard"/>).
/// </summary>
public static class SlideTags
{
    /// <summary>The most techniques a tactic's row lists on a slide; the rest are named in a "+N more".</summary>
    public const int MaxTechniques = 6;

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
            // Each change is to one of the brief's articles: its topic is that article's.
            if (before.Items.FirstOrDefault(i => c.Topic == ChangeFeed.Article(i.ArticleId)) is not { } item) return false;
            var was = item.Article;
            var now = after.Items.FirstOrDefault(i => i.ArticleId == item.ArticleId)?.Article;
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
