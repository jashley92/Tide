using Tide.Application.Abstractions;
using Tide.Application.Preferences;
using Tide.Domain.Enums;

namespace Tide.Application.Operations;

/// <summary>
/// A page's tip: what changed on it in <paramref name="Version"/>, pointing at <paramref name="Target"/> (a CSS selector
/// for the control that changed, or "" for none; the tip sits in a corner when it has none or that control isn't shown,
/// unless it <paramref name="NeedsTarget"/>, when it isn't shown either). Shown on a page the person can use
/// (<paramref name="Needs"/>), once: "Got it" keeps the release dismissed in the person's <see cref="Preference"/>.
/// </summary>
public sealed record PageTip(string Key, ReleaseVersion Version, Permission Needs, string Target, string Title, string Text, bool NeedsTarget = false)
{
    /// <summary>The preference that holds the release whose tip the person dismissed here ("tip.triage").</summary>
    public string Preference => "tip." + Key;

    /// <summary>Whether it's due for someone running <paramref name="running"/> who dismissed this page's tip of
    /// <paramref name="dismissed"/> (null: never). Whether they can use its page is PageTipService's to ask.</summary>
    public bool IsDue(ReleaseVersion running, ReleaseVersion? dismissed) =>
        Version <= running && (dismissed is null || dismissed < Version);
}

/// <summary>Each changed page's tip, and which page an address is.</summary>
public static class PageTips
{
    private static readonly ReleaseVersion V1_13 = new(1, 13, 0);
    private const string Rail = "#tide-rail || #tide-rail-toggle";

    public static readonly IReadOnlyList<PageTip> All =
    [
        new("today", V1_13, Permission.ReadTeamEdition, Rail, "The context rail",
            "What TIDE knows about what you select is in the context rail. Press ] to show or hide it, or use the rail button in the top bar."),
        new("triage", V1_13, Permission.WriteArticles, ".tide-reading", "The reading pane",
            "Read an item here without leaving Triage. It fetches the article only when you press V or Read it here. The decisions sit under it.",
            NeedsTarget: true),
        new("editor", V1_13, Permission.ReadTeamEdition, Rail, "One column",
            "The editor is one column, in the order the brief reads. The reader preview is in the context rail: press ] to show or hide it."),
        new("actions", V1_13, Permission.ReadActions, Rail, "The selected action",
            "Select a row with J and K, or a click. The context rail shows it; Enter opens it. No owner finds work nobody has."),
        new("action", V1_13, Permission.ReadActions, Rail, "Facts and files in the rail",
            "An action's facts, its source article and its files are in the context rail. Press ] to show or hide it."),
        new("search", V1_13, Permission.ReadTeamEdition, "#main-content form[role=\"search\"]", "Answers first",
            "A lookup answers in its first line. The hunt query, the sweeps and the export are in the context rail."),
        new("library", V1_13, Permission.ReadActions, Rail, "One Library",
            "Saved queries, outreach templates, playbooks and board rules are one Library: four tabs and one search."),
        new("stack", V1_13, Permission.ReadTeamEdition, Rail, "What needs a look",
            "Products that need a look come first. Select one to see it, and what to do, in the context rail."),
    ];

    /// <summary>The tip for the page at <paramref name="relativePath"/> (as <c>NavigationManager.ToBaseRelativePath</c>
    /// gives it), or null for a page without one: present mode never has one.</summary>
    public static PageTip? For(string relativePath)
    {
        var path = relativePath.Split('?', '#')[0].Trim('/').ToLowerInvariant();
        var parts = path.Length == 0 ? [] : path.Split('/');
        var key = parts switch
        {
            [] => "today",
            ["triage"] => "triage",
            ["articles", var id] when Guid.TryParse(id, out _) => "editor",
            ["actions"] => "actions",
            ["actions", var n] when int.TryParse(n, out _) => "action",
            ["search"] => "search",
            ["library", ..] => "library",
            ["stack"] => "stack",
            _ => null,
        };
        return key is null ? null : All.First(t => t.Key == key);
    }
}

/// <summary>
/// The signed-in person's page tips: which is due on a page, Got it, and Help's Show tips again. One per circuit: the
/// person's tips are read once, in one query, and kept up to date by its own writes, so moving between pages reads
/// nothing. (A tip put away in another tab still shows here until a reload.)
/// </summary>
public sealed class PageTipService(UserPreferenceService prefs, ICurrentUser user, WhatsNewService whatsNew)
{
    private Dictionary<string, ReleaseVersion?>? _dismissed;

    private IEnumerable<PageTip> Usable => PageTips.All.Where(t => user.Has(t.Needs));

    /// <summary>The tip due on the page at <paramref name="relativePath"/>, or null: only on a page the person can use.</summary>
    public async Task<PageTip?> DueAsync(string relativePath, CancellationToken ct = default)
    {
        if (PageTips.For(relativePath) is not { } tip || whatsNew.Running is not { } running || !user.Has(tip.Needs)) return null;
        _dismissed ??= (await prefs.GetAsync(Usable.Select(t => t.Preference), ct))
            .ToDictionary(p => p.Key, p => ReleaseVersion.Parse(p.Value), StringComparer.Ordinal);
        return tip.IsDue(running, _dismissed.GetValueOrDefault(tip.Preference)) ? tip : null;
    }

    /// <summary>Got it: the tip isn't shown again.</summary>
    public async Task DismissAsync(PageTip tip, CancellationToken ct = default)
    {
        await prefs.SetAsync(tip.Preference, tip.Version.ToString(), ct);
        if (_dismissed is not null) _dismissed[tip.Preference] = tip.Version;
    }

    /// <summary>Show tips again: each tip on a page the person can use shows on their next visit to it.</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await prefs.SetAsync(Usable.ToDictionary(t => t.Preference, _ => "", StringComparer.Ordinal), ct);
        _dismissed = null;
    }

    /// <summary>Every tip up to the release running is put away, as for someone the tour has just shown round.</summary>
    public async Task DismissAllAsync(CancellationToken ct = default)
    {
        if (whatsNew.Running is not { } running) return;
        var tips = Usable.Where(t => t.Version <= running).ToList();
        if (tips.Count == 0) return;
        await prefs.SetAsync(tips.ToDictionary(t => t.Preference, t => t.Version.ToString(), StringComparer.Ordinal), ct);
        _dismissed = null;
    }
}
