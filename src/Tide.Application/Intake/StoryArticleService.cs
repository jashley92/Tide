using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Search;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.Application.Intake;

/// <summary>
/// What a new article on a story starts with, at a marking: the CVEs, live scorecard tags and products on Our stack (in
/// use) that the story's articles at or below that marking name. <paramref name="LeftOut"/> is how many of its articles
/// sit above the marking, whose CVEs, tags and products aren't copied unless another article at or below it names them too.
/// </summary>
public sealed record StoryPrefill(IReadOnlyList<string> Cves, IReadOnlyList<Guid> TagIds, IReadOnlyList<Guid> ProductIds, int LeftOut);

/// <summary>An article a story's page offers to add to it: one not on that story, and the story it's on now, if any.</summary>
public sealed record StoryCandidate(Guid Id, string Title, TlpLevel Tlp, ArticleStatus Status, DateTimeOffset CreatedAtUtc, string? OnStory);

/// <summary>
/// A story's page's two ways to add an article: start a new one on the story, prefilled from what its articles share, or
/// add an existing one. Each goes through the article and story services' own saves (their permission checks, edit
/// versions and audit), so nothing here writes the database itself. Analysts only.
/// </summary>
public sealed class StoryArticleService(IAppDbContextFactory dbs, ICurrentUser user, ArticleService articles, StoryService stories,
    Stack.StackService stack)
{
    /// <summary>
    /// What a new article at <paramref name="tlp"/> would start with. Only the story's articles at or below that marking
    /// count: the new article must never carry, even as a CVE or a tag, what only an article above its own marking says.
    /// Threat actors and ATT&amp;CK techniques are never copied.
    /// </summary>
    public async Task<StoryPrefill> PrefillAsync(Guid storyId, TlpLevel tlp, CancellationToken ct = default)
    {
        RequireWrite();
        tlp = Tlp.Checked(tlp);
        await using var db = await dbs.CreateAsync(ct);
        if (!await db.Stories.AnyAsync(s => s.Id == storyId, ct)) throw new KeyNotFoundException("That story no longer exists.");
        var onStory = db.Articles.AsNoTracking().Where(a => a.StoryId == storyId);
        var within = onStory.Where(a => a.Tlp <= tlp);
        var cves = await db.ArticleCves.AsNoTracking().Where(c => within.Any(a => a.Id == c.ArticleId))
            .Select(c => c.CveId).Distinct().ToListAsync(ct);
        var tags = await db.ArticleTags.AsNoTracking()
            .Where(t => within.Any(a => a.Id == t.ArticleId) && !t.Tag!.IsRetired && !t.Tag.Category!.IsRetired)
            .Select(t => t.TagId).Distinct().ToListAsync(ct);
        var products = await db.ArticleProducts.AsNoTracking()
            .Where(p => within.Any(a => a.Id == p.ArticleId) && !p.Product!.IsRetired)
            .Select(p => p.ProductId).Distinct().ToListAsync(ct);
        var leftOut = await onStory.CountAsync(a => a.Tlp > tlp, ct);
        return new StoryPrefill(cves.Order(StringComparer.Ordinal).ToList(), tags, products, leftOut);
    }

    /// <summary>
    /// Starts a draft on the story: created at <paramref name="tlp"/>, linked to the story, then given
    /// <see cref="PrefillAsync"/>'s CVEs and tags through the editor's own save and its products through Our stack's
    /// tagging. It isn't put on a brief. Returns the new article's id.
    /// </summary>
    public async Task<Guid> NewArticleAsync(Guid storyId, string title, TlpLevel tlp, CancellationToken ct = default)
    {
        var prefill = await PrefillAsync(storyId, tlp, ct);
        var id = await articles.CreateAsync(title, tlp: tlp, ct: ct);
        await stories.LinkAsync(id, storyId, ct);
        if (prefill.Cves.Count > 0 || prefill.TagIds.Count > 0)
        {
            var form = ArticleForm.From(await articles.GetAsync(id, ct) ?? throw new KeyNotFoundException("That article no longer exists."));
            form.Cves = prefill.Cves.ToList();
            form.TagIds = prefill.TagIds.ToHashSet();
            await articles.SaveAsync(form, ct);
        }
        foreach (var product in prefill.ProductIds) await stack.TagArticleAsync(id, product, ct);
        return id;
    }

    /// <summary>
    /// The articles the story's page offers to add: every article the person may read that isn't on this story (one on
    /// another story moves), newest first, at most <see cref="StoryService.PickLimit"/>. Words narrow them to titles,
    /// bottom lines and CVEs that hold every word.
    /// </summary>
    public async Task<IReadOnlyList<StoryCandidate>> CandidatesAsync(Guid storyId, string? words, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var q = db.Articles.AsNoTracking().Where(a => a.StoryId == null || a.StoryId != storyId);
        foreach (var word in (words ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = SearchText.Contains(word);
            q = q.Where(a => EF.Functions.Like(a.Title, p, SearchText.Escape) || EF.Functions.Like(a.Bluf, p, SearchText.Escape)
                             || a.Cves.Any(c => EF.Functions.Like(c.CveId, p, SearchText.Escape)));
        }
        return await q.OrderByDescending(a => a.CreatedAtUtc).ThenBy(a => a.Id).Take(StoryService.PickLimit)
            .Select(a => new StoryCandidate(a.Id, a.Title, a.Tlp, a.Status, a.CreatedAtUtc,
                db.Stories.Where(s => s.Id == a.StoryId).Select(s => s.Name).FirstOrDefault()))
            .ToListAsync(ct);
    }

    // Reading the team edition comes with writing articles, so who may add one may read every article the picker lists.
    private void RequireWrite()
    {
        if (!user.Has(Permission.WriteArticles) || !user.Has(Permission.ReadTeamEdition))
            throw new ForbiddenException("Only analysts can add articles to a story.");
    }
}
