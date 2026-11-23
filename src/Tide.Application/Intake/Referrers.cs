using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Tide.Application.Abstractions;

namespace Tide.Application.Intake;

/// <summary>
/// How many rows of each table point at one row (an article, a brief), read from the model's foreign keys rather than
/// from a list kept by hand, so a table added later is counted without anyone remembering to. Soft-deleted rows count:
/// query filters are ignored. Undo of a promote compares these with what the promote itself left, so it never deletes
/// work done on the draft since (a tag, a link, an AI draft, a comment), whatever table that work went into.
/// </summary>
internal static class Referrers
{
    /// <summary>The count for each foreign key to <typeparamref name="T"/>, keyed "Dependent.Property". Throws for a
    /// foreign key it can't count (several columns, an owned type, or a key other than the primary one): an undo that
    /// can't see every table must not delete.</summary>
    public static async Task<IReadOnlyDictionary<string, int>> CountAsync<T>(IAppDbContext db, Guid id, CancellationToken ct)
    {
        var ctx = db.ChangeTracker.Context;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var fk in Keys<T>(ctx.Model))
        {
            var prop = fk.Properties[0];
            var count = (Task<int>)CountOf.MakeGenericMethod(fk.DeclaringEntityType.ClrType)
                .Invoke(null, [ctx, prop.Name, prop.ClrType, id, ct])!;
            counts[Name(fk)] = await count;
        }
        return counts;
    }

    /// <summary>Every foreign key to <typeparamref name="T"/> in <paramref name="model"/>, each checked as countable.</summary>
    public static IReadOnlyList<IForeignKey> Keys<T>(IModel model)
    {
        var principal = model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} isn't in the model.");
        var keys = principal.GetReferencingForeignKeys().ToList();
        foreach (var fk in keys)
        {
            if (fk.Properties.Count != 1 || fk.DeclaringEntityType.IsOwned() || !fk.PrincipalKey.IsPrimaryKey() || fk.DeclaringEntityType.HasSharedClrType)
                throw new InvalidOperationException($"{Name(fk)} points at {typeof(T).Name} in a way the undo can't count.");
        }
        return keys;
    }

    /// <summary>"ArticleTag.ArticleId": a foreign key's name in the counts.</summary>
    public static string Name(IForeignKey fk) => $"{fk.DeclaringEntityType.ClrType.Name}.{string.Join("+", fk.Properties.Select(p => p.Name))}";

    private static readonly MethodInfo CountOf = typeof(Referrers).GetMethod(nameof(Count), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Task<int> Count<TDependent>(DbContext ctx, string property, Type type, Guid id, CancellationToken ct) where TDependent : class
    {
        // e => EF.Property<type>(e, property) == id, for a Guid or Guid? column, shadow or not.
        var e = Expression.Parameter(typeof(TDependent), "e");
        var column = Expression.Call(typeof(EF), nameof(EF.Property), [type], e, Expression.Constant(property));
        var match = Expression.Lambda<Func<TDependent, bool>>(Expression.Equal(column, Expression.Convert(Expression.Constant(id), type)), e);
        return ctx.Set<TDependent>().IgnoreQueryFilters().CountAsync(match, ct);
    }
}
