using Tide.Application.Abstractions;
using Tide.Application.Preferences;
using Tide.Domain.Enums;

namespace Tide.Application.Operations;

/// <summary>
/// One step of the first-sign-in tour: a page (<paramref name="Route"/>, as a link from the app's root has it: "" for
/// Today), the permission that page or control needs, the control it points at (a CSS selector; none for the page as a
/// whole), and what to say. <paramref name="Opens"/>: a tab, section, menu or picker to open first when the control is
/// inside it and shut (opening it only shows it), and <paramref name="Closes"/>, what closes it again without keeping
/// anything chosen in it, when it has a close of its own.
/// </summary>
public sealed record TourStep(string Key, string Route, Permission Needs, string? Target, string Title, string Text, string? Opens = null,
    string? Closes = null);

/// <summary>The tour each role takes: the main pages and controls they'll use, and only those.</summary>
public static class Tours
{
    private static readonly TourStep[] Steps =
    [
        new("today", "", Permission.ReadTeamEdition, ".tide-page-head h1", "Today",
            "Today is the day's brief: what's on it, how far it's got and the actions that are yours. Every page has it in the sidebar."),
        new("rail", "", Permission.ReadTeamEdition, "#tide-rail || #tide-rail-toggle", "The context rail",
            "What TIDE already knows about what you select is in the context rail on the right. After the tour, ] shows or hides it, as does the rail button in the top bar."),
        new("triage", "triage", Permission.WriteArticles, "ol.tide-triage || .tide-tabbar[aria-label=\"Inbox\"]", "Triage",
            "New items from the feeds wait here. J and K move, V reads one in the reading pane, P promotes it to an article. Nothing is fetched until you ask."),
        new("articles", "articles", Permission.WriteArticles, ".tide-page-head .btn-primary || #main-content .tide-dt-scroll", "Articles",
            "Articles are written here and put on a brief. The editor is one column, in the order the brief reads, and saves itself after a pause."),
        new("actions", "actions", Permission.ReadActions, ".tide-actions-views", "Actions",
            "What the brief asks teams to do. Each tab shows its count; J and K select a row, the rail shows it, and Enter opens it."),
        new("search", "search", Permission.ReadTeamEdition, ".tide-top-find", "Search",
            "Search finds articles, and looks up an indicator or a CVE: the answer comes first, with a hunt query in the rail."),
        new("present", "", Permission.PresentBriefs, ".tide-page-head a[href^=\"present/\"] || #meeting-h || #tide-rail-toggle", "Presenting",
            "When the brief meets, Present on Today runs the meeting. Your controls are in the presenter's rail, never on a follower's screen."),
        new("meeting", "", Permission.JoinMeetings, ".tide-live-banner a[href^=\"present/\"] || #meeting-h || #tide-rail-toggle", "The meeting",
            "When the brief meeting is live, a banner across every page says so. Join follows the presenter's slides, and you can raise a hand."),
        new("admin", "admin", Permission.Administer, ".sidebar a[href=\"admin\"] || .tide-admin-name", "Administration",
            "Sources, teams, roles and the organization's settings are in Administration."),
        new("help", "help", Permission.ReadTeamEdition, "#help-guides-title || #help-take-tour", "Help",
            "This is Help, in the account menu: every release's notes, Show tips again, and this tour whenever you want it. There's a guide for each page in Help."),
    ];

    // Which steps each group's tour has; a person with several groups takes each step once, in the order above.
    private static readonly Dictionary<RoleGroup, string[]> ByGroup = new()
    {
        [RoleGroup.Analysts] = ["today", "rail", "triage", "articles", "actions", "search", "present", "help"],
        [RoleGroup.Presenters] = ["today", "rail", "present", "help"],
        [RoleGroup.Administrators] = ["admin", "help"],
        [RoleGroup.ViewersAndActionOwners] = ["today", "rail", "actions", "search", "meeting", "help"],
    };

    /// <summary>The tour for someone in <paramref name="groups"/>, without any step whose page or control they
    /// <paramref name="can"/>'t use. None for a Reader or a Visitor, who are in no group.</summary>
    public static IReadOnlyList<TourStep> For(IReadOnlyList<RoleGroup> groups, Func<Permission, bool> can)
    {
        var keys = groups.SelectMany(g => ByGroup[g]).ToHashSet(StringComparer.Ordinal);
        return Steps.Where(s => keys.Contains(s.Key) && can(s.Needs)).ToList();
    }
}

/// <summary>
/// The signed-in person's tour: whether it's due (the user mirror marks it on their first sign-in), and what starting it
/// first time settles: someone shown round doesn't also need What's new or the what-changed tips.
/// </summary>
public sealed class TourService(UserPreferenceService prefs, ICurrentUser user, WhatsNewService whatsNew, PageTipService tips)
{
    private IReadOnlyList<TourStep>? _steps;

    /// <summary>The person's tour (their roles don't change within a circuit).</summary>
    public IReadOnlyList<TourStep> Steps => _steps ??= Tours.For(whatsNew.Groups, user.Has);

    /// <summary>Whether the person has never signed in before, and has a tour to take.</summary>
    public async Task<bool> IsDueAsync(CancellationToken ct = default) =>
        Steps.Count > 0 && await prefs.GetAsync(UserPreferences.TourState, ct) == UserPreferences.TourDue;

    /// <summary>The first tour has ended (Done, Skip tour, Escape, or the person went elsewhere): it isn't due again, and
    /// What's new and the tips up to this release are treated as seen. Until then it stays due, so a reload mid-tour
    /// starts it again.</summary>
    public async Task FinishedAsync(CancellationToken ct = default)
    {
        var settled = new Dictionary<string, string>(StringComparer.Ordinal) { [UserPreferences.TourState] = UserPreferences.TourDone };
        if (whatsNew.Running is { } running) settled[UserPreferences.WhatsNewSeen] = running.ToString();
        await prefs.SetAsync(settled, ct);
        await tips.DismissAllAsync(ct);
    }
}
