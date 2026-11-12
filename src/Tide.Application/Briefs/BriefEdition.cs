using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Briefs;

/// <summary>An action raised on an article, as an edition lists it. <see cref="Id"/>, <see cref="TeamId"/> and the
/// assignee are for the meeting, which can hand it to someone; a preview leaves them out. <see cref="Recommendation"/> is
/// the recommended line it was raised from, if any (<see cref="ActionItem.RecommendationLine"/>): the slide shows it there.</summary>
public sealed record EditionAction(int Number, string Title, string TeamName, DateOnly? DueDate, ActionStatus Status,
    Guid Id = default, Guid TeamId = default, string? AssigneeId = null, string? AssigneeName = null, string? Recommendation = null);

/// <summary>
/// An article as an edition shows it: the parts the edition's ceiling lets through. <see cref="ShowWhy"/> is false when
/// "why it matters" is above the ceiling (or empty). Indicators and actions are on the team edition only.
/// </summary>
public sealed record EditionArticle(
    Article Article,
    IReadOnlyList<ArticleBullet> Bullets,
    IReadOnlyList<ArticleScreenshot> Screenshots,
    int WithheldParts,
    bool ShowWhy = false,
    IReadOnlyList<ArticleIndicator>? Indicators = null,
    IReadOnlyList<EditionAction>? Actions = null)
{
    public IReadOnlyList<ArticleIndicator> Indicators { get; init; } = Indicators ?? [];
    public IReadOnlyList<EditionAction> Actions { get; init; } = Actions ?? [];

    /// <summary>The highest marking of what this edition shows of the article.</summary>
    public TlpLevel Marking => new[] { Article.Tlp, ShowWhy ? Article.Effective(Article.WhyItMattersTlp) : Article.Tlp }
        .Concat(Bullets.Select(b => Article.Effective(b.Tlp)))
        .Concat(Screenshots.Select(s => Article.Effective(s.Tlp)))
        .Max();
}

/// <summary>
/// A brief cut for one audience. <see cref="WithheldArticles"/> counts the articles left out entirely; <see cref="ShowIntro"/>
/// is false when the title slide line is above the ceiling (or empty).
/// </summary>
public sealed record BriefEdition(
    Edition Edition,
    TlpLevel Ceiling,
    Brief Brief,
    IReadOnlyList<EditionArticle> Articles,
    int WithheldArticles,
    bool ShowIntro = false)
{
    /// <summary>The open actions the team edition lists (the email's "Still open from earlier briefs", the meeting's
    /// "Actions that need review"), when the cut carries them:
    /// each at its article's TLP, so they count in <see cref="Marking"/>.</summary>
    public Actions.OpenActionsReview? Open { get; init; }

    /// <summary>Whether this edition carries the team's opening note (<see cref="Brief.TeamNote"/>): the team edition,
    /// when the note isn't blank. The company-wide edition never does, whatever its ceiling. Everything that shows the
    /// note (the slide, the email, the preview) asks this, so nothing reads the note past an edition that leaves it out.</summary>
    public bool ShowTeamNote => Edition == Edition.Team && !string.IsNullOrWhiteSpace(Brief.TeamNote);

    /// <summary>The team's opening note when this edition carries it, otherwise null.</summary>
    public string? TeamNote => ShowTeamNote ? Brief.TeamNote : null;

    /// <summary>The highest marking anything in this edition carries: what the edition is marked as.</summary>
    public TlpLevel Marking => Articles.Select(a => a.Marking).Append(ShowIntro ? Brief.IntroTlp : TlpLevel.Clear)
        .Append(Open?.Marking ?? TlpLevel.Clear).Max();
}

/// <summary>
/// Cuts a brief down to what one audience may see. A part (bullet, screenshot, "why it matters") counts at its article's
/// TLP or its own, whichever is higher, so marking a whole article AMBER can't be undone by leaving a bullet at CLEAR.
/// Nothing above the ceiling is in the result: not the article, its bullets, its screenshots or their captions, nor the
/// brief's opening note. The team
/// edition also carries each article's indicators and the actions raised from it; the company-wide one never does.
/// </summary>
public static class BriefEditions
{
    /// <summary>The actions raised from the brief's articles, for the team edition: cancelled ones left out.</summary>
    public static async Task<ILookup<Guid, EditionAction>> ActionsAsync(IAppDbContext db, Brief brief, CancellationToken ct = default)
    {
        var ids = brief.Items.Select(i => i.ArticleId).ToList();
        var rows = await db.Actions.AsNoTracking()
            .Where(a => a.ArticleId != null && ids.Contains(a.ArticleId.Value) && a.Status != ActionStatus.Cancelled)
            .OrderBy(a => a.Number)
            .Select(a => new { ArticleId = a.ArticleId!.Value, Row = new EditionAction(a.Number, a.Title, a.Team!.Name, a.DueDate, a.Status, a.Id, a.TeamId, a.AssigneeId, a.AssigneeName, a.RecommendationLine) })
            .ToListAsync(ct);
        return rows.ToLookup(r => r.ArticleId, r => r.Row);
    }

    /// <summary>
    /// The edition of <paramref name="brief"/> (its items, their articles and the articles' bullets, screenshots and
    /// indicators loaded) that <paramref name="ceiling"/> allows, in the brief's order. <paramref name="actions"/>, by
    /// article id, are listed on the team edition.
    /// </summary>
    public static BriefEdition Build(Brief brief, Edition edition, TlpLevel ceiling, ILookup<Guid, EditionAction>? actions = null)
    {
        var shown = new List<EditionArticle>();
        var withheld = 0;
        foreach (var item in brief.Items.OrderBy(i => i.SortOrder))
        {
            if (item.Article is not { } article) continue;
            if (BuildArticle(article, edition, ceiling, actions?[article.Id].ToList()) is { } cut) shown.Add(cut);
            else withheld++;
        }
        var showIntro = !string.IsNullOrWhiteSpace(brief.Intro) && brief.IntroTlp <= ceiling;
        return new BriefEdition(edition, ceiling, brief, shown, withheld, showIntro);
    }

    /// <summary>
    /// <paramref name="article"/> (its bullets, screenshots and indicators loaded) as <paramref name="edition"/> shows it
    /// under <paramref name="ceiling"/>, or null when the article itself is above the ceiling and is left out whole. This
    /// is the one cut: <see cref="Build"/> makes every edition with it, and the editor's preview runs it on an unsaved
    /// draft (<see cref="Articles.ArticleForm.ToPreview"/>). <paramref name="actions"/>, the ones raised from the
    /// article, are listed on the team edition only.
    /// </summary>
    public static EditionArticle? BuildArticle(Article article, Edition edition, TlpLevel ceiling, IReadOnlyList<EditionAction>? actions = null)
    {
        if (article.Tlp > ceiling) return null;
        var team = edition == Edition.Team;
        var bullets = article.Bullets.OrderBy(b => b.SortOrder).ToList();
        var shots = article.Screenshots.OrderBy(s => s.SortOrder).ToList();
        var keptBullets = bullets.Where(b => article.Effective(b.Tlp) <= ceiling).ToList();
        var keptShots = shots.Where(s => article.Effective(s.Tlp) <= ceiling).ToList();
        var hasWhy = !string.IsNullOrWhiteSpace(article.WhyItMatters);
        var showWhy = hasWhy && article.Effective(article.WhyItMattersTlp) <= ceiling;
        return new EditionArticle(article, keptBullets, keptShots,
            bullets.Count - keptBullets.Count + shots.Count - keptShots.Count + (hasWhy && !showWhy ? 1 : 0), showWhy,
            team ? article.Indicators.OrderBy(i => i.Type).ThenBy(i => i.Value, StringComparer.OrdinalIgnoreCase).ToList() : null,
            team ? actions : null);
    }
}
