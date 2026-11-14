using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Application.Preferences;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using static Tide.Application.Actions.ActionEmails;
using static Tide.Application.Distribution.EmailComposer;

namespace Tide.Application.Actions;

/// <summary>One team's part of a split: its team, its due date (null: the parent's) and its ticket link, if any.</summary>
public sealed record SplitPart(Guid TeamId, DateOnly? DueDate = null, string? TicketUrl = null);

/// <summary>
/// Work moving between teams. A hand-off gives an action to another team, saying why; a split raises a part of an action
/// for each of several teams at once. Either may take a team over its active limit (<see cref="ActiveLimits"/>). A team
/// that's handed an action is told by email (<see cref="HandoffNotices"/>), unless it's external: an external team hears
/// only through its own reminders. A team changed in the action's editor is a hand-off too (<see cref="ActionService.SaveAsync"/>).
/// </summary>
public sealed class HandoffService(IAppDbContextFactory dbs, ICurrentUser user, OrgClock clock, HandoffNotices notices,
    Microsoft.Extensions.Logging.ILogger<HandoffService>? log = null)
{
    /// <summary>The most teams one split makes parts for.</summary>
    public const int MaxParts = 20;

    /// <summary>What a hand-off's history line adds when it turned the action's reminders back on.</summary>
    public const string RemindersBackOn = "Its reminders are back on.";

    /// <summary>A hand-off made: the team it came from (by name), the team it went to, with its members, and why.</summary>
    internal sealed record Handover(string From, Team To, string Why);

    /// <summary>
    /// Hands an action to <paramref name="toTeamId"/>, for whoever may work it, with a short reason (required) kept in its
    /// history and shown on it. Its assignee stays only if they're on the receiving team. When its reminders are off
    /// ("Don't remind"), they stay off for the new team unless <paramref name="turnRemindersOn"/> (the hand-off's history
    /// line then says so); while they're on it changes nothing. Returns how many emails told the team (none for an
    /// external team, or when email is off).
    /// </summary>
    public async Task<int> HandOffAsync(Guid actionId, Guid toTeamId, string reason, string? overLimitReason = null, bool turnRemindersOn = false,
        CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var a = await ActionService.FindAsync(db, actionId, ct);
        await ActionService.RequireWork(db, user, a, ct, "Only analysts and members of the owning team can hand this action off.");
        if (toTeamId == a.TeamId) throw new ArgumentException("It's that team's already: choose another.");
        var handover = await HandOverAsync(db, user, a, await ReceivingTeamAsync(db, toTeamId, ct), reason, overLimitReason, turnRemindersOn, ct);
        await ActionService.SaveEditAsync(db, ct);
        return await notices.SendAsync(db, a, handover, user.DisplayName, ct);
    }

    /// <summary>The team an action is handed to, with its members (who may keep it); refused when it's gone or retired.</summary>
    internal static async Task<Team> ReceivingTeamAsync(IAppDbContext db, Guid teamId, CancellationToken ct)
    {
        var to = await db.Teams.AsNoTracking().Include(t => t.Members).FirstOrDefaultAsync(t => t.Id == teamId, ct)
                 ?? throw new ArgumentException("Choose a team to hand it to.");
        return to.IsRetired ? throw new ArgumentException($"{to.Name} is retired; choose another team.") : to;
    }

    /// <summary>
    /// The hand-off itself, on a tracked action the caller may work, before the save: the receiving team's limit holds, an
    /// assignee not on that team goes, who handed it and why are recorded, reminders that were off stay off unless
    /// <paramref name="turnRemindersOn"/>, and its history says so. The Hand off button and a team changed in
    /// the editor both come here.
    /// </summary>
    internal static async Task<Handover> HandOverAsync(IAppDbContext db, ICurrentUser user, ActionItem a, Team to, string reason,
        string? overLimitReason, bool turnRemindersOn, CancellationToken ct)
    {
        var why = ArticleService.CleanOrNull(reason, ActionItem.MaxHandoffReason) ?? throw new ArgumentException("Say why you're handing it off.");
        var over = await ActiveLimits.CheckChangeAsync(db, (a.TeamId, a.Status), (to.Id, a.Status), a.Id, overLimitReason, to, ct);
        var from = await db.Teams.Where(t => t.Id == a.TeamId).Select(t => t.Name).FirstOrDefaultAsync(ct) ?? "another team";

        // Someone not on the receiving team, or on it but unable to work actions (a Reader kept on it), can't go on working
        // it there, so it goes to the team, unassigned.
        var dropped = a.AssigneeId is { } held && (await TeamMemberRoles.OfAsync(db, to.Id, held, ct))?.CanHold != true ? a.AssigneeName ?? held : null;
        if (dropped is not null) (a.AssigneeId, a.AssigneeName) = (null, null);
        (a.HandedFromTeamId, a.TeamId) = (a.TeamId, to.Id);
        (a.HandedById, a.HandedByName, a.HandedAtUtc, a.HandoffReason) = (user.UserId, user.DisplayName, DateTimeOffset.UtcNow, why);
        // "Don't remind" was chosen for the team it's leaving: whoever hands it says whether it holds for the next one.
        var remindersOn = a.NoReminders && turnRemindersOn;
        if (remindersOn) a.NoReminders = false;
        a.EditVersion = Guid.NewGuid();
        db.ActionEvents.Add(ActionService.Event(user, a.Id, $"Handed from {from} to {to.Name}: {why}"
            + (dropped is null ? "" : $" {dropped} is no longer its assignee.") + (remindersOn ? " " + RemindersBackOn : "") + over));
        return new Handover(from, to, why);
    }

    /// <summary>
    /// Splits a top-level action into a part for each of <paramref name="parts"/>' teams, together: each titled "<parent> –
    /// <team>", with the parent's details, kind, priority and article, its own due date (the parent's when none is given)
    /// and ticket link, and the parent's own indicators when it has no article. No assignee or checklist: the team picks
    /// it up. Analysts only, as creating an action is. Returns the new parts' numbers, in the order given.
    /// </summary>
    public Task<IReadOnlyList<int>> SplitAsync(Guid parentId, IReadOnlyList<SplitPart> parts, string? overLimitReason = null,
        CancellationToken ct = default)
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts can split an action.");
        if (parts.Count == 0) throw new ArgumentException("Choose at least one team.");
        if (parts.Count > MaxParts) throw new ArgumentException($"Split to at most {MaxParts} teams at once.");
        if (parts.Select(p => p.TeamId).Distinct().Count() != parts.Count) throw new ArgumentException("Choose each team once.");
        ActionItem? parent = null;
        Dictionary<Guid, Team> teams = [];
        return ActionService.AddActionsAsync(dbs, user, async db =>
        {
            (parent, teams) = await SplitTargetsAsync(db, parentId, parts, ct);
            var over = await ActiveLimits.CheckNewAsync(db, teams.Values, overLimitReason, ct);
            return [.. parts.Select(p => Part(parent, teams[p.TeamId], p, over[p.TeamId]))];
        }, (db, made) => db.ActionEvents.Add(ActionService.Event(user, parent!.Id, "Split into "
            + string.Join(", ", made.Select(m => $"A-{m.Number} ({teams[m.TeamId].Name})")) + ".")), ct, log);
    }

    // The parent (open and top-level) and the teams (live), checked.
    private static async Task<(ActionItem Parent, Dictionary<Guid, Team> Teams)> SplitTargetsAsync(IAppDbContext db, Guid parentId,
        IReadOnlyList<SplitPart> parts, CancellationToken ct)
    {
        var parent = await db.Actions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == parentId, ct) ?? throw new KeyNotFoundException("That action no longer exists.");
        if (parent.ParentId is not null) throw new ArgumentException("Actions split one level deep: split its parent instead.");
        if (!parent.IsOpen) throw new InvalidOperationException($"A-{parent.Number} is closed. Reopen it to split it.");
        var ids = parts.Select(p => p.TeamId).ToList();
        var teams = await db.Teams.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        if (teams.Count != ids.Count) throw new ArgumentException("One of those teams no longer exists.");
        if (teams.Values.FirstOrDefault(t => t.IsRetired) is { } retired) throw new ArgumentException($"{retired.Name} is retired; choose another team.");
        return (parent, teams);
    }

    // One team's part, and the first line of its history.
    private ActionService.NewAction Part(ActionItem parent, Team team, SplitPart part, string over)
    {
        var due = part.DueDate ?? parent.DueDate;
        if (due is { } d && d < clock.Today.AddYears(-1)) throw new ArgumentException($"{team.Name}'s due date is too far in the past.");
        var child = new ActionItem
        {
            Title = ActionRollup.PartTitle(parent.Title, team.Name), Description = parent.Description, Kind = parent.Kind, Priority = parent.Priority,
            TeamId = team.Id, DueDate = due, TicketUrl = ArticleService.CheckUrl(part.TicketUrl, "The ticket link"), ArticleId = parent.ArticleId,
            ParentId = parent.Id,
        };
        return new(child, $"Split from A-{parent.Number} for {team.Name}" + (due is { } dd ? $", due {ActionService.Day(dd)}" : "") + "." + over);
    }
}

/// <summary>
/// Tells a team an action was handed to it: its mailbox, or its members who have an address and haven't turned
/// hand-offs off in My notifications. Never an external team (its reminders tell it). Sent after the save, so a relay
/// that's down leaves the hand-off made.
/// </summary>
public sealed class HandoffNotices(OrgClock clock, IEmailSender email, IOptions<AppOptions> app, EmailTemplateStore? templates = null)
{
    private readonly EmailTemplateStore _templates = templates ?? EmailTemplateStore.Defaults;

    /// <summary>Emails the receiving team about <paramref name="a"/>, handed by <paramref name="by"/>; how many emails went
    /// (or may have: the relay didn't answer in time, and it most likely arrived).</summary>
    internal async Task<int> SendAsync(IAppDbContext db, ActionItem a, HandoffService.Handover handover, string by, CancellationToken ct)
    {
        var to = handover.To;
        if (to.IsExternal) return 0;
        IReadOnlyList<string> recipients = !string.IsNullOrWhiteSpace(to.Email) ? [to.Email]
            : (await RecipientResolver.ResolveAsync(db, NotificationKinds.Handoffs, to.Members.Select(m => m.UserId), ct)).Reachable();
        if (recipients.Count == 0) return 0;
        // As the reminders list it: with its article's marking, which the action may not have loaded.
        var tlp = a.ArticleId is { } article ? await db.Articles.AsNoTracking().Where(x => x.Id == article).Select(x => (TlpLevel?)x.Tlp).FirstOrDefaultAsync(ct) : null;
        ActionItem[] shown =
        [
            new()
            {
                Number = a.Number, Title = a.Title, Kind = a.Kind, Priority = a.Priority, AssigneeName = a.AssigneeName, DueDate = a.DueDate,
                Status = a.Status, Article = tlp is { } level ? new Article { Tlp = level } : null,
            },
        ];
        var today = clock.Today;
        var t = _templates.Render(EmailTemplateCatalog.Handoff, new Dictionary<string, string>
        {
            ["Team"] = to.Name, ["FromTeam"] = handover.From, ["By"] = by, ["Reason"] = handover.Why,
            ["ActionList"] = ActionTable(shown, today, app.Value.BaseUrl, clock.Days),
        });
        var html = Shell(t.Heading, clock.TeamName + " · " + DayText(today), MarkingLine(shown) + t.BodyHtml, t.Footer);
        var result = await email.SendAsync(new OutgoingEmail(recipients, Marked(t.Subject, shown), html, ToText(html), []) { Template = t.TemplateId }, ct);
        return result.MayHaveGone ? 1 : 0;
    }
}
