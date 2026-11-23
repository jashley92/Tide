using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Tide.Domain.Common;

namespace Tide.Application.Abstractions;

/// <summary>
/// Writes a value per row to many rows in one UPDATE: <c>SET col = CASE WHEN Id = @id1 THEN @v1 WHEN ... ELSE col END</c>
/// for each column, where the id is one of the batch's. For the background fills that compute a stored column in the
/// app's own code (a hash, a key) and write it back a batch at a time, without moving any row's modified time, edit
/// version or history (an UPDATE bypasses the interceptors).
/// </summary>
internal static class CaseUpdate
{
    /// <summary>
    /// Sets each of <paramref name="columns"/> on the rows in <paramref name="rows"/> (only those
    /// <paramref name="target"/> still matches) to that row's value at the column's position.
    /// </summary>
    public static Task<int> RunAsync<T>(IQueryable<T> target, IReadOnlyList<Expression<Func<T, string?>>> columns,
        IReadOnlyList<(Guid Id, string?[] Values)> rows, CancellationToken ct) where T : Entity
    {
        // Read from a captured object, as a lambda's closure is, not written in as constants: so each id and value is a
        // parameter and the statement is the same for every full batch (one cached query and plan, not one per batch).
        var batch = new Values(rows.Select(r => r.Id).ToArray(),
            Enumerable.Range(0, columns.Count).Select(c => rows.Select(r => r.Values[c]).ToArray()).ToArray());
        var ids = batch.Ids;
        var held = Expression.Constant(batch);
        var values = columns.Select((column, c) =>
        {
            var x = column.Parameters[0];
            var id = Expression.Property(x, nameof(Entity.Id));
            var of = Expression.ArrayIndex(Expression.Field(held, nameof(Values.Columns)), Expression.Constant(c));
            Expression value = column.Body;
            for (var i = rows.Count - 1; i >= 0; i--)
                value = Expression.Condition(
                    Expression.Equal(id, Expression.ArrayIndex(Expression.Field(held, nameof(Values.Ids)), Expression.Constant(i))),
                    Expression.ArrayIndex(of, Expression.Constant(i)), value);
            return Expression.Lambda<Func<T, string?>>(value, x);
        }).ToList();
        return target.Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(s =>
        {
            for (var c = 0; c < columns.Count; c++) s.SetProperty(columns[c], values[c]);
        }, ct);
    }

    private sealed class Values(Guid[] ids, string?[][] columns)
    {
        public readonly Guid[] Ids = ids;
        public readonly string?[][] Columns = columns;
    }
}
