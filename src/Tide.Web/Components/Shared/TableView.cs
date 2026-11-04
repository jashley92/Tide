using System.Globalization;

namespace Tide.Web.Components.Shared;

/// <summary>
/// A column of a <see cref="DataTable{TItem}"/>: its heading, what it sorts by (none: not sortable), and what the filter
/// box looks for in it (none: not searched).
/// </summary>
public sealed record TableColumn<TItem>(string Key, string Title)
{
    public Func<TItem, object?>? SortBy { get; init; }
    public Func<TItem, string?>? FilterBy { get; init; }

    /// <summary>Classes for the heading (<c>tide-table-num</c> for a number column).</summary>
    public string? Class { get; init; }

    /// <summary>The first click sorts largest or newest first, as a date or a count is usually wanted.</summary>
    public bool DescendingFirst { get; init; }

    public bool Sortable => SortBy is not null;
}

/// <summary>Which column a table is sorted by, and which way.</summary>
public readonly record struct TableSort(string Key, bool Descending);

/// <summary>What a server-side table asks its page for: the filter's words, the sort, and the page (from 1).</summary>
public sealed record TableQuery(string Filter, TableSort? Sort, int Page = 1);

/// <summary>
/// Rows under one group heading: those on this page, and how many of the matching rows are in the group, here or on
/// other pages.
/// </summary>
public sealed record TableGroup<TItem>(string Name, IReadOnlyList<TItem> Rows, int Total)
{
    public TableGroup(string name, IReadOnlyList<TItem> rows) : this(name, rows, rows.Count) { }

    /// <summary>How many of the group's rows are on other pages.</summary>
    public int Hidden => Total - Rows.Count;
}

/// <summary>
/// The filtering, sorting, grouping and counting behind <see cref="DataTable{TItem}"/>, kept out of the markup so it can
/// be tested on its own.
/// </summary>
public static class TableView
{
    /// <summary>The filter's words: every one must appear, in any searched column, ignoring case.</summary>
    public static IReadOnlyList<string> Words(string? filter) =>
        (filter ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static IReadOnlyList<TItem> Filter<TItem>(IEnumerable<TItem> items, IReadOnlyList<TableColumn<TItem>> columns, string? filter)
    {
        var words = Words(filter);
        var searched = columns.Where(c => c.FilterBy is not null).Select(c => c.FilterBy!).ToList();
        if (words.Count == 0 || searched.Count == 0) return items.ToList();
        return items.Where(item =>
        {
            var text = string.Join('\n', searched.Select(f => f(item)));
            return words.All(w => text.Contains(w, StringComparison.CurrentCultureIgnoreCase));
        }).ToList();
    }

    /// <summary>
    /// <paramref name="items"/> in <paramref name="sort"/>'s order, or as given when there's no sort (or its column
    /// doesn't sort). Stable, so rows that tie keep the order they came in; blanks go last either way.
    /// </summary>
    public static IReadOnlyList<TItem> Sort<TItem>(IEnumerable<TItem> items, IReadOnlyList<TableColumn<TItem>> columns, TableSort? sort)
    {
        if (sort is not { } s || columns.FirstOrDefault(c => c.Key == s.Key)?.SortBy is not { } key) return items.ToList();
        return items.OrderBy(key, Comparer<object?>.Create((a, b) => Compare(a, b, s.Descending))).ToList();
    }

    private static int Compare(object? a, object? b, bool descending)
    {
        if (a is null) return b is null ? 0 : 1;
        if (b is null) return -1;
        var order = a is string x && b is string y
            ? StringComparer.CurrentCultureIgnoreCase.Compare(x, y)
            : Comparer<object>.Default.Compare(a, b);
        return descending ? -order : order;
    }

    /// <summary>A click on <paramref name="column"/>'s heading: the other way when it's already the sort, else its first way.</summary>
    public static TableSort Toggle<TItem>(TableSort? current, TableColumn<TItem> column) =>
        current is { } c && c.Key == column.Key ? c with { Descending = !c.Descending } : new TableSort(column.Key, column.DescendingFirst);

    /// <summary>The heading's <c>aria-sort</c>: only the sorted column carries one.</summary>
    public static string? AriaSort(TableSort? sort, string key) =>
        sort is { } s && s.Key == key ? (s.Descending ? "descending" : "ascending") : null;

    /// <summary>
    /// <paramref name="rows"/> under their group's heading: the groups named in <paramref name="order"/> first, in that
    /// order, then any others as they first appear. Empty groups are left out.
    /// </summary>
    public static IReadOnlyList<TableGroup<TItem>> Group<TItem>(IReadOnlyList<TItem> rows, Func<TItem, string> groupBy, IReadOnlyList<string>? order = null) =>
        Group(rows, rows, groupBy, order);

    /// <summary>
    /// The rows on this page (<paramref name="shown"/>, some of <paramref name="matching"/>) under their group's heading,
    /// each group counting every matching row in it, so a count doesn't change from page to page. A group with matching
    /// rows keeps its heading even when none of them is on this page; a group with none is left out. Groups are ordered as
    /// <see cref="Group{TItem}(IReadOnlyList{TItem}, Func{TItem, string}, IReadOnlyList{string}?)"/> orders them, over
    /// all the matching rows.
    /// </summary>
    public static IReadOnlyList<TableGroup<TItem>> Group<TItem>(IReadOnlyList<TItem> shown, IReadOnlyList<TItem> matching, Func<TItem, string> groupBy, IReadOnlyList<string>? order = null)
    {
        var groups = matching.GroupBy(groupBy).ToList();
        var rows = shown.GroupBy(groupBy).ToDictionary(g => g.Key, g => (IReadOnlyList<TItem>)g.ToList());
        var rank = (order ?? []).Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
        return groups.Select((g, seen) => (g, at: rank.TryGetValue(g.Key, out var r) ? r : rank.Count + seen))
            .OrderBy(x => x.at)
            .Select(x => new TableGroup<TItem>(x.g.Key, rows.GetValueOrDefault(x.g.Key, []), x.g.Count())).ToList();
    }

    /// <summary>
    /// The count over a table: which rows this page shows (<paramref name="first"/>, counting from 1, and how many,
    /// <paramref name="shown"/>), of how many match the filter, of how many there are (null when only the server knows,
    /// as when it does the filtering). "1–100 of 235 profiles"; on one page, as before: "12 articles", "Showing 8 of 12".
    /// </summary>
    public static string Count(int first, int shown, int matching, int? all, bool filtered, string one, string many)
    {
        string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        string Of(int n) => n == 1 ? one : many;
        if (shown >= matching)
        {
            if (!filtered) return $"{N(matching)} {Of(matching)}";
            return all is { } total ? $"Showing {N(matching)} of {N(total)} {Of(total)}" : $"{N(matching)} matching {Of(matching)}";
        }
        var range = shown == 0 ? $"0 of {N(matching)}" : $"{N(first)}–{N(first + shown - 1)} of {N(matching)}";
        if (!filtered) return $"{range} {many}";
        return all is { } count ? $"{range} matching, {N(count)} {many} in all" : $"{range} matching {many}";
    }

    /// <summary>The most a page number in an address is taken as: beyond any real list, and small enough that the rows
    /// skipped to reach it can't overflow.</summary>
    public const int MaxPage = 100_000;

    /// <summary>The page an address asks for (?page=3): a whole number from 1, else the first; capped at <see cref="MaxPage"/>.</summary>
    public static int PageFrom(string? text) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
            ? Math.Min(n, MaxPage) : 1;

    /// <summary>
    /// Back to the first page, as a new filter starts: drops ?page= from the address (in place, not a step for Back), when
    /// it's there. A page whose own filter changes outside the table (a team, a switch) calls it.
    /// </summary>
    public static void FirstPage(Microsoft.AspNetCore.Components.NavigationManager nav, string parameter = "page")
    {
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(nav.Uri).Query);
        if (query.ContainsKey(parameter))
            nav.NavigateTo(Microsoft.AspNetCore.Components.NavigationManagerExtensions.GetUriWithQueryParameter(nav, parameter, (int?)null), replace: true);
    }

    /// <summary>How many pages <paramref name="rows"/> make at <paramref name="size"/> a page (at least one).</summary>
    public static int Pages(int rows, int size) => Math.Max(1, (rows + size - 1) / Math.Max(1, size));

    /// <summary>
    /// The page numbers a pager shows for <paramref name="page"/> of <paramref name="pages"/>: every one up to seven, else the
    /// first, the last, and the page with its neighbours, a null standing for the gap between ("1 … 4 5 6 … 12").
    /// </summary>
    public static IReadOnlyList<int?> PageNumbers(int page, int pages)
    {
        if (pages <= 7) return Enumerable.Range(1, pages).Select(n => (int?)n).ToList();
        page = Math.Clamp(page, 1, pages);
        // Near an end, five in a row there, so the pager keeps its width: 1 2 3 4 5 … 12, 1 … 8 9 10 11 12.
        var (from, to) = page <= 4 ? (2, 5) : page >= pages - 3 ? (pages - 4, pages - 1) : (page - 1, page + 1);
        var numbers = new List<int?> { 1 };
        if (from > 2) numbers.Add(null);
        for (var n = from; n <= to; n++) numbers.Add(n);
        if (to < pages - 1) numbers.Add(null);
        numbers.Add(pages);
        return numbers;
    }
}
