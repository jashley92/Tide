using System.Linq.Expressions;

namespace Tide.Application.Content;

/// <summary>
/// Lists of names in the order people read them: ignoring case, as SQL Server's collation sorts them. SQLite's own order
/// is case-sensitive, which put "alpha" after "Zeta". In a query the lower-casing runs in the database (lower(),
/// LOWER()), never in .NET, so no culture applies; the exact name then breaks a tie. A list sorted in memory (one read
/// for other reasons) is put in the same order, so a name sits in the same place wherever it's shown.
/// </summary>
public static class NameOrder
{
    private static readonly System.Reflection.MethodInfo ToLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

    public static IOrderedQueryable<T> OrderByName<T>(this IQueryable<T> query, Expression<Func<T, string>> name) =>
        query.OrderBy(Lowered(name)).ThenBy(name);

    public static IOrderedQueryable<T> ThenByName<T>(this IOrderedQueryable<T> query, Expression<Func<T, string>> name) =>
        query.ThenBy(Lowered(name)).ThenBy(name);

    public static IOrderedEnumerable<T> OrderByName<T>(this IEnumerable<T> items, Func<T, string> name) =>
        items.OrderBy(x => name(x).ToLowerInvariant(), StringComparer.Ordinal).ThenBy(name, StringComparer.Ordinal);

    public static IOrderedEnumerable<T> ThenByName<T>(this IOrderedEnumerable<T> items, Func<T, string> name) =>
        items.ThenBy(x => name(x).ToLowerInvariant(), StringComparer.Ordinal).ThenBy(name, StringComparer.Ordinal);

    private static Expression<Func<T, string>> Lowered<T>(Expression<Func<T, string>> name) =>
        Expression.Lambda<Func<T, string>>(Expression.Call(name.Body, ToLower), name.Parameters);
}
