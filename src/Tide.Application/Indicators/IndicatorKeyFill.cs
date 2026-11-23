using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Domain.Common;
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
        var (filled, articlesDone) = (0, false);
        for (var n = 0; n < batches; n++)
        {
            var keyed = articlesDone ? 0 : await StepAsync(Articles(db), ArticleColumns, i => [IndicatorKey.ValueOf(i.Value), IndicatorKey.HostRevOf(i.Type, i.Value)], batch, ct);
            articlesDone = keyed == 0;
            if (articlesDone) keyed = await StepAsync(Actions(db), ActionColumns, i => [IndicatorKey.ValueOf(i.Value)], batch, ct);
            if (keyed == 0) return (filled, true);
            filled += keyed;
        }
        return (filled, false);
    }

    // Keys the first batch of rows left in one table: reads them, then writes their keys in one UPDATE. How many it keyed.
    private static async Task<int> StepAsync<T>(IQueryable<T> left, IReadOnlyList<Expression<Func<T, string?>>> columns, Func<T, string?[]> keys,
        int batch, CancellationToken ct) where T : Entity
    {
        var rows = await left.AsNoTracking().OrderBy(i => i.Id).Take(batch).ToListAsync(ct);
        if (rows.Count > 0) await CaseUpdate.RunAsync(left, columns, rows.Select(i => (i.Id, keys(i))).ToList(), ct);
        return rows.Count;
    }

    private static readonly IReadOnlyList<Expression<Func<ArticleIndicator, string?>>> ArticleColumns = [i => i.ValueKey, i => i.HostRev];
    private static readonly IReadOnlyList<Expression<Func<ActionIndicator, string?>>> ActionColumns = [i => i.ValueKey];
}
