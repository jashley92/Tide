using System.Globalization;
using System.Linq.Expressions;

namespace Tide.Application.Content;

/// <summary>
/// Lists of names in the order people read them: ignoring case, as SQL Server's collation sorts them. SQLite's own order
/// is case-sensitive, which put "alpha" after "Zeta". In a query the lower-casing runs in the database (lower(),
/// LOWER()); on SQLite the app gives lower() every letter, not only A to Z, and orders what it returns by
/// <see cref="Compare"/>, so "Égout" sits beside "éclair" and before "Zebra" there too. The exact name then breaks a tie.
/// A list sorted in memory (one read for other reasons) is put in the same order, so a name sits in the same place
/// wherever it's shown.
/// </summary>
public static class NameOrder
{
    private static readonly System.Reflection.MethodInfo ToLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

    /// <summary>
    /// Text in reading order, ignoring case: accented letters beside their plain ones, as SQL Server's collation puts
    /// them, not after "z" where their code points would. Text that differs only in ways the culture overlooks falls back
    /// to code points, so only identical text compares equal.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        // Culture-aware on purpose: reading order is the point, and the ordinal fallback keeps it a total order.
#pragma warning disable CA1309
        var c = string.Compare(a, b, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
#pragma warning restore CA1309
        return c != 0 ? c : string.CompareOrdinal(a, b);
    }

    private static readonly IComparer<string> Reading = Comparer<string>.Create(Compare);

    public static IOrderedQueryable<T> OrderByName<T>(this IQueryable<T> query, Expression<Func<T, string>> name) =>
        query.OrderBy(Lowered(name)).ThenBy(name);

    public static IOrderedQueryable<T> ThenByName<T>(this IOrderedQueryable<T> query, Expression<Func<T, string>> name) =>
        query.ThenBy(Lowered(name)).ThenBy(name);

    public static IOrderedEnumerable<T> OrderByName<T>(this IEnumerable<T> items, Func<T, string> name) =>
        items.OrderBy(x => name(x).ToLowerInvariant(), Reading).ThenBy(name, StringComparer.Ordinal);

    public static IOrderedEnumerable<T> ThenByName<T>(this IOrderedEnumerable<T> items, Func<T, string> name) =>
        items.ThenBy(x => name(x).ToLowerInvariant(), Reading).ThenBy(name, StringComparer.Ordinal);

    private static Expression<Func<T, string>> Lowered<T>(Expression<Func<T, string>> name) =>
        Expression.Lambda<Func<T, string>>(Expression.Call(name.Body, ToLower), name.Parameters);
}
