using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using static Tide.Application.Distribution.EmailComposer;

namespace Tide.Application.Actions;

/// <summary>The reminder and digest emails (config section <c>Notifications</c>). Every one is off by default.</summary>
public sealed class ReminderOptions
{
    /// <summary>When, in the organization's time zone, the day's due-soon and overdue reminders go ("08:00").</summary>
    public string ReminderTime { get; set; } = "08:00";

    public Toggle DueSoon { get; set; } = new();

    /// <summary>At <see cref="ReminderTime"/>, tell whoever presents the brief the next business day (and doesn't today).</summary>
    public Toggle Presenting { get; set; } = new();
    public Toggle Overdue { get; set; } = new();
    public DigestOptions Digest { get; set; } = new();
    public BriefNotReadyOptions BriefNotReady { get; set; } = new();

    public sealed class Toggle
    {
        public bool Enabled { get; set; }
    }

    public sealed class DigestOptions
    {
        public bool Enabled { get; set; }

        /// <summary>The day and local time each team's weekly digest goes.</summary>
        public DayOfWeek Day { get; set; } = DayOfWeek.Monday;
        public string Time { get; set; } = "08:00";
    }

    public sealed class BriefNotReadyOptions
    {
        public bool Enabled { get; set; }

        /// <summary>When, on a business day, the presenting analyst is nudged if today's brief isn't ready ("10:00").</summary>
        public string Time { get; set; } = "10:00";

        /// <summary>Who's told when there's no brief yet (or its analyst has no address): the team lead, a team mailbox.</summary>
        public string[] Recipients { get; set; } = [];
    }
}

/// <summary>The scans that send reminders and digests.</summary>
public enum ReminderKind
{
    DueSoon = 0,
    Overdue = 1,
    Digest = 2,
    BriefNotReady = 3,
    Presenting = 4,
}

/// <summary>A scan's last run, for the administration page.</summary>
public sealed record ReminderRun(ReminderKind Kind, DateTimeOffset AtUtc, int Sent, string? Error);

/// <summary>The scans' last runs, kept in memory (a restart clears it). Singleton.</summary>
public sealed class ReminderLog
{
    private readonly ConcurrentDictionary<ReminderKind, ReminderRun> _runs = new();

    public void Record(ReminderRun run) => _runs[run.Kind] = run;

    public ReminderRun? Last(ReminderKind kind) => _runs.TryGetValue(kind, out var r) ? r : null;
}

/// <summary>
/// Reminders and digests about actions, and the morning nudge about an unready brief. Each scan finds what's due,
/// sends one email per recipient (batched, not one per action), and records a mark so the same thing is never sent
/// twice. An action goes to its assignee; an unassigned one to its team's mailbox, else to the team's members. The
/// scans read actions and write only marks, so they run as the system, not as a person.
/// </summary>
public sealed class ReminderService(IAppDbContextFactory dbs, IEmailSender email, OrgClock clock, IOptionsMonitor<ReminderOptions> options,
    IOptions<AppOptions> app, ReminderLog log, ICurrentUser user, Rotation.IPresenterLookup? presenters = null, EmailTemplateStore? templates = null)
{
    // The wording an administrator set in Administration › Email templates, or the defaults.
    private readonly EmailTemplateStore _templates = templates ?? EmailTemplateStore.Defaults;

    public ReminderOptions Options => options.CurrentValue;

    public bool IsEnabled(ReminderKind kind) => kind switch
    {
        ReminderKind.DueSoon => Options.DueSoon.Enabled,
        ReminderKind.Overdue => Options.Overdue.Enabled,
        ReminderKind.Digest => Options.Digest.Enabled,
        ReminderKind.Presenting => Options.Presenting.Enabled,
        _ => Options.BriefNotReady.Enabled,
    };

    /// <summary>
    /// Runs every enabled scan that's due now (its time of day has come). Each scan's failure is its own: it's logged
    /// and the others still run.
    /// </summary>
    public async Task RunDueAsync(CancellationToken ct = default)
    {
        foreach (var kind in Enum.GetValues<ReminderKind>())
        {
            if (IsEnabled(kind) && IsTime(kind)) await RunAsync(kind, rethrow: false, ct);
        }
    }

    // One scan at a time in this process: an administrator's "Run now" and the poller can't both read the marks, both
    // send, and then both write the same marks.
    private static readonly SemaphoreSlim Running = new(1, 1);

    /// <summary>An administrator runs a scan now, from Administration (marks still stop repeats).</summary>
    public Task<int> RunNowAsync(ReminderKind kind, CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new Security.ForbiddenException("Only an administrator can run a reminder scan.");
        return RunAsync(kind, ct);
    }

    public ReminderRun? LastRun(ReminderKind kind) => log.Last(kind);

    /// <summary>Runs one scan now, whatever the time (marks still stop repeats). Returns how many emails went.</summary>
    public Task<int> RunAsync(ReminderKind kind, CancellationToken ct = default) => RunAsync(kind, rethrow: true, ct);

    // A failure is logged either way; the scheduled run doesn't rethrow it, so the next scan still runs.
    private async Task<int> RunAsync(ReminderKind kind, bool rethrow, CancellationToken ct)
    {
        await Running.WaitAsync(ct);
        try
        {
            var sent = kind switch
            {
                ReminderKind.DueSoon => await DueAsync(overdue: false, ct),
                ReminderKind.Overdue => await DueAsync(overdue: true, ct),
                ReminderKind.Digest => await DigestAsync(ct),
                ReminderKind.Presenting => await PresentingAsync(ct),
                _ => await BriefNotReadyAsync(ct),
            };
            log.Record(new ReminderRun(kind, DateTimeOffset.UtcNow, sent, null));
            return sent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Record(new ReminderRun(kind, DateTimeOffset.UtcNow, 0, ex.GetType().Name + ": " + ex.Message));
            if (rethrow) throw;
            return 0;
        }
        finally
        {
            Running.Release();
        }
    }

    // A mark is saved as soon as its email went (and whatever the caller's token says), so a scan that stops partway
    // (shutdown, a database error, a later send failing) doesn't email the same people again next time.
    private static async Task MarkAsync(IAppDbContext db, string kind, IEnumerable<string> keys)
    {
        var added = false;
        foreach (var key in keys)
        {
            db.NotificationMarks.Add(new NotificationMark { Kind = kind, Key = key, SentAtUtc = DateTimeOffset.UtcNow });
            added = true;
        }
        if (added) await db.SaveChangesAsync(CancellationToken.None);
    }

    private bool IsTime(ReminderKind kind)
    {
        var now = clock.Now;
        var time = TimeOnly.FromDateTime(now.DateTime);
        return kind switch
        {
            ReminderKind.Digest => now.DayOfWeek == Options.Digest.Day && time >= Parse(Options.Digest.Time, 8),
            ReminderKind.BriefNotReady => clock.Days.IsWorkingDay(clock.Today) && time >= Parse(Options.BriefNotReady.Time, 10) && time < clock.MeetingTime,
            _ => clock.Days.IsWorkingDay(clock.Today) && time >= Parse(Options.ReminderTime, 8),
        };
    }

    private static TimeOnly Parse(string? text, int fallbackHour) =>
        TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out var t) ? t : new TimeOnly(fallbackHour, 0);

    // --- due soon and overdue -----------------------------------------------------------------------------------

    private async Task<int> DueAsync(bool overdue, CancellationToken ct)
    {
        var today = clock.Today;
        var soon = clock.Days.Next(today);
        var kind = overdue ? "overdue" : "due-soon";
        await using var db = await dbs.CreateAsync(ct);
        var open = db.Actions.AsNoTracking().Include(a => a.Article).Include(a => a.Team).ThenInclude(t => t!.Members)
            .WhereOpen().Where(a => a.DueDate != null);
        var actions = await (overdue ? open.Where(a => a.DueDate < today) : open.Where(a => a.DueDate >= today && a.DueDate <= soon)).ToListAsync(ct);
        var marks = (await db.NotificationMarks.AsNoTracking().Where(m => m.Kind == kind).Select(m => m.Key).ToListAsync(ct)).ToHashSet();
        var pending = actions.Where(a => !marks.Contains(MarkKey(a))).ToList();
        if (pending.Count == 0) return 0;

        var emails = await AddressesAsync(db, pending.Select(a => a.AssigneeId).OfType<string>()
            .Concat(pending.SelectMany(a => a.Team!.Members.Select(m => m.UserId))), ct);
        var byRecipient = new Dictionary<string, List<ActionItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in pending)
            foreach (var to in RecipientsOf(a, emails).Where(to => !marks.Contains(RecipientKey(a, to))))
                (byRecipient.TryGetValue(to, out var list) ? list : byRecipient[to] = []).Add(a);

        // Each recipient's email is marked as it goes, so a scan stopped partway resumes with only those not reached.
        // Once a scan has tried everyone, an action anyone got is marked whole, so a failing address isn't retried
        // every poll; an action whose every recipient failed is tried at the next scan.
        var sent = 0;
        var reached = new HashSet<Guid>();
        foreach (var (to, list) in byRecipient)
        {
            var t = _templates.Render(overdue ? EmailTemplateCatalog.Overdue : EmailTemplateCatalog.DueSoon, new Dictionary<string, string>
            {
                ["Actions"] = $"{list.Count} action{S(list.Count)}",
                ["OverdueActions"] = $"{list.Count} overdue action{S(list.Count)}",
                ["ActionList"] = Table(list, today),
            });
            var html = Shell(t.Heading, clock.TeamName + " · " + DayText(today), MarkingLine(list) + t.BodyHtml, Footer);
            var result = await email.SendAsync(new OutgoingEmail([to], Marked(t.Subject, list), html, ToText(html), []), ct);
            if (result.Status != EmailSendStatus.Sent) continue;
            sent++;
            reached.UnionWith(list.Select(a => a.Id));
            await MarkAsync(db, kind, list.Select(a => RecipientKey(a, to)).ToList());
        }
        await MarkAsync(db, kind, pending.Where(a => reached.Contains(a.Id)).Select(MarkKey).ToList());
        return sent;
    }

    // One recipient's reminder about an action (the address hashed: keys are short).
    private static string RecipientKey(ActionItem a, string to) =>
        MarkKey(a) + "|" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(to.Trim().ToLowerInvariant())))[..16];

    // Once per action per due date: moving the date earns a new reminder.
    private static string MarkKey(ActionItem a) => $"{a.Id:N}:{a.DueDate:yyyy-MM-dd}";

    private static IEnumerable<string> RecipientsOf(ActionItem a, IReadOnlyDictionary<string, string> emails)
    {
        if (a.AssigneeId is { } id && emails.TryGetValue(id, out var mine)) return [mine];
        if (!string.IsNullOrWhiteSpace(a.Team?.Email)) return [a.Team.Email];
        return a.Team?.Members.Select(m => emails.GetValueOrDefault(m.UserId)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase) ?? [];
    }

    private static async Task<Dictionary<string, string>> AddressesAsync(IAppDbContext db, IEnumerable<string> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Sid) && u.Email != null && u.Email != "")
            .ToDictionaryAsync(u => u.Sid, u => u.Email!, ct);
    }

    // --- weekly digest ------------------------------------------------------------------------------------------

    private async Task<int> DigestAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var weekAgo = ActionQueries.LastSevenDaysFrom(today);
        await using var db = await dbs.CreateAsync(ct);
        var teams = await db.Teams.AsNoTracking().Include(t => t.Members).Where(t => !t.IsRetired).ToListAsync(ct);
        var marks = (await db.NotificationMarks.AsNoTracking().Where(m => m.Kind == "digest").Select(m => m.Key).ToListAsync(ct)).ToHashSet();
        var emails = await AddressesAsync(db, teams.SelectMany(t => t.Members.Select(m => m.UserId)), ct);
        var sent = 0;
        foreach (var team in teams)
        {
            var key = $"{team.Id:N}:{weekStart:yyyy-MM-dd}";
            if (marks.Contains(key)) continue;
            var to = !string.IsNullOrWhiteSpace(team.Email) ? [team.Email!]
                : team.Members.Select(m => emails.GetValueOrDefault(m.UserId)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (to.Count == 0) continue;
            var actions = await db.Actions.AsNoTracking().Include(a => a.Article).Where(a => a.TeamId == team.Id)
                .WhereOpenOrClosedSince(weekAgo).ToListAsync(ct);
            var open = actions.Where(a => a.IsOpen).OrderBy(a => a.DueDate ?? DateOnly.MaxValue).ToList();
            var closed = actions.Where(a => !a.IsOpen).OrderBy(a => a.ClosedDate).ToList();
            var overdue = open.Count(a => ActionSla.StateOf(a, today, clock.Days) == SlaState.Overdue);
            var t = _templates.Render(EmailTemplateCatalog.Digest, new Dictionary<string, string>
            {
                ["Team"] = team.Name,
                ["WeekOf"] = weekStart.ToString("MMM d", CultureInfo.InvariantCulture),
                ["OpenCount"] = open.Count.ToString(CultureInfo.InvariantCulture),
                ["OverdueCount"] = OverdueCount(overdue),
                ["ClosedCount"] = closed.Count.ToString(CultureInfo.InvariantCulture),
                ["OpenList"] = open.Count > 0 ? ListHeading("Open", top: 8) + Table(open, today) : "",
                ["ClosedList"] = closed.Count > 0 ? ListHeading("Closed in the last week", top: 14) + Table(closed, today) : "",
            });
            var html = Shell(t.Heading, clock.TeamName + " · week of " + weekStart.ToString("MMMM d", CultureInfo.InvariantCulture), MarkingLine(actions) + t.BodyHtml, Footer);
            var result = await email.SendAsync(new OutgoingEmail(to, Marked(t.Subject, actions), html, ToText(html), []), ct);
            if (result.Status != EmailSendStatus.Sent) continue;
            await MarkAsync(db, "digest", [key]);
            sent++;
        }
        return sent;
    }

    // --- brief not ready ----------------------------------------------------------------------------------------

    private async Task<int> BriefNotReadyAsync(CancellationToken ct)
    {
        var today = clock.Today;
        if (!clock.Days.IsWorkingDay(today)) return 0;
        var key = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var db = await dbs.CreateAsync(ct);
        if (await db.NotificationMarks.AnyAsync(m => m.Kind == "brief-not-ready" && m.Key == key, ct)) return 0;
        var brief = await db.Briefs.AsNoTracking().Include(b => b.Items).FirstOrDefaultAsync(b => b.BriefDate == today, ct);
        if (brief is not null && brief.Status != BriefStatus.Draft) return 0;

        var to = new List<string>();
        // The brief's analyst, or (no brief yet) whoever the rotation says presents today.
        var analystId = brief?.AnalystId ?? (presenters is null ? null : (await presenters.PresenterAsync(today, ct))?.Id);
        if (analystId is not null && await db.Users.AsNoTracking().Where(u => u.Sid == analystId).Select(u => u.Email).FirstOrDefaultAsync(ct) is { Length: > 0 } analyst)
            to.Add(analyst);
        else to.AddRange(Options.BriefNotReady.Recipients.Where(r => System.Net.Mail.MailAddress.TryCreate(r, out _)));
        if (to.Count == 0) return 0;

        var what = brief is null ? "There's no brief for today yet." : $"Today's brief has {brief.Items.Count} article{S(brief.Items.Count)} and isn't marked ready.";
        var link = string.IsNullOrWhiteSpace(app.Value.BaseUrl) ? "" :
            $" <a style=\"color:{Palette.Link};\" href=\"{Esc(app.Value.BaseUrl.TrimEnd('/') + "/briefs/" + key)}\">Open it in TIDE</a>.";
        var meeting = clock.MeetingTime.ToString("h:mm tt", CultureInfo.InvariantCulture);
        var t = _templates.Render(EmailTemplateCatalog.BriefNotReady, new Dictionary<string, string>
        {
            ["Status"] = what, ["BriefLink"] = link, ["MeetingTime"] = meeting,
        });
        var html = Shell(t.Heading, $"{clock.TeamName} · the meeting is at {meeting}", t.BodyHtml, Footer);
        var result = await email.SendAsync(new OutgoingEmail(to, t.Subject, html, ToText(html), []), ct);
        if (result.Status != EmailSendStatus.Sent) return 0;
        await MarkAsync(db, "brief-not-ready", [key]);
        return 1;
    }

    // --- presenting next -----------------------------------------------------------------------------------------

    // Whoever presents the next business day and doesn't today gets one reminder for that run.
    private async Task<int> PresentingAsync(CancellationToken ct)
    {
        if (presenters is null) return 0;
        var today = clock.Today;
        var next = clock.Days.Next(today);
        if (await presenters.PresenterAsync(next, ct) is not { } who) return 0;
        if ((await presenters.PresenterAsync(today, ct))?.Id == who.Id && clock.Days.IsWorkingDay(today)) return 0;
        var key = $"{who.Id}:{next:yyyy-MM-dd}";
        await using var db = await dbs.CreateAsync(ct);
        if (await db.NotificationMarks.AnyAsync(m => m.Kind == "presenting" && m.Key == key, ct)) return 0;
        var address = await db.Users.AsNoTracking().Where(u => u.Sid == who.Id).Select(u => u.Email).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(address)) return 0;
        var day = next.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);
        var link = string.IsNullOrWhiteSpace(app.Value.BaseUrl) ? "" :
            $" <a style=\"color:{Palette.Link};\" href=\"{Esc(app.Value.BaseUrl.TrimEnd('/') + "/rotation")}\">See the rotation</a>.";
        var t = _templates.Render(EmailTemplateCatalog.Presenting, new Dictionary<string, string>
        {
            ["Day"] = day,
            ["FirstDay"] = next.ToString("ddd MMM d", CultureInfo.InvariantCulture),
            ["MeetingTime"] = clock.MeetingTime.ToString("h:mm tt", CultureInfo.InvariantCulture),
            ["RotationLink"] = link,
        });
        var html = Shell(t.Heading, $"{clock.TeamName} · from {day}", t.BodyHtml, Footer);
        var result = await email.SendAsync(new OutgoingEmail([address], t.Subject, html, ToText(html), []), ct);
        if (result.Status != EmailSendStatus.Sent) return 0;
        await MarkAsync(db, "presenting", [key]);
        return 1;
    }

    // --- shared -------------------------------------------------------------------------------------------------

    private const string Footer = "An automatic message from TIDE. Replies aren't read.";

    private static string S(int n) => n == 1 ? "" : "s";

    // An action carries the TLP of the article it was raised from (one raised on its own carries none). A reminder or
    // digest is marked with the highest it lists, in its subject and at its top, as a brief's email is, since it may go
    // to a team mailbox wider than TIDE's users.
    private static TlpLevel? MarkingOf(IEnumerable<ActionItem> actions) => actions.Select(a => a.Article?.Tlp).Max();

    private static string Marked(string subject, IEnumerable<ActionItem> actions) =>
        MarkingOf(actions) is { } tlp ? $"{subject} [{Tlp.Label(tlp)}]" : subject;

    private static string MarkingLine(IEnumerable<ActionItem> actions) => MarkingOf(actions) is { } tlp
        ? $"<p style=\"margin:0 0 12px;font-size:13px;color:{Palette.Muted};\">{TlpLabel(tlp)} {Esc(Tlp.Meaning(tlp))}</p>"
        : "";

    private static string DayText(DateOnly d) => d.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);

    /// <summary>The digest's overdue count: bold, and red when there are any.</summary>
    internal static string OverdueCount(int overdue) =>
        $"<strong style=\"color:{(overdue > 0 ? Palette.Threat : Palette.Text)};\">{overdue}</strong>";

    /// <summary>A list's heading in the digest ("Open", "Closed in the last week").</summary>
    internal static string ListHeading(string text, int top) => $"<div style=\"font-weight:700;margin:{top}px 0 4px;\">{Esc(text)}</div>";

    private string Table(IEnumerable<ActionItem> actions, DateOnly today) => ActionTable(actions, today, app.Value.BaseUrl, clock.Days);

    /// <summary>The reminders' table of actions (the email templates' preview uses it too, with sample actions).</summary>
    internal static string ActionTable(IEnumerable<ActionItem> actions, DateOnly today, string? appBaseUrl, WorkingDays? workingDays = null)
    {
        var baseUrl = appBaseUrl?.TrimEnd('/');
        var sb = new StringBuilder("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"font-size:14px;\">");
        foreach (var a in actions)
        {
            var label = $"A-{a.Number}";
            var number = string.IsNullOrWhiteSpace(baseUrl) ? Esc(label) : $"<a style=\"color:{Palette.Link};\" href=\"{Esc(baseUrl + "/actions/" + a.Number)}\">{Esc(label)}</a>";
            var state = ActionSla.StateOf(a, today, workingDays);
            var due = a.ClosedDate is { } c ? $"closed {c.ToString("MMM d", CultureInfo.InvariantCulture)} ({ActionService.OutcomeName(a.Outcome)})"
                : a.DueDate is { } d ? "due " + d.ToString("ddd MMM d", CultureInfo.InvariantCulture) : "no due date";
            var color = state == SlaState.Overdue || state == SlaState.Missed ? Palette.Threat : state == SlaState.DueSoon ? Palette.Warn : Palette.Muted;
            sb.Append(CultureInfo.InvariantCulture, $"""
                <tr>
                  <td style="padding:6px 8px 6px 0;border-bottom:1px solid {Palette.Border};white-space:nowrap;vertical-align:top;">{number}</td>
                  <td style="padding:6px 8px;border-bottom:1px solid {Palette.Border};vertical-align:top;">{(a.Article is { } from ? TlpLabel(from.Tlp, 10) + " " : "")}{Esc(a.Title)}<div style="font-size:12px;color:{Palette.Muted};">{Esc(ActionService.KindName(a.Kind))} &middot; {a.Priority}{(a.AssigneeName is { } who ? " &middot; " + Esc(who) : "")}</div></td>
                  <td style="padding:6px 0 6px 8px;border-bottom:1px solid {Palette.Border};white-space:nowrap;vertical-align:top;color:{color};font-size:13px;">{Esc(due)}</td>
                </tr>
                """);
        }
        return sb.Append("</table>").ToString();
    }
}
