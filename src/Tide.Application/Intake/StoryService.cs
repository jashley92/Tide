using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Search;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Intake;

/// <summary>A story on the list: how many articles, and when it last had one.</summary>
public sealed record StoryRow(Guid Id, string Name, StoryStatus Status, int Articles, DateTimeOffset? LastArticleAtUtc);

/// <summary>A story with its timeline: the linked articles oldest first, and what they add up to.</summary>
public sealed record StoryDetail(Story Story, IReadOnlyList<Article> Articles, IReadOnlyList<string> Cves, IReadOnlyList<string> Actors,
    IReadOnlyList<string> Techniques, IReadOnlyDictionary<Guid, DateOnly> BriefedOn);

/// <summary>
/// Threat stories: a campaign, a vulnerability, an actor's run of activity, followed across days. Analysts create them
/// and link articles; the story page is the timeline of those articles and what they share.
/// </summary>
public sealed class StoryService(IAppDbContextFactory dbs, ICurrentUser user)
{
    /// <summary>The most stories the article editor's picker offers at once.</summary>
    public const int PickLimit = 8;

    public async Task<IReadOnlyList<StoryRow>> ListAsync(bool includeClosed = true, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return Ordered(await Rows(db, db.Stories.AsNoTracking().Where(s => includeClosed || s.Status != StoryStatus.Closed)).ToListAsync(ct));
    }

    /// <summary>
    /// The stories the article editor offers to put an article in, at most <see cref="PickLimit"/> so the picker stays
    /// short however many there are: with nothing typed, the open ones that had an article most recently; with words,
    /// every story whose name holds them, closed ones too, open ones first.
    /// </summary>
    public async Task<IReadOnlyList<StoryRow>> FindAsync(string? words, CancellationToken ct = default)
    {
        RequireRead();
        var text = (words ?? "").Trim();
        await using var db = await dbs.CreateAsync(ct);
        var stories = db.Stories.AsNoTracking();
        stories = text.Length == 0
            ? stories.Where(s => s.Status != StoryStatus.Closed)
            : stories.Where(s => EF.Functions.Like(s.Name, SearchText.Contains(text), SearchText.Escape));
        return Ordered(await Rows(db, stories).ToListAsync(ct)).Take(PickLimit).ToList();
    }

    /// <summary>One story as the lists show it, or null when it's gone (the story an article is in, for its picker).</summary>
    public async Task<StoryRow?> RowAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await Rows(db, db.Stories.AsNoTracking().Where(s => s.Id == id)).FirstOrDefaultAsync(ct);
    }

    private static IQueryable<StoryRow> Rows(IAppDbContext db, IQueryable<Story> stories) =>
        stories.Select(s => new StoryRow(s.Id, s.Name, s.Status, db.Articles.Count(a => a.StoryId == s.Id),
            db.Articles.Where(a => a.StoryId == s.Id).Max(a => (DateTimeOffset?)a.CreatedAtUtc)));

    // Open before closed, then the story with the newest article first (read here: SQLite can't order a DateTimeOffset).
    private static List<StoryRow> Ordered(IEnumerable<StoryRow> rows) =>
        rows.OrderBy(r => r.Status).ThenByDescending(r => r.LastArticleAtUtc ?? DateTimeOffset.MinValue).ThenBy(r => r.Name).ToList();

    public async Task<StoryDetail?> GetAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var story = await db.Stories.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (story is null) return null;
        var articles = await db.Articles.AsNoTracking().Where(a => a.StoryId == id)
            .Include(a => a.Cves).Include(a => a.Techniques).AsSplitQuery().ToListAsync(ct);
        articles = articles.OrderBy(a => a.PublishedAtUtc ?? a.CreatedAtUtc).ToList();
        var ids = articles.Select(a => a.Id).ToList();
        var briefed = await ArticleDates.FirstBriefedAsync(db, ct, ids);
        // The actors: each article's adversary tags, by name, and what its earlier free text names that no tag stands for.
        var actors = await Adversaries.AdversaryService.ActorNamesAsync(db, articles.Select(a => (a.Id, a.Actors)).ToList(), ct);
        return new StoryDetail(story, articles,
            articles.SelectMany(a => a.Cves.Select(c => c.CveId)).Distinct().Order().ToList(),
            articles.SelectMany(a => actors[a.Id])
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            articles.SelectMany(a => a.Techniques.Select(t => t.TechniqueId)).Distinct().Order().ToList(),
            briefed);
    }

    public async Task<Guid> CreateAsync(string name, string? summary, CancellationToken ct = default)
    {
        RequireWrite();
        var story = new Story { Name = Name(name), Summary = ArticleService.CleanOrNull(summary, 4000, multiline: true) };
        await using var db = await dbs.CreateAsync(ct);
        db.Stories.Add(story);
        await db.SaveChangesAsync(ct);
        return story.Id;
    }

    /// <summary>Saves a story's form. <paramref name="editVersion"/> is the story's <see cref="Story.EditVersion"/> when the
    /// form opened: a save over a colleague's since is refused, rather than putting back what they changed.</summary>
    public async Task UpdateAsync(Guid id, string name, string? summary, StoryStatus status, Guid editVersion, CancellationToken ct = default)
    {
        RequireWrite();
        if (!Enum.IsDefined(status)) throw new ArgumentException("Choose a status.");
        await using var db = await dbs.CreateAsync(ct);
        var story = await db.Stories.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new KeyNotFoundException("That story no longer exists.");
        if (story.EditVersion != editVersion) throw new StaleEditException(Stale(story));
        story.Name = Name(name);
        story.Summary = ArticleService.CleanOrNull(summary, 4000, multiline: true);
        story.Status = status;
        story.EditVersion = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new StaleEditException(Stale(story)); }
    }

    private static string Stale(Story s) =>
        $"Someone else saved {s.Name} since you opened it. Reload to see their changes, then make yours again.";

    /// <summary>Links an article to a story, or (null) takes it off its story. Returns the name of the other story it was
    /// on until now, if any, as read in the same transaction (what a picker showed may be out of date).</summary>
    public async Task<string?> LinkAsync(Guid articleId, Guid? storyId, CancellationToken ct = default)
    {
        RequireWrite();
        string? left = null;
        await Serialized(async db =>
        {
            var article = await db.Articles.FirstOrDefaultAsync(a => a.Id == articleId, ct) ?? throw new KeyNotFoundException("That article no longer exists.");
            if (storyId is { } sid && !await db.Stories.AnyAsync(s => s.Id == sid, ct)) throw new KeyNotFoundException("That story no longer exists.");
            left = article.StoryId is { } was && was != storyId ? await db.Stories.Where(s => s.Id == was).Select(s => s.Name).FirstOrDefaultAsync(ct) : null;
            // A link isn't an edit of the article's content, so an editor open on it doesn't go stale.
            article.StoryId = storyId;
        }, ct);
        return left;
    }

    /// <summary>Deletes a story with no articles (one with articles is closed instead).</summary>
    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        RequireWrite();
        return Serialized(async db =>
        {
            var story = await db.Stories.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new KeyNotFoundException("That story no longer exists.");
            if (await db.Articles.AnyAsync(a => a.StoryId == id, ct)) throw new InvalidOperationException("A story with articles is closed, not deleted.");
            db.Stories.Remove(story);
        }, ct);
    }

    // The check and the save in one serializable transaction (SerializedAsync): an article linked to a story as it was
    // deleted, after the delete found none, lost its link to the foreign key (set to null), unsaid. Now the one waits for
    // the other and checks again: the delete refuses a story that has an article, and a link to a story gone is refused.
    private Task Serialized(Func<IAppDbContext, Task> change, CancellationToken ct) => dbs.SerializedAsync(change, ct);

    private static string Name(string? name)
    {
        var t = ArticleService.Clean(name, 200);
        return t.Length == 0 ? throw new ArgumentException("A story needs a name.") : t;
    }

    private void RequireRead()
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("You don't have access to stories.");
    }

    private void RequireWrite()
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts can change stories.");
    }
}
