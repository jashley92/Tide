using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;

namespace Tide.Application.Search;

/// <summary>
/// Builds and rebuilds the articles' search text (<see cref="ArticleSearchRow"/>). <see cref="Text"/> is what a row
/// holds; <see cref="RebuildAsync"/> rebuilds the rows a save has marked, a batch at a time, run by a background job. A
/// rebuild is written only if no save marked the row again while it was being built (its
/// <see cref="ArticleSearchRow.Stamp"/> is unchanged), so a rebuild never stores text older than the article.
/// </summary>
public static class ArticleSearchIndex
{
    /// <summary>
    /// The version of <see cref="Text"/>. Raise it when what's in the text changes: every row is then out of date, searched
    /// the old way until it's rebuilt, and marked for the rebuild when the app starts (<see cref="MarkOlderBuildsAsync"/>).
    /// </summary>
    public const int Builder = 1;

    /// <summary>Articles rebuilt at a time.</summary>
    public const int Batch = 100;

    /// <summary>
    /// Whether an article's search text is current: it has a row, no save has marked it since it was built, and this
    /// <see cref="Builder"/> built it. A search matches the text of such an article, and every other article part by part.
    /// </summary>
    public static readonly Expression<Func<Article, bool>> Current = a =>
        a.SearchRow != null && !a.SearchRow.Dirty && a.SearchRow.Builder == Builder;

    /// <summary>Every article <see cref="Current"/> isn't: no row, marked by a save, or built by another builder. The search
    /// matches these part by part. Written out rather than negated, so each query reads as a plain filter.</summary>
    public static readonly Expression<Func<Article, bool>> Stale = a =>
        a.SearchRow == null || a.SearchRow.Dirty || a.SearchRow.Builder != Builder;

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

    /// <summary>How many rows are marked for a rebuild (a seek on the marked rows' index).</summary>
    public static Task<int> LeftAsync(IAppDbContext db, CancellationToken ct = default) => db.ArticleSearch.CountAsync(r => r.Dirty, ct);

    /// <summary>
    /// Marks every row an older <see cref="Builder"/> built, so the rebuild reaches them; once when the app starts, as only
    /// a new build changes the builder. Returns how many.
    /// </summary>
    public static Task<int> MarkOlderBuildsAsync(IAppDbContext db, CancellationToken ct = default) =>
        db.ArticleSearch.Where(r => r.Builder != Builder && !r.Dirty).ExecuteUpdateAsync(s => s.SetProperty(r => r.Dirty, true), ct);

    /// <summary>
    /// Rebuilds up to <paramref name="batch"/> marked rows. Returns how many it took: fewer than <paramref name="batch"/>
    /// means none was left. A row a save marked again meanwhile stays marked, for the next run.
    /// </summary>
    public static async Task<int> RebuildAsync(IAppDbContext db, int batch = Batch, CancellationToken ct = default)
    {
        // The stamp is read before the parts: a save after this read marks the row again with a new stamp, so a rebuild
        // from parts older than that save isn't written.
        var stamps = await db.ArticleSearch.AsNoTracking().Where(r => r.Dirty).OrderBy(r => r.ArticleId).Take(batch)
            .ToDictionaryAsync(r => r.ArticleId, r => r.Stamp, ct);
        if (stamps.Count == 0) return 0;
        var ids = stamps.Keys.ToList();
        var articles = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Include(a => a.Bullets).Include(a => a.Indicators).Include(a => a.Cves).Include(a => a.Techniques).Include(a => a.Screenshots)
            .AsSplitQuery().ToListAsync(ct);
        foreach (var a in articles)
        {
            var (text, stamp) = (Text(a), stamps[a.Id]);
            await db.ArticleSearch.Where(r => r.ArticleId == a.Id && r.Stamp == stamp).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Text, text).SetProperty(r => r.Dirty, false).SetProperty(r => r.Builder, Builder), ct);
        }
        return stamps.Count;
    }
}
