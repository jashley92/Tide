using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>Where an action stands against its due date.</summary>
public enum SlaState
{
    /// <summary>No due date.</summary>
    NoDue = 0,
    OnTrack = 1,

    /// <summary>Due today or the next business day.</summary>
    DueSoon = 2,
    Overdue = 3,

    /// <summary>Closed on or before its due date.</summary>
    Met = 4,

    /// <summary>Closed after its due date.</summary>
    Missed = 5,
}

/// <summary>How soon an action needs doing: the "Mine" list's group headings, in their order.</summary>
public enum ActionUrgency
{
    Overdue = 0,
    DueToday = 1,

    /// <summary>Due after today, by the end of the current business week.</summary>
    ThisWeek = 2,

    /// <summary>Due after this business week, or no due date.</summary>
    Later = 3,

    /// <summary>Blocked, whatever its due date.</summary>
    Blocked = 4,
}

/// <summary>
/// How long each kind of action gets, in business days. A kind's target, when set, wins over the priority's; with
/// neither, an action has no default due date.
/// </summary>
public sealed record SlaTargets(IReadOnlyDictionary<Priority, int> ByPriority, IReadOnlyDictionary<ActionKind, int> ByKind)
{
    /// <summary>Business days for an action of this priority and kind, or null for no target.</summary>
    public int? DaysFor(Priority priority, ActionKind kind) =>
        ByKind.TryGetValue(kind, out var k) && k > 0 ? k
        : ByPriority.TryGetValue(priority, out var p) && p > 0 ? p
        : null;
}

/// <summary>
/// The SLA rules, kept pure so they're tested on their own. Dates are the organization's working days: callers pass the
/// clock's (<see cref="OrgClock.Days"/>, with its holidays); left out, they're Monday to Friday.
/// </summary>
public static class ActionSla
{
    /// <summary>The longest target accepted, in business days (about a year).</summary>
    public const int MaxDays = 260;

    /// <summary>The due date for a new action: <paramref name="days"/> business days after <paramref name="from"/>.</summary>
    public static DateOnly? DueDate(DateOnly from, int? days, WorkingDays? workingDays = null)
    {
        if (days is not > 0) return null;
        var w = workingDays ?? WorkingDays.Weekdays;
        var d = from;
        for (var i = 0; i < days; i++) d = w.Next(d);
        return d;
    }

    /// <summary>How many days past <paramref name="due"/> <paramref name="today"/> is (calendar days, as people say "3 days
    /// late"); 0 when it isn't past, or either is unknown.</summary>
    public static int Late(DateOnly? due, DateOnly? today) =>
        due is { } d && today is { } t && t > d ? t.DayNumber - d.DayNumber : 0;

    /// <summary>Where an action stands on <paramref name="today"/>.</summary>
    public static SlaState StateOf(ActionStatus status, DateOnly? due, DateOnly? closed, DateOnly today, WorkingDays? workingDays = null)
    {
        if (due is not { } d) return SlaState.NoDue;
        if (ActionItem.IsClosedStatus(status))
            return status == ActionStatus.Done ? (closed is { } c && c > d ? SlaState.Missed : SlaState.Met) : SlaState.NoDue;
        if (d < today) return SlaState.Overdue;
        return d <= (workingDays ?? WorkingDays.Weekdays).Next(today) ? SlaState.DueSoon : SlaState.OnTrack;
    }

    public static SlaState StateOf(ActionItem a, DateOnly today, WorkingDays? workingDays = null) =>
        StateOf(a.Status, a.DueDate, a.ClosedDate, today, workingDays);

    /// <summary>
    /// The last business day of the current business week: this week's on a business day, the coming week's on a weekend
    /// (as the brief day is).
    /// </summary>
    public static DateOnly EndOfBusinessWeek(DateOnly today, WorkingDays? workingDays = null)
    {
        // That week's last working day: walk back from its Friday, so a holiday mid-week doesn't end the week early.
        var w = workingDays ?? WorkingDays.Weekdays;
        var first = w.Next(today, includeToday: true);
        var d = first.AddDays(DayOfWeek.Friday - first.DayOfWeek);
        while (d > first && !w.IsWorkingDay(d)) d = d.AddDays(-1);
        return d;
    }

    /// <summary>How many days past its due date an action is on <paramref name="today"/> (0 when it isn't late or has none).</summary>
    public static int DaysLate(DateOnly? due, DateOnly today) => due is { } d && d < today ? today.DayNumber - d.DayNumber : 0;

    /// <summary>"3 days late", "1 day late", or null when it isn't.</summary>
    public static string? LateText(DateOnly? due, DateOnly today) => DaysLate(due, today) switch
    {
        0 => null,
        1 => "1 day late",
        var n => $"{n} days late",
    };

    /// <summary>Which urgency group an action goes under on <paramref name="today"/>. Blocked wins over any due date.</summary>
    public static ActionUrgency UrgencyOf(ActionStatus status, DateOnly? due, DateOnly today, WorkingDays? workingDays = null)
    {
        if (status == ActionStatus.Blocked) return ActionUrgency.Blocked;
        if (due is not { } d || d > EndOfBusinessWeek(today, workingDays)) return ActionUrgency.Later;
        if (d < today) return ActionUrgency.Overdue;
        return d == today ? ActionUrgency.DueToday : ActionUrgency.ThisWeek;
    }

    public static string UrgencyName(ActionUrgency u) => u switch
    {
        ActionUrgency.Overdue => "Overdue",
        ActionUrgency.DueToday => "Due today",
        ActionUrgency.ThisWeek => "This week",
        ActionUrgency.Blocked => "Blocked",
        _ => "Later",
    };

    /// <summary>Business days from <paramref name="today"/> to <paramref name="due"/>: negative when overdue.</summary>
    public static int BusinessDaysUntil(DateOnly today, DateOnly due, WorkingDays? workingDays = null)
    {
        var w = workingDays ?? WorkingDays.Weekdays;
        var n = 0;
        if (due >= today)
            for (var d = today; d < due; d = d.AddDays(1)) { if (w.IsWorkingDay(d.AddDays(1))) n++; }
        else
            for (var d = due; d < today; d = d.AddDays(1)) { if (w.IsWorkingDay(d.AddDays(1))) n--; }
        return n;
    }
}

/// <summary>The default SLA targets (config section <c>Actions:Sla</c>); administrators can override them in the app.</summary>
public sealed class SlaOptions
{
    /// <summary>Business days per priority.</summary>
    public Dictionary<string, int> Priority { get; set; } = new() { ["P1"] = 1, ["P2"] = 3, ["P3"] = 10, ["P4"] = 20 };

    /// <summary>Business days per kind, overriding the priority's ("IocSweep": 1).</summary>
    public Dictionary<string, int> Kind { get; set; } = [];
}

/// <summary>The SLA targets in force: the administered value when one is saved, else the configured default.</summary>
public sealed class SlaSettings(IAppDbContextFactory dbs, ICurrentUser user, IOptions<SlaOptions> defaults)
{
    private const string PriorityPrefix = "Actions:Sla:Priority:";
    private const string KindPrefix = "Actions:Sla:Kind:";

    public async Task<SlaTargets> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var saved = await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith("Actions:Sla:")).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var byPriority = new Dictionary<Priority, int>();
        foreach (var p in Enum.GetValues<Priority>())
        {
            var v = saved.TryGetValue(PriorityPrefix + p, out var s) ? Parse(s) : defaults.Value.Priority.GetValueOrDefault(p.ToString());
            if (v > 0) byPriority[p] = Math.Min(v, ActionSla.MaxDays);
        }
        var byKind = new Dictionary<ActionKind, int>();
        foreach (var k in Enum.GetValues<ActionKind>())
        {
            var v = saved.TryGetValue(KindPrefix + k, out var s) ? Parse(s) : DefaultFor(k);
            if (v > 0) byKind[k] = Math.Min(v, ActionSla.MaxDays);
        }
        return new SlaTargets(byPriority, byKind);
    }

    /// <summary>Saves the targets; zero or blank means none (for a kind: use the priority's).</summary>
    public async Task SaveAsync(IReadOnlyDictionary<Priority, int?> byPriority, IReadOnlyDictionary<ActionKind, int?> byKind, CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can change the SLA targets.");
        if (byPriority.Values.Concat(byKind.Values).Any(v => v is < 0 or > ActionSla.MaxDays))
            throw new ArgumentException($"A target is 0 to {ActionSla.MaxDays} business days.");
        await using var db = await dbs.CreateAsync(ct);
        foreach (var (p, v) in byPriority) await Upsert(db, PriorityPrefix + p, (v ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        foreach (var (k, v) in byKind) await Upsert(db, KindPrefix + k, (v ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        await db.SaveChangesAsync(ct);
    }

    // The server's value for a kind, by its name; Outreach's also under its name before the rename, so an appsettings file that
    // still says "VendorInquiry" keeps its target.
    private int DefaultFor(ActionKind k)
    {
        var kinds = defaults.Value.Kind;
        return kinds.TryGetValue(k.ToString(), out var days) ? days : kinds.FirstOrDefault(kv => ActionService.KindNamed(kv.Key) == k).Value;
    }

    private static int Parse(string s) => int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private async Task Upsert(IAppDbContext db, string key, string value, CancellationToken ct)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.Settings.Add(row = new AppSetting { Key = key });
        row.Value = value;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        row.UpdatedBy = user.UserId;
    }
}
