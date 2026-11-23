using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Search;
using Tide.Domain.Enums;
using Tide.Web.Components.Shared;
using Tide.Web.Services;

namespace Tide.Web.Components.Layout;

/// <summary>What a palette entry opens: a page, a tab of one (the Library's), a command (a verb: "New article", the
/// page's own "Mark the brief ready"), a search hit, or the search page itself.</summary>
public enum PaletteKind { Page, Tab, Command, Article, Action, Story, Search }

/// <summary>One line in the command palette: what it says, where it goes (or, for a page's verb, what it runs), and a
/// word or two about what it is.</summary>
public sealed record PaletteEntry(string Label, string Href, string Icon, PaletteKind Kind, string Detail, string? Keywords = null, Func<Task>? Run = null);

/// <summary>
/// The command palette's entries (CommandPalette): the pages someone may open, as the sidebar lists them (NavPages),
/// the Library's tabs under its page (LibraryTabs), the "New …" commands they may use, and what SearchService finds as
/// they type. Nothing here grants anything; each page still checks its own policy.
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
        // Each Library tab after its page, found by the words its own page had ("kql", "checklist", "card colour").
        if (list.FindIndex(e => e.Href == LibraryTabs.Root) is var library and >= 0)
            list.InsertRange(library + 1, LibraryTabs.For(user).Select(t => new PaletteEntry(t.Label, t.Href, t.Icon, PaletteKind.Tab, "Library", t.Keywords)));
        // The same checks as the buttons these stand for (Articles' New article, the board's New action).
        if (user.Has(Permission.WriteArticles) && user.Has(Permission.ReadTeamEdition))
            list.Insert(0, new("New article", "articles?new=1", "bi-plus-lg", PaletteKind.Command, "Command", "create write draft"));
        if (user.Has(Permission.WriteArticles) && user.Has(Permission.WorkActions))
            list.Insert(list.Count(e => e.Kind == PaletteKind.Command), new("New action", "actions/new", "bi-plus-lg", PaletteKind.Command, "Command", "create raise task"));
        if (user.Has(Permission.PresentBriefs))
            list.Add(new("Present today's brief", Ui.PresentUrl(briefDay), "bi-easel", PaletteKind.Command, "Command", "meeting slides"));
        return list;
    }

    /// <summary>What starts a query that lists only the commands (verbs), as in an editor's command palette.</summary>
    public const char VerbsOnly = '>';

    /// <summary>Whether <paramref name="query"/> asks for the commands only ("&gt;", "&gt; ready"), and what's typed after it.</summary>
    public static bool OnlyVerbs(string? query, out string rest)
    {
        var q = (query ?? "").Trim();
        var only = q.StartsWith(VerbsOnly);
        rest = only ? q[1..].Trim() : q;
        return only;
    }

    /// <summary>The page's own verbs (<see cref="PaletteCommands"/>) as palette lines, said with where they apply.</summary>
    public static IReadOnlyList<PaletteEntry> FromVerbs(IEnumerable<PaletteCommand> verbs) =>
        verbs.Select(v => new PaletteEntry(v.Label, "", v.Icon, PaletteKind.Command, v.Scope, v.Keywords, v.Run)).ToList();

    /// <summary>
    /// A verb on an action named in what's typed, from anywhere: "take A-12" and "hand A-12" (or "hand off 12") open the
    /// action with its own Take it or Hand off (<c>?do=</c>), which asks what its button would ask (whose action it is,
    /// which team and why) and runs only when the page offers that flow. Only for someone who may work actions.
    /// </summary>
    public static IReadOnlyList<PaletteEntry> Typed(string? query, ICurrentUser user)
    {
        if (!(user.Has(Permission.WriteArticles) || user.Has(Permission.WorkActions))) return [];
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length is < 2 or > 3 || SearchText.ActionNumber(words[^1]) is not { } n) return [];
        var label = Ui.ActionLabel(n);
        return string.Join(' ', words[..^1]).ToLowerInvariant() switch
        {
            "take" => [new($"Take {label}", Ui.ActionUrl(n, "do=takeit"), "bi-person-check", PaletteKind.Command, "Action", "assign me mine")],
            "hand" or "hand off" or "handoff" => [new($"Hand {label} to…", Ui.ActionUrl(n, "do=handoff"), "bi-box-arrow-right", PaletteKind.Command, "Action", "hand off team")],
            _ => [],
        };
    }

    /// <summary>
    /// What the palette lists for <paramref name="query"/>: the page's verbs first, then a verb what's typed asks for
    /// (unless the page has the same one), then the pages and commands; only the verbs (commands) after "&gt;". Without
    /// <paramref name="verbs"/> (present mode, keys only), no verb at all.
    /// </summary>
    public static IReadOnlyList<PaletteEntry> Filter(IReadOnlyList<PaletteEntry> pageVerbs, IReadOnlyList<PaletteEntry> entries, string? query,
        ICurrentUser user, bool verbs = true)
    {
        var only = OnlyVerbs(query, out var rest);
        if (!verbs) return only ? [] : Match(entries.Where(e => e.Kind != PaletteKind.Command).ToList(), rest);
        var typed = Typed(rest, user).Where(t => !pageVerbs.Any(v => string.Equals(v.Label, t.Label, StringComparison.OrdinalIgnoreCase)));
        List<PaletteEntry> all = [.. Match(pageVerbs, rest), .. typed, .. Match(entries, rest)];
        return only ? all.Where(e => e.Kind == PaletteKind.Command).ToList() : all;
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
