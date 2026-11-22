using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;

namespace Tide.Application.Search;

/// <summary>
/// Builds and rebuilds the articles' search text (<see cref="ArticleSearchRow"/>). <see cref="Text"/> is what a row
/// holds; <see cref="RebuildAsync"/> brings the rows that are out of date (marked by a save, missing, or built by an older
/// <see cref="Builder"/>) up to date a batch at a time, run by a background job. A rebuild is written only if no save
/// marked the row again while it was being built (its <see cref="ArticleSearchRow.Stamp"/> is unchanged), so a rebuild
/// never stores text older than the article.
/// </summary>
public static class ArticleSearchIndex
{
    /// <summary>
    /// The version of <see cref="Text"/>. Raise it when what's in the text changes: every row is then out of date, searched
    /// the old way until it's rebuilt, and rebuilt in the background.
    /// </summary>
    public const int Builder = 1;

    /// <summary>Articles rebuilt at a time.</summary>
    public const int Batch = 100;

    /// <summary>
    /// The text a search matches <paramref name="a"/> by: the same parts the per-part search reads, as stored, each
    /// apart from the next by <see cref="ArticleSearchRow.Separator"/>. Needs its bullets, indicators, CVEs, techniques
    /// and screenshots loaded.
    /// </summary>
    public static string Text(Article a)
    {
        IEnumerable<string?> parts =
        [
            a.Title, a.Bluf, a.WhyItMatters, a.RecommendedActions, a.Actors, a.Sectors, a.SourceName,
            .. a.Bullets.OrderBy(b => b.SortOrder).Select(b => b.Text),
            .. a.Indicators.OrderBy(i => i.Id).SelectMany(i => new[] { i.Value, i.Note }),
            .. a.Cves.Select(c => c.CveId).Order(StringComparer.Ordinal),
            .. a.Techniques.Select(t => t.TechniqueId).Order(StringComparer.Ordinal),
            .. a.Screenshots.OrderBy(s => s.SortOrder).Select(s => s.Caption),
        ];
        return string.Join(ArticleSearchRow.Separator, parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    /// <summary>
    /// The articles a search can't yet match by their row: none, out of date (marked by a save), or built by an older
    /// <see cref="Builder"/>. The same test the search's fallback makes.
    /// </summary>
    public static IQueryable<Article> Stale(IAppDbContext db) =>
        db.Articles.Where(a => a.SearchRow == null || a.SearchRow.Dirty || a.SearchRow.Builder != Builder);

    /// <summary>How many articles are left to build.</summary>
    public static Task<int> LeftAsync(IAppDbContext db, CancellationToken ct = default) => Stale(db).CountAsync(ct);

    /// <summary>
    /// Rebuilds up to <paramref name="batch"/> articles' rows: those a save marked first (a seek on the marked rows), then
    /// any with no row or an older builder's (after an upgrade) unless <paramref name="markedOnly"/>. Returns how many it
    /// built and whether any are left among those it looked at.
    /// </summary>
    public static async Task<(int Built, bool More)> RebuildAsync(IAppDbContext db, DateTimeOffset now, bool markedOnly, int batch = Batch, CancellationToken ct = default)
    {
        var ids = await db.ArticleSearch.AsNoTracking().Where(r => r.Dirty).OrderBy(r => r.ArticleId).Select(r => r.ArticleId).Take(batch).ToListAsync(ct);
        if (ids.Count < batch && !markedOnly)
            ids.AddRange(await db.Articles.AsNoTracking().Where(a => a.SearchRow == null || a.SearchRow.Builder != Builder)
                .Where(a => !ids.Contains(a.Id)).OrderBy(a => a.Id).Select(a => a.Id).Take(batch - ids.Count).ToListAsync(ct));
        if (ids.Count == 0) return (0, false);

        // The stamp is read before the parts: a save after this read marks the row again with a new stamp, so a rebuild
        // from parts older than that save isn't written.
        var stamps = await db.ArticleSearch.AsNoTracking().Where(r => ids.Contains(r.ArticleId)).ToDictionaryAsync(r => r.ArticleId, r => r.Stamp, ct);
        var articles = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Include(a => a.Bullets).Include(a => a.Indicators).Include(a => a.Cves).Include(a => a.Techniques).Include(a => a.Screenshots)
            .AsSplitQuery().ToListAsync(ct);
        var built = 0;
        foreach (var a in articles)
        {
            var text = Text(a);
            if (stamps.TryGetValue(a.Id, out var stamp))
                built += await db.ArticleSearch.Where(r => r.ArticleId == a.Id && r.Stamp == stamp).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Text, text).SetProperty(r => r.Dirty, false).SetProperty(r => r.Builder, Builder)
                    .SetProperty(r => r.BuiltAtUtc, now), ct);
            else
            {
                db.ArticleSearch.Add(new ArticleSearchRow { ArticleId = a.Id, Text = text, Builder = Builder, Stamp = Guid.NewGuid(), BuiltAtUtc = now });
                built++;
            }
        }
        if (built > 0) await AddedAsync(db, ct);
        return (built, true);
    }

    // Saves the rows added for articles that had none. A save of one of those articles at the same moment adds its own
    // (marked) row first; then this one fails as a duplicate, and the marked row is rebuilt on the next run.
    private static async Task AddedAsync(IAppDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); }
    }
}
