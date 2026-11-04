using Microsoft.EntityFrameworkCore;

namespace Tide.Application.Abstractions;

/// <summary>When articles were briefed, as the histories and reports count them (no permission check).</summary>
internal static class ArticleDates
{
    /// <summary>The day each article was first on a brief, for those that have been: every article's, or only those in
    /// <paramref name="articleIds"/>.</summary>
    public static async Task<Dictionary<Guid, DateOnly>> FirstBriefedAsync(IAppDbContext db, CancellationToken ct,
        IReadOnlyCollection<Guid>? articleIds = null)
    {
        var items = db.BriefItems.AsNoTracking();
        if (articleIds is not null) items = items.Where(i => articleIds.Contains(i.ArticleId));
        return (await items.Join(db.Briefs, i => i.BriefId, b => b.Id, (i, b) => new { i.ArticleId, b.BriefDate }).ToListAsync(ct))
            .GroupBy(x => x.ArticleId).ToDictionary(g => g.Key, g => g.Min(x => x.BriefDate));
    }
}
