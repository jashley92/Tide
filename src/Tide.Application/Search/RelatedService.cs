using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Intake;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.Application.Search;

/// <summary>An action still open on a related article: who has it, when it's due, and how many days late it is (0 when it isn't).</summary>
public sealed record RelatedAction(int Number, string Title, ActionStatus Status, string TeamName, string? AssigneeName, DateOnly? DueDate, int DaysLate);

/// <summary>
/// Another article about the same thing: why (the CVEs it shares, "Same link", "Same story"), the briefs it was on
/// (oldest first), its story, and the actions raised from it that are still open.
/// </summary>
public sealed record RelatedArticle(
    Guid Id, string Title, ArticleStatus Status, string AuthorName, DateTimeOffset CreatedAtUtc, IReadOnlyList<DateOnly> BriefDates,
    Guid? StoryId, string? StoryName, IReadOnlyList<string> Why, IReadOnlyList<RelatedAction> OpenActions)
{
    /// <summary>The last brief it was on, if any.</summary>
    public DateOnly? LastBriefed => BriefDates.Count == 0 ? null : BriefDates[^1];
}

/// <summary>
/// An article's related work: the newest <see cref="RelatedService.MaxArticles"/> related articles, how many there are in
/// all, and what to search for to see the rest (a shared CVE, or the story's name; null when neither applies).
/// </summary>
public sealed record RelatedWork(IReadOnlyList<RelatedArticle> Articles, int Total, string? SearchQuery)
{
    public static readonly RelatedWork None = new([], 0, null);

    /// <summary>There are more than are shown.</summary>
    public bool More => Total > Articles.Count;
}

/// <summary>
/// What's already been written about the same thing as an article, for the editor's Similar articles: other articles that share
/// a CVE, a link (the source or another report, compared as stored and canonical) or the story, newest first, each with
/// the briefs it was on and its open actions. The same queries as Search's CVE and indicator lookups (<see cref="Lookups"/>).
/// Open to the team, as Search is: it shows every TLP, as the article pages do. Readers never get it.
/// </summary>
public sealed class RelatedService(IAppDbContextFactory dbs, ICurrentUser user, OrgClock clock)
{
    /// <summary>The most related articles shown; Search has the rest.</summary>
    public const int MaxArticles = 5;

    public const string SameLink = "Same link", SameStory = "Same story";

    public async Task<RelatedWork> ForArticleAsync(Guid articleId, CancellationToken ct = default)
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("Related work is open to the team.");
        await using var db = await dbs.CreateAsync(ct);
        var me = await db.Articles.AsNoTracking().Where(a => a.Id == articleId).Select(a => new { a.SourceUrl, a.StoryId }).FirstOrDefaultAsync(ct);
        if (me is null) return RelatedWork.None;

        // Why each other article is related, in the order found: CVEs, then the link, then the story.
        var why = new Dictionary<Guid, List<string>>();
        void Add(Guid id, string reason)
        {
            if (id == articleId) return;
            if (!why.TryGetValue(id, out var list)) why[id] = list = [];
            if (!list.Contains(reason)) list.Add(reason);
        }

        var cves = await db.ArticleCves.AsNoTracking().Where(c => c.ArticleId == articleId).Select(c => c.CveId).ToListAsync(ct);
        var shared = new List<string>();
        if (cves.Count > 0)
            foreach (var c in (await db.ArticleCves.AsNoTracking().Where(c => cves.Contains(c.CveId) && c.ArticleId != articleId)
                         .Select(c => new { c.ArticleId, c.CveId }).ToListAsync(ct)).OrderBy(c => c.CveId, StringComparer.Ordinal))
            {
                Add(c.ArticleId, c.CveId);
                if (!shared.Contains(c.CveId)) shared.Add(c.CveId);
            }

        var mine = (await db.ArticleLinks.AsNoTracking().Where(l => l.ArticleId == articleId).Select(l => l.Url).ToListAsync(ct))
            .Append(me.SourceUrl).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u!.Trim()).ToList();
        var urls = mine.Concat(mine.Select(UrlCanonicalizer.Canonicalize).OfType<string>()).Distinct().ToList();
        if (urls.Count > 0)
        {
            foreach (var id in await db.Articles.AsNoTracking().Where(a => a.Id != articleId && a.SourceUrl != null && urls.Contains(a.SourceUrl))
                         .Select(a => a.Id).ToListAsync(ct))
                Add(id, SameLink);
            foreach (var id in await db.ArticleLinks.AsNoTracking().Where(l => l.ArticleId != articleId && urls.Contains(l.Url))
                         .Select(l => l.ArticleId).ToListAsync(ct))
                Add(id, SameLink);
        }

        string? storyName = null;
        if (me.StoryId is { } story)
        {
            storyName = await db.Stories.AsNoTracking().Where(s => s.Id == story).Select(s => s.Name).FirstOrDefaultAsync(ct);
            foreach (var id in await db.Articles.AsNoTracking().Where(a => a.Id != articleId && a.StoryId == story).Select(a => a.Id).ToListAsync(ct))
                Add(id, SameStory);
        }

        var query = shared.FirstOrDefault() ?? storyName;
        if (why.Count == 0) return new RelatedWork([], 0, query);

        var ids = why.Keys.ToList();
        var rows = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id)).OrderByDescending(a => a.CreatedAtUtc).Take(MaxArticles)
            .Select(a => new
            {
                a.Id, a.Title, a.Status, a.AuthorName, a.CreatedAtUtc, a.StoryId,
                StoryName = db.Stories.Where(s => s.Id == a.StoryId).Select(s => s.Name).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var shown = rows.Select(r => r.Id).ToList();
        var dates = await Lookups.BriefDatesAsync(db, shown, ct);
        var today = clock.Today;
        var open = (await Lookups.ActionRows(db, db.Actions.AsNoTracking()
                .Where(a => a.ArticleId != null && shown.Contains(a.ArticleId.Value) && a.Status < ActionStatus.Done)
                .OrderByDescending(a => a.Number), ct))
            .GroupBy(a => a.ArticleId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<RelatedAction>)g
                .Select(a => new RelatedAction(a.Number, a.Title, a.Status, a.TeamName, a.AssigneeName, a.DueDate, ActionSla.DaysLate(a.DueDate, today)))
                .ToList());
        var articles = rows.Select(r => new RelatedArticle(r.Id, r.Title, r.Status, r.AuthorName, r.CreatedAtUtc, dates.GetValueOrDefault(r.Id) ?? [],
            r.StoryId, r.StoryName, why[r.Id], open.GetValueOrDefault(r.Id) ?? [])).ToList();
        return new RelatedWork(articles, ids.Count, query);
    }

    /// <summary>
    /// What "Mention" puts in why it matters: the action's number and that it's still open ("A-13 (open)"), never how
    /// late it is, which would go stale once the brief is sent.
    /// </summary>
    public static string MentionText(RelatedAction a) => $"A-{a.Number} (open)";
}
