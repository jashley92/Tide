using System.Text;
using Tide.Domain.Enums;

namespace Tide.Application.Security;

/// <summary>
/// One way to be allowed an activity: holding every permission in <see cref="Needs"/>, and, when <see cref="Qualifier"/>
/// says so, only within it ("own team's", "only in a live meeting").
/// </summary>
public sealed record Grant(IReadOnlyList<Permission> Needs, string? Qualifier = null);

/// <summary>
/// Something a person does in TIDE, and what it takes: the first of its <see cref="Grants"/> a role's permissions meet
/// decides the role's cell. <see cref="Check"/> names the policy or service check that really decides it.
/// </summary>
public sealed record Capability(string Group, string Activity, string Check, IReadOnlyList<Grant> Grants)
{
    /// <summary>What <paramref name="role"/> gets: null when it can't; "" when it can, fully; else the qualifier.</summary>
    public string? For(AppRole role) => For(RoleDefinitions.SystemRoles[role]);

    /// <summary>As <see cref="For(AppRole)"/>, for a set of permissions.</summary>
    public string? For(IReadOnlyCollection<Permission> held) =>
        Grants.FirstOrDefault(g => g.Needs.All(held.Contains)) is { } g ? g.Qualifier ?? "" : null;
}

/// <summary>
/// What each role can do, the "What each role can do" table on the Role mappings page and in docs/INSTALL.md. Built
/// from this one list and <see cref="RoleDefinitions.SystemRoles"/>, so the table moves with the permissions; tests hold
/// each row to the check named in it, and the doc's table to <see cref="Markdown"/>.
/// </summary>
public static class Capabilities
{
    public const string Briefs = "Briefs and meetings";
    public const string Articles = "Articles and intake";
    public const string Actions = "Actions";
    public const string Numbers = "Numbers";
    public const string Stack = "Our stack and adversaries";
    public const string Reports = "Reports";
    public const string Administration = "Administration";

    /// <summary>What a cell says when a Visitor reads it from a seat in a live meeting.</summary>
    public const string InMeeting = "only in a live meeting";

    /// <summary>The roles' columns, most access first.</summary>
    public static readonly IReadOnlyList<AppRole> Columns =
        [AppRole.Admin, AppRole.Analyst, AppRole.Manager, AppRole.ActionOwner, AppRole.Viewer, AppRole.Visitor, AppRole.Reader];

    private static Grant G(params Permission[] needs) => new(needs);
    private static Grant Q(string qualifier, params Permission[] needs) => new(needs, qualifier);

    public static readonly IReadOnlyList<Capability> All =
    [
        new(Briefs, "Read the company-wide archive (published briefs)", "DistributionService.RequireReadPublished", [G(Permission.ReadPublished)]),
        new(Briefs, "Read the team edition: briefs, the brief archive, Today", "Policies.ReadTeamEdition; BriefService.RequireRead",
            [G(Permission.ReadTeamEdition), Q("only its live meeting, while in it", Permission.JoinMeetings)]),
        new(Briefs, "Join and follow a live meeting", "Policies.JoinMeetings (Present)", [G(Permission.JoinMeetings)]),
        new(Briefs, "Raise a hand in a meeting", "Policies.JoinMeetings (a seat)", [G(Permission.JoinMeetings)]),
        new(Briefs, "Add meeting notes", "MeetingService.CanNote", [G(Permission.ReadTeamEdition, Permission.MeetingNotes)]),
        new(Briefs, "In a live meeting, add someone who was there, and take off whom you added", "MeetingService.CanAddAttendees",
            [G(Permission.ReadTeamEdition, Permission.JoinMeetings)]),
        new(Briefs, "With no meeting live, correct who attended (add, or take off anyone)", "MeetingService.CanAddAttendees",
            [G(Permission.PresentBriefs)]),
        new(Briefs, "Present, start and end a meeting", "BriefService.RequirePresent", [G(Permission.PresentBriefs)]),
        new(Briefs, "Build a brief (its articles, order, team note)", "BriefService.RequireWrite", [G(Permission.WriteArticles)]),
        new(Briefs, "Reopen a brief", "BriefService.ReopenAsync", [G(Permission.PresentBriefs, Permission.WriteArticles)]),
        new(Briefs, "Send a brief, finish one not sent, mark it posted", "DistributionService.RequireSend", [G(Permission.PresentBriefs)]),
        new(Briefs, "Read the brief rotation", "RotationService.RequireRead", [G(Permission.ReadTeamEdition)]),
        new(Briefs, "Ask for a rotation swap or cover", "RotationService.RequestAsync", [G(Permission.WriteArticles)]),

        new(Articles, "Read articles, stories and search", "ArticleService.RequireRead; SearchService", [G(Permission.ReadTeamEdition)]),
        new(Articles, "Triage the inbox", "IntakeService.RequireTriage", [G(Permission.WriteArticles)]),
        new(Articles, "Write and edit articles and stories", "ArticleService.RequireWrite", [G(Permission.WriteArticles)]),
        new(Articles, "Review articles", "ReviewService.RequireWrite", [G(Permission.WriteArticles)]),

        new(Actions, "Read actions: their pages, the board, Workload", "Policies.ReadActions", [G(Permission.ReadActions), Q("the one shown, in a live meeting", Permission.JoinMeetings)]),
        new(Actions, "Comment on actions", "ActionService.CanComment",
            [G(Permission.ReadTeamEdition, Permission.WriteArticles), G(Permission.ReadTeamEdition, Permission.WorkActions)]),
        new(Actions, "Raise actions and edit every field", "ActionService.CreateAsync", [G(Permission.WriteArticles)]),
        new(Actions, "Work actions: status, outcome, assignee, checklist, hunts", "ActionService.CanWork",
            [G(Permission.WriteArticles), Q("own team's", Permission.WorkActions)]),
        new(Actions, "Add and describe files", "ActionFileService.RequireWorkAsync", [G(Permission.WriteArticles), Q("own team's", Permission.WorkActions)]),
        new(Actions, "Remove files", "ActionFileService.CanRemove", [G(Permission.WriteArticles), Q("own files", Permission.WorkActions)]),
        new(Actions, "Restore removed files", "ActionFileService.RestoreAsync", [G(Permission.Administer)]),
        new(Actions, "Add indicators for an action's hunts", "ActionService.AddIndicatorsAsync",
            [G(Permission.WriteArticles), Q("own team's, with no article", Permission.WorkActions)]),
        new(Actions, "Hand an action off to another team", "HandoffService.HandOffAsync (ActionService.RequireWork)",
            [G(Permission.WriteArticles), Q("own team's", Permission.WorkActions)]),
        new(Actions, "Split an action to several teams", "HandoffService.SplitAsync", [G(Permission.WriteArticles)]),
        new(Actions, "Add, edit and retire external teams (teams that work outside TIDE)", "TeamService.RequireExternal",
            [G(Permission.WorkActions), G(Permission.Administer)]),
        new(Actions, "Set a team's active limit", "TeamService.UpdateAsync (a team's form), UpdateExternalAsync, SetLimitAsync", [G(Permission.Administer)]),
        new(Actions, "Read the saved hunt queries", "Policies.ReadActions; SavedQueryService.RequireRead", [G(Permission.ReadActions)]),
        new(Actions, "Save, edit, retire and restore saved hunt queries", "SavedQueryService.RequireWrite", [G(Permission.WorkActions)]),
        new(Actions, "Delete saved hunt queries", "SavedQueryService.CanDelete", [G(Permission.Administer), Q("own ones", Permission.WorkActions)]),
        new(Actions, "Write an Outreach action's email, mark it sent and record the reply", "OutreachService (ActionService.RequireWork)",
            [G(Permission.WriteArticles), Q("own team's", Permission.WorkActions)]),
        new(Actions, "Read the outreach templates", "Policies.ReadActions; OutreachTemplateService.RequireRead", [G(Permission.ReadActions)]),
        new(Actions, "Save, edit, retire and restore outreach templates", "OutreachTemplateService.RequireWrite", [G(Permission.WorkActions)]),
        new(Actions, "Delete outreach templates", "OutreachTemplateService.CanDelete", [G(Permission.Administer), Q("own ones", Permission.WorkActions)]),
        new(Actions, "Read the playbooks", "Policies.ReadActions; PlaybookService.RequireRead", [G(Permission.ReadActions)]),
        new(Actions, "Add, edit, reorder, retire and restore playbooks", "PlaybookService (TeamLibrary.RequireWrite)", [G(Permission.WorkActions)]),
        new(Actions, "Delete playbooks no action was started from", "PlaybookService.CanDelete", [G(Permission.Administer), Q("own ones", Permission.WorkActions)]),
        new(Actions, "Read the board rules (the card rules, read-only)", "Policies.ReadActions; CardRuleService.ListAsync", [G(Permission.ReadActions)]),
        new(Actions, "Choose my own notifications (reminders, digests, rotation emails)", "NotificationPreferenceService.Require (My notifications)",
            [G(Permission.ReadActions)]),

        new(Numbers, "Enter the day's numbers", "MetricsService.SaveValueAsync", [G(Permission.EnterMetrics)]),
        new(Numbers, "Read the metrics history", "MetricsService.RequireRead", [G(Permission.ReadTeamEdition)]),

        new(Stack, "Read Our stack and adversaries", "StackService.RequireRead; AdversaryService.RequireRead",
            [G(Permission.ReadTeamEdition), Q("the card shown, in a live meeting", Permission.JoinMeetings)]),
        new(Stack, "Edit Our stack and adversaries", "StackService.RequireWrite; AdversaryService.RequireWrite", [G(Permission.WriteArticles)]),

        new(Reports, "Reports, trends and exports", "Policies.ReadTeamEdition; ExportService", [G(Permission.ReadTeamEdition)]),
        new(Reports, "Team commitments scorecard and its CSV", "Policies.ReadActions; TeamScorecardService.BuildAsync", [G(Permission.ReadActions)]),
        new(Reports, "Brief timeliness: every analyst's days and the team summary, and the CSV", "BriefTimelinessService.BuildAsync",
            [G(Permission.ManagePeople), Q("own days only", Permission.PresentBriefs)]),
        new(Reports, "Excuse a late brief day the analyst warned of, or undo that", "BriefTimelinessService.ExcuseAsync", [G(Permission.ManagePeople)]),
        new(Reports, "Coverage of detection rules", "Policies.WriteArticles (Coverage)", [G(Permission.WriteArticles)]),

        new(Administration, "Organization, holidays, teams and SLAs, rotation", "Policies.Administer", [G(Permission.Administer)]),
        new(Administration, "Card rules, query templates", "Policies.Administer", [G(Permission.Administer)]),
        new(Administration, "Sources, source health, scorecard tags, detection rules", "Policies.Administer", [G(Permission.Administer)]),
        new(Administration, "Opening metrics and metric imports", "Policies.Administer", [G(Permission.Administer)]),
        new(Administration, "Role mappings, edition ceilings, recipients, email templates", "Policies.Administer", [G(Permission.Administer)]),
        new(Administration, "API tokens and webhooks", "Policies.Administer", [G(Permission.Administer)]),
        new(Administration, "Diagnostics, configuration, audit log, updates", "Policies.Administer", [G(Permission.Administer)]),
    ];

    /// <summary>A cell as text: "Yes", "No", or the qualifier.</summary>
    public static string Cell(string? value) => value is null ? "No" : value.Length == 0 ? "Yes" : value;

    /// <summary>The table as Markdown, as docs/INSTALL.md carries it (a test keeps the two the same).</summary>
    public static string Markdown()
    {
        var sb = new StringBuilder();
        sb.Append("| Activity | ").AppendJoin(" | ", Columns.Select(RoleDefinitions.Label)).Append(" |\n");
        sb.Append("| --- |").Append(string.Concat(Columns.Select(_ => " --- |"))).Append('\n');
        foreach (var group in All.GroupBy(c => c.Group))
        {
            sb.Append("| **").Append(group.Key).Append("** |").Append(string.Concat(Columns.Select(_ => " |"))).Append('\n');
            foreach (var c in group)
                sb.Append("| ").Append(c.Activity).Append(" | ").AppendJoin(" | ", Columns.Select(r => Cell(c.For(r)))).Append(" |\n");
        }
        return sb.ToString();
    }
}
