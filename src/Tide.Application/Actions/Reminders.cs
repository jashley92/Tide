using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Application.Preferences;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using static Tide.Application.Distribution.EmailComposer;
using static Tide.Application.Actions.ActionEmails;

namespace Tide.Application.Actions;

/// <summary>The reminder and digest emails (config section <c>Notifications</c>). Every one is off by default, but for the
/// personal digest's offer: it's on, and still sends nothing until someone chooses a digest in My notifications.</summary>
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

    /// <summary>Whether people may choose a personal digest (daily at <see cref="ReminderTime"/> on business days, or weekly
    /// at the digest's day and time). On by default.</summary>
    public Toggle PersonalDigest { get; set; } = new() { Enabled = true };

    /// <summary>At <see cref="ReminderTime"/>, remind whoever marked an Outreach action's email sent when no reply has
    /// been recorded for <see cref="FollowUpOptions.Days"/> business days.</summary>
    public FollowUpOptions OutreachFollowUp { get; set; } = new();

    /// <summary>At <see cref="ReminderTime"/>, email each external team (with its reminders on) about its actions due soon or
    /// overdue, as its own cadence sets; and whether a new external team starts with its reminders on.</summary>
    public ExternalReminderOptions ExternalReminders { get; set; } = new();

    /// <summary>At <see cref="ReminderTime"/> on business days, climb the escalation ladder: each open action that has
    /// reached a step gets what the step does (<see cref="EscalationLadder"/>).</summary>
    public EscalationOptions Escalation { get; set; } = new();

    public const int MinActiveWithinDays = 1, MaxActiveWithinDays = 365, DefaultActiveWithinDays = 30;

    /// <summary>A person is reminded only if they signed in within this many days (and their roles, as last seen then,
    /// still grant what the email shows): someone who left, or lost access, stops getting them. A team mailbox is an
    /// address, not a person, so it's never held to this. 30 by default.</summary>
    public int ActiveWithinDays { get; set; } = DefaultActiveWithinDays;

    /// <summary><see cref="ActiveWithinDays"/> in its range.</summary>
    public int ActiveDays => Math.Clamp(ActiveWithinDays, MinActiveWithinDays, MaxActiveWithinDays);

    /// <summary>Whether an administrator has the scan on.</summary>
    public bool IsEnabled(ReminderKind kind) => kind switch
    {
        ReminderKind.DueSoon => DueSoon.Enabled,
        ReminderKind.Overdue => Overdue.Enabled,
        ReminderKind.Digest => Digest.Enabled,
        ReminderKind.Presenting => Presenting.Enabled,
        ReminderKind.PersonalDigest => PersonalDigest.Enabled,
        ReminderKind.OutreachFollowUp => OutreachFollowUp.Enabled,
        ReminderKind.ExternalReminder => ExternalReminders.Enabled,
        ReminderKind.Escalation => Escalation.Enabled,
        _ => BriefNotReady.Enabled,
    };

    public sealed class Toggle
    {
        public bool Enabled { get; set; }
    }

    public sealed class ExternalReminderOptions
    {
        public bool Enabled { get; set; }

        /// <summary>Whether a new external team starts with its own reminders on (<see cref="Team.RemindersOn"/>). On by
        /// default; whoever adds the team can still change it, and it never touches a team already added.</summary>
        public bool NewTeamsOn { get; set; } = true;

        /// <summary>A new external team's schedule, as whoever adds it may change: business days before the due date (0 to
        /// <see cref="Team.MaxRemindBefore"/>; 0 is the due day), and every how many while overdue (0: once).</summary>
        public int NewTeamsBeforeDays { get; set; } = Team.DefaultRemindBefore;
        public int NewTeamsEveryDays { get; set; } = Team.DefaultRemindEvery;

        /// <summary><see cref="NewTeamsBeforeDays"/> in its range.</summary>
        public int BeforeDays => Math.Clamp(NewTeamsBeforeDays, 0, Team.MaxRemindBefore);

        /// <summary><see cref="NewTeamsEveryDays"/> in its range, or null for once.</summary>
        public int? EveryDays => NewTeamsEveryDays <= 0 ? null : Math.Clamp(NewTeamsEveryDays, Team.MinRemindEvery, Team.MaxRemindEvery);
    }

    public sealed class DigestOptions
    {
        public bool Enabled { get; set; }

        /// <summary>The day and local time each team's weekly digest goes.</summary>
        public DayOfWeek Day { get; set; } = DayOfWeek.Monday;
        public string Time { get; set; } = "08:00";
    }

    public sealed class FollowUpOptions
    {
        public const int MinDays = 1, MaxDays = 30;

        public bool Enabled { get; set; }

        /// <summary>How many business days an email waits for its reply before the reminder.</summary>
        public int Days { get; set; } = 5;

        /// <summary>A configured wait held to what the settings page accepts.</summary>
        public static int Clamp(int days) => Math.Clamp(days, MinDays, MaxDays);
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

    /// <summary>Each person's own digest of their actions, for those who chose one (daily or weekly).</summary>
    PersonalDigest = 5,

    /// <summary>A reminder to follow up an Outreach action's email that has had no reply (ADR 0019).</summary>
    OutreachFollowUp = 6,

    /// <summary>An external team's email about its actions due soon or overdue, at its own cadence (no TIDE links).</summary>
    ExternalReminder = 7,

    /// <summary>The escalation ladder: the step each open action has reached as it slips, once per due date.</summary>
    Escalation = 8,
}

/// <summary>A scan's last run, for the administration page. <paramref name="Note"/> says who wasn't emailed because they
/// opted out ("all recipients opted out" when that was everyone), and how many were skipped as inactive (no access now,
/// or not signed in within <see cref="ReminderOptions.ActiveWithinDays"/>).</summary>
/// <paramref name="Unsure"/> counts its emails the relay may not have taken (a timeout, a dropped connection): marked as
/// gone, not sent again by themselves, and named in the note so someone checks.
public sealed record ReminderRun(ReminderKind Kind, DateTimeOffset AtUtc, int Sent, string? Error, string? Note = null, int Unsure = 0);

/// <summary>What one pass of the due scans did: the emails sent, each scan that failed (its run, with its error), and each
/// whose emails may not have arrived (<see cref="ReminderRun.Unsure"/>).</summary>
public sealed record ReminderPass(int Sent, IReadOnlyList<ReminderRun> Failed, IReadOnlyList<ReminderRun> Unsure);

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
/// scans read actions and write only marks, so they run as the system, not as a person. Each leaves out whoever turned
/// that kind off in My notifications (<see cref="NotificationPreferenceService"/>), and whoever is inactive: their roles
/// as last seen no longer grant what the email shows (reading actions; presenting briefs, for the rotation's), or they
/// haven't signed in within <see cref="ReminderOptions.ActiveWithinDays"/>. An inactive assignee is passed over as one
/// with no address is (the team hears instead). A team mailbox and the brief-not-ready fallback recipients are
/// addresses, not people, so they never opt out and are never inactive.
/// </summary>
public sealed partial class ReminderService(IAppDbContextFactory dbs, IEmailSender email, OrgClock clock, IOptionsMonitor<ReminderOptions> options,
    IOptions<AppOptions> app, ReminderLog log, ICurrentUser user, Rotation.IPresenterLookup? presenters = null, EmailTemplateStore? templates = null,
    Microsoft.Extensions.Logging.ILogger<ReminderService>? logger = null, Settings.DistributionSettings? editions = null, EmailBoundary? boundary = null)
{
    // The team edition's ceiling: an email to someone outside the organization (an external team's contact, an escalation
    // contact outside it) names only the requests marked up to it, as the webhooks hold to it.
    private async Task<TlpLevel> CeilingAsync(CancellationToken ct) =>
        editions is not null ? (await editions.ReadAsync(ct)).Team : new Settings.DistributionOptions().TeamMaxTlp;

    private OrganizationBoundary Boundary => boundary.CurrentOrUnknown();

    // The wording an administrator set in Administration › Email templates, or the defaults.
    private readonly EmailTemplateStore _templates = templates ?? EmailTemplateStore.Defaults;

    public ReminderOptions Options => options.CurrentValue;

    public bool IsEnabled(ReminderKind kind) => Options.IsEnabled(kind);

    /// <summary>
    /// Runs every enabled scan that's due now (its time of day has come). Each scan's failure is its own: it's logged
    /// and the others still run. Returns the emails sent and the scans that failed, so the poller's run is recorded as
    /// failed (on Diagnostics) when one did, not as a success.
    /// </summary>
    public async Task<ReminderPass> RunDueAsync(CancellationToken ct = default)
    {
        var sent = 0;
        var failed = new List<ReminderRun>();
        var unsure = new List<ReminderRun>();
        foreach (var kind in Enum.GetValues<ReminderKind>())
        {
            if (!IsEnabled(kind) || !IsTime(kind)) continue;
            var before = log.Last(kind);
            sent += await RunAsync(kind, rethrow: false, scheduled: true, ct);
            if (log.Last(kind) is not { } run || ReferenceEquals(run, before)) continue;
            if (run.Error is not null) failed.Add(run);
            else if (run.Unsure > 0) unsure.Add(run);
        }
        return new ReminderPass(sent, failed, unsure);
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
    public Task<int> RunAsync(ReminderKind kind, CancellationToken ct = default) => RunAsync(kind, rethrow: true, scheduled: false, ct);

    // What a scan did: the emails that went, the people left out because they opted out, and what stopped it doing more
    // (a setting that doesn't read).
    private readonly record struct Scan(int Sent, int OptedOut = 0, string? Problem = null)
    {
        public static implicit operator Scan(int sent) => new(sent);

        public string? Note => Problem ?? (OptedOut == 0 ? null : Sent == 0 ? "all recipients opted out" : $"{OptedOut} opted out");
    }

    // This scan's emails the relay may have taken without saying so (a timeout, a dropped connection): counted as gone, so
    // they're marked and never sent again by themselves, and the run's note says to check. Scans run one at a time.
    private int _unsure, _refusedCopies;

    // The people this scan left out as inactive (Active), each once, for the run's note.
    private readonly HashSet<string> _inactive = [];

    /// <summary>What a run's note says of people left out as inactive.</summary>
    public static string InactiveNote(int n, int days) =>
        $"{n} skipped as inactive (no access now, or not signed in for {days} day{(days == 1 ? "" : "s")})";

    // Who this scan may still remind: roles (as last seen) granting what the email shows, and a sign-in within the
    // setting's days.
    private ActiveRule Active(Permission needed) => new(needed, clock.Now.ToUniversalTime().AddDays(-Options.ActiveDays));

    // The people a resolved set left out as inactive, among these ids, counted for the run's note.
    private void Inactive(IEnumerable<string> ids) => _inactive.UnionWith(ids);

    /// <summary>The run's note when standing copies were refused: the emails still went, but a copy address is wrong.</summary>
    public static string RefusedCopiesNote(int n) =>
        $"{n} cop{(n == 1 ? "y" : "ies")} refused by the relay (the emails still went): check the copy addresses on Email templates";

    /// <summary>What a run's note says of emails that may not have arrived.</summary>
    public static string UnsureNote(int n) =>
        $"{n} email{(n == 1 ? "" : "s")} may not have arrived (the relay didn't answer in time or dropped the connection): check with the recipients before sending again";

    // Every scan's emails go through here, so one the relay may not have taken is counted for the run's note wherever
    // it's sent. Its MayHaveGone is what the scan marks by: gone or maybe gone, it isn't sent again by itself.
    private async Task<EmailSendResult> SendAsync(OutgoingEmail message, CancellationToken ct)
    {
        var result = await email.SendAsync(message, ct);
        if (result.Status == EmailSendStatus.Unknown) _unsure++;
        _refusedCopies += result.RefusedCopies;
        return result;
    }

    // A failure is logged either way; the scheduled run doesn't rethrow it, so the next scan still runs. A scheduled
    // personal digest goes only to those whose time has come (daily or weekly); run now, to everyone who chose one.
    private async Task<int> RunAsync(ReminderKind kind, bool rethrow, bool scheduled, CancellationToken ct)
    {
        await Running.WaitAsync(ct);
        (_unsure, _refusedCopies) = (0, 0);
        _inactive.Clear();
        try
        {
            var scan = kind switch
            {
                ReminderKind.DueSoon => await DueAsync(overdue: false, ct),
                ReminderKind.Overdue => await DueAsync(overdue: true, ct),
                ReminderKind.Digest => await DigestAsync(ct),
                ReminderKind.Presenting => await PresentingAsync(ct),
                ReminderKind.PersonalDigest => await PersonalDigestAsync(scheduled, ct),
                ReminderKind.OutreachFollowUp => await FollowUpAsync(ct),
                ReminderKind.ExternalReminder => await ExternalAsync(ct),
                ReminderKind.Escalation => await EscalateAsync(ct),
                _ => await BriefNotReadyAsync(ct),
            };
            // The poll comes round every few minutes after the scan's time, and most find nothing new to send (or only
            // people who opted out). That mustn't overwrite what the day's real run sent.
            var last = log.Last(kind);
            var quiet = scheduled && scan.Sent == 0 && scan.Problem is null && _unsure == 0 && _refusedCopies == 0 && last is { Error: null, Sent: > 0 }
                        && clock.ToLocal(last.AtUtc).Date == clock.Now.Date;
            var notes = new[]
            {
                scan.Note, _inactive.Count == 0 ? null : InactiveNote(_inactive.Count, Options.ActiveDays),
                _unsure == 0 ? null : UnsureNote(_unsure), _refusedCopies == 0 ? null : RefusedCopiesNote(_refusedCopies),
            }.OfType<string>().ToList();
            var note = notes.Count == 0 ? null : string.Join("; ", notes);
            if (!quiet) log.Record(new ReminderRun(kind, clock.Now.ToUniversalTime(), scan.Sent, null, note, Unsure: _unsure));
            return scan.Sent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Record(new ReminderRun(kind, clock.Now.ToUniversalTime(), 0, ex.GetType().Name + ": " + ex.Message));
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
        var time = TimeOnly.FromDateTime(clock.Now.DateTime);
        return kind switch
        {
            ReminderKind.Digest => IsWeeklyTime(),
            ReminderKind.BriefNotReady => clock.Days.IsWorkingDay(clock.Today) && time >= Parse(Options.BriefNotReady.Time, 10) && time < clock.MeetingTime,
            ReminderKind.PersonalDigest => IsDailyTime() || IsWeeklyTime(),
            _ => IsDailyTime(),
        };
    }

    // The reminders' time on a business day (a daily digest's too), and the digest's day and time (a weekly one's).
    private bool IsDailyTime() =>
        clock.Days.IsWorkingDay(clock.Today) && TimeOnly.FromDateTime(clock.Now.DateTime) >= Parse(Options.ReminderTime, 8);

    private bool IsWeeklyTime() =>
        clock.Now.DayOfWeek == Options.Digest.Day && TimeOnly.FromDateTime(clock.Now.DateTime) >= Parse(Options.Digest.Time, 8);

    private static TimeOnly Parse(string? text, int fallbackHour) =>
        TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out var t) ? t : new TimeOnly(fallbackHour, 0);

    // --- due soon and overdue -----------------------------------------------------------------------------------

    private async Task<Scan> DueAsync(bool overdue, CancellationToken ct)
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

        var people = pending.Select(a => a.AssigneeId).OfType<string>().Concat(pending.SelectMany(a => a.Team!.Members.Select(m => m.UserId))).ToList();
        var reach = await RecipientResolver.ResolveAsync(db, NotificationKinds.For(overdue ? ReminderKind.Overdue : ReminderKind.DueSoon), people, ct,
            Active(Permission.ReadActions));
        var byRecipient = new Dictionary<string, List<ActionItem>>(StringComparer.OrdinalIgnoreCase);
        var leftOut = new HashSet<string>();
        foreach (var a in pending)
        {
            var (recipients, skipped, inactive) = RecipientsOf(a, reach);
            leftOut.UnionWith(skipped);
            Inactive(inactive);
            foreach (var to in recipients.Where(to => !marks.Contains(RecipientKey(a, to))))
                (byRecipient.TryGetValue(to, out var list) ? list : byRecipient[to] = []).Add(a);
        }

        // Each recipient's email is marked as it goes, so a scan stopped partway resumes with only those not reached.
        // Once a scan has tried everyone, an action anyone got is marked whole, so a failing address isn't retried
        // every poll; an action whose every recipient failed is tried at the next scan.
        var sent = 0;
        var reached = new HashSet<Guid>();
        foreach (var (to, list) in byRecipient)
        {
            var result = await SendAsync(DueEmail(to, list, overdue, today), ct);
            if (!result.MayHaveGone) continue;
            sent++;
            reached.UnionWith(list.Select(a => a.Id));
            await MarkAsync(db, kind, list.Select(a => RecipientKey(a, to)).ToList());
        }
        await MarkAsync(db, kind, pending.Where(a => reached.Contains(a.Id)).Select(MarkKey).ToList());
        return new Scan(sent, leftOut.Count);
    }

    // A due-soon or overdue reminder to one recipient about their actions (the escalation ladder's reminder too).
    private OutgoingEmail DueEmail(string to, IReadOnlyList<ActionItem> list, bool overdue, DateOnly today)
    {
        var t = _templates.Render(overdue ? EmailTemplateCatalog.Overdue : EmailTemplateCatalog.DueSoon, new Dictionary<string, string>
        {
            ["Actions"] = $"{list.Count} action{S(list.Count)}",
            ["OverdueActions"] = $"{list.Count} overdue action{S(list.Count)}",
            ["ActionList"] = Table(list, today),
        });
        var html = Shell(t.Heading, clock.TeamName + " · " + DayText(today), MarkingLine(list) + t.BodyHtml, t.Footer);
        return new OutgoingEmail([to], Marked(t.Subject, list), html, ToText(html), []) { Template = t.TemplateId };
    }

    // One recipient's reminder about an action (the address hashed: keys are short).
    private static string RecipientKey(ActionItem a, string to) => MarkKey(a) + "|" + Hash(to);

    // Once per action per due date: moving the date earns a new reminder.
    private static string MarkKey(ActionItem a) => $"{a.Id:N}:{a.DueDate:yyyy-MM-dd}";

    // Who's emailed about an action, who would have been but opted out, and who was left out as inactive. An assignee who
    // opted out isn't swapped for the team: the action is theirs. One who's inactive is, as one with no address is: they
    // can't act on it. A team mailbox is an address, not a person, so it always gets it.
    private static (IReadOnlyList<string> To, IReadOnlyList<string> OptedOut, IReadOnlyList<string> Inactive) RecipientsOf(ActionItem a, Recipients people)
    {
        if (a.AssigneeId is { } id && people.Addresses.TryGetValue(id, out var mine)) return people.OptedOut.Contains(id) ? ([], [id], []) : ([mine], [], []);
        IReadOnlyList<string> gone = a.AssigneeId is { } was && people.Inactive.Contains(was) ? [was] : [];
        if (!string.IsNullOrWhiteSpace(a.Team?.Email)) return ([a.Team.Email], [], gone);
        var (to, optedOut, inactive) = Members(a.Team, people);
        return (to, optedOut, [.. gone, .. inactive]);
    }

    // A team's members' addresses, but for those who opted out (listed apart) and those left out as inactive (listed too).
    private static (IReadOnlyList<string> To, IReadOnlyList<string> OptedOut, IReadOnlyList<string> Inactive) Members(Team? team, Recipients people)
    {
        var ids = team?.Members.Select(m => m.UserId).Distinct().ToList() ?? [];
        var members = ids.Where(people.Addresses.ContainsKey).ToList();
        return ([.. members.Where(m => !people.OptedOut.Contains(m)).Select(m => people.Addresses[m]).Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. members.Where(people.OptedOut.Contains)], [.. ids.Where(people.Inactive.Contains)]);
    }

    // --- weekly digest ------------------------------------------------------------------------------------------

    private async Task<Scan> DigestAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var weekStart = Rotation.RotationService.WeekStart(today);
        var weekAgo = ActionQueries.LastSevenDaysFrom(today);
        await using var db = await dbs.CreateAsync(ct);
        var teams = await db.Teams.AsNoTracking().Include(t => t.Members).Where(t => !t.IsRetired).ToListAsync(ct);
        var marks = (await db.NotificationMarks.AsNoTracking().Where(m => m.Kind == "digest").Select(m => m.Key).ToListAsync(ct)).ToHashSet();
        var members = await RecipientResolver.ResolveAsync(db, NotificationKinds.For(ReminderKind.Digest),
            teams.SelectMany(t => t.Members.Select(m => m.UserId)), ct, Active(Permission.ReadActions));
        var sent = 0;
        var leftOut = new HashSet<string>();
        foreach (var team in teams)
        {
            var key = $"{team.Id:N}:{weekStart:yyyy-MM-dd}";
            if (marks.Contains(key)) continue;
            IReadOnlyList<string> to = [team.Email!];
            if (string.IsNullOrWhiteSpace(team.Email))
            {
                (to, var skipped, var inactive) = Members(team, members);
                leftOut.UnionWith(skipped);
                Inactive(inactive);
            }
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
            var html = Shell(t.Heading, clock.TeamName + " · week of " + weekStart.ToString("MMMM d", CultureInfo.InvariantCulture), MarkingLine(actions) + t.BodyHtml, t.Footer);
            var result = await SendAsync(new OutgoingEmail(to, Marked(t.Subject, actions), html, ToText(html), []) { Template = t.TemplateId }, ct);
            if (!result.MayHaveGone) continue;
            await MarkAsync(db, "digest", [key]);
            sent++;
        }
        return new Scan(sent, leftOut.Count);
    }

    // --- brief not ready ----------------------------------------------------------------------------------------

    private async Task<Scan> BriefNotReadyAsync(CancellationToken ct)
    {
        var today = clock.Today;
        if (!clock.Days.IsWorkingDay(today)) return 0;
        var key = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var db = await dbs.CreateAsync(ct);
        if (await db.NotificationMarks.AnyAsync(m => m.Kind == "brief-not-ready" && m.Key == key, ct)) return 0;
        var brief = await db.Briefs.AsNoTracking().Include(b => b.Items).FirstOrDefaultAsync(b => b.BriefDate == today, ct);
        if (brief is not null && brief.Status != BriefStatus.Draft) return 0;

        var to = new List<string>();
        // The brief's analyst, or (no brief yet) whoever the rotation says presents today. An analyst who opted out of
        // rotation reminders isn't nudged, but the fallback recipients (a team lead, a mailbox: not a person's choice) are;
        // so are they for an analyst who's inactive.
        var analystId = brief?.AnalystId ?? (presenters is null ? null : (await presenters.PresenterAsync(today, ct))?.Id);
        var analyst = analystId is null ? null : await RecipientResolver.ResolveAsync(db, NotificationKinds.For(ReminderKind.BriefNotReady), [analystId], ct,
            Active(Permission.PresentBriefs));
        if (analyst is not null) Inactive(analyst.Inactive);
        var optedOut = analyst?.OptedOut.Count > 0;
        if (analyst is not null && !optedOut && analyst.Addresses.TryGetValue(analystId!, out var address))
            to.Add(address);
        else to.AddRange(Options.BriefNotReady.Recipients.Where(r => System.Net.Mail.MailAddress.TryCreate(r, out _)));
        if (to.Count == 0) return new Scan(0, optedOut ? 1 : 0);

        var what = brief is null ? "There's no brief for today yet." : $"Today's brief has {brief.Items.Count} article{S(brief.Items.Count)} and isn't marked ready.";
        var link = string.IsNullOrWhiteSpace(app.Value.BaseUrl) ? "" :
            $" <a style=\"color:{Palette.Link};\" href=\"{Esc(app.Value.BaseUrl.TrimEnd('/') + "/briefs/" + key)}\">Open it in TIDE</a>.";
        var meeting = clock.MeetingTime.ToString("h:mm tt", CultureInfo.InvariantCulture);
        var t = _templates.Render(EmailTemplateCatalog.BriefNotReady, new Dictionary<string, string>
        {
            ["Status"] = what, ["BriefLink"] = link, ["MeetingTime"] = meeting,
        });
        var html = Shell(t.Heading, $"{clock.TeamName} · the meeting is at {meeting}", t.BodyHtml, t.Footer);
        var result = await SendAsync(new OutgoingEmail(to, t.Subject, html, ToText(html), []) { Template = t.TemplateId }, ct);
        if (!result.MayHaveGone) return 0;
        await MarkAsync(db, "brief-not-ready", [key]);
        return new Scan(1, optedOut ? 1 : 0);
    }

    // --- presenting next -----------------------------------------------------------------------------------------

    // Whoever presents the next business day and doesn't today gets one reminder for that run.
    private async Task<Scan> PresentingAsync(CancellationToken ct)
    {
        if (presenters is null) return 0;
        var today = clock.Today;
        var next = clock.Days.Next(today);
        if (await presenters.PresenterAsync(next, ct) is not { } who) return 0;
        if ((await presenters.PresenterAsync(today, ct))?.Id == who.Id && clock.Days.IsWorkingDay(today)) return 0;
        var key = $"{who.Id}:{next:yyyy-MM-dd}";
        await using var db = await dbs.CreateAsync(ct);
        if (await db.NotificationMarks.AnyAsync(m => m.Kind == "presenting" && m.Key == key, ct)) return 0;
        var presenter = await RecipientResolver.ResolveAsync(db, NotificationKinds.For(ReminderKind.Presenting), [who.Id], ct,
            Active(Permission.PresentBriefs));
        Inactive(presenter.Inactive);
        if (presenter.OptedOut.Contains(who.Id)) return new Scan(0, 1);
        if (!presenter.Addresses.TryGetValue(who.Id, out var address) || string.IsNullOrWhiteSpace(address)) return 0;
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
        var html = Shell(t.Heading, $"{clock.TeamName} · from {day}", t.BodyHtml, t.Footer);
        var result = await SendAsync(new OutgoingEmail([address], t.Subject, html, ToText(html), []) { Template = t.TemplateId }, ct);
        if (!result.MayHaveGone) return 0;
        await MarkAsync(db, "presenting", [key]);
        return 1;
    }

    // --- personal digest ----------------------------------------------------------------------------------------

    // Each person who chose a digest gets one a day (on business days) or a week: their open actions that are overdue
    // or due soon, and the others not in an earlier digest of theirs. "Theirs" is as the reminders have it: assigned to
    // them, or unassigned on a team of theirs without a mailbox. A mark per period stops repeats, and one per action
    // listed says it's no longer new to them.
    private async Task<Scan> PersonalDigestAsync(bool scheduled, CancellationToken ct)
    {
        var today = clock.Today;
        var soon = clock.Days.Next(today);
        var weekStart = Rotation.RotationService.WeekStart(today);
        bool daily = !scheduled || IsDailyTime(), weekly = !scheduled || IsWeeklyTime();
        await using var db = await dbs.CreateAsync(ct);
        var choices = await db.UserPreferences.AsNoTracking()
            .Where(p => p.Name == UserPreferences.NotifyDigest && (p.Value == UserPreferences.Daily || p.Value == UserPreferences.Weekly)).ToListAsync(ct);
        choices = [.. choices.Where(c => c.Value == UserPreferences.Daily ? daily : weekly)];
        if (choices.Count == 0) return 0;
        var ids = choices.Select(c => c.UserId).ToList();
        var people = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Sid)).ToDictionaryAsync(u => u.Sid, ct);
        // Someone who has since lost sight of actions, or hasn't signed in lately (or has no address), isn't sent their titles.
        var active = Active(Permission.ReadActions);
        var byAccess = choices.Where(c => people.TryGetValue(c.UserId, out var p) && !string.IsNullOrWhiteSpace(p.Email))
            .ToLookup(c => active.Allows(people[c.UserId].RolesCsv, people[c.UserId].LastSeenUtc));
        Inactive(byAccess[false].Select(c => c.UserId));
        choices = [.. byAccess[true]];
        if (choices.Count == 0) return 0;
        var readers = choices.Select(c => c.UserId).ToList();

        // The period's marks already sent, and the actions each reader has seen in an earlier digest, each in one query.
        string KeyOf(UserPreference c) => c.Value == UserPreferences.Daily ? $"{Hash(c.UserId)}:{today:yyyy-MM-dd}" : $"{Hash(c.UserId)}:w{weekStart:yyyy-MM-dd}";
        var keys = choices.Select(KeyOf).ToList();
        var done = (await db.NotificationMarks.AsNoTracking().Where(m => m.Kind == "personal-digest" && keys.Contains(m.Key))
            .Select(m => m.Key).ToListAsync(ct)).ToHashSet();
        var whos = readers.Select(Hash).Distinct().ToList();
        var seenBy = (await db.NotificationMarks.AsNoTracking().Where(m => m.Kind == "digest-seen" && whos.Contains(m.Key.Substring(0, HashLength)))
                .Select(m => m.Key).ToListAsync(ct))
            .Where(k => k.Length > HashLength && k[HashLength] == ':')
            .GroupBy(k => k[..HashLength]).ToDictionary(g => g.Key, g => g.ToHashSet());

        // Each reader's open actions, from one load of only theirs.
        var open = await db.Actions.AsNoTracking().Include(a => a.Article).Include(a => a.Team).ThenInclude(t => t!.Members).WhereOpen()
            .Where(a => (a.AssigneeId != null && readers.Contains(a.AssigneeId)) || (a.AssigneeId == null && a.Team!.Members.Any(m => readers.Contains(m.UserId))))
            .AsSplitQuery().ToListAsync(ct);
        var theirs = new Dictionary<string, List<ActionItem>>();
        void Add(string id, ActionItem a) => (theirs.TryGetValue(id, out var list) ? list : theirs[id] = []).Add(a);
        foreach (var a in open)
        {
            if (a.AssigneeId is { } assignee) Add(assignee, a);
            else if (string.IsNullOrWhiteSpace(a.Team?.Email))
                foreach (var member in a.Team!.Members.Select(m => m.UserId).Distinct()) Add(member, a);
        }

        var sent = 0;
        foreach (var choice in choices)
        {
            var person = people[choice.UserId];
            var who = Hash(choice.UserId);
            var isDaily = choice.Value == UserPreferences.Daily;
            var key = KeyOf(choice);
            if (done.Contains(key)) continue;
            var seen = seenBy.TryGetValue(who, out var s) ? s : seenBy[who] = [];

            var mine = theirs.GetValueOrDefault(choice.UserId) ?? [];
            var overdue = mine.Where(a => a.DueDate < today).OrderBy(a => a.DueDate).ThenBy(a => a.Number).ToList();
            var dueSoon = mine.Where(a => a.DueDate >= today && a.DueDate <= soon).OrderBy(a => a.DueDate).ThenBy(a => a.Number).ToList();
            var fresh = mine.Except(overdue).Except(dueSoon).Where(a => !seen.Contains(SeenKey(who, a))).OrderBy(a => a.Number).ToList();
            var listed = overdue.Concat(dueSoon).Concat(fresh).ToList();
            if (listed.Count == 0)
            {
                // Nothing to tell them this time: the period is done all the same.
                await MarkAsync(db, "personal-digest", [key]);
                done.Add(key);
                continue;
            }

            string Section(string heading, List<ActionItem> list, int top) => list.Count == 0 ? "" : ListHeading(heading, top) + Table(list, today);
            var period = isDaily ? DayText(today) : "the week of " + weekStart.ToString("MMMM d", CultureInfo.InvariantCulture);
            var t = _templates.Render(EmailTemplateCatalog.PersonalDigest, new Dictionary<string, string>
            {
                ["Name"] = person.DisplayName,
                ["Period"] = period,
                ["Cadence"] = isDaily ? "daily" : "weekly",
                ["OverdueCount"] = OverdueCount(overdue.Count),
                ["DueSoonCount"] = dueSoon.Count.ToString(CultureInfo.InvariantCulture),
                ["NewCount"] = fresh.Count.ToString(CultureInfo.InvariantCulture),
                ["OverdueList"] = Section("Overdue", overdue, 8),
                ["DueSoonList"] = Section("Due today or the next business day", dueSoon, 14),
                ["NewList"] = Section(seen.Count == 0 ? "Your other open actions" : "New to you since your last digest", fresh, 14),
            });
            var html = Shell(t.Heading, clock.TeamName + " · " + period, MarkingLine(listed) + t.BodyHtml, t.Footer);
            var result = await SendAsync(new OutgoingEmail([person.Email!], Marked(t.Subject, listed), html, ToText(html), []) { Template = t.TemplateId }, ct);
            if (!result.MayHaveGone) continue;
            sent++;
            await MarkAsync(db, "personal-digest", [key]);
            done.Add(key);
            var newlySeen = listed.Select(a => SeenKey(who, a)).Where(k => !seen.Contains(k)).ToList();
            await MarkAsync(db, "digest-seen", newlySeen);
            seen.UnionWith(newlySeen);
        }
        return sent;
    }

    private static string SeenKey(string who, ActionItem a) => $"{who}:{a.Id:N}";

    // --- outreach follow-up -------------------------------------------------------------------------------------

    // Whoever marked an Outreach action's email sent is reminded once, when it has waited the configured business days
    // with no reply recorded and the action is still open: one email per sender, listing their waiting actions. A mark
    // per email stops repeats; someone who opted out isn't sent it, and their emails aren't marked, so turning it back
    // on brings the reminder.
    private async Task<Scan> FollowUpAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var days = ReminderOptions.FollowUpOptions.Clamp(Options.OutreachFollowUp.Days);
        await using var db = await dbs.CreateAsync(ct);
        // A business day is never shorter than a calendar day, so nothing sent after the start of the day `days` calendar
        // days back can be due yet: the database leaves those out, and the exact business-day rule runs here on the rest.
        var sentBefore = clock.StartOfDay(today.AddDays(1 - days));
        var waiting = await db.OutreachMessages.AsNoTracking().Where(m => m.RepliedAtUtc == null && m.SentAtUtc < sentBefore)
            .Select(m => new FollowUpEmail(m.Id, m.ActionId, m.To, m.SentAtUtc, m.SentById)).ToListAsync(ct);
        waiting = [.. waiting.Where(m => OutreachDates.FollowUpOn(m.SentAtUtc, days, clock) <= today)];
        if (waiting.Count == 0) return 0;
        var keys = waiting.Select(m => m.Key).ToList();
        var marks = (await db.NotificationMarks.AsNoTracking().Where(m => m.Kind == FollowUpMark && keys.Contains(m.Key)).Select(m => m.Key).ToListAsync(ct)).ToHashSet();
        waiting = [.. waiting.Where(m => !marks.Contains(m.Key))];
        var ids = waiting.Select(m => m.ActionId).Distinct().ToList();
        var open = await db.Actions.AsNoTracking().Include(a => a.Article).WhereOpen().Where(a => ids.Contains(a.Id) && a.Kind == ActionKind.Outreach)
            .ToDictionaryAsync(a => a.Id, ct);
        waiting = [.. waiting.Where(m => open.ContainsKey(m.ActionId))];
        if (waiting.Count == 0) return 0;

        var senders = await RecipientResolver.ResolveAsync(db, NotificationKinds.For(ReminderKind.OutreachFollowUp), waiting.Select(m => m.SentById), ct,
            Active(Permission.ReadActions));
        Inactive(senders.Inactive);
        var sent = 0;
        var leftOut = new HashSet<string>();
        foreach (var mine in waiting.GroupBy(m => m.SentById))
        {
            if (senders.OptedOut.Contains(mine.Key)) { leftOut.Add(mine.Key); continue; }
            if (!senders.Addresses.TryGetValue(mine.Key, out var to)) continue;
            var emails = mine.OrderBy(m => m.SentAtUtc).ToList();
            var list = emails.Select(m => open[m.ActionId]).DistinctBy(a => a.Id).OrderBy(a => a.Number).ToList();
            var t = _templates.Render(EmailTemplateCatalog.OutreachFollowUp, new Dictionary<string, string>
            {
                ["Actions"] = $"{list.Count} action{S(list.Count)}",
                ["Days"] = $"{days} business day{S(days)}",
                ["ActionList"] = Table(list, today),
                ["WaitingList"] = WaitingList(emails.Select(m => (open[m.ActionId].Number, m.To, OutreachDates.SentOn(m.SentAtUtc, clock)))),
            });
            var html = Shell(t.Heading, clock.TeamName + " · " + DayText(today), MarkingLine(list) + t.BodyHtml, t.Footer);
            var result = await SendAsync(new OutgoingEmail([to], Marked(t.Subject, list), html, ToText(html), []) { Template = t.TemplateId }, ct);
            if (!result.MayHaveGone) continue;
            sent++;
            await MarkAsync(db, FollowUpMark, emails.Select(m => m.Key).ToList());
        }
        return new Scan(sent, leftOut.Count);
    }

    private const string FollowUpMark = "outreach-follow-up";

    // The columns of an email the follow-up reads (not its body); Key is its mark's key.
    private sealed record FollowUpEmail(Guid Id, Guid ActionId, string To, DateTimeOffset SentAtUtc, string SentById)
    {
        public string Key => Id.ToString("N");
    }

    /// <summary>The follow-up's lines saying where each email went and when ("A-12 to support@vendor.example, sent Mon Nov 2").</summary>
    internal static string WaitingList(IEnumerable<(int Number, string To, DateOnly Sent)> emails)
    {
        var sb = new StringBuilder("<ul style=\"margin:0 0 12px;padding-left:20px;\">");
        foreach (var (number, to, sentOn) in emails)
            sb.Append(CultureInfo.InvariantCulture, $"<li>A-{number} to {Esc(to)}, sent {sentOn.ToString("ddd MMM d", CultureInfo.InvariantCulture)}</li>");
        return sb.Append("</ul>").ToString();
    }

    // A short, fixed-length stand-in for an address or a user id in a mark's key.
    private const int HashLength = 16;

    private static string Hash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes(text.Trim().ToLowerInvariant())))[..HashLength];

    // --- shared -------------------------------------------------------------------------------------------------

    private static string S(int n) => n == 1 ? "" : "s";

    /// <summary>The digest's overdue count: bold, and red when there are any.</summary>
    internal static string OverdueCount(int overdue) =>
        $"<strong style=\"color:{(overdue > 0 ? Palette.Threat : Palette.Text)};\">{overdue}</strong>";

    /// <summary>A list's heading in the digest ("Open", "Closed in the last week").</summary>
    internal static string ListHeading(string text, int top) => $"<div style=\"font-weight:700;margin:{top}px 0 4px;\">{Esc(text)}</div>";

    private string Table(IEnumerable<ActionItem> actions, DateOnly today) => ActionTable(actions, today, app.Value.BaseUrl, clock.Days);
}
