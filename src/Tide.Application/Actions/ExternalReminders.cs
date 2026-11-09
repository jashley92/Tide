using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Application.Preferences;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using static Tide.Application.Actions.ActionEmails;
using static Tide.Application.Distribution.EmailComposer;

namespace Tide.Application.Actions;

/// <summary>
/// When an external team is reminded about an action, by its cadence, kept pure so it's tested on its own. Days are the
/// organization's working days (its holidays too): the scan runs only on one, and counts in them.
/// </summary>
/// <summary>What keeps an external team from being emailed, each with its own fix.</summary>
public enum NotEmailedCause
{
    /// <summary>External team reminders are off for everyone (an administrator's, on Teams &amp; SLA).</summary>
    OffForEveryone = 0,

    /// <summary>The team's own reminders are off (whoever works actions, on External teams).</summary>
    TeamOff = 1,

    /// <summary>The team has no contact email (likewise).</summary>
    NoContact = 2,
}

public sealed record NotEmailedReason(NotEmailedCause Cause, string Text);

public static class ExternalCadence
{
    /// <summary>
    /// Why <paramref name="team"/> won't be emailed its reminders, or null when it will be (or isn't external): the
    /// scan is off for everyone (<paramref name="scanOn"/>), the team's own reminders are off, or it has no contact email.
    /// Said wherever work is given to one, so a request isn't left with someone who never hears of it.
    /// </summary>
    public static string? NotEmailedWhy(Team team, bool scanOn) => NotEmailed(team, scanOn)?.Text;

    /// <summary>Why <paramref name="team"/> won't be emailed, as a reason (so the fix can be offered) and its words; null
    /// when it will be, or isn't external.</summary>
    public static NotEmailedReason? NotEmailed(Team team, bool scanOn) =>
        !team.IsExternal ? null
        : !scanOn ? new(NotEmailedCause.OffForEveryone, $"External team reminders are off for everyone, so {team.Name} won't be emailed about this.")
        : !team.RemindersOn ? new(NotEmailedCause.TeamOff, $"{team.Name}'s reminders are off, so they won't be emailed about this.")
        : string.IsNullOrWhiteSpace(team.ContactEmail) ? new(NotEmailedCause.NoContact, $"{team.Name} has no contact email, so they won't be emailed about this.")
        : null;

    /// <summary>The step before the due date.</summary>
    public const string Before = "before";

    /// <summary>
    /// Which reminder is due for an action due <paramref name="due"/> on <paramref name="today"/>, or null for none: "before"
    /// from <paramref name="before"/> working days before it to the day itself (0: on the due day only, or the working day
    /// before it when it falls on a day off); then, once it's a working day late, "late-0", and every
    /// <paramref name="every"/> working days a new step ("late-1", "late-2"…), or only "late-0" when <paramref name="every"/>
    /// is null. Each step is sent once (its mark), so a missed day sends it the next, and never twice.
    /// </summary>
    public static string? Step(DateOnly today, DateOnly due, int before, int? every, WorkingDays days)
    {
        if (today <= due) return today >= WindowStart(due, before, days) ? Before : null;
        var late = -ActionSla.BusinessDaysUntil(today, due, days);
        if (late < 1) return null;   // past a due date on a day off, but not yet a working day late
        var step = every is { } n && n > 0 ? (late - 1) / n : 0;
        return "late-" + step.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The first day of the reminder before <paramref name="due"/>: <paramref name="before"/> working days ahead of
    /// it (counting from the working day it falls on, or the one before when it's a day off).</summary>
    public static DateOnly WindowStart(DateOnly due, int before, WorkingDays days)
    {
        var start = days.IsWorkingDay(due) ? due : days.Previous(due);
        for (var i = 0; i < before; i++) start = days.Previous(start);
        return start;
    }

    /// <summary>The latest due date whose window, starting <paramref name="before"/> working days ahead of it, has opened by
    /// <paramref name="today"/>: those working days ahead, and the days off just after them (a due date on a day off is
    /// reached from the working day before). Nothing due later can be due a reminder or a step yet.</summary>
    public static DateOnly LastDueInReach(DateOnly today, int before, WorkingDays days) =>
        days.Next(Enumerable.Range(0, before).Aggregate(today, (d, _) => days.Next(d))).AddDays(-1);

    /// <summary>
    /// Where a reply to an external team's reminder about <paramref name="a"/> goes: for a part, its parent's assignee, else
    /// the parent's team mailbox; for a top-level one, its own assignee, else the mailbox of the team that handed it over,
    /// else whoever created it. Null when none has an address.
    /// </summary>
    public static string? ReplyTo(ActionItem a, (string? AssigneeId, Guid TeamId)? parent, Func<string?, string?> personEmail, Func<Guid?, string?> mailbox) =>
        parent is { } p
            ? personEmail(p.AssigneeId) ?? mailbox(p.TeamId)
            : personEmail(a.AssigneeId) ?? mailbox(a.HandedFromTeamId) ?? personEmail(a.CreatedBy);
}

public sealed partial class ReminderService
{
    private const string ExternalMark = "external-reminder";

    // Each external team with its reminders on, and an address, gets one email listing its open actions whose reminder is
    // due by its cadence (ExternalCadence), unless an action is set not to remind. Replies go to whoever follows each one
    // (ExternalCadence.ReplyTo). A mark per action, due date and step stops a repeat; a step marked is never sent again,
    // and moving the due date starts the cadence over.
    private async Task<Scan> ExternalAsync(CancellationToken ct)
    {
        var today = clock.Today;
        await using var db = await dbs.CreateAsync(ct);
        var teams = await db.Teams.AsNoTracking()
            .Where(t => t.IsExternal && !t.IsRetired && t.RemindersOn && t.ContactEmail != null && t.ContactEmail != "")
            .ToDictionaryAsync(t => t.Id, ct);
        if (teams.Count == 0) return 0;
        var ids = teams.Keys.ToList();
        // Nothing due after the furthest any team looks ahead can be due a reminder.
        var latest = ExternalCadence.LastDueInReach(today, teams.Values.Max(t => t.RemindBeforeDays), clock.Days);
        // Only what the email and its Reply-To use: no article content, only its marking.
        var open = await db.Actions.AsNoTracking().WhereOpen()
            .Where(a => ids.Contains(a.TeamId) && a.DueDate != null && a.DueDate <= latest && !a.NoReminders)
            .Select(a => new ActionItem
            {
                Id = a.Id, Number = a.Number, TeamId = a.TeamId, Title = a.Title, Description = a.Description, DueDate = a.DueDate,
                TicketRef = a.TicketRef, TicketUrl = a.TicketUrl, ParentId = a.ParentId, AssigneeId = a.AssigneeId, CreatedBy = a.CreatedBy,
                HandedFromTeamId = a.HandedFromTeamId, HandedAtUtc = a.HandedAtUtc, Article = a.Article == null ? null : new Article { Tlp = a.Article.Tlp },
            })
            .ToListAsync(ct);
        var due = open.Select(a => (Action: a, Step: ExternalCadence.Step(today, a.DueDate!.Value, teams[a.TeamId].RemindBeforeDays,
                teams[a.TeamId].RemindOverdueEveryDays, clock.Days)))
            .Where(x => x.Step is not null).Select(x => (x.Action, Key: Key(x.Action, x.Step!), Unhanded: UnhandedKey(x.Action, x.Step!))).ToList();
        if (due.Count == 0) return 0;
        var keys = due.Select(x => x.Key).Concat(due.Where(x => x.Key != x.Unhanded).Select(x => x.Unhanded)).ToList();
        // With them, in the same read, the contacts' marks for today: who the ladder already reminded about what.
        var contacts = teams.Values.ToDictionary(t => t.Id, t => Hash(t.ContactEmail!));
        var contactKeys = due.Select(x => ContactKey(x.Action.Id, today, contacts[x.Action.TeamId])).ToList();
        var marks = await db.NotificationMarks.AsNoTracking()
            .Where(m => (m.Kind == ExternalMark && keys.Contains(m.Key)) || (m.Kind == ContactMark && contactKeys.Contains(m.Key)))
            .Select(m => new { m.Kind, m.Key, m.SentAtUtc }).ToListAsync(ct);
        var marked = marks.Where(m => m.Kind == ExternalMark).ToDictionary(m => m.Key, m => m.SentAtUtc);
        var remindedToday = marks.Where(m => m.Kind == ContactMark).Select(m => m.Key).ToHashSet();
        // A mark without the team (as they were written before the team was in them) is the current team's when it was
        // written after the last hand-off.
        due = [.. due.Where(x => !marked.ContainsKey(x.Key)
                                 && !(x.Action.HandedAtUtc is { } handed && marked.TryGetValue(x.Unhanded, out var at) && at >= handed))];
        if (due.Count == 0) return 0;

        var replies = await ReplyToAsync(db, due.Select(x => x.Action).ToList(), ct);
        var ceiling = await CeilingAsync(ct);
        var sent = 0;
        foreach (var batch in due.GroupBy(x => x.Action.TeamId))
        {
            var team = teams[batch.Key];
            var step = batch.ToDictionary(x => x.Action.Id, x => x.Key);
            var (result, went, skipped) = await SendExternalReminderAsync(db, team, batch.Select(x => x.Action), contacts[team.Id], remindedToday, replies,
                ceiling, today, ct);
            // The escalation ladder's remind step reached the contact about these today: their step is done all the same,
            // so it doesn't go the next day instead.
            if (skipped.Count > 0)
            {
                foreach (var a in skipped)
                    db.ActionEvents.Add(new ActionEvent { ActionId = a.Id, AtUtc = DateTimeOffset.UtcNow, ByName = EscalatedBy, Text = Sentence(SkippedReminder(team.ContactEmail!)) });
                await MarkAsync(db, ExternalMark, skipped.Select(a => step[a.Id]).ToList());
            }
            if (result is not { MayHaveGone: true }) continue;
            sent++;
            await MarkAsync(db, ExternalMark, went.Select(a => step[a.Id]).ToList());
        }
        return sent;
    }

    // --- one reminder per contact per action per day ------------------------------------------------------------

    // That an external team's contact was reminded about an action today, by its cadence or by the ladder's remind step:
    // the other is then skipped that day.
    private const string ContactMark = "contact-reminded";

    // "{action}:{day}:{contact}", the contact's address as its short hash (Hash) so a long one fits the key.
    private static string ContactKey(Guid action, DateOnly day, string contactHash) => $"{action:N}:{day:yyyy-MM-dd}:{contactHash}";

    // The contacts' marks among these keys: who was reminded about what today.
    private static async Task<HashSet<string>> ContactMarksAsync(IAppDbContext db, IReadOnlyCollection<string> keys, CancellationToken ct) =>
        keys.Count == 0 ? [] : (await db.NotificationMarks.AsNoTracking().Where(m => m.Kind == ContactMark && keys.Contains(m.Key))
            .Select(m => m.Key).ToListAsync(ct)).ToHashSet();

    /// <summary>Why a reminder didn't go: the contact already had one about the action that day. The ladder lists it
    /// among a step's outcomes; the cadence's history line is it as a sentence (<see cref="Sentence"/>).</summary>
    internal static string SkippedReminder(string contact) => $"reminder skipped: {contact} was already reminded today";

    internal static string Sentence(string phrase) => phrase.Length == 0 ? phrase : char.ToUpperInvariant(phrase[0]) + phrase[1..] + ".";

    // The one reminder email to an external team's contact, for the cadence and the ladder's remind step alike: the
    // actions the contact was already reminded about today (in remindedToday, by the other) are skipped; the rest go in
    // one email, in due order, and when it went or may have, each is marked so the other skips it today. Returns the
    // send's result (null when every one was skipped) and which went and which were skipped; each caller records its own.
    // Held to the team edition's ceiling (RequestsEmail): one marked above it is counted, not named, and is marked as
    // reminded all the same.
    private async Task<(EmailSendResult? Result, IReadOnlyList<ActionItem> Went, IReadOnlyList<ActionItem> Skipped)> SendExternalReminderAsync(
        IAppDbContext db, Team team, IEnumerable<ActionItem> actions, string contactHash, IReadOnlySet<string> remindedToday,
        IReadOnlyDictionary<Guid, string?> replies, TlpLevel ceiling, DateOnly today, CancellationToken ct)
    {
        var all = actions.ToList();
        var skipped = all.Where(a => remindedToday.Contains(ContactKey(a.Id, today, contactHash))).ToList();
        var go = all.Except(skipped).OrderBy(a => a.DueDate).ThenBy(a => a.Number).ToList();
        if (go.Count == 0) return (null, go, skipped);
        var result = await SendAsync(RequestsEmail(EmailTemplateCatalog.ExternalReminder, team, team.ContactEmail!, team.ContactName ?? team.Name,
            go, today, replies, ceiling), ct);
        if (result.MayHaveGone) await MarkAsync(db, ContactMark, go.Select(a => ContactKey(a.Id, today, contactHash)).ToList());
        return (result, go, skipped);
    }

    // An email about a team's requests, to someone who may not use TIDE (the external reminder template, and the escalation
    // contact's): the team, who it's to (<paramref name="contact"/>), the request list (linked to TIDE only with
    // <paramref name="tideUrl"/>), and replies to whoever follows each one. With a <paramref name="ceiling"/> (the team
    // edition's, for anyone outside the organization), a request whose article is marked above it isn't named: no title,
    // no details, nothing in the marking. It's still counted, and the list says how many more there are and who to ask.
    private OutgoingEmail RequestsEmail(EmailTemplate template, Team team, string to, string contact, IReadOnlyList<ActionItem> list, DateOnly today,
        IReadOnlyDictionary<Guid, string?> replies, TlpLevel? ceiling, string? tideUrl = null)
    {
        var (shown, withheld) = Held(list, ceiling);
        var t = _templates.Render(template, new Dictionary<string, string>
        {
            ["Team"] = team.Name,
            ["Contact"] = contact,
            ["From"] = clock.TeamName,
            ["Requests"] = $"{list.Count} request{S(list.Count)}",
            ["RequestList"] = RequestList(shown, today, clock.Days, tideUrl, withheld, clock.TeamName),
        });
        var html = Shell(t.Heading, clock.TeamName + " · " + DayText(today), MarkingLine(shown) + t.BodyHtml, t.Footer);
        var replyTo = list.Select(a => replies.GetValueOrDefault(a.Id)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new OutgoingEmail([to], Marked(t.Subject, shown), html, ToText(html), [], replyTo) { Template = t.TemplateId };
    }

    /// <summary>The requests that may be named in an email held to <paramref name="ceiling"/> (an action raised on its own
    /// carries no marking, so it may), and how many may not. No ceiling: all of them.</summary>
    internal static (IReadOnlyList<ActionItem> Shown, int Withheld) Held(IReadOnlyList<ActionItem> list, TlpLevel? ceiling)
    {
        if (ceiling is not { } max) return (list, 0);
        var shown = list.Where(a => a.Article is null || a.Article.Tlp <= max).ToList();
        return (shown, list.Count - shown.Count);
    }

    /// <summary>What an email says in place of the requests it doesn't name: "1 more request is marked above what we share
    /// by email: ask {sender}."</summary>
    internal static string WithheldText(int withheld, string sender) =>
        $"{withheld} more request{S(withheld)} {(withheld == 1 ? "is" : "are")} marked above what we share by email: ask {sender}.";

    // An action's mark for its reminder at a step: "{action}:{due}:{step}", and for one handed from another team, the team
    // it's with now after that (":{team}"). The team it was handed to hears only through its reminders, so a step the
    // team before it was sent is sent to it too; each team still gets each step once.
    private static string Key(ActionItem a, string step) => UnhandedKey(a, step) + (a.HandedAtUtc is null ? "" : $":{a.TeamId:N}");

    // The mark without the team, as every one was before the team was named in it.
    private static string UnhandedKey(ActionItem a, string step) => $"{a.Id:N}:{a.DueDate:yyyy-MM-dd}:{step}";

    // Where replies about each action go, read for them all at once: their parents, the people's addresses
    // (RecipientResolver.AddressesAsync) and the team mailboxes.
    private static async Task<Dictionary<Guid, string?>> ReplyToAsync(IAppDbContext db, List<ActionItem> actions, CancellationToken ct)
    {
        var parentIds = actions.Select(a => a.ParentId).OfType<Guid>().Distinct().ToList();
        var parents = await db.Actions.AsNoTracking().Where(p => parentIds.Contains(p.Id))
            .Select(p => new { p.Id, p.AssigneeId, p.TeamId }).ToDictionaryAsync(p => p.Id, ct);
        var people = actions.SelectMany(a => new[] { a.AssigneeId, a.CreatedBy }).Concat(parents.Values.Select(p => p.AssigneeId))
            .OfType<string>().Where(s => s.Length > 0).Distinct().ToList();
        var emails = await RecipientResolver.AddressesAsync(db, people, ct);
        var teamIds = actions.Select(a => a.HandedFromTeamId).OfType<Guid>().Concat(parents.Values.Select(p => p.TeamId)).Distinct().ToList();
        var mailboxes = await db.Teams.AsNoTracking().Where(t => teamIds.Contains(t.Id) && t.Email != null && t.Email != "")
            .ToDictionaryAsync(t => t.Id, t => t.Email!, ct);
        string? Person(string? id) => id is not null && emails.TryGetValue(id, out var e) ? e : null;
        string? Mailbox(Guid? id) => id is { } t && mailboxes.TryGetValue(t, out var e) ? e : null;
        return actions.ToDictionary(a => a.Id, a => ExternalCadence.ReplyTo(a,
            a.ParentId is { } pid && parents.TryGetValue(pid, out var p) ? (p.AssigneeId, p.TeamId) : null, Person, Mailbox));
    }

    /// <summary>
    /// An external team's requests, for someone without TIDE: each one's title, what's asked (its details as written on
    /// the action, nothing from its article), when it's due and how late, and its ticket link. No TIDE number or link,
    /// unless <paramref name="tideUrl"/> (this server's address) is given for readers who use TIDE: then each links to its page.
    /// <paramref name="withheld"/> more, marked above what's shared by email, are said after them, as a number, with who
    /// to ask (<paramref name="sender"/>).
    /// </summary>
    internal static string RequestList(IEnumerable<ActionItem> actions, DateOnly today, WorkingDays days, string? tideUrl = null,
        int withheld = 0, string? sender = null)
    {
        var sb = new StringBuilder();
        foreach (var a in actions)
        {
            var late = a.DueDate is { } d && d < today ? -ActionSla.BusinessDaysUntil(today, d, days) : 0;
            var color = late > 0 ? Palette.Threat : Palette.Warn;
            sb.Append(CultureInfo.InvariantCulture, $"<div style=\"margin:0 0 14px;padding:0 0 12px;border-bottom:1px solid {Palette.Border};\">");
            sb.Append(CultureInfo.InvariantCulture, $"<div style=\"font-weight:700;\">{Esc(a.Title)}</div>");
            if (!string.IsNullOrWhiteSpace(a.Description))
                sb.Append(CultureInfo.InvariantCulture, $"<div style=\"margin:4px 0;white-space:pre-line;\">{Esc(a.Description)}</div>");
            sb.Append(CultureInfo.InvariantCulture, $"<div style=\"font-size:13px;color:{color};\">{Esc(DueText(a.DueDate, today, late))}</div>");
            if (!string.IsNullOrWhiteSpace(a.TicketUrl))
                sb.Append(CultureInfo.InvariantCulture,
                    $"<div style=\"font-size:13px;\">Ticket: <a style=\"color:{Palette.Link};\" href=\"{Esc(a.TicketUrl)}\">{Esc(a.TicketRef ?? a.TicketUrl)}</a></div>");
            if (!string.IsNullOrWhiteSpace(tideUrl))
                sb.Append(CultureInfo.InvariantCulture,
                    $"<div style=\"font-size:13px;\"><a style=\"color:{Palette.Link};\" href=\"{Esc(tideUrl.TrimEnd('/') + "/actions/" + a.Number)}\">A-{a.Number} in TIDE</a></div>");
            sb.Append("</div>");
        }
        if (withheld > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $"<p style=\"margin:0 0 14px;font-size:13px;color:{Palette.Muted};\">{Esc(WithheldText(withheld, sender ?? "us"))}</p>");
        return sb.ToString();
    }

    // "Due Fri Nov 20", "Due today", or "Was due Mon Nov 16: 3 business days overdue".
    private static string DueText(DateOnly? due, DateOnly today, int late)
    {
        if (due is not { } d) return "No due date";
        var day = d.ToString("ddd MMM d", CultureInfo.InvariantCulture);
        if (d == today) return "Due today";
        if (d > today) return "Due " + day;
        return late > 0 ? $"Was due {day}: {late} business day{S(late)} overdue" : $"Was due {day}";
    }
}
