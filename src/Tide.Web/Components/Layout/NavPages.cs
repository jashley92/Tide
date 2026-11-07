using Microsoft.AspNetCore.Components.Routing;
using Tide.Application.Abstractions;
using Tide.Domain.Enums;

namespace Tide.Web.Components.Layout;

/// <summary>
/// A page in the sidebar: its name, icon, link, the group it's listed under and the permissions it needs (all of them).
/// <see cref="Keywords"/> are other words the command palette finds it by.
/// </summary>
public sealed record NavPage(
    string Key, string Label, string Icon, string Href, string Group, IReadOnlyList<Permission> Requires,
    NavLinkMatch Match = NavLinkMatch.Prefix, string? Keywords = null)
{
    public bool VisibleTo(ICurrentUser user) => Requires.All(user.Has);
}

/// <summary>
/// The pages the app's sidebar lists, in order, and who sees each. The sidebar (NavMenu) and the command palette both
/// read this, so they never disagree about what someone can open. Each page's own [Authorize] policy still decides.
/// </summary>
public static class NavPages
{
    public const string Triage = "triage";
    public const string Actions = "actions";

    // The sidebar's groups: a small label over each, the daily pages first.
    public const string Daily = "Daily";
    public const string Intelligence = "Intelligence";
    public const string Team = "Team";
    public const string CompanyWide = "Company-wide";
    public const string Administration = "Administration";

    /// <summary>Every page, in its groups, the daily ones first. Today is today's brief (its /briefs/{day} address redirects there).</summary>
    public static IReadOnlyList<NavPage> All() =>
    [
        new("today", "Today", "bi-sunrise", "", Daily, [Permission.ReadTeamEdition], NavLinkMatch.All, "home dashboard brief running order"),
        new(Triage, "Triage", "bi-inbox", "triage", Daily, [Permission.ReadTeamEdition, Permission.WriteArticles], Keywords: "inbox feeds intake"),
        new("articles", "Articles", "bi-newspaper", "articles", Daily, [Permission.ReadTeamEdition]),
        new(Actions, "Actions", "bi-list-check", "actions", Daily, [Permission.ReadActions], Keywords: "board tasks"),
        new("search", "Search", "bi-search", "search", Daily, [Permission.ReadTeamEdition], Keywords: "indicator lookup cve ioc"),
        new("stories", "Stories", "bi-bezier2", "stories", Intelligence, [Permission.ReadTeamEdition]),
        new("archive", "Brief archive", "bi-archive", "briefs", Intelligence, [Permission.ReadTeamEdition], NavLinkMatch.All, "past briefs"),
        new("trends", "Trends", "bi-bar-chart-line", "trends", Intelligence, [Permission.ReadTeamEdition]),
        new("stack", "Our stack", "bi-hdd-stack", "stack", Intelligence, [Permission.ReadTeamEdition], Keywords: "what we run products inventory versions"),
        new("adversaries", "Adversaries", "bi-incognito", "adversaries", Intelligence, [Permission.ReadTeamEdition], Keywords: "threat actors groups campaigns apt aliases mitre"),
        new("reports", "Reports", "bi-file-earmark-bar-graph", "reports", Intelligence, [Permission.ReadTeamEdition, Permission.WriteArticles], Keywords: "weekly monthly quarterly board report"),
        new("coverage", "Coverage", "bi-shield-check", "coverage", Intelligence, [Permission.WriteArticles], Keywords: "detection rules gaps sigma sentinel attack"),
        new("metrics", "Metrics", "bi-graph-up", "metrics", Intelligence, [Permission.ReadTeamEdition, Permission.WriteArticles]),   // listed for those who make the brief; the page stays open to action owners
        new("rotation", "Brief rotation", "bi-calendar3", "rotation", Team, [Permission.ReadTeamEdition, Permission.WriteArticles], Keywords: "rota schedule"),
        new("workload", "Workload", "bi-people", "workload", Team, [Permission.ReadActions]),
        new("saved-queries", "Saved queries", "bi-bookmark", "hunts/saved", Team, [Permission.ReadActions], Keywords: "hunt query library siem kql xql"),
        new("outreach-templates", "Outreach templates", "bi-envelope-paper", "outreach/templates", Team, [Permission.ReadActions], Keywords: "email vendor partner isac ask template library"),
        new("playbooks", "Playbooks", "bi-journal-check", "playbooks", Team, [Permission.ReadActions], Keywords: "checklist steps runbook library"),
        new("board-rules", "Board rules", "bi-palette", "board-rules", Team, [Permission.ReadActions], Keywords: "card rules colour color label"),
        new("published", "Published briefs", "bi-megaphone", "published", CompanyWide, [Permission.ReadPublished], Keywords: "archive company"),
        // One entry; the Administration page's tabs reach every admin page (AdminLayout).
        new("admin", "Administration", "bi-sliders", "admin", Administration, [Permission.Administer], Keywords: "settings admin"),
    ];

    /// <summary>The pages <paramref name="user"/> may open, in sidebar order.</summary>
    public static IReadOnlyList<NavPage> For(ICurrentUser user) =>
        All().Where(p => p.VisibleTo(user)).ToList();

    /// <summary>
    /// The pages <paramref name="user"/> may open, by group, in sidebar order. A group they can open nothing in isn't
    /// there, so its label never shows over no links.
    /// </summary>
    public static IReadOnlyList<IGrouping<string, NavPage>> GroupsFor(ICurrentUser user) =>
        For(user).GroupBy(p => p.Group).ToList();
}
