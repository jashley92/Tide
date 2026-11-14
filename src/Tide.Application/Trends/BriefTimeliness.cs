using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Content;
using Tide.Application.Org;
using Tide.Application.Rotation;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Trends;

/// <summary>How a brief day went against its deadlines, before any excuse.</summary>
public enum DayVerdict
{
    /// <summary>Sent (and posted, where required) by the deadlines.</summary>
    OnTime = 0,

    /// <summary>Sent, and posted where required, but after a deadline.</summary>
    Late = 1,

    /// <summary>A deadline has passed and what it asks for hasn't happened (yet): not sent, or not posted.</summary>
    Missed = 2,

    /// <summary>Today, before its deadlines, with what they ask for not done yet: not judged.</summary>
    Pending = 3,
}

/// <summary>
/// What a brief day's deadlines are judged on: when its brief went (<see cref="SentAtUtc"/>: both editions, or the one
/// with anything in it; null while not), when its SharePoint copy was confirmed posted, and whether that was required.
/// </summary>
public sealed record DayFacts(DateTimeOffset? SentAtUtc, DateTimeOffset? PostedAtUtc, bool PostRequired);

/// <summary>
/// One brief day of an analyst's: whose it was, its deadlines, when its brief went and was posted, how it went
/// (<see cref="Verdict"/>) and by how much it was late (<see cref="LateBy"/>, when it went late), and the excuse a manager
/// gave for it, if any. An excused day that was late or missed doesn't count against the analyst (<see cref="Counts"/>).
/// </summary>
public sealed record TimelinessDay(DateOnly Date, string AnalystId, string AnalystName, DateTimeOffset SendBy, DateTimeOffset? PostBy,
    DateTimeOffset? SentAtUtc, DateTimeOffset? PostedAtUtc, DayVerdict Verdict, TimeSpan? LateBy, BriefExcuse? Excuse)
{
    /// <summary>Late or missed, and excused: it doesn't count against them.</summary>
    public bool Excused => Excuse is not null && Verdict is DayVerdict.Late or DayVerdict.Missed;

    /// <summary>Whether it's in the on-time rate: judged, and not excused.</summary>
    public bool Counts => Verdict != DayVerdict.Pending && !Excused;

    /// <summary>Whether it went out by its deadlines and isn't excused (an excused day is neither on time nor late).</summary>
    public bool OnTime => Verdict == DayVerdict.OnTime;

    /// <summary>The SharePoint copy is required and hasn't been confirmed, with its deadline passed.</summary>
    public bool NotPosted => Verdict == DayVerdict.Missed && SentAtUtc is not null;
}

/// <summary>How many days went each way. <see cref="Rate"/> is the share on time, 0 to 100, of those that count (on
/// time, late or missed; not excused or still to come); null when none count.</summary>
public sealed record Tally(int OnTime, int Late, int Missed, int Excused, int Pending)
{
    public static readonly Tally None = new(0, 0, 0, 0, 0);

    public int Counted => OnTime + Late + Missed;

    public double? Rate => Counted == 0 ? null : Math.Round(100.0 * OnTime / Counted, 1);

    public static Tally Of(IEnumerable<TimelinessDay> days)
    {
        int onTime = 0, late = 0, missed = 0, excused = 0, pending = 0;
        foreach (var d in days)
        {
            if (d.Excused) excused++;
            else switch (d.Verdict)
            {
                case DayVerdict.OnTime: onTime++; break;
                case DayVerdict.Late: late++; break;
                case DayVerdict.Missed: missed++; break;
                default: pending++; break;
            }
        }
        return new(onTime, late, missed, excused, pending);
    }
}

/// <summary>One analyst's days over the report: in all, and by week or month (<see cref="ByPeriod"/>, oldest first), with
/// the average lateness of the days that went late.</summary>
public sealed record AnalystTimeliness(string AnalystId, string AnalystName, Tally Total, IReadOnlyList<(Period Period, Tally Tally)> ByPeriod,
    TimeSpan? AverageLate);

/// <summary>
/// The Brief timeliness report: the business days from <see cref="From"/> to <see cref="To"/> (today at the latest), each
/// day's record (<see cref="Days"/>, newest first) and each analyst's (<see cref="People"/>) for whoever the reader may
/// see. <see cref="Everyone"/> says the reader sees every analyst (and so the team's figures, <see cref="Team"/>);
/// otherwise they see only their own days. <see cref="AnalystId"/> is the one analyst asked for, or null for all the reader sees.
/// </summary>
public sealed record TimelinessReport(PeriodKind By, IReadOnlyList<Period> Periods, DateOnly From, DateOnly To, string? AnalystId, bool Everyone,
    TimeOnly SentBy, TimeOnly PostedBy, IReadOnlyList<TimelinessDay> Days, IReadOnlyList<AnalystTimeliness> People, Tally? Team,
    IReadOnlyList<(string Id, string Name)> Analysts);

/// <summary>
/// The rules of a brief day's deadlines, kept apart from the database so they can be held to by unit tests.
/// </summary>
public static class BriefDeadlines
{
    /// <summary>
    /// When a brief's sending was done, from its real sends that went (<paramref name="went"/>: each edition and when): once
    /// every edition with anything in it has gone (<paramref name="nothingIn"/> are those with nothing), at the latest of
    /// their first sends. A brief marked distributed with email off went when it was marked
    /// (<paramref name="distributedAtUtc"/>). Null while it hasn't gone, or a brief with nothing on it.
    /// </summary>
    public static DateTimeOffset? SentAt(IEnumerable<(Edition Edition, DateTimeOffset AtUtc)> went, IReadOnlyCollection<Edition> nothingIn,
        DateTimeOffset? distributedAtUtc = null)
    {
        var first = went.GroupBy(w => w.Edition).ToDictionary(g => g.Key, g => g.Min(w => w.AtUtc));
        var needed = new[] { Edition.Team, Edition.Public }.Where(e => !nothingIn.Contains(e)).ToList();
        if (needed.Count > 0 && needed.All(first.ContainsKey)) return needed.Max(e => first[e]);
        return first.Count == 0 ? distributedAtUtc : null;
    }

    /// <summary>
    /// How a day went against its deadlines at <paramref name="now"/>: missed when a deadline has passed and what it asks
    /// for hasn't happened; else late when either happened after its deadline (<c>LateBy</c> the later of the two); else
    /// still to come while a deadline hasn't passed; else on time. Without a SharePoint deadline (<paramref name="postBy"/>
    /// null: posting isn't required) only the sending counts.
    /// </summary>
    public static (DayVerdict Verdict, TimeSpan? LateBy) Judge(DayFacts facts, DateTimeOffset sendBy, DateTimeOffset? postBy, DateTimeOffset now)
    {
        var parts = new List<(DateTimeOffset? Done, DateTimeOffset By)> { (facts.SentAtUtc, sendBy) };
        if (postBy is { } p) parts.Add((facts.PostedAtUtc, p));
        if (parts.Any(x => x.Done is null && now > x.By)) return (DayVerdict.Missed, null);
        var late = parts.Where(x => x.Done > x.By).Select(x => x.Done!.Value - x.By).DefaultIfEmpty().Max();
        if (late > TimeSpan.Zero) return (DayVerdict.Late, late);
        return parts.Any(x => x.Done is null) ? (DayVerdict.Pending, null) : (DayVerdict.OnTime, null);
    }
}

/// <summary>
/// The analysts' brief timeliness (Reports › Brief timeliness): each business day's brief owner judged against the day's
/// deadlines (<see cref="OrgClock.SentBy"/>, <see cref="OrgClock.PostedBy"/>), from the send log and the SharePoint
/// records, so every past brief is in it from the first. The owner is the brief's presenter (the rotation's analyst, or
/// whoever it was handed or covered to); with no brief, the rotation's analyst. An analyst sees only their own days;
/// whoever manages people (<see cref="Permission.ManagePeople"/>) sees everyone's and excuses a late day the analyst warned
/// of, which is audited and can be undone.
/// </summary>
public sealed class BriefTimelinessService(IAppDbContextFactory dbs, ICurrentUser user, OrgClock clock, RotationService rotation,
    DistributionSettings settings, IOptionsMonitor<DistributionOptions> distribution)
{
    /// <summary>How many weeks, or months, the report covers, ending with the current one.</summary>
    public const int Weeks = 12, Months = 6;

    /// <summary>Whether the current user sees every analyst's days.</summary>
    public bool SeesEveryone => user.Has(Permission.ManagePeople);

    /// <summary>Whether the current user may open the report at all: their own days, or everyone's.</summary>
    public bool CanRead => SeesEveryone || user.Has(Permission.PresentBriefs);

    /// <summary>
    /// The report by week or by month (<paramref name="by"/>), over the last <see cref="Weeks"/> weeks or
    /// <see cref="Months"/> months to today. <paramref name="analystId"/> narrows it to one analyst; someone who doesn't
    /// see everyone gets their own days whatever they ask for, and asking for anyone else's is refused.
    /// </summary>
    public async Task<TimelinessReport> BuildAsync(PeriodKind by = PeriodKind.Month, string? analystId = null, CancellationToken ct = default)
    {
        var who = Whose(analystId);
        by = by == PeriodKind.Week ? PeriodKind.Week : PeriodKind.Month;
        var today = clock.Today;
        var last = Period.Of(by, today);
        var periods = Enumerable.Range(0, by == PeriodKind.Week ? Weeks : Months).Select(i => last.Shift(-i)).Reverse().ToList();
        var from = periods[0].Start;
        var all = await DaysAsync(from, today, ct);
        var visible = who is null ? all : all.Where(d => d.AnalystId == who).ToList();
        var people = visible.GroupBy(d => d.AnalystId)
            .Select(g =>
            {
                var late = g.Where(d => d.Verdict == DayVerdict.Late && !d.Excused).Select(d => d.LateBy!.Value).ToList();
                return new AnalystTimeliness(g.Key, g.First().AnalystName, Tally.Of(g),
                    periods.Select(p => (p, Tally.Of(g.Where(d => p.Contains(d.Date))))).ToList(),
                    late.Count == 0 ? null : TimeSpan.FromTicks((long)late.Average(t => t.Ticks)));
            })
            .OrderBy(p => p.AnalystName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var analysts = (SeesEveryone ? all : visible).GroupBy(d => d.AnalystId).Select(g => (g.Key, g.First().AnalystName))
            .OrderBy(a => a.AnalystName, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new TimelinessReport(by, periods, from, today, who, SeesEveryone, clock.SentBy, clock.PostedBy,
            visible.OrderByDescending(d => d.Date).ToList(), people, SeesEveryone && who is null ? Tally.Of(visible) : null, analysts);
    }

    /// <summary>
    /// Marks the brief day <paramref name="date"/> as one its analyst warned would be late, with <paramref name="reason"/>:
    /// it no longer counts against them. Today or an earlier business day only, once (undo it to give another reason).
    /// For whoever manages people.
    /// </summary>
    public async Task<BriefExcuse> ExcuseAsync(DateOnly date, string? reason, CancellationToken ct = default)
    {
        RequireManage();
        var why = ArticleService.CleanOrNull(reason, BriefExcuse.MaxReason) ?? throw new ArgumentException("Say what the analyst told you, and when.");
        if (date > clock.Today) throw new ArgumentException("Only today or an earlier day can be excused.");
        if (!clock.Days.IsWorkingDay(date)) throw new ArgumentException("That isn't a business day: no brief was due.");
        var day = (await DaysAsync(date, date, ct)).SingleOrDefault()
                  ?? throw new InvalidOperationException("Nobody had that day's brief, so there's nothing to excuse.");
        if (day.Excuse is { } had)
            throw new InvalidOperationException($"{had.ExcusedByName} has already excused that day. Undo theirs to change it.");
        await using var db = await dbs.CreateAsync(ct);
        var excuse = new BriefExcuse
        {
            BriefDate = date, AnalystId = day.AnalystId, AnalystName = day.AnalystName, Reason = why,
            ExcusedAtUtc = DateTimeOffset.UtcNow, ExcusedById = user.UserId, ExcusedByName = user.DisplayName,
        };
        db.BriefExcuses.Add(excuse);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Another manager's excuse for the day landed first (the date is unique).
            throw new InvalidOperationException("Someone else excused that day just now. Check it again.");
        }
        return excuse;
    }

    /// <summary>
    /// Takes back the excuse for <paramref name="date"/>, as read (<paramref name="editVersion"/>): refused if it has
    /// changed or gone since. The audit trail keeps it. For whoever manages people.
    /// </summary>
    public async Task UndoExcuseAsync(DateOnly date, Guid editVersion, CancellationToken ct = default)
    {
        RequireManage();
        await using var db = await dbs.CreateAsync(ct);
        var excuse = await db.BriefExcuses.FirstOrDefaultAsync(e => e.BriefDate == date, ct)
                     ?? throw new InvalidOperationException("That day isn't excused (someone may have undone it just now).");
        if (excuse.EditVersion != editVersion) throw new InvalidOperationException(ExcuseChanged);
        db.BriefExcuses.Remove(excuse);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new InvalidOperationException(ExcuseChanged); }
    }

    private const string ExcuseChanged = "Someone else changed that excuse just now. Check it again.";

    /// <summary>The report's days as CSV, a row per day, newest first: what the reader sees, nothing more.</summary>
    public static string ToCsv(TimelinessReport report, TimeZoneInfo zone)
    {
        var sb = new StringBuilder("date,analyst,sent_by,sent_at,posted_by,posted_at,verdict,minutes_late,excused,excuse,excused_by\n");
        string Local(DateTimeOffset? t) => t is { } v ? TimeZoneInfo.ConvertTime(v, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
        foreach (var d in report.Days)
            sb.AppendJoin(',', [
                d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Csv.Cell(d.AnalystName), Local(d.SendBy), Local(d.SentAtUtc),
                Local(d.PostBy), Local(d.PostedAtUtc), Verdict(d),
                d.LateBy is { } l ? ((int)Math.Ceiling(l.TotalMinutes)).ToString(CultureInfo.InvariantCulture) : "",
                d.Excused ? "yes" : "no", Csv.Cell(d.Excuse?.Reason ?? ""), Csv.Cell(d.Excuse?.ExcusedByName ?? ""),
            ]).Append('\n');
        return sb.ToString();
    }

    /// <summary>A day's verdict as a word: "on time", "late", "not sent", "not posted", "to come".</summary>
    public static string Verdict(TimelinessDay d) => d.Verdict switch
    {
        DayVerdict.OnTime => "on time",
        DayVerdict.Late => "late",
        DayVerdict.Missed => d.NotPosted ? "not posted" : "not sent",
        _ => "to come",
    };

    // Whose days the reader asked for, as they may see them: anyone's (null: everyone's) for a manager; their own otherwise.
    private string? Whose(string? analystId)
    {
        if (SeesEveryone) return string.IsNullOrWhiteSpace(analystId) ? null : analystId.Trim();
        if (!user.Has(Permission.PresentBriefs)) throw new ForbiddenException("You don't have access to brief timeliness.");
        if (!string.IsNullOrWhiteSpace(analystId) && analystId.Trim() != user.UserId)
            throw new ForbiddenException("You can see only your own brief timeliness.");
        return user.UserId;
    }

    private void RequireManage()
    {
        if (!SeesEveryone) throw new ForbiddenException("Only a manager or an administrator can excuse a late brief.");
    }

    // Every business day from `from` to `to` (today at the latest) with an owner, judged. Days before the first brief TIDE
    // has aren't: there was nothing to send then.
    private async Task<List<TimelinessDay>> DaysAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var today = clock.Today;
        if (to > today) to = today;
        await using var db = await dbs.CreateAsync(ct);
        var firstBrief = await db.Briefs.AsNoTracking().OrderBy(b => b.BriefDate).Select(b => (DateOnly?)b.BriefDate).FirstOrDefaultAsync(ct);
        if (firstBrief is not { } first) return [];
        if (from < first) from = first;
        if (from > to) return [];

        var briefs = await db.Briefs.AsNoTracking().Where(b => b.BriefDate >= from && b.BriefDate <= to).ToDictionaryAsync(b => b.BriefDate, ct);
        var ids = briefs.Values.Select(b => b.Id).ToList();
        var went = (await db.BriefSends.AsNoTracking()
                .Where(s => ids.Contains(s.BriefId) && !s.IsTest && s.Status == SendStatus.Sent)
                .Select(s => new { s.BriefId, s.Edition, s.SentAtUtc }).ToListAsync(ct))
            .ToLookup(s => s.BriefId, s => (s.Edition, s.SentAtUtc));
        // Each brief's lowest article marking, which says which edition has nothing in it (BriefSteps.NothingToSend).
        var lowest = await db.BriefItems.AsNoTracking().Where(i => ids.Contains(i.BriefId))
            .GroupBy(i => i.BriefId).Select(g => new { g.Key, Lowest = g.Min(i => i.Article!.Tlp) })
            .ToDictionaryAsync(g => g.Key, g => (TlpLevel?)g.Lowest, ct);
        var excuses = await db.BriefExcuses.AsNoTracking().Where(e => e.BriefDate >= from && e.BriefDate <= to).ToDictionaryAsync(e => e.BriefDate, ct);
        var rota = (await rotation.CalendarAsync(from, to, ct)).ToDictionary(d => d.Date);
        var (publicMax, teamMax) = await settings.ReadAsync(ct);
        var requireSharePoint = distribution.CurrentValue.RequireSharePoint;
        var now = clock.Now;

        var days = new List<TimelinessDay>();
        foreach (var date in RotationService.BusinessDays(from, to, clock.Days))
        {
            var brief = briefs.GetValueOrDefault(date);
            var day = rota.GetValueOrDefault(date);
            var (ownerId, ownerName) = brief is not null ? (brief.AnalystId, brief.AnalystName) : (day?.AnalystId, day?.AnalystName);
            if (string.IsNullOrEmpty(ownerId)) continue;

            var facts = brief is null ? new DayFacts(null, null, requireSharePoint)
                : Facts(brief, went[brief.Id], BriefSteps.NothingToSend(lowest.GetValueOrDefault(brief.Id), teamMax, publicMax), requireSharePoint);
            var sendBy = OrgClock.At(clock.Zone, date, clock.SentBy);
            DateTimeOffset? postBy = facts.PostRequired ? OrgClock.At(clock.Zone, date, clock.PostedBy) : null;
            var (verdict, lateBy) = BriefDeadlines.Judge(facts, sendBy, postBy, now);
            days.Add(new TimelinessDay(date, ownerId, ownerName ?? ownerId, sendBy, postBy, facts.SentAtUtc, facts.PostedAtUtc, verdict, lateBy,
                excuses.GetValueOrDefault(date)));
        }
        return days;
    }

    // What a day is judged on. The SharePoint copy counts where it's required now and was when the brief went (a brief not
    // gone yet would be held to it; one finished as not sent had nothing to post), and the company-wide edition has
    // anything in it to post.
    private static DayFacts Facts(Brief brief, IEnumerable<(Edition, DateTimeOffset)> went, IReadOnlyCollection<Edition> nothingIn, bool requireSharePoint)
    {
        var sentAt = BriefDeadlines.SentAt(went, nothingIn, brief.DistributedAtUtc);
        var gone = brief.Status == BriefStatus.Sent;
        var postRequired = requireSharePoint && (!gone || brief.SharePointRequired) && !nothingIn.Contains(Edition.Public);
        return new DayFacts(sentAt, brief.SharePointPostedAtUtc, postRequired);
    }
}
