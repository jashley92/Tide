namespace Tide.Web.Components.Shared;

/// <summary>Where J or K took the selection: to another row of this page, or off its end to the next or previous page.</summary>
public enum RowMove { Within, NextPage, PreviousPage }

/// <summary>
/// A list page's selected row: the one row that is highlighted, <c>aria-current</c> and the list's tab stop, named in
/// the address by its place (<c>?row=</c>). The page keeps the place as a number of its own; this is how it is read,
/// moved and drawn, the same on every list. The keys and focus are <c>rows.js</c>'s (<c>tideRows</c>), wired by
/// <see cref="RowKeys"/>.
/// </summary>
public static class RowSelection
{
    /// <summary>The address parameter that names the selected row.</summary>
    public const string Param = "row";

    /// <summary>The row an address names: its place from 0. Nothing, or a value that isn't a place, is the first row. The
    /// value is only ever read as a number; a place past the end is brought back by <see cref="Clamp"/> once the rows
    /// are read.</summary>
    public static int From(string? row) => Math.Max(0, FilterValues.Number(row) ?? 0);

    /// <summary>The place as an address's parameter ("row=3"); null for the first row, which the address leaves out.</summary>
    public static string? Query(int selected) => selected > 0 ? $"{Param}={selected}" : null;

    /// <summary>The place held to the rows there are (0 for an empty list).</summary>
    public static int Clamp(int selected, int count) => Math.Clamp(selected, 0, Last(count));

    /// <summary>The last row's place (0 for an empty list).</summary>
    public static int Last(int count) => Math.Max(0, count - 1);

    /// <summary>
    /// J (<paramref name="next"/>) or K: the selection moves one row and stops at the ends of the list, except that J on
    /// a page's last row asks for the next page and K on its first for the previous one, so a paged list reads on as
    /// one. Then <paramref name="selected"/> is left for the page to set once it has read the other page: its first row
    /// after J, its last after K. A list of one page leaves <paramref name="page"/> and <paramref name="pages"/> out.
    /// </summary>
    public static RowMove Step(bool next, ref int selected, int count, int page = 1, int pages = 1)
    {
        if (next && selected >= count - 1 && page < pages) return RowMove.NextPage;
        if (!next && selected == 0 && page > 1) return RowMove.PreviousPage;
        selected = next ? Math.Min(selected + 1, Last(count)) : Math.Max(selected - 1, 0);
        return RowMove.Within;
    }

    /// <summary>The selected row's extra class, for <c>class="tide-x-row @RowSelection.Css(selected)"</c>.</summary>
    public static string Css(bool selected) => selected ? "selected" : "";

    /// <summary>The row's <c>aria-current</c>: "true" on the selected row, no attribute on the rest.</summary>
    public static string? Current(bool selected) => selected ? "true" : null;

    /// <summary>The row's <c>tabindex</c>: the selected row is the list's one tab stop (a roving tabindex).</summary>
    public static string TabIndex(bool selected) => selected ? "0" : "-1";
}
