namespace Tide.Application.Abstractions;

/// <summary>
/// What the lists administrators keep (teams, metrics and their groups, scorecard categories and tags, the rotation's
/// roster, playbooks, card rules) have in common: a name and description on one line, a key made unique, and an order.
/// </summary>
internal static class AdminLists
{
    /// <summary>A name on one line, required, at most 100 characters.</summary>
    public static string Name(string? name)
    {
        var t = Content.MarkdownService.OneLine(name);
        if (t.Length == 0) throw new ArgumentException("A name is needed.");
        if (t.Length > 100) throw new ArgumentException("A name can be at most 100 characters.");
        return t;
    }

    /// <summary>A description on one line, at most 500 characters; null when blank.</summary>
    public static string? Description(string? d)
    {
        var t = Content.MarkdownService.OneLine(d);
        if (t.Length > 500) throw new ArgumentException("A description can be at most 500 characters.");
        return t.Length == 0 ? null : t;
    }

    /// <summary><paramref name="key"/>, or "key-2", "key-3"… the first that isn't <paramref name="taken"/>.</summary>
    public static async Task<string> UniqueKeyAsync(string key, Func<string, Task<bool>> taken)
    {
        var candidate = key;
        for (var n = 2; await taken(candidate); n++) candidate = $"{key}-{n}";
        return candidate;
    }

    /// <summary>
    /// Moves the item at <paramref name="at"/> by <paramref name="by"/> places, stopping at either end, and numbers the
    /// list from 0. An index below 0 (the item is gone) changes nothing.
    /// </summary>
    public static void Move<T>(List<T> items, int at, int by, Action<T, int> setOrder)
    {
        if (at < 0) return;
        var to = Math.Clamp(at + by, 0, items.Count - 1);
        var item = items[at];
        items.RemoveAt(at);
        items.Insert(to, item);
        for (var i = 0; i < items.Count; i++) setOrder(items[i], i);
    }

    /// <summary>
    /// Swaps the item at <paramref name="at"/> with the one <paramref name="by"/> places away and numbers the list from 0.
    /// Nothing changes when either is off the list (a move past the end does nothing).
    /// </summary>
    public static void Swap<T>(List<T> items, int at, int by, Action<T, int> setOrder)
    {
        var to = at + by;
        if (at < 0 || to < 0 || to >= items.Count) return;
        (items[at], items[to]) = (items[to], items[at]);
        for (var i = 0; i < items.Count; i++) setOrder(items[i], i);
    }
}
