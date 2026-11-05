using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Content;

namespace Tide.Application.Search;

/// <summary>Why the article editor's adversary or "We run" picker lists something.</summary>
public enum PickReason
{
    /// <summary>The article's text names it (with nothing typed).</summary>
    Named,
    /// <summary>It's watched (with nothing typed): adversary profiles only.</summary>
    Watched,
    /// <summary>It was tagged on an article lately (with nothing typed).</summary>
    Recent,
    /// <summary>One of its names (or a profile's MITRE id) holds what was typed.</summary>
    Match,
}

/// <summary>
/// Something the adversary or "We run" picker lists: <see cref="Detail"/> is shown beside it (a profile's MITRE id, a
/// product's versions); <see cref="Other"/> is the other name that matched what was typed, when its own name didn't
/// ("APT29 (also Cozy Bear)"); a retired one is listed last and asked about before it's tagged.
/// </summary>
public sealed record TagPick(Guid Id, string Name, string? Detail, bool IsRetired, string? Other, PickReason Reason);

/// <summary>
/// What a picker lists for the words typed. <see cref="Exact"/>: one in use goes by exactly that name (or MITRE id), so
/// there's no "Add it"; <see cref="AlreadyTagged"/> names the one the article already has, when that's it.
/// </summary>
public sealed record TagPicks(IReadOnlyList<TagPick> Found, bool Exact, string? AlreadyTagged)
{
    public static readonly TagPicks None = new([], false, null);
}

/// <summary>What the editor's tag cards (Threat actors, We run) do to an article's tags, alike for profiles and products.</summary>
public interface IArticleTagSource
{
    /// <summary>What the picker lists for <paramref name="words"/> (<see cref="NamedSearch.FindAsync"/>).</summary>
    Task<TagPicks> FindForArticleAsync(Guid articleId, string? words, CancellationToken ct = default);

    Task TagArticleAsync(Guid articleId, Guid id, CancellationToken ct = default);

    Task UntagArticleAsync(Guid articleId, Guid id, CancellationToken ct = default);

    /// <summary>Adds one by name alone, or finds it if it's there; its id.</summary>
    Task<Guid> AddByNameAsync(string name, CancellationToken ct = default);
}

/// <summary>
/// The fields of something tagged on articles (an adversary profile, a product) that its picker matches: its id, name,
/// other names (kept "one; two"), an optional code (a MITRE id), whether it's retired, and what the list shows beside it.
/// </summary>
public sealed class NamedFields<T>(
    Expression<Func<T, Guid>> id,
    Expression<Func<T, string>> name,
    Expression<Func<T, string?>> others,
    Expression<Func<T, bool>> isRetired,
    Func<T, string?> detail,
    Expression<Func<T, string?>>? code = null)
{
    internal Expression<Func<T, Guid>> IdOf { get; } = id;
    internal Expression<Func<T, string>> NameOf { get; } = name;
    internal Expression<Func<T, string?>> OthersOf { get; } = others;
    internal Expression<Func<T, bool>> RetiredOf { get; } = isRetired;
    internal Expression<Func<T, string?>>? CodeOf { get; } = code;

    internal Func<T, Guid> Id { get; } = id.Compile();
    internal Func<T, string> Name { get; } = name.Compile();
    internal Func<T, bool> IsRetired { get; } = isRetired.Compile();
    internal Func<T, string?> Detail { get; } = detail;
    private readonly Func<T, string?> _others = others.Compile();
    private readonly Func<T, string?>? _code = code?.Compile();

    /// <summary>How well one matches what was typed (<see cref="NameRank.Best"/>), and the other name that did when
    /// its own name and code matched less well.</summary>
    public (int Rank, string? Other) Match(T item, string text) =>
        NameRank.Best(text, Name(item), Stack.StackService.SplitNames(_others(item)), _code?.Invoke(item));

    /// <summary>One as the picker lists it.</summary>
    internal TagPick Pick(T item, PickReason why, string? other = null) => new(Id(item), Name(item), Detail(item), IsRetired(item), other, why);
}

/// <summary>
/// The adversary and "We run" pickers' search, alike for both: what's typed, matched against a name, other names and an
/// optional code in the database, a bounded few read and then ranked here by <see cref="NameRank"/>.
/// </summary>
public static class NamedSearch
{
    /// <summary>The most a picker lists at once.</summary>
    public const int PickLimit = 8;

    // How many a typed search reads from the database before ordering them: the best matches by the database's own
    // reckoning (exact, then a name starting with it), so however long the list is, only these are read.
    internal const int PickCandidates = 50;

    /// <summary>What's typed, trimmed and cut to <paramref name="max"/>; null when nothing is.</summary>
    public static string? Text(string? words, int max)
    {
        var text = (words ?? "").Trim();
        if (text.Length > max) text = text[..max];
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// What the picker lists for <paramref name="text"/> (from <see cref="Text"/>), at most <see cref="PickLimit"/>: any
    /// in <paramref name="all"/> whose name, other names or code holds it, ignoring case; exact matches first, then those
    /// starting with it, then the rest, retired ones last. Those in <paramref name="tagged"/> (the article's) aren't
    /// listed, and say whether it's one of them that goes by exactly that.
    /// </summary>
    public static async Task<TagPicks> FindAsync<T>(IQueryable<T> all, IReadOnlyList<T> tagged, NamedFields<T> f, string text, CancellationToken ct)
        where T : class
    {
        var taggedIds = tagged.Select(f.Id).ToList();
        var m = NameRank.Patterns.For(text);
        var x = Expression.Parameter(typeof(T), "x");
        Expression Field<TField>(Expression<Func<T, TField>> selector) => new Swap(selector.Parameters[0], x).Visit(selector.Body);
        var name = Field(f.NameOf);
        var others = Field(f.OthersOf);
        var code = f.CodeOf is null ? null : Field(f.CodeOf);
        Expression AnyOf(params Expression?[] likes) => likes.OfType<Expression>().Aggregate(Expression.OrElse);

        var holds = AnyOf(Like(name, m.Any), Like(others, m.Any), code is null ? null : Like(code, m.Any));
        var exact = AnyOf(Like(name, m.Exact), code is null ? null : Like(code, m.Exact), Like(others, m.Exact), Like(others, m.OtherFirst),
            Like(others, m.OtherLast), Like(others, m.OtherMiddle));
        var starts = AnyOf(Like(name, m.Starts), Like(others, m.Starts), Like(others, m.OtherStarts), code is null ? null : Like(code, m.Starts));
        var untagged = Expression.Not(Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(Guid)],
            Expression.Property(Expression.Constant(new Box<List<Guid>>(taggedIds)), nameof(Box<List<Guid>>.Value)), Field(f.IdOf)));
        var tier = Expression.Condition(exact, Expression.Constant(0), Expression.Condition(starts, Expression.Constant(1), Expression.Constant(2)));

        var candidates = await all
            .Where(Expression.Lambda<Func<T, bool>>(untagged, x))
            .Where(Expression.Lambda<Func<T, bool>>(holds, x))
            .OrderBy(Expression.Lambda<Func<T, bool>>(Field(f.RetiredOf), x))
            .ThenBy(Expression.Lambda<Func<T, int>>(tier, x))
            .ThenBy(Expression.Lambda<Func<T, string>>(name, x))
            .Take(PickCandidates).ToListAsync(ct);
        var matched = candidates.Select(c => (Item: c, Match: f.Match(c, text))).Where(c => c.Match.Rank < NameRank.None).ToList();
        var found = matched.OrderBy(c => f.IsRetired(c.Item)).ThenBy(c => c.Match.Rank).ThenByName(c => f.Name(c.Item)).Take(PickLimit)
            .Select(c => f.Pick(c.Item, PickReason.Match, c.Match.Other))
            .ToList();
        var already = tagged.FirstOrDefault(a => !f.IsRetired(a) && f.Match(a, text).Rank == NameRank.Exact) is { } one ? f.Name(one) : null;
        var isExact = already is not null || matched.Exists(c => !f.IsRetired(c.Item) && c.Match.Rank == NameRank.Exact);
        return new TagPicks(found, isExact, already);
    }

    // EF.Functions.Like(field, pattern, "\"), the pattern a query parameter as a captured value would be.
    private static MethodCallExpression Like(Expression field, string pattern) =>
        Expression.Call(LikeMethod, Expression.Property(null, typeof(EF), nameof(EF.Functions)), field,
            Expression.Property(Expression.Constant(new Box<string>(pattern)), nameof(Box<string>.Value)), Expression.Constant(SearchText.Escape));

    private static readonly System.Reflection.MethodInfo LikeMethod = typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like),
        [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

    private sealed record Box<TValue>(TValue Value);

    private sealed class Swap(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    /// <summary>
    /// What a picker offers before anything's typed, gathered in order, at most <see cref="PickLimit"/>: each once, and
    /// never one the article already has.
    /// </summary>
    internal sealed class Offers(IEnumerable<Guid> tagged)
    {
        private readonly HashSet<Guid> _skip = [.. tagged];

        public List<TagPick> Picks { get; } = [];

        public bool Full => Picks.Count >= PickLimit;

        public void Add<T>(IEnumerable<T> items, NamedFields<T> f, PickReason why)
        {
            foreach (var item in items)
            {
                if (Full) return;
                if (_skip.Add(f.Id(item))) Picks.Add(f.Pick(item, why) with { IsRetired = false });
            }
        }
    }
}
