using System.Globalization;
using System.Net.Mail;
using System.Text;
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
using static Tide.Application.Distribution.EmailComposer;

namespace Tide.Application.Rotation;

/// <summary>Who presents the brief on a day: the analyst, and whether that's the week's regular analyst or an approved change.</summary>
public sealed record RotationDay(DateOnly Date, string? AnalystId, string? AnalystName, string? RegularId, string? RegularName, Guid? ChangeId)
{
    public bool IsChanged => ChangeId is not null && AnalystId != RegularId;
}

/// <summary>
/// What setting a week did to its presented days: those kept for whoever presented them, or, as a correction, those
/// changed to the new analyst.
/// </summary>
public sealed record WeekSet(IReadOnlyList<(DateOnly Date, string AnalystName)> Kept, IReadOnlyList<DateOnly> Corrected);

/// <summary>A run of consecutive business days one analyst presents the brief.</summary>
public sealed record RotationRun(string AnalystId, string AnalystName, DateOnly From, DateOnly To);

/// <summary>What's coming up for a person: their next brief days, and requests waiting on them or on others.</summary>
public sealed record Upcoming(IReadOnlyList<RotationRun> MyRuns, IReadOnlyList<RotationChange> ToApprove, IReadOnlyList<RotationChange> MyRequests);

/// <summary>A swap or cover as the request form fills it in.</summary>
public sealed class RotationRequest
{
    public RotationChangeKind Kind { get; set; } = RotationChangeKind.Cover;

    /// <summary>Whose days are given away: blank for the current user (only an administrator names someone else).</summary>
    public string? RequesterId { get; set; }
    public string PartnerId { get; set; } = "";
    public DateOnly GiveFrom { get; set; }
    public DateOnly GiveTo { get; set; }
    public DateOnly? TakeFrom { get; set; }
    public DateOnly? TakeTo { get; set; }
    public string? Note { get; set; }
}

/// <summary>Looks up who presents the brief on a day. Used when a brief is started, and by the reminders.</summary>
public interface IPresenterLookup
{
    Task<(string Id, string Name)?> PresenterAsync(DateOnly date, CancellationToken ct = default);
}

/// <summary>
/// The brief rotation (who presents the daily brief; not an on-call schedule): a weekly rota handed out from an ordered roster, and day-level changes on top of it. An analyst
/// asks a colleague to cover some of their days, or to swap days or weeks; only that colleague approves, and the
/// manager is emailed a notice of every approved change. An administrator keeps the roster and the rota, can arrange
/// cover on someone's behalf (still approved by the colleague taking it on), and can undo a change.
/// </summary>
public sealed class RotationService(IAppDbContextFactory dbs, ICurrentUser user, OrgClock clock, IEmailSender email, IOptions<AppOptions> app, EmailTemplateStore? templates = null)
    : IPresenterLookup
{
    public const string ManagerEmailsKey = "Rotation:ManagerEmails";

    /// <summary>The longest stretch one request can move, in business days (four weeks).</summary>
    public const int MaxRequestDays = 20;

    public DateOnly Today => clock.Today;

    public static DateOnly WeekStart(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// <summary>The working days from <paramref name="from"/> to <paramref name="to"/>: a holiday has no presenter.</summary>
    public static IEnumerable<DateOnly> BusinessDays(DateOnly from, DateOnly to, WorkingDays workingDays)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
            if (workingDays.IsWorkingDay(d)) yield return d;
    }

    /// <summary>Consecutive working days with the same analyst, as runs (a weekend or holiday doesn't break a run).</summary>
    public static IReadOnlyList<RotationRun> Runs(IEnumerable<RotationDay> days, WorkingDays? workingDays = null)
    {
        var w = workingDays ?? WorkingDays.Weekdays;
        var runs = new List<RotationRun>();
        foreach (var d in days.Where(d => d.AnalystId is not null).OrderBy(d => d.Date))
        {
            if (runs.Count > 0 && runs[^1].AnalystId == d.AnalystId && w.Next(runs[^1].To) == d.Date)
                runs[^1] = runs[^1] with { To = d.Date };
            else runs.Add(new RotationRun(d.AnalystId!, d.AnalystName!, d.Date, d.Date));
        }
        return runs;
    }

    // --- who presents ---------------------------------------------------------------------------------------------

    public async Task<(string Id, string Name)?> PresenterAsync(DateOnly date, CancellationToken ct = default)
    {
        var days = await DaysAsync(date, date, ct);
        var day = days.Count > 0 ? days[0] : null;
        return day?.AnalystId is { } id ? (id, day.AnalystName!) : null;
    }

    /// <summary>Who presents the brief each business day from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public async Task<IReadOnlyList<RotationDay>> CalendarAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        RequireRead();
        if (to.DayNumber - from.DayNumber > 400) throw new ArgumentException("Choose a range of a year or less.");
        return await DaysAsync(from, to, ct);
    }

    private async Task<IReadOnlyList<RotationDay>> DaysAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        var firstWeek = WeekStart(from);
        var weeks = await db.RotationWeeks.AsNoTracking().Where(w => w.WeekStart >= firstWeek && w.WeekStart <= to).ToDictionaryAsync(w => w.WeekStart, ct);
        var overrides = await db.RotationOverrides.AsNoTracking().Where(o => o.Date >= from && o.Date <= to).ToDictionaryAsync(o => o.Date, ct);
        return BusinessDays(from, to, clock.Days).Select(d =>
        {
            var week = weeks.GetValueOrDefault(WeekStart(d));
            var o = overrides.GetValueOrDefault(d);
            return new RotationDay(d, o?.AnalystId ?? week?.AnalystId, o?.AnalystName ?? week?.AnalystName, week?.AnalystId, week?.AnalystName, o?.ChangeId);
        }).ToList();
    }

    /// <summary>What's coming up for the current user over the next couple of months.</summary>
    public async Task<Upcoming> UpcomingAsync(CancellationToken ct = default)
    {
        RequireRead();
        var today = clock.Today;
        var days = await DaysAsync(today, today.AddDays(70), ct);
        await using var db = await dbs.CreateAsync(ct);
        var pending = await db.RotationChanges.AsNoTracking().Where(c => c.Status == RotationChangeStatus.Pending
            && (c.PartnerId == user.UserId || c.RequesterId == user.UserId || c.CreatedById == user.UserId)).ToListAsync(ct);
        return new Upcoming(Runs(days, clock.Days).Where(r => r.AnalystId == user.UserId).ToList(),
            pending.Where(c => c.PartnerId == user.UserId).OrderBy(c => c.GiveFrom).ToList(),
            pending.Where(c => c.PartnerId != user.UserId).OrderBy(c => c.GiveFrom).ToList());
    }

    /// <summary>Recent and upcoming changes (any status), newest first.</summary>
    public async Task<IReadOnlyList<RotationChange>> ChangesAsync(int take = 50, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return (await db.RotationChanges.AsNoTracking().ToListAsync(ct)).OrderByDescending(c => c.CreatedAtUtc).Take(take).ToList();
    }

    // --- the roster and the rota (administrators) ----------------------------------------------------------------

    public async Task<IReadOnlyList<RotationMember>> MembersAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return InOrder(await db.RotationMembers.AsNoTracking().Where(m => !activeOnly || m.IsActive).ToListAsync(ct));
    }

    public async Task AddMemberAsync(string userId, string displayName, CancellationToken ct = default)
    {
        RequireAdmin();
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("Choose a person.");
        var id = userId.Trim();
        await using var db = await dbs.CreateAsync(ct);
        if (await db.RotationMembers.FirstOrDefaultAsync(m => m.UserId == id, ct) is { } existing)
        {
            existing.IsActive = true;
        }
        else
        {
            var order = await db.RotationMembers.Select(m => (int?)m.SortOrder).MaxAsync(ct) ?? -1;
            db.RotationMembers.Add(new RotationMember { UserId = id, DisplayName = ArticleService.Clean(displayName, 256), SortOrder = order + 1 });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Takes someone out of the rotation (their past and assigned weeks stay as they are).</summary>
    public async Task SetMemberActiveAsync(string userId, bool active, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var m = await db.RotationMembers.FirstOrDefaultAsync(x => x.UserId == userId, ct) ?? throw new KeyNotFoundException("They aren't in the rotation.");
        m.IsActive = active;
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveMemberAsync(string userId, int by, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var all = InOrder(await db.RotationMembers.ToListAsync(ct));
        AdminLists.Move(all, all.FindIndex(m => m.UserId == userId), by, (m, i) => m.SortOrder = i);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Fills the next <paramref name="weeks"/> weeks from <paramref name="from"/> that have nobody, in roster order,
    /// carrying on after whoever had the last assigned week before them. Returns how many weeks were filled.
    /// </summary>
    public async Task<int> GenerateAsync(DateOnly from, int weeks, CancellationToken ct = default)
    {
        RequireAdmin();
        if (weeks is < 1 or > 52) throw new ArgumentException("Fill 1 to 52 weeks at a time.");
        var start = WeekStart(from);
        await using var db = await dbs.CreateAsync(ct);
        var roster = InOrder(await db.RotationMembers.Where(m => m.IsActive).ToListAsync(ct));
        if (roster.Count == 0) throw new InvalidOperationException("Add analysts to the rotation first.");
        var existing = await db.RotationWeeks.Where(w => w.WeekStart >= start.AddDays(-7 * 52)).ToDictionaryAsync(w => w.WeekStart, ct);
        var last = existing.Values.Where(w => w.WeekStart < start).OrderByDescending(w => w.WeekStart).FirstOrDefault();
        var next = last is null ? 0 : (roster.FindIndex(m => m.UserId == last.AnalystId) + 1) % roster.Count;
        var filled = 0;
        for (var i = 0; i < weeks; i++)
        {
            var week = start.AddDays(7 * i);
            if (existing.TryGetValue(week, out var taken))
            {
                var idx = roster.FindIndex(m => m.UserId == taken.AnalystId);
                if (idx >= 0) next = (idx + 1) % roster.Count;
                continue;
            }
            var m = roster[next];
            db.RotationWeeks.Add(new RotationWeek { WeekStart = week, AnalystId = m.UserId, AnalystName = m.DisplayName, UpdatedAtUtc = DateTimeOffset.UtcNow, UpdatedBy = user.UserId });
            next = (next + 1) % roster.Count;
            filled++;
        }
        await db.SaveChangesAsync(ct);
        return filled;
    }

    /// <summary>
    /// Sets (or, with no one, clears) a week's analyst. The week's days whose meeting was held (the brief presented or sent,
    /// or held and put back to ready: <see cref="HeldAsync"/>) keep whoever presented them, as days kept for them, and are
    /// returned in <see cref="WeekSet.Kept"/>; the rest pass to the new analyst. With <paramref name="correctPastDays"/>, an
    /// administrator's deliberate correction, those days change too, and the audit trail records it as a correction.
    /// A day an approved change moved stays with that change either way.
    /// </summary>
    public async Task<WeekSet> SetWeekAsync(DateOnly weekStart, string? userId, bool correctPastDays = false, CancellationToken ct = default)
    {
        RequireAdmin();
        var start = WeekStart(weekStart);
        var end = start.AddDays(6);
        await using var db = await dbs.CreateAsync(ct);
        var row = await db.RotationWeeks.FirstOrDefaultAsync(w => w.WeekStart == start, ct);
        var was = row?.AnalystId;
        var wasName = row?.AnalystName;
        RotationMember? m = null;
        if (!string.IsNullOrWhiteSpace(userId))
            m = await db.RotationMembers.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct) ?? throw new ArgumentException("Add them to the rotation first.");
        if (m?.UserId == was && !correctPastDays) return new WeekSet([], []);

        var overrides = await db.RotationOverrides.Where(o => o.Date >= start && o.Date <= end).ToListAsync(ct);
        var days = BusinessDays(start, end, clock.Days).ToList();
        var briefs = await db.Briefs.AsNoTracking().Where(b => b.BriefDate >= start && b.BriefDate <= end).ToListAsync(ct);
        var held = await HeldAsync(db, briefs, ct);
        var heldDays = briefs.Where(b => b.Status is BriefStatus.Presented or BriefStatus.Sent || held.Contains(b.Id)).Select(b => b.BriefDate).ToHashSet();
        var kept = new List<(DateOnly, string)>();
        var corrected = new List<DateOnly>();
        foreach (var d in days.Where(heldDays.Contains))
        {
            var o = overrides.FirstOrDefault(x => x.Date == d);
            if (o?.ChangeId is not null) continue;   // an approved change moved it: not the week's to give
            if (correctPastDays)
            {
                // The day follows the week again, kept or not before.
                if (o is not null) db.RotationOverrides.Remove(o);
                if ((o?.AnalystId ?? was) != m?.UserId) corrected.Add(d);
            }
            else if (o is null && was is not null)
            {
                db.RotationOverrides.Add(new RotationOverride { Date = d, AnalystId = was, AnalystName = wasName!, ChangeId = null });
                kept.Add((d, wasName!));
            }
            else if (o is not null && o.AnalystId != m?.UserId) kept.Add((d, o.AnalystName));
            else if (o is not null) db.RotationOverrides.Remove(o);   // kept for the analyst the week goes back to: the week says so again
        }

        if (m is null)
        {
            if (row is not null) db.RotationWeeks.Remove(row);
        }
        else
        {
            if (row is null) db.RotationWeeks.Add(row = new RotationWeek { WeekStart = start });
            row.AnalystId = m.UserId;
            row.AnalystName = m.DisplayName;
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            row.UpdatedBy = user.UserId;
            // The week's days that no change moves and that weren't presented pass to the new analyst, and so do their briefs.
            var moved = overrides.Select(o => o.Date).ToHashSet();
            if (was is not null)
                await FollowAsync(db, days.Where(d => !moved.Contains(d) && !heldDays.Contains(d)).Select(d => (d, was, m.UserId, m.DisplayName)), ct);
        }
        if (corrected.Count > 0)
        {
            // The interceptor records the week's row; this says the presented days were changed on purpose.
            db.AuditLog.Add(new AuditLogEntry
            {
                AtUtc = DateTimeOffset.UtcNow, UserId = user.UserId, UserName = user.DisplayName, Action = "Modified", EntityType = nameof(RotationWeek),
                EntityKey = start.ToString(CultureInfo.InvariantCulture),
                ChangesJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?[]>
                {
                    ["Correction"] = [null, "Presented days corrected: " + string.Join(", ", corrected.Select(d => d.ToString("ddd MMM d", CultureInfo.InvariantCulture)))],
                    ["AnalystName"] = [wasName, m?.DisplayName],
                }),
            });
        }
        await db.SaveChangesAsync(ct);
        return new WeekSet(kept, corrected);
    }

    public async Task<IReadOnlyList<string>> ManagerEmailsAsync(CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == ManagerEmailsKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return (value ?? "").Split([';', ',', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => MailAddress.TryCreate(a, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Who's told about every approved swap and cover (they don't approve them).</summary>
    public async Task SaveManagerEmailsAsync(string? addresses, CancellationToken ct = default)
    {
        RequireAdmin();
        var list = (addresses ?? "").Split([';', ',', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var bad = list.Where(a => !MailAddress.TryCreate(a, out var p) || !p.Address.Equals(a, StringComparison.OrdinalIgnoreCase)).ToList();
        if (bad.Count > 0) throw new ArgumentException("Not an email address: " + string.Join(", ", bad.Take(3)));
        var value = string.Join("; ", list.Distinct(StringComparer.OrdinalIgnoreCase));
        // The setting's column holds 4,000 characters; SQL Server refuses more, SQLite would keep it.
        if (value.Length > 4000) throw new ArgumentException("That's too many addresses; use a distribution list instead.");
        await using var db = await dbs.CreateAsync(ct);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == ManagerEmailsKey, ct);
        if (row is null) db.Settings.Add(row = new AppSetting { Key = ManagerEmailsKey });
        row.Value = value;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        row.UpdatedBy = user.UserId;
        await db.SaveChangesAsync(ct);
    }

    // --- swaps and cover -----------------------------------------------------------------------------------------

    /// <summary>
    /// Asks a colleague to cover some of the requester's days, or to swap them for some of the colleague's. The requester
    /// must hold every business day they give; for a swap, the colleague on every day they give back. The
    /// colleague is emailed to approve. Returns the request.
    /// </summary>
    public async Task<RotationChange> RequestAsync(RotationRequest r, CancellationToken ct = default)
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts in the rotation can ask for a swap or cover.");
        var requesterId = string.IsNullOrWhiteSpace(r.RequesterId) || r.RequesterId == user.UserId ? user.UserId
            : user.Has(Permission.Administer) ? r.RequesterId.Trim() : throw new ForbiddenException("Only an administrator can arrange cover for someone else.");
        if (!Enum.IsDefined(r.Kind)) throw new ArgumentException("Choose cover or swap.");
        if (r.PartnerId == requesterId) throw new ArgumentException("Choose someone else to take the days.");
        var give = Range(r.GiveFrom, r.GiveTo, "days given");
        IReadOnlyList<DateOnly> take = [];
        if (r.Kind == RotationChangeKind.Swap)
        {
            if (r.TakeFrom is not { } tf || r.TakeTo is not { } tt) throw new ArgumentException("A swap needs the days taken in return.");
            take = Range(tf, tt, "days taken");
            if (take.Intersect(give).Any()) throw new ArgumentException("The days given and taken can't overlap.");
        }
        await using var db = await dbs.CreateAsync(ct);
        var members = await db.RotationMembers.AsNoTracking().ToDictionaryAsync(m => m.UserId, ct);
        if (!members.TryGetValue(requesterId, out var requester)) throw new ArgumentException("Only someone in the rotation can give days away.");
        if (!members.TryGetValue(r.PartnerId, out var partner) || !partner.IsActive) throw new ArgumentException("Choose someone in the rotation to take the days.");
        await CheckHolds(give, requesterId, requester.DisplayName, ct);
        if (take.Count > 0) await CheckHolds(take, partner.UserId, partner.DisplayName, ct);
        var change = new RotationChange
        {
            Kind = r.Kind, RequesterId = requesterId, RequesterName = requester.DisplayName, PartnerId = partner.UserId, PartnerName = partner.DisplayName,
            GiveFrom = give[0], GiveTo = give[^1], TakeFrom = take.Count > 0 ? take[0] : null, TakeTo = take.Count > 0 ? take[^1] : null,
            Note = ArticleService.CleanOrNull(r.Note, 500), CreatedById = user.UserId, CreatedByName = user.DisplayName, CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.RotationChanges.Add(change);
        await db.SaveChangesAsync(ct);
        await NotifyAsync(db, [partner.UserId], Render(EmailTemplateCatalog.RotationRequest, change, new()
        {
            ["Ask"] = change.Kind == RotationChangeKind.Swap ? "swap brief days" : "cover their brief days",
            ["Note"] = change.Note is null ? "" : $"<p style=\"margin:0 0 12px;\">\u201C{Esc(change.Note)}\u201D</p>",
        }), ct);
        return change;
    }

    /// <summary>
    /// The colleague approves: the days still to come change hands (the request is narrowed to them), and the manager
    /// is told.
    /// </summary>
    public async Task ApproveAsync(Guid id, string? note = null, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        await Deciding.WaitAsync(ct);
        RotationChange change;
        try
        {
            change = await Pending(db, id, ct);
            if (change.PartnerId != user.UserId) throw new ForbiddenException($"Only {change.PartnerName} can approve this.");
            var allGive = BusinessDays(change.GiveFrom, change.GiveTo, clock.Days).ToList();
            var allTake = change.TakeFrom is { } tf && change.TakeTo is { } tt ? BusinessDays(tf, tt, clock.Days).ToList() : [];
            // Holidays added since the request may have taken every day on one side.
            if (allGive.Count == 0 || (change.TakeFrom is not null && allTake.Count == 0))
                throw new InvalidOperationException("The days in this request are now holidays, so there's nothing to hand over. Decline it, and ask again for other days if needed.");
            // Approved late, only the days still to come change hands; a past day stays with whoever the rota shows (its
            // brief's presenter is recorded from the brief). Today is past once its meeting has been held. A swap needs
            // days to come on both sides, or it's a gift.
            var first = await FirstDayToComeAsync(db, ct);
            var give = allGive.Where(d => d >= first).ToList();
            var take = allTake.Where(d => d >= first).ToList();
            if (give.Count == 0 || (change.TakeFrom is not null && take.Count == 0))
                throw new InvalidOperationException(change.TakeFrom is not null && (give.Count > 0 || take.Count > 0)
                    ? "One side of this swap has passed, so it can't be a swap any more. Decline it, and ask again if needed."
                    : "The days in this request have passed, so there's nothing to hand over. Decline it.");
            change.GiveFrom = give[0];
            change.GiveTo = give[^1];
            if (take.Count > 0) (change.TakeFrom, change.TakeTo) = (take[0], take[^1]);
            // Still true? Another change may have moved these days since the request.
            await CheckHolds(give, change.RequesterId, change.RequesterName, ct);
            if (take.Count > 0) await CheckHolds(take, change.PartnerId, change.PartnerName, ct);
            var existing = await db.RotationOverrides.Where(o => give.Contains(o.Date) || take.Contains(o.Date)).ToDictionaryAsync(o => o.Date, ct);
            void Set(DateOnly d, string id2, string name)
            {
                if (!existing.TryGetValue(d, out var o)) db.RotationOverrides.Add(o = new RotationOverride { Date = d });
                o.AnalystId = id2;
                o.AnalystName = name;
                o.ChangeId = change.Id;
            }
            foreach (var d in give) Set(d, change.PartnerId, change.PartnerName);
            foreach (var d in take) Set(d, change.RequesterId, change.RequesterName);
            await FollowAsync(db, give.Select(d => (d, change.RequesterId, change.PartnerId, change.PartnerName))
                .Concat(take.Select(d => (d, change.PartnerId, change.RequesterId, change.RequesterName))), ct);
            Decide(change, RotationChangeStatus.Approved);
            change.DecisionNote = ArticleService.CleanOrNull(note, 500);
            await db.SaveChangesAsync(ct);
        }
        finally { Deciding.Release(); }

        var approved = Render(EmailTemplateCatalog.RotationApproved, change, new()
        {
            ["DecisionNote"] = change.DecisionNote is null ? "" : $": \u201C{change.DecisionNote}\u201D",
        });
        await NotifyAsync(db, [change.RequesterId, change.CreatedById], approved, ct);
        await NotifyManagersAsync(approved, ct);
    }

    /// <summary>
    /// Records, after the fact, that a past day's brief was presented by someone other than the analyst the rotation
    /// named: an approved cover from the rota's analyst to the brief's presenter. The brief is the evidence, so there's
    /// nothing left to approve (ADR 0010 covers days to come); the brief's presenter, the rota's analyst or an
    /// administrator records it, and the other analyst and the manager are told as for any change.
    /// </summary>
    public async Task<RotationChange?> RecordPastCoverAsync(DateOnly date, CancellationToken ct = default)
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts can record a change to the rotation.");
        if (date >= clock.Today) throw new ArgumentException("Only a past day can be recorded this way; ask for cover for days to come.");
        await using var db = await dbs.CreateAsync(ct);
        await Deciding.WaitAsync(ct);
        RotationChange change;
        try
        {
            var brief = await db.Briefs.AsNoTracking().FirstOrDefaultAsync(b => b.BriefDate == date, ct)
                        ?? throw new KeyNotFoundException("There's no brief for that day.");
            var day = (await DaysAsync(date, date, ct)).FirstOrDefault(d => d.Date == date);
            if (day?.AnalystId is null) throw new InvalidOperationException("The rotation names nobody for that day.");
            if (day.AnalystId == brief.AnalystId) return null;
            if (user.UserId != brief.AnalystId && user.UserId != day.AnalystId && !user.Has(Permission.Administer))
                throw new ForbiddenException($"Only {brief.AnalystName}, {day.AnalystName} or an administrator can record this.");
            var now = DateTimeOffset.UtcNow;
            change = new RotationChange
            {
                Kind = RotationChangeKind.Cover, RequesterId = day.AnalystId, RequesterName = day.AnalystName!, PartnerId = brief.AnalystId, PartnerName = brief.AnalystName,
                GiveFrom = date, GiveTo = date, Note = "Recorded after the fact from the day's brief.", Status = RotationChangeStatus.Approved,
                CreatedById = user.UserId, CreatedByName = user.DisplayName, CreatedAtUtc = now, DecidedAtUtc = now, DecidedByName = user.DisplayName,
            };
            db.RotationChanges.Add(change);
            if (await db.RotationOverrides.FirstOrDefaultAsync(o => o.Date == date, ct) is not { } o) db.RotationOverrides.Add(o = new RotationOverride { Date = date });
            o.AnalystId = change.PartnerId;
            o.AnalystName = change.PartnerName;
            o.ChangeId = change.Id;
            await db.SaveChangesAsync(ct);
        }
        finally { Deciding.Release(); }

        var recorded = Render(EmailTemplateCatalog.RotationRecorded, change, new() { ["RecordedBy"] = user.DisplayName });
        await NotifyAsync(db, new[] { change.RequesterId, change.PartnerId }.Where(id => id != user.UserId), recorded, ct);
        await NotifyManagersAsync(recorded, ct);
        return change;
    }

    // The managers' copy: the same subject and body, headed as for their information. Their addresses are the
    // rotation's settings, not people's own choices, so no one's opt-out removes them.
    private async Task NotifyManagersAsync(RenderedTemplate t, CancellationToken ct)
    {
        var managers = await ManagerEmailsAsync(ct);
        if (managers.Count == 0) return;
        var notice = Html("Brief rotation change (for your information)", t.BodyHtml);
        await email.SendAsync(new OutgoingEmail(managers, t.Subject, notice, ToText(notice), []), ct);
    }

    /// <summary>The colleague declines; the requester is told.</summary>
    public async Task DeclineAsync(Guid id, string? note = null, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        await Deciding.WaitAsync(ct);
        RotationChange change;
        try
        {
            change = await Pending(db, id, ct);
            if (change.PartnerId != user.UserId) throw new ForbiddenException($"Only {change.PartnerName} can decline this.");
            Decide(change, RotationChangeStatus.Declined);
            change.DecisionNote = ArticleService.CleanOrNull(note, 500);
            await db.SaveChangesAsync(ct);
        }
        finally { Deciding.Release(); }
        await NotifyAsync(db, [change.RequesterId, change.CreatedById], Render(EmailTemplateCatalog.RotationDeclined, change, new()
        {
            ["DecisionNote"] = change.DecisionNote is null ? "" : $" \u201C{change.DecisionNote}\u201D",
        }), ct);
    }

    /// <summary>
    /// Withdraws a pending request (its requester or whoever made it), or undoes an approved change (an administrator):
    /// the days it moved go back to the weekly rota.
    /// </summary>
    public async Task CancelAsync(Guid id, CancellationToken ct = default)
    {
        await Deciding.WaitAsync(ct);
        try { await CancelLockedAsync(id, ct); }
        finally { Deciding.Release(); }
    }

    private async Task CancelLockedAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        var change = await db.RotationChanges.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new KeyNotFoundException("That request no longer exists.");
        if (change.Status == RotationChangeStatus.Pending)
        {
            if (change.RequesterId != user.UserId && change.CreatedById != user.UserId && !user.Has(Permission.Administer))
                throw new ForbiddenException("Only whoever asked can withdraw this.");
        }
        else if (change.Status == RotationChangeStatus.Approved)
        {
            if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can undo an approved change.");
            if (!HasDaysFrom(change, await FirstDayToComeAsync(db, ct)))
                throw new InvalidOperationException("Every day in this change has passed, so there's nothing to undo.");
            await RestoreDaysAsync(db, change, ct);
        }
        else return;
        Decide(change, RotationChangeStatus.Cancelled);
        await db.SaveChangesAsync(ct);
    }

    // The days an approved change held go back to what they were: the latest other approved change that still covers the
    // day (this one may have been layered on it), else the weekly rota. A day that has passed (today, once its meeting
    // was held) stays with whoever had it.
    private async Task RestoreDaysAsync(IAppDbContext db, RotationChange change, CancellationToken ct)
    {
        var toCome = await FirstDayToComeAsync(db, ct);
        var held = await db.RotationOverrides.Where(o => o.ChangeId == change.Id && o.Date >= toCome).ToListAsync(ct);
        if (held.Count == 0) return;
        var first = held.Min(o => o.Date);
        var last = held.Max(o => o.Date);
        var others = await db.RotationChanges.AsNoTracking()
            .Where(c => c.Id != change.Id && c.Status == RotationChangeStatus.Approved)
            .Where(c => (c.GiveFrom <= last && c.GiveTo >= first) || (c.TakeFrom != null && c.TakeFrom <= last && c.TakeTo >= first))
            .ToListAsync(ct);
        var firstWeek = WeekStart(first);
        var weeks = await db.RotationWeeks.AsNoTracking().Where(w => w.WeekStart >= firstWeek && w.WeekStart <= last).ToDictionaryAsync(w => w.WeekStart, ct);
        var moves = new List<(DateOnly, string, string, string)>();
        foreach (var o in held)
        {
            var earlier = others.Select(c => (Change: c, Who: Assigns(c, o.Date))).Where(x => x.Who is not null)
                .OrderByDescending(x => x.Change.DecidedAtUtc ?? DateTimeOffset.MinValue).FirstOrDefault();
            if (earlier.Who is not { } who)
            {
                if (weeks.GetValueOrDefault(WeekStart(o.Date)) is { } week) moves.Add((o.Date, o.AnalystId, week.AnalystId, week.AnalystName));
                db.RotationOverrides.Remove(o);
                continue;
            }
            moves.Add((o.Date, o.AnalystId, who.Id, who.Name));
            o.AnalystId = who.Id;
            o.AnalystName = who.Name;
            o.ChangeId = earlier.Change.Id;
        }
        await FollowAsync(db, moves, ct);
    }

    // A brief already started for a day the rota moves (carry-over starts the next day's brief early, say) follows it,
    // until its meeting: one whose presenter is still who held the day takes whoever holds it now, so its emails and the
    // "not ready" reminder name the right analyst. A presenter set by hand to someone else is kept; a past day is left be.
    // So is a brief whose meeting was held (HeldAsync): Back to ready clears its times, and its presenter stays who held it.
    private async Task FollowAsync(IAppDbContext db, IEnumerable<(DateOnly Date, string From, string ToId, string ToName)> moves, CancellationToken ct)
    {
        var byDay = moves.Where(m => m.Date >= clock.Today && m.From != m.ToId).GroupBy(m => m.Date).ToDictionary(g => g.Key, g => g.Last());
        if (byDay.Count == 0) return;
        var days = byDay.Keys.ToList();
        var briefs = await db.Briefs.Where(b => days.Contains(b.BriefDate) && (b.Status == BriefStatus.Draft || b.Status == BriefStatus.Ready)).ToListAsync(ct);
        var held = await HeldAsync(db, briefs, ct);
        foreach (var b in briefs.Where(b => !held.Contains(b.Id)))
        {
            var m = byDay[b.BriefDate];
            if (b.AnalystId != m.From) continue;
            b.AnalystId = m.ToId;
            b.AnalystName = m.ToName;
            b.EditVersion = Guid.NewGuid();
        }
    }

    /// <summary>
    /// The approved changes among <paramref name="changes"/> that an Undo would still change (<see cref="CancelAsync"/>
    /// refuses the rest): a day of either half still to come, today not once its meeting has been held. The rotation
    /// page offers Undo for these only.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> UndoableAsync(IEnumerable<RotationChange> changes, CancellationToken ct = default)
    {
        RequireRead();
        var approved = changes.Where(c => c.Status == RotationChangeStatus.Approved).ToList();
        if (approved.Count == 0) return new HashSet<Guid>();
        await using var db = await dbs.CreateAsync(ct);
        var first = await FirstDayToComeAsync(db, ct);
        return approved.Where(c => HasDaysFrom(c, first)).Select(c => c.Id).ToHashSet();
    }

    // Whether a change still moves a day from first on: either half of a swap.
    private static bool HasDaysFrom(RotationChange c, DateOnly first) => c.GiveTo >= first || (c.TakeTo is { } takeTo && takeTo >= first);

    // The first day whose presenter can still change: today, unless today's meeting has been held (its brief presented,
    // sent, or held and put back to ready), when today is as past as yesterday and its brief says who presented it.
    private async Task<DateOnly> FirstDayToComeAsync(IAppDbContext db, CancellationToken ct)
    {
        var today = clock.Today;
        var brief = await db.Briefs.AsNoTracking().FirstOrDefaultAsync(b => b.BriefDate == today, ct);
        if (brief is null) return today;
        if (brief.Status is BriefStatus.Presented or BriefStatus.Sent) return today.AddDays(1);
        return (await HeldAsync(db, [brief], ct)).Contains(brief.Id) ? today.AddDays(1) : today;
    }

    // The briefs among these whose meeting was held, though Back to ready (BriefService.ReopenAsync) took them back to
    // Ready and cleared MeetingHeldAtUtc and PresentedAtUtc. What a reopen keeps says so: anyone recorded as attending, a
    // meeting note (or the notes kept on the brief before they were per person), and, for a meeting ended with neither,
    // the times in the brief's audit trail. A discussed tick isn't a sign: it can be set before the meeting.
    private static async Task<HashSet<Guid>> HeldAsync(IAppDbContext db, IReadOnlyCollection<Brief> briefs, CancellationToken ct)
    {
        var held = briefs.Where(b => b.MeetingHeldAtUtc is not null || b.PresentedAtUtc is not null || !string.IsNullOrWhiteSpace(b.MeetingNotes))
            .Select(b => b.Id).ToHashSet();
        var ids = briefs.Select(b => b.Id).Where(id => !held.Contains(id)).ToList();
        if (ids.Count == 0) return held;
        held.UnionWith(await db.MeetingAttendees.Where(a => ids.Contains(a.BriefId)).Select(a => a.BriefId).Distinct().ToListAsync(ct));
        held.UnionWith(await db.MeetingNotes.Where(n => ids.Contains(n.BriefId)).Select(n => n.BriefId).Distinct().ToListAsync(ct));
        // The audit interceptor writes a Guid key in the "D" format.
        var keys = ids.Where(id => !held.Contains(id)).ToDictionary(id => id.ToString("D"));
        if (keys.Count == 0) return held;
        const string ended = "\"" + nameof(Brief.PresentedAtUtc) + "\"";
        var list = keys.Keys.ToList();
        held.UnionWith((await db.AuditLog.Where(e => e.EntityType == nameof(Brief) && list.Contains(e.EntityKey) && e.ChangesJson!.Contains(ended))
            .Select(e => e.EntityKey).Distinct().ToListAsync(ct)).Select(k => keys[k]));
        return held;
    }

    // Who an approved change puts on a day, or null when it doesn't touch it.
    private static (string Id, string Name)? Assigns(RotationChange c, DateOnly day) =>
        day >= c.GiveFrom && day <= c.GiveTo ? (c.PartnerId, c.PartnerName)
        : c.TakeFrom is { } tf && c.TakeTo is { } tt && day >= tf && day <= tt ? (c.RequesterId, c.RequesterName)
        : null;

    // --- the calendar file ---------------------------------------------------------------------------------------

    /// <summary>The current user's brief days over the next six months, as an iCalendar file (one all-day event per run).</summary>
    public async Task<string> MyCalendarAsync(CancellationToken ct = default)
    {
        RequireRead();
        var today = clock.Today;
        var runs = Runs(await DaysAsync(today.AddDays(-7), today.AddDays(183), ct), clock.Days).Where(r => r.AnalystId == user.UserId).ToList();
        return Ics.Write(runs.SelectMany(r => AroundHolidays(r.From, r.To, clock.Days)).Select(r => (r.From, r.To, $"Presenting the {clock.TeamName} daily brief")),
            "TIDE brief rotation");
    }

    // A run as calendar events: one, unless a holiday falls inside it, which no event covers (a weekend inside one does,
    // as it always has).
    internal static IEnumerable<(DateOnly From, DateOnly To)> AroundHolidays(DateOnly from, DateOnly to, WorkingDays workingDays)
    {
        DateOnly? start = null;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (workingDays.HolidayOn(d) is not null)
            {
                if (start is { } s) yield return (s, workingDays.Previous(d));
                start = null;
            }
            else if (start is null && workingDays.IsWorkingDay(d)) start = d;
        }
        if (start is { } last) yield return (last, to);
    }

    // --- helpers -------------------------------------------------------------------------------------------------

    private List<DateOnly> Range(DateOnly from, DateOnly to, string what)
    {
        if (to < from) throw new ArgumentException($"The {what} end before they start.");
        if (from < clock.Today) throw new ArgumentException($"The {what} can't be in the past.");
        var days = BusinessDays(from, to, clock.Days).ToList();
        if (days.Count == 0) throw new ArgumentException($"The {what} include no business day.");
        if (days.Count > MaxRequestDays) throw new ArgumentException($"One request can move at most {MaxRequestDays} business days.");
        return days;
    }

    private async Task CheckHolds(IReadOnlyList<DateOnly> days, string userId, string name, CancellationToken ct)
    {
        var who = await DaysAsync(days[0], days[^1], ct);
        var notTheirs = who.Where(d => days.Contains(d.Date) && d.AnalystId != userId).Select(d => d.Date).ToList();
        if (notTheirs.Count > 0)
            throw new InvalidOperationException($"{name} isn't presenting on {string.Join(", ", notTheirs.Take(3).Select(Day))}{(notTheirs.Count > 3 ? "…" : "")}.");
    }

    // Decisions (approve, decline, undo, a past cover) check who holds the days and then write; run one at a time, two
    // decisions about overlapping days can't both pass the check. (TIDE runs as one process: ADR 0001.)
    private static readonly SemaphoreSlim Deciding = new(1, 1);

    private static async Task<RotationChange> Pending(IAppDbContext db, Guid id, CancellationToken ct)
    {
        var change = await db.RotationChanges.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new KeyNotFoundException("That request no longer exists.");
        return change.Status == RotationChangeStatus.Pending ? change : throw new InvalidOperationException("That request has already been decided.");
    }

    private void Decide(RotationChange change, RotationChangeStatus status)
    {
        change.Status = status;
        change.DecidedAtUtc = DateTimeOffset.UtcNow;
        change.DecidedByName = user.DisplayName;
    }

    // The roster in its order (by name where two share a place).
    private static List<RotationMember> InOrder(IEnumerable<RotationMember> members) =>
        members.OrderBy(m => m.SortOrder).ThenBy(m => m.DisplayName).ThenBy(m => m.UserId, StringComparer.Ordinal).ToList();

    private static string Day(DateOnly d) => d.ToString("ddd MMM d", CultureInfo.InvariantCulture);

    private static string Span(DateOnly from, DateOnly to) => from == to ? Day(from) : $"{Day(from)} to {Day(to)}";

    /// <summary>"Robin covers Sam's brief days Mon Nov 2 to Wed Nov 4." or the swap's two halves.</summary>
    public static string Describe(RotationChange c, bool brief = false) => c.Kind == RotationChangeKind.Swap && c.TakeFrom is { } tf && c.TakeTo is { } tt
        ? brief ? $"{c.RequesterName} and {c.PartnerName} swap {Span(c.GiveFrom, c.GiveTo)} / {Span(tf, tt)}"
            : $"{c.PartnerName} takes {c.RequesterName}'s brief days {Span(c.GiveFrom, c.GiveTo)}, and {c.RequesterName} takes {c.PartnerName}'s {Span(tf, tt)}."
        : brief ? $"{c.PartnerName} covers {c.RequesterName} {Span(c.GiveFrom, c.GiveTo)}"
            : $"{c.PartnerName} covers {c.RequesterName}'s brief days {Span(c.GiveFrom, c.GiveTo)}.";

    private string Link(string path) => string.IsNullOrWhiteSpace(app.Value.BaseUrl) ? ""
        : $" (<a style=\"color:{Palette.Link};\" href=\"{Esc(app.Value.BaseUrl.TrimEnd('/') + "/" + path)}\">open it</a>)";

    private string Html(string title, string body) =>
        Shell(title, clock.TeamName + " brief rotation", body, "An automatic message from TIDE. Replies aren't read.");

    // A rotation email in the wording set in Administration › Email templates (or the default): the change's own values,
    // and any the email adds.
    private RenderedTemplate Render(EmailTemplate t, RotationChange change, Dictionary<string, string> values)
    {
        values["Requester"] = change.RequesterName;
        values["Partner"] = change.PartnerName;
        values["Change"] = Describe(change);
        values["ChangeShort"] = Describe(change, brief: true);
        values["RotationLink"] = Link("rotation");
        return (templates ?? EmailTemplateStore.Defaults).Render(t, values);
    }

    // Emails the people (by user id) who have an address and haven't turned rotation requests and notices off in My
    // notifications; a send that fails doesn't undo the change.
    private async Task NotifyAsync(IAppDbContext db, IEnumerable<string> userIds, RenderedTemplate t, CancellationToken ct)
    {
        var to = (await RecipientResolver.ResolveAsync(db, NotificationKinds.RotationRequests, userIds, ct)).Reachable();
        if (to.Count == 0) return;
        var html = Html(t.Heading, t.BodyHtml);
        await email.SendAsync(new OutgoingEmail(to, t.Subject, html, ToText(html), []), ct);
    }

    private void RequireRead()
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("You don't have access to the rotation.");
    }

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can change the rotation.");
    }
}

/// <summary>A minimal iCalendar (RFC 5545) writer for all-day events.</summary>
public static class Ics
{
    public static string Write(IEnumerable<(DateOnly From, DateOnly To, string Summary)> events, string calendarName)
    {
        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//TIDE//Brief rotation//EN\r\nCALSCALE:GREGORIAN\r\nMETHOD:PUBLISH\r\n");
        sb.Append("X-WR-CALNAME:").Append(Escape(calendarName)).Append("\r\n");
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var (from, to, summary) in events)
        {
            sb.Append("BEGIN:VEVENT\r\n");
            sb.Append("UID:").Append(CultureInfo.InvariantCulture, $"tide-brief-{from:yyyyMMdd}-{to:yyyyMMdd}@tide").Append("\r\n");
            sb.Append("DTSTAMP:").Append(stamp).Append("\r\n");
            sb.Append("DTSTART;VALUE=DATE:").Append(from.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append("\r\n");
            // DTEND is exclusive for all-day events.
            sb.Append("DTEND;VALUE=DATE:").Append(to.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("SUMMARY:").Append(Escape(summary)).Append("\r\n");
            sb.Append("TRANSP:TRANSPARENT\r\nEND:VEVENT\r\n");
        }
        sb.Append("END:VCALENDAR\r\n");
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");
}
