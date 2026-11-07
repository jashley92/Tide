using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tide.Application.Abstractions;
using Tide.Application.Distribution;
using Tide.Application.Preferences;
using Tide.Domain.Entities;

namespace Tide.Application.Actions;

public sealed partial class ReminderService
{
    private const string EscalationMark = "escalation";

    /// <summary>Who an escalation's history line is by: the ladder, not whoever ran the scan.</summary>
    public const string EscalatedBy = "TIDE";

    // One action on the ladder this run: the due date it climbs for, the steps it runs, and what its emails did.
    private sealed class Climb(ActionItem action, DateOnly due, IReadOnlyList<EscalationStep> run)
    {
        public ActionItem Action { get; } = action;
        public DateOnly Due { get; } = due;
        public IReadOnlyList<EscalationStep> Run { get; } = run;
        public EscalationDoes Does { get; } = run.Aggregate(EscalationDoes.None, (d, s) => d | s.Does);
        public Team Team => Action.Team!;
        public IEnumerable<string> Keys => Run.Select(s => StepKey(Action.Id, Due, s));

        /// <summary>What its emails did, for its history line, each said once.</summary>
        public List<string> Said { get; } = [];
        public bool Reached { get; set; }
        public bool Failed { get; set; }

        public void Say(string text)
        {
            if (!Said.Contains(text)) Said.Add(text);
        }
    }

    // Each open action in the ladder's scope that has reached a step runs it (EscalationLadder.Plan), once per due date:
    // a mark per action, due date and step, so moving the date starts it over. The emails go first, batched; then every
    // action's own changes (its priority, its flag), its history line and its marks are saved together. An action whose
    // every email failed waits for the next poll, as a reminder does. Steps that don't read (a hand-edited server value)
    // climb nothing, and the run says why.
    private async Task<Scan> EscalateAsync(CancellationToken ct)
    {
        var o = Options.Escalation;
        IReadOnlyList<EscalationStep> steps;
        try { steps = EscalationLadder.Parse(o.Steps); }
        catch (ArgumentException ex)
        {
            logger?.LogWarning("The escalation ladder climbed nothing: its steps don't read. {Problem}", ex.Message);
            return new Scan(0, Problem: "its steps don't read: " + ex.Message);
        }
        if (steps.Count == 0) return 0;
        var today = clock.Today;
        await using var db = await dbs.CreateAsync(ct);
        var climbs = await PlanClimbsAsync(db, steps, o.Scope, today, ct);
        if (climbs.Count == 0) return 0;

        // Where replies go, read once for every email that names it.
        var replies = await ReplyToAsync(db, [.. climbs.Where(NeedsReplyTo).Select(c => c.Action)], ct);
        var sent = 0;
        var finished = false;
        try
        {
            sent = await EscalationRemindAsync(db, [.. climbs.Where(c => c.Does.HasFlag(EscalationDoes.Remind))], replies, today, ct);
            sent += await CopyContactsAsync([.. climbs.Where(c => c.Does.HasFlag(EscalationDoes.CopyContact))], replies, today, ct);
            finished = true;
        }
        finally
        {
            // A run stopped partway (shutdown, a send that threw) still keeps the climbs an email reached, as a reminder
            // keeps its marks, so the next run doesn't email them again; those not yet tried wait for it.
            await ClimbAllAsync([.. climbs.Where(c => c.Reached || (finished && !c.Failed))], today);
        }
        return sent;
    }

    private static bool NeedsReplyTo(Climb c) =>
        c.Does.HasFlag(EscalationDoes.CopyContact) || (c.Does.HasFlag(EscalationDoes.Remind) && c.Team.IsExternal);

    // Which actions have a step to run: the candidates' ids and due dates only, planned, and what's marked left out; then
    // just those read in full for their emails (no article but its marking), with their teams and members.
    private async Task<List<Climb>> PlanClimbsAsync(IAppDbContext db, IReadOnlyList<EscalationStep> steps, EscalationScope scope, DateOnly today,
        CancellationToken ct)
    {
        var latest = ExternalCadence.LastDueInReach(today, Math.Max(0, -steps.Min(s => s.Offset)), clock.Days);
        var q = db.Actions.AsNoTracking().WhereOpen().Where(a => a.DueDate != null && a.DueDate <= latest);
        q = scope switch
        {
            EscalationScope.PartsOnly => q.Where(a => a.ParentId != null),
            EscalationScope.ExternalOnly => q.Where(a => a.Team!.IsExternal),
            _ => q,
        };
        var planned = (await q.Select(a => new { a.Id, Due = a.DueDate!.Value }).ToListAsync(ct))
            .Select(a => (a.Id, a.Due, Run: EscalationLadder.Plan(steps, a.Due, today, clock.Days)))
            .Where(x => x.Run.Count > 0).ToList();
        var keys = planned.SelectMany(x => x.Run.Select(s => StepKey(x.Id, x.Due, s))).ToList();
        var marked = (await db.NotificationMarks.AsNoTracking().Where(m => m.Kind == EscalationMark && keys.Contains(m.Key))
            .Select(m => m.Key).ToListAsync(ct)).ToHashSet();
        var todo = planned.Select(x => (x.Id, x.Due, Run: (IReadOnlyList<EscalationStep>)[.. x.Run.Where(s => !marked.Contains(StepKey(x.Id, x.Due, s)))]))
            .Where(x => x.Run.Count > 0).ToDictionary(x => x.Id);
        if (todo.Count == 0) return [];

        var ids = todo.Keys.ToList();
        var actions = await db.Actions.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new ActionItem
            {
                Id = a.Id, Number = a.Number, Title = a.Title, Description = a.Description, Kind = a.Kind, Priority = a.Priority, TeamId = a.TeamId,
                AssigneeId = a.AssigneeId, AssigneeName = a.AssigneeName, DueDate = a.DueDate, Status = a.Status, TicketRef = a.TicketRef,
                TicketUrl = a.TicketUrl, ParentId = a.ParentId, CreatedBy = a.CreatedBy, HandedFromTeamId = a.HandedFromTeamId, NoReminders = a.NoReminders,
                Article = a.Article == null ? null : new Article { Tlp = a.Article.Tlp },
            })
            .ToListAsync(ct);
        var teamIds = actions.Select(a => a.TeamId).Distinct().ToList();
        var teams = await db.Teams.AsNoTracking().Include(t => t.Members).Where(t => teamIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        foreach (var a in actions) a.Team = teams[a.TeamId];
        return [.. actions.Select(a => new Climb(a, todo[a.Id].Due, todo[a.Id].Run))];
    }

    // "{action}:{due}:{step}": once per action, due date and step.
    private static string StepKey(Guid action, DateOnly due, EscalationStep s) => $"{action:N}:{due:yyyy-MM-dd}:{s.Key}";

    // Notes one email's outcome on the actions it listed; 1 when it went. Email turned off isn't a failure to retry.
    private static int Went(EmailSendResult result, IEnumerable<Climb> climbs, string said)
    {
        foreach (var c in climbs)
        {
            switch (result.Status)
            {
                case EmailSendStatus.Sent:
                    c.Reached = true;
                    c.Say(said);
                    break;
                case EmailSendStatus.Disabled:
                    c.Say("not emailed: email is off");
                    break;
                default:
                    c.Failed = true;
                    break;
            }
        }
        return result.Status == EmailSendStatus.Sent ? 1 : 0;
    }

    private static List<ActionItem> InOrder(IEnumerable<Climb> climbs) =>
        [.. climbs.Select(c => c.Action).OrderBy(a => a.DueDate).ThenBy(a => a.Number)];

    // --- remind -------------------------------------------------------------------------------------------------

    // The owning team is reminded as it is reminded already: an external team by its own reminder email (one per team,
    // unless its reminders or the action's are off); an internal one at each action's assignee, else its mailbox, else its
    // members, with the due-soon or overdue email, leaving out whoever opted out of that.
    private async Task<int> EscalationRemindAsync(IAppDbContext db, List<Climb> climbs, IReadOnlyDictionary<Guid, string?> replies, DateOnly today,
        CancellationToken ct)
    {
        var sent = 0;
        foreach (var team in climbs.Where(c => c.Team.IsExternal).GroupBy(c => c.Action.TeamId))
            sent += await RemindExternalAsync([.. team], replies, today, ct);
        var inside = climbs.Where(c => !c.Team.IsExternal).ToList();
        return inside.Count == 0 ? sent : sent + await RemindInternalAsync(db, inside, today, ct);
    }

    private async Task<int> RemindExternalAsync(List<Climb> climbs, IReadOnlyDictionary<Guid, string?> replies, DateOnly today, CancellationToken ct)
    {
        var team = climbs[0].Team;
        var why = !team.RemindersOn ? $"not reminded: {team.Name}'s reminders are off"
            : string.IsNullOrWhiteSpace(team.ContactEmail) ? $"not reminded: {team.Name} has no contact email" : null;
        foreach (var c in climbs.Where(c => why is not null || c.Action.NoReminders)) c.Say(why ?? "not reminded: its reminders are off");
        var list = climbs.Where(c => why is null && !c.Action.NoReminders).ToList();
        if (list.Count == 0) return 0;
        var result = await email.SendAsync(RequestsEmail(EmailTemplateCatalog.ExternalReminder, team, team.ContactEmail!, team.ContactName ?? team.Name,
            InOrder(list), today, replies), ct);
        return Went(result, list, $"reminded {team.Name} at {team.ContactEmail}");
    }

    private async Task<int> RemindInternalAsync(IAppDbContext db, List<Climb> climbs, DateOnly today, CancellationToken ct)
    {
        var people = climbs.Select(c => c.Action.AssigneeId).OfType<string>().Concat(climbs.SelectMany(c => c.Team.Members.Select(m => m.UserId))).ToList();
        var late = await RecipientResolver.ResolveAsync(db, NotificationKinds.For(ReminderKind.Overdue), people, ct);
        var soon = await RecipientResolver.ResolveAsync(db, NotificationKinds.For(ReminderKind.DueSoon), people, ct);
        var byRecipient = new Dictionary<(string To, bool Late), List<Climb>>();
        foreach (var c in climbs)
        {
            var isLate = c.Due < today;
            var (to, optedOut) = RecipientsOf(c.Action, isLate ? late : soon);
            if (to.Count == 0) c.Say(optedOut.Count > 0 ? "not reminded: they opted out" : "not reminded: no one has an address");
            foreach (var address in to)
            {
                var key = (address.ToLowerInvariant(), isLate);
                (byRecipient.TryGetValue(key, out var list) ? list : byRecipient[key] = []).Add(c);
            }
        }
        var sent = 0;
        foreach (var ((to, isLate), list) in byRecipient)
        {
            var result = await email.SendAsync(DueEmail(to, InOrder(list), isLate, today), ct);
            sent += Went(result, list, "reminded " + to);
        }
        return sent;
    }

    // --- copy the escalation contact ----------------------------------------------------------------------------

    // One email per team to its escalation contact, listing its actions that reached such a step. Someone outside TIDE
    // may read it, so an external team's names no TIDE page; an internal team's links each request to its page.
    private async Task<int> CopyContactsAsync(List<Climb> climbs, IReadOnlyDictionary<Guid, string?> replies, DateOnly today, CancellationToken ct)
    {
        var sent = 0;
        foreach (var batch in climbs.GroupBy(c => c.Action.TeamId))
        {
            var list = batch.ToList();
            var team = list[0].Team;
            if (string.IsNullOrWhiteSpace(team.EscalationEmail))
            {
                foreach (var c in list) c.Say($"no escalation contact to copy for {team.Name}");
                continue;
            }
            var result = await email.SendAsync(RequestsEmail(EmailTemplateCatalog.Escalation, team, team.EscalationEmail, team.EscalationName ?? "there",
                InOrder(list), today, replies, team.IsExternal ? null : app.Value.BaseUrl), ct);
            sent += Went(result, list, $"copied {team.EscalationName ?? team.EscalationEmail}, the escalation contact for {team.Name}");
        }
        return sent;
    }

    // --- the actions' own changes -------------------------------------------------------------------------------

    // Every climb on one context, read afresh (so a colleague's edit since the scan read it isn't undone) and saved once. A
    // clash with a colleague's save at that moment loses the whole save, so each is then read again and saved on its own,
    // tried once more if it clashes again.
    private async Task ClimbAllAsync(List<Climb> climbs, DateOnly today)
    {
        if (climbs.Count == 0) return;
        try
        {
            await using var db = await dbs.CreateAsync(CancellationToken.None);
            var ids = climbs.Select(c => c.Action.Id).ToList();
            var rows = await db.Actions.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, CancellationToken.None);
            foreach (var c in climbs) Apply(db, rows.GetValueOrDefault(c.Action.Id), c, today);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            foreach (var c in climbs) await ClimbOneAsync(c, today);
        }
    }

    private async Task ClimbOneAsync(Climb c, DateOnly today)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await dbs.CreateAsync(CancellationToken.None);
            Apply(db, await db.Actions.FirstOrDefaultAsync(x => x.Id == c.Action.Id, CancellationToken.None), c, today);
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2) { /* read it again and redo the step */ }
        }
    }

    // One climb on its fresh row: what the steps do, unless it closed or moved its date meanwhile; its marks either way
    // (the steps were for that date).
    private void Apply(IAppDbContext db, ActionItem? a, Climb c, DateOnly today)
    {
        if (a is { IsOpen: true } && a.DueDate == c.Due) Escalate(db, a, c, today);
        foreach (var key in c.Keys) db.NotificationMarks.Add(new NotificationMark { Kind = EscalationMark, Key = key, SentAtUtc = DateTimeOffset.UtcNow });
    }

    private void Escalate(IAppDbContext db, ActionItem a, Climb c, DateOnly today)
    {
        var parts = new List<string>();
        var (priority, flagged) = (a.Priority, a.EscalatedForDue);
        if (c.Does.HasFlag(EscalationDoes.RaisePriority))
        {
            var raised = EscalationLadder.Raised(a.Priority);
            parts.Add(raised == a.Priority ? $"priority already {a.Priority}" : $"priority {a.Priority} → {raised}");
            a.Priority = raised;
        }
        if (c.Does.HasFlag(EscalationDoes.Flag))
        {
            parts.Add(a.IsEscalated ? "still flagged escalated" : "flagged escalated");
            if (!a.IsEscalated) (a.EscalatedForDue, a.EscalatedAtUtc) = (a.DueDate, DateTimeOffset.UtcNow);
        }
        parts.AddRange(c.Said);
        // An editor open on the action is refused only when the step changed what it shows.
        if (a.Priority != priority || a.EscalatedForDue != flagged) a.EditVersion = Guid.NewGuid();
        db.ActionEvents.Add(new ActionEvent
        {
            ActionId = a.Id, AtUtc = DateTimeOffset.UtcNow, ByName = EscalatedBy,
            Text = ActionService.ClipEvent($"Escalated, {EscalationLadder.Lateness(c.Due, today, clock.Days)}: {string.Join("; ", parts)}."),
        });
    }
}
