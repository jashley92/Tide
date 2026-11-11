using Tide.Application.Abstractions;
using Tide.Domain.Enums;

namespace Tide.Web.Components.Layout;

/// <summary>
/// A tab of the Library page: its name, icon, the permission it needs (what its page needed when it was a page of its
/// own), the other words the command palette finds it by, the addresses it had before (<see cref="Was"/>), which now
/// lead to it, and the change feed topic its entries are published on (<see cref="Topic"/>; with one, the tab is a
/// list of entries the team keeps; the board rules are an administrator's, read-only, and have none).
/// </summary>
public sealed record LibraryTab(string Key, string Label, string Icon, Permission Requires, string Keywords, IReadOnlyList<string> Was, string? Topic = null)
{
    /// <summary>The tab's address: each tab has its own, so its page and selected entry (?page=, ?row=) are its alone.</summary>
    public string Href => LibraryTabs.Root + "/" + Key;

    /// <summary>What the tab holds, in a line: the page header says it under the Library's name.</summary>
    public string About { get; init; } = "";

    public bool VisibleTo(ICurrentUser user) => user.Has(Requires);
}

/// <summary>
/// The Library page's tabs, in order. The page, the sidebar's one link, the command palette's entries and the old
/// addresses' redirect all read this, so they never disagree about what the Library holds or who sees each part.
/// </summary>
public static class LibraryTabs
{
    public const string Root = "library";
    public const string Queries = "queries";
    public const string Templates = "templates";
    public const string Playbooks = "playbooks";
    public const string BoardRules = "board-rules";

    public static readonly IReadOnlyList<LibraryTab> All =
    [
        new(Queries, "Saved queries", "bi-bookmark", Permission.ReadActions, "hunt query library siem kql xql", ["hunts/saved"], ChangeFeed.SavedQueries)
            { About = "Hunt queries for actions: {{days}} becomes the action's window and {{values}} its indicators. TIDE never runs one." },
        new(Templates, "Outreach templates", "bi-envelope-paper", Permission.ReadActions, "email vendor partner isac ask template library", ["outreach/templates"], ChangeFeed.OutreachTemplates)
            { About = "Emails to ask a vendor, partner or ISAC, filled from an action up to their TLP. TIDE never sends them." },
        new(Playbooks, "Playbooks", "bi-journal-check", Permission.ReadActions, "checklist steps runbook library", ["playbooks", "admin/playbooks"], ChangeFeed.Playbooks)
            { About = "Checklists an action starts from. Their steps are copied, so an edit leaves started actions alone." },
        new(BoardRules, "Board rules", "bi-palette", Permission.ReadActions, "card rules colour color label library", ["board-rules"])
            { About = "How the Actions board and list mark actions: the first rule that matches sets the colour. Administrators set them." },
    ];

    /// <summary>The tabs <paramref name="user"/> may open, in order.</summary>
    public static IReadOnlyList<LibraryTab> For(ICurrentUser user) => All.Where(t => t.VisibleTo(user)).ToList();

    /// <summary>Where an old address (base-relative, "hunts/saved") leads now: its tab, or the Library when it isn't one.</summary>
    public static string Moved(string was) =>
        All.FirstOrDefault(t => t.Was.Contains(was.Trim('/'), StringComparer.OrdinalIgnoreCase))?.Href ?? Root;
}
