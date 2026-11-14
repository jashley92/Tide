using Tide.Application.Abstractions;
using Tide.Application.Security;

namespace Tide.Web.Components.Layout;

/// <summary>
/// A tab of Reports: its name, icon, address (each tab keeps the address its page had) and its page's policy, which
/// decides who's offered it, so the tab and the page never disagree.
/// </summary>
public sealed record ReportsTab(string Key, string Label, string Icon, string Href, string Policy)
{
    public bool VisibleTo(ICurrentUser user) => Policies.Meeting(Policy).Any(user.Has);
}

/// <summary>
/// The Reports pages' tabs, in order: Monthly report, Team commitments, Brief timeliness. Each page draws the bar
/// (<c>ReportsTabBar</c>) with the tabs its reader may open, so nobody is offered a tab whose page would turn them away.
/// </summary>
public static class ReportsTabs
{
    public const string Report = "report";
    public const string Teams = "teams";
    public const string Timeliness = "timeliness";

    public static readonly IReadOnlyList<ReportsTab> All =
    [
        new(Report, "Monthly report", "bi-file-earmark-bar-graph", "reports", Policies.ReadTeamEdition),
        new(Teams, "Team commitments", "bi-people", "reports/teams", Policies.ReadActions),
        new(Timeliness, "Brief timeliness", "bi-alarm", "reports/timeliness", Policies.BriefTimeliness),
    ];

    /// <summary>The tabs <paramref name="user"/> may open, in order.</summary>
    public static IReadOnlyList<ReportsTab> For(ICurrentUser user) => All.Where(t => t.VisibleTo(user)).ToList();
}
