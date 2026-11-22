using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;
using Tide.Domain.Observables;

namespace Tide.Application.Indicators;

/// <summary>
/// Gives the indicators saved before their lookup keys were kept (<see cref="ArticleIndicator.ValueKey"/>,
/// <see cref="ArticleIndicator.HostRev"/>, <see cref="ActionIndicator.ValueKey"/>) those keys. The key is a hash only the
/// app's own code computes, so the rows are read and written here, a batch at a time, each batch's keys written by one
/// UPDATE (<see cref="CaseUpdate"/>); no article's or action's modified time or history moves. Run by a background job
/// after start-up, so a large database doesn't hold up the start; each batch is committed as it goes, so a fill that
/// stops carries on where it got to.
/// <para>
/// A row with no <c>ValueKey</c> is one still to fill: every indicator has a value, so every keyed row has one, and the
/// column's own index finds those left. Until then a lookup also matches those rows the old way (by their value), so it
/// is complete throughout. A new install, or a database once filled, has none.
/// </para>
/// </summary>
public static class IndicatorKeyFill
{
    /// <summary>Rows read and written at a time: up to five parameters each in an UPDATE, well inside SQL Server's 2,100.</summary>
    public const int Batch = 200;

    /// <summary>Whether every stored indicator has its keys.</summary>
    public static async Task<bool> DoneAsync(IAppDbContext db, CancellationToken ct = default) =>
        !await Articles(db).AnyAsync(ct) && !await Actions(db).AnyAsync(ct);

    /// <summary>The indicators still without their keys.</summary>
    public static async Task<int> LeftAsync(IAppDbContext db, CancellationToken ct = default) =>
        await Articles(db).CountAsync(ct) + await Actions(db).CountAsync(ct);

    // Each table's rows without keys; an action's, deleted or not (it may be restored).
    private static IQueryable<ArticleIndicator> Articles(IAppDbContext db) => db.ArticleIndicators.Where(i => i.ValueKey == null);

    private static IQueryable<ActionIndicator> Actions(IAppDbContext db) =>
        db.ActionIndicators.IgnoreQueryFilters([ActionItem.DeletedFilter]).Where(i => i.ValueKey == null);

    /// <summary>
    /// Keys up to <paramref name="batches"/> batches of <paramref name="batch"/> rows, articles' first, then actions'.
    /// Returns how many rows it keyed, and whether none is left.
    /// </summary>
    public static async Task<(int Filled, bool Done)> FillAsync(IAppDbContext db, int batches, int batch = Batch, CancellationToken ct = default)
    {
        var filled = 0;
        for (var n = 0; n < batches; n++)
        {
            var articles = await Articles(db).AsNoTracking().OrderBy(i => i.Id).Take(batch).Select(i => new { i.Id, i.Type, i.Value }).ToListAsync(ct);
            if (articles.Count > 0)
            {
                await CaseUpdate.RunAsync(Articles(db), ArticleColumns,
                    articles.Select(i => (i.Id, new string?[] { IndicatorKey.ValueOf(i.Value), IndicatorKey.HostRevOf(i.Type, i.Value) })).ToList(), ct);
                filled += articles.Count;
                continue;
            }
            var actions = await Actions(db).AsNoTracking().OrderBy(i => i.Id).Take(batch).Select(i => new { i.Id, i.Value }).ToListAsync(ct);
            if (actions.Count == 0) return (filled, true);
            await CaseUpdate.RunAsync(Actions(db), ActionColumns, actions.Select(i => (i.Id, new string?[] { IndicatorKey.ValueOf(i.Value) })).ToList(), ct);
            filled += actions.Count;
        }
        return (filled, false);
    }

    private static readonly IReadOnlyList<Expression<Func<ArticleIndicator, string?>>> ArticleColumns = [i => i.ValueKey, i => i.HostRev];
    private static readonly IReadOnlyList<Expression<Func<ActionIndicator, string?>>> ActionColumns = [i => i.ValueKey];
}
