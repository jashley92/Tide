using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Tide.Domain.Entities;

namespace Tide.Infrastructure.Persistence;

/// <summary>
/// Marks an article's search text (<see cref="ArticleSearchRow"/>) out of date in the same save that changes a part of
/// it: an article added, a searched field of one edited, or a bullet, indicator, CVE, technique or screenshot caption
/// added, edited or removed. A new article gets a marked row; so does one without a row (added outside the app). The
/// background rebuild (<c>ArticleSearchIndex</c>) builds it again; until then a search matches that article the old way,
/// so it finds the change at once. A bulk <c>ExecuteUpdate</c> or <c>ExecuteDelete</c> doesn't pass through here (a test
/// holds the code to that: <c>SearchIndexBypassTests</c>).
/// </summary>
public sealed class SearchDirtyInterceptor : SaveChangesInterceptor
{
    // The article's own fields a search reads.
    private static readonly string[] ArticleFields =
    [
        nameof(Article.Title), nameof(Article.Bluf), nameof(Article.WhyItMatters), nameof(Article.RecommendedActions),
        nameof(Article.Actors), nameof(Article.Sectors), nameof(Article.SourceName),
    ];

    // The articles' ids read at a time: the most parameters one query takes here.
    private const int Chunk = 500;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } ctx && Changed(ctx) is { } changed)
        {
            foreach (var chunk in Untracked(ctx, changed.Existing).Chunk(Chunk)) ctx.Set<ArticleSearchRow>().Where(r => chunk.Contains(r.ArticleId)).Load();
            Mark(ctx, changed);
        }
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } ctx && Changed(ctx) is { } changed)
        {
            foreach (var chunk in Untracked(ctx, changed.Existing).Chunk(Chunk))
                await ctx.Set<ArticleSearchRow>().Where(r => chunk.Contains(r.ArticleId)).LoadAsync(cancellationToken);
            Mark(ctx, changed);
        }
        return result;
    }

    // The articles this save adds, and those it changes a searched part of; null for none.
    private static (HashSet<Guid> Added, HashSet<Guid> Existing)? Changed(DbContext ctx)
    {
        ctx.ChangeTracker.DetectChanges();
        var (added, changed, removed) = (new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>());
        foreach (var e in ctx.ChangeTracker.Entries())
        {
            if (e.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            switch (e.Entity)
            {
                case Article a when e.State == EntityState.Added: added.Add(a.Id); break;
                case Article a when e.State == EntityState.Deleted: removed.Add(a.Id); break;
                case Article a when Edited(e, ArticleFields): changed.Add(a.Id); break;
                case ArticleBullet x when Counts(e, nameof(ArticleBullet.Text)): changed.Add(x.ArticleId); break;
                case ArticleIndicator x when Counts(e, nameof(ArticleIndicator.Value), nameof(ArticleIndicator.Note)): changed.Add(x.ArticleId); break;
                case ArticleScreenshot x when Counts(e, nameof(ArticleScreenshot.Caption)): changed.Add(x.ArticleId); break;
                case ArticleCve x: changed.Add(x.ArticleId); break;
                case ArticleTechnique x: changed.Add(x.ArticleId); break;
            }
        }
        // An article removed takes its row with it (a cascade); one added has none yet.
        changed.ExceptWith(removed);
        changed.ExceptWith(added);
        return added.Count == 0 && changed.Count == 0 ? null : (added, changed);
    }

    // The changed articles whose row this context isn't tracking yet: those are read into it.
    private static List<Guid> Untracked(DbContext ctx, HashSet<Guid> existing)
    {
        var local = ctx.Set<ArticleSearchRow>().Local;
        return existing.Where(id => local.FindEntry(id) is null).ToList();
    }

    // Marks each article's row with a new stamp; one with no row (a new article, or one added outside the app) gets one.
    private static void Mark(DbContext ctx, (HashSet<Guid> Added, HashSet<Guid> Existing) changed)
    {
        var rows = ctx.Set<ArticleSearchRow>();
        foreach (var id in changed.Added.Concat(changed.Existing))
        {
            if (rows.Local.FindEntry(id)?.Entity is { } row)
            {
                row.Dirty = true;
                row.Stamp = Guid.NewGuid();
            }
            else rows.Add(new ArticleSearchRow { ArticleId = id, Dirty = true, Stamp = Guid.NewGuid() });
        }
    }

    // Added or removed, or one of the fields a search reads edited.
    private static bool Counts(EntityEntry e, params string[] fields) =>
        e.State != EntityState.Modified || Edited(e, fields);

    private static bool Edited(EntityEntry e, IEnumerable<string> fields) =>
        fields.Any(f => e.Property(f) is { IsModified: true } p && !Equals(p.OriginalValue, p.CurrentValue));
}
