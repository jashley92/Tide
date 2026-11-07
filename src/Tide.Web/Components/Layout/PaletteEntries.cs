using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Search;
using Tide.Domain.Enums;
using Tide.Web.Components.Shared;

namespace Tide.Web.Components.Layout;

/// <summary>What a palette entry opens: a page, a "New …" command, a search hit, or the search page itself.</summary>
public enum PaletteKind { Page, Command, Article, Action, Story, Search }

/// <summary>One line in the command palette: what it says, where it goes, and a word or two about what it is.</summary>
public sealed record PaletteEntry(string Label, string Href, string Icon, PaletteKind Kind, string Detail, string? Keywords = null);

/// <summary>
/// The command palette's entries (CommandPalette): the pages someone may open, as the sidebar lists them (NavPages),
/// the "New …" commands they may use, and what SearchService finds as they type. Nothing here grants anything; each page
/// still checks its own policy.
/// </summary>
public static class PaletteEntries
{
    /// <summary>The search hits of each kind the palette shows; the search page has the rest.</summary>
    public const int HitsPerKind = 5;

    /// <summary>The pages and commands <paramref name="user"/> may use, before anything is typed.</summary>
    public static IReadOnlyList<PaletteEntry> For(ICurrentUser user, DateOnly briefDay)
    {
        var list = NavPages.For(user)
            .Select(p => new PaletteEntry(p.Label, p.Href, p.Icon, PaletteKind.Page, "Page", p.Keywords))
            .ToList();
        // The same checks as the buttons these stand for (Articles' New article, the board's New action).
        if (user.Has(Permission.WriteArticles) && user.Has(Permission.ReadTeamEdition))
            list.Insert(0, new("New article", "articles?new=1", "bi-plus-lg", PaletteKind.Command, "Command", "create write draft"));
        if (user.Has(Permission.WriteArticles) && user.Has(Permission.WorkActions))
            list.Insert(list.Count(e => e.Kind == PaletteKind.Command), new("New action", "actions/new", "bi-plus-lg", PaletteKind.Command, "Command", "create raise task"));
        if (user.Has(Permission.PresentBriefs))
            list.Add(new("Present today's brief", Ui.PresentUrl(briefDay), "bi-easel", PaletteKind.Command, "Command", "meeting slides"));
        return list;
    }

    /// <summary>The entries whose name (or other words) has every word of <paramref name="query"/>; all of them when it's empty.</summary>
    public static IReadOnlyList<PaletteEntry> Match(IReadOnlyList<PaletteEntry> entries, string? query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return entries;
        return entries.Where(e => words.All(w =>
            e.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || (e.Keywords?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();
    }

    /// <summary>
    /// What a search found, a few of each kind: actions first (a number typed is usually one), then articles and stories,
    /// and a last entry that opens the search page with everything. Actions only for someone who may read them, as the
    /// search page links them (defence in depth: the action page checks too).
    /// </summary>
    public static IReadOnlyList<PaletteEntry> FromSearch(SearchResults results, ICurrentUser user)
    {
        var list = new List<PaletteEntry>();
        if (user.Has(Permission.ReadActions))
            list.AddRange(results.Actions.Items.Take(HitsPerKind).Select(a => new PaletteEntry(
                $"{Ui.ActionLabel(a.Number)} {a.Title}", $"actions/{a.Number}", "bi-list-check", PaletteKind.Action, "Action · " + ActionService.StatusName(a.Status))));
        list.AddRange(results.Articles.Items.Take(HitsPerKind).Select(a => new PaletteEntry(
            a.Title, $"articles/{a.Id}", "bi-newspaper", PaletteKind.Article, "Article · " + a.Status)));
        list.AddRange(results.Stories.Items.Take(HitsPerKind).Select(s => new PaletteEntry(
            s.Name, $"stories/{s.Id}", "bi-bezier2", PaletteKind.Story, "Story")));
        if (results.Query.Length > 0)
            list.Add(SearchAll(results.Query));
        return list;
    }

    /// <summary>The entry that opens the search page for <paramref name="query"/>.</summary>
    public static PaletteEntry SearchAll(string query) =>
        new($"Search everything for “{query}”", "search?q=" + Uri.EscapeDataString(query), "bi-search", PaletteKind.Search, "Search");
}
