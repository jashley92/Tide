using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Content;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Trends;

/// <summary>
/// How one team did against the due dates of the actions it owns. <see cref="Parts"/> are its actions due in the window;
/// of those, <see cref="OnTime"/> were done by their due date and <see cref="Late"/> were done after it or are still open
/// past it. Cancelled and not applicable ones are neither (as <see cref="ActionSla.StateOf(ActionStatus, DateOnly?, DateOnly?, DateOnly, WorkingDays?)"/>
/// reads them), nor is one due today and still open. Open and overdue are as of today, whatever their due date.
/// <para>
/// The same is measured against the date each action was first given (<see cref="ActionItem.OriginalDueDate"/>), so moving
/// a date doesn't make a commitment kept: <see cref="OriginallyDue"/> are its actions first due in the window, of which
/// <see cref="OnTimeOriginal"/> were done by that first date and <see cref="LateOriginal"/> after it or are still open past
/// it; <see cref="Moved"/> of them had their date moved, <see cref="Moves"/> times in all.
/// </para>
/// </summary>
public sealed record TeamScore(Team Team, int Parts, int OnTime, int Late, double? AverageDaysLate, int OpenNow, int OverdueNow)
{
    /// <summary>The share done on time, 0 to 100, of those that were on time or late; null when none were either.</summary>
    public double? OnTimePercent => Share(OnTime, Late);

    /// <summary>Its actions whose first due date is in the window.</summary>
    public int OriginallyDue { get; init; }

    /// <summary>Of <see cref="OriginallyDue"/>, those done by their first due date.</summary>
    public int OnTimeOriginal { get; init; }

    /// <summary>Of <see cref="OriginallyDue"/>, those done after their first due date or still open past it.</summary>
    public int LateOriginal { get; init; }

    /// <summary>Of <see cref="OriginallyDue"/>, those whose due date has moved at least once.</summary>
    public int Moved { get; init; }

    /// <summary>How many times, in all, the dates of <see cref="OriginallyDue"/>'s actions have moved.</summary>
    public int Moves { get; init; }

    /// <summary>The share done by the date first given, 0 to 100, of those on time or late against it; null when none were.</summary>
    public double? OnTimeOriginalPercent => Share(OnTimeOriginal, LateOriginal);

    // The on-time share both measures give: to one decimal place, or null with nothing on time or late.
    internal static double? Share(int onTime, int late) => onTime + late == 0 ? null : Math.Round(100.0 * onTime / (onTime + late), 1);
}

/// <summary>
/// One person's line of a team's commitments: the team's actions due in the window that they held on their due date
/// (<see cref="Parts"/>), of which <see cref="OnTime"/> were done by it and <see cref="Late"/> after it or are still open
/// past it, by <see cref="TeamScore"/>'s rules; and the team's open and overdue actions they hold now. A null
/// <see cref="PersonId"/> is the team's own: actions nobody held.
/// </summary>
public sealed record PersonScore(string? PersonId, string PersonName, int Parts, int OnTime, int Late, double? AverageDaysLate, int OpenNow, int OverdueNow)
{
    /// <summary>The share done on time, 0 to 100, of those on time or late; null when none were either.</summary>
    public double? OnTimePercent => TeamScore.Share(OnTime, Late);
}

/// <summary>A team's commitments by person (Reports › Team commitments › a team): its line as the scorecard has it, and a
/// line per person the reader may see (<see cref="Everyone"/>: all of them, the team's own unassigned line too; otherwise
/// only the reader's own).</summary>
public sealed record TeamPeople(TeamScorecard Card, TeamScore Team, bool Everyone, IReadOnlyList<PersonScore> People);

/// <summary>The team commitments scorecard: a row per team, for the actions due from <see cref="From"/> to <see cref="To"/>
/// (today), the last <see cref="Days"/> days.</summary>
public sealed record TeamScorecard(int Days, DateOnly From, DateOnly To, bool PartsOnly, bool ExternalOnly, IReadOnlyList<TeamScore> Teams);

/// <summary>
/// The team commitments scorecard (Reports › Team commitments): each team against the due dates on its actions. Counted
/// in the database, grouped by team, with the met, late and overdue rules <see cref="ActionQueries"/> shares with
/// <see cref="ActionSla.StateOf(ActionItem, DateOnly, WorkingDays?)"/>; only the late actions' dates come back, for their days late.
/// </summary>
public sealed class TeamScorecardService(IAppDbContextFactory dbs, ICurrentUser user, OrgClock clock)
{
    /// <summary>The windows offered, in days ending today; <see cref="DefaultDays"/> unless another is asked for.</summary>
    public static readonly IReadOnlyList<int> Windows = [30, 90, 180, 365];

    public const int DefaultDays = 90;

    /// <summary><paramref name="days"/> when it's one of the <see cref="Windows"/>, else the default (as Trends'
    /// <see cref="TrendsService.SpanOrDefault"/> is for its spans).</summary>
    public static int WindowOrDefault(int? days) => days is { } d && Windows.Contains(d) ? d : DefaultDays;

    /// <summary>
    /// The scorecard over the last <paramref name="days"/> days, today included. <paramref name="partsOnly"/> counts only
    /// the parts of split actions; <paramref name="externalOnly"/> lists only external teams. Readable by whoever reads
    /// actions.
    /// </summary>
    public async Task<TeamScorecard> BuildAsync(int? days = null, bool partsOnly = false, bool externalOnly = false, CancellationToken ct = default)
    {
        if (!user.Has(Permission.ReadActions)) throw new ForbiddenException("You don't have access to actions.");
        var window = WindowOrDefault(days);
        var today = clock.Today;
        var from = ActionQueries.LastDaysFrom(today, window);
        await using var db = await dbs.CreateAsync(ct);

        var actions = db.Actions.AsNoTracking().Where(a => (!partsOnly || a.ParentId != null) && (!externalOnly || a.Team!.IsExternal));
        var due = actions.Where(a => a.DueDate >= from && a.DueDate <= today);
        var parts = await CountByTeam(due, ct);
        var met = await CountByTeam(due.WhereMet(), ct);
        var open = await CountByTeam(actions.WhereOpen(), ct);
        var overdue = await CountByTeam(actions.WhereOverdue(today), ct);
        // Each late one's days, from its due date to its close, or to today while open (reopening clears the close).
        var late = (await due.WhereLate(today).Select(a => new { a.TeamId, a.DueDate, a.ClosedDate }).ToListAsync(ct))
            .ToLookup(a => a.TeamId, a => ActionSla.Late(a.DueDate, a.ClosedDate ?? today));
        // The same against the date each was first given, over those first due in the window: a team that moves its dates
        // doesn't move them out of here, or turn a broken commitment into a kept one.
        var first = actions.Where(a => a.OriginalDueDate >= from && a.OriginalDueDate <= today);
        var firstDue = await first.GroupBy(a => a.TeamId)
            .Select(g => new { g.Key, Count = g.Count(), Moved = g.Count(a => a.DueDateMoves > 0), Moves = g.Sum(a => a.DueDateMoves) })
            .ToDictionaryAsync(g => g.Key, ct);
        var metFirst = await CountByTeam(first.WhereMetOriginal(), ct);
        var lateFirst = await CountByTeam(first.WhereLateOriginal(today), ct);
        var teams = (await db.Teams.AsNoTracking().Where(t => !externalOnly || t.IsExternal).ToListAsync(ct)).InOrder(t => t.SortOrder, t => t.Name).ToList();

        var rows = new List<TeamScore>();
        foreach (var t in teams)
        {
            var lateDays = late[t.Id].ToList();
            var firstly = firstDue.GetValueOrDefault(t.Id);
            var row = new TeamScore(t, parts.GetValueOrDefault(t.Id), met.GetValueOrDefault(t.Id), lateDays.Count,
                lateDays.Count == 0 ? null : Math.Round(lateDays.Average(), 1), open.GetValueOrDefault(t.Id), overdue.GetValueOrDefault(t.Id))
            {
                OriginallyDue = firstly?.Count ?? 0, OnTimeOriginal = metFirst.GetValueOrDefault(t.Id), LateOriginal = lateFirst.GetValueOrDefault(t.Id),
                Moved = firstly?.Moved ?? 0, Moves = firstly?.Moves ?? 0,
            };
            // A retired team is listed only while it has something to show.
            if (!t.IsRetired || row.Parts > 0 || row.OriginallyDue > 0 || row.OpenNow > 0) rows.Add(row);
        }
        return new TeamScorecard(window, from, today, partsOnly, externalOnly, rows);
    }

    /// <summary>
    /// <paramref name="teamId"/>'s commitments over the last <paramref name="days"/> days by person: each action due in the
    /// window counts for whoever held it at the end of its due date, read back from its audit trail, so a hand-off or a
    /// reassignment before the due date moves it and one after doesn't. Its line is the scorecard's. Whoever manages
    /// people (<see cref="Permission.ManagePeople"/>) sees every person's line and the actions nobody held; anyone else
    /// who reads actions, only their own.
    /// </summary>
    public async Task<TeamPeople> PeopleAsync(Guid teamId, int? days = null, bool partsOnly = false, CancellationToken ct = default)
    {
        var card = await BuildAsync(days, partsOnly, false, ct);
        var team = card.Teams.FirstOrDefault(t => t.Team.Id == teamId) ?? throw new KeyNotFoundException("There's no such team on the scorecard.");
        var everyone = user.Has(Permission.ManagePeople);
        var today = card.To;
        await using var db = await dbs.CreateAsync(ct);

        var mine = db.Actions.AsNoTracking().Where(a => a.TeamId == teamId && (!partsOnly || a.ParentId != null));
        var due = mine.Where(a => a.DueDate >= card.From && a.DueDate <= today);
        var held = await due.Select(a => new Held(a.Id, a.DueDate!.Value, a.ClosedDate, a.AssigneeId, a.AssigneeName)).ToListAsync(ct);
        var met = (await due.WhereMet().Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        var late = (await due.WhereLate(today).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        var holders = await HoldersOnDueDateAsync(db, held, ct);
        var now = await mine.WhereOpen().Select(a => new { a.AssigneeId, a.AssigneeName, Overdue = a.DueDate < today }).ToListAsync(ct);

        // Each person's name as last recorded with them; whose line is whose is by id ("" for nobody).
        var names = new Dictionary<string, string>();
        foreach (var (id, name) in held.Select(h => holders[h.Id]).Concat(now.Select(n => (n.AssigneeId, n.AssigneeName))))
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) names.TryAdd(id, name);
        var dueBy = held.ToLookup(h => holders[h.Id].Id ?? "");
        var openBy = now.ToLookup(n => n.AssigneeId ?? "");
        var people = dueBy.Select(g => g.Key).Union(openBy.Select(g => g.Key))
            .Where(key => everyone || key == user.UserId)
            .Select(key =>
            {
                var theirs = dueBy[key].ToList();
                var lateDays = theirs.Where(h => late.Contains(h.Id)).Select(h => ActionSla.Late(h.Due, h.Closed ?? today)).ToList();
                var open = openBy[key].ToList();
                return new PersonScore(key.Length == 0 ? null : key, key.Length == 0 ? "Nobody (the team's)" : names.GetValueOrDefault(key, key),
                    theirs.Count, theirs.Count(h => met.Contains(h.Id)), lateDays.Count, lateDays.Count == 0 ? null : Math.Round(lateDays.Average(), 1),
                    open.Count, open.Count(n => n.Overdue));
            })
            .OrderBy(p => p.PersonId is null).ThenBy(p => p.PersonName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new TeamPeople(card, team, everyone, people);
    }

    private sealed record Held(Guid Id, DateOnly Due, DateOnly? Closed, string? AssigneeId, string? AssigneeName);

    /// <summary>
    /// Who held each action at the end of its due date: who holds it now, unless its assignee changed after that day, in
    /// which case whoever its audit trail says held it before the first such change. A change on the day itself counts.
    /// </summary>
    private async Task<Dictionary<Guid, (string? Id, string? Name)>> HoldersOnDueDateAsync(IAppDbContext db, IReadOnlyList<Held> held, CancellationToken ct)
    {
        var result = held.ToDictionary(h => h.Id, h => (h.AssigneeId, h.AssigneeName));
        if (held.Count == 0) return result;
        var keys = held.ToDictionary(h => h.Id.ToString("D", CultureInfo.InvariantCulture));
        var keyList = keys.Keys.ToList();
        const string assignee = "\"" + nameof(ActionItem.AssigneeId) + "\"";
        var since = clock.StartOfDay(held.Min(h => h.Due).AddDays(1));
        var changes = await db.AuditLog.AsNoTracking()
            .Where(e => e.EntityType == nameof(ActionItem) && e.Action == nameof(EntityState.Modified) && keyList.Contains(e.EntityKey)
                        && e.AtUtc >= since && e.ChangesJson!.Contains(assignee))
            .Select(e => new { e.EntityKey, e.AtUtc, e.ChangesJson }).ToListAsync(ct);
        // The earliest change after each one's due date says who held it until then.
        foreach (var g in changes.GroupBy(c => c.EntityKey))
        {
            var h = keys[g.Key];
            var after = clock.StartOfDay(h.Due.AddDays(1));
            var first = g.Where(c => c.AtUtc >= after).MinBy(c => c.AtUtc);
            if (first is null || History.AuditRecords.Values(first.ChangesJson, nameof(ActionItem.AssigneeId)) is not { Length: 2 } ids) continue;
            var name = History.AuditRecords.Values(first.ChangesJson, nameof(ActionItem.AssigneeName)) is { Length: 2 } names ? names[0] : null;
            result[h.Id] = (string.IsNullOrEmpty(ids[0]) ? null : ids[0], name);
        }
        return result;
    }

    private static Task<Dictionary<Guid, int>> CountByTeam(IQueryable<ActionItem> q, CancellationToken ct) =>
        q.GroupBy(a => a.TeamId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);

    /// <summary>
    /// The scorecard as CSV, a row per team. Team names and counts carry no TLP (nothing from an article is in it). The
    /// measures against the first due date come last, after the window, so a sheet reading the earlier columns by
    /// position still reads them.
    /// </summary>
    public static string ToCsv(TeamScorecard card)
    {
        var sb = new StringBuilder("team,external,parts,on_time,late,on_time_percent,average_days_late,open_now,overdue_now,from,to,"
            + "originally_due,on_time_vs_original,late_vs_original,on_time_vs_original_percent,dates_moved,moves\n");
        foreach (var r in card.Teams)
            sb.Append(Csv.Cell(r.Team.Name)).Append(',').Append(r.Team.IsExternal ? "yes" : "no").Append(',')
                .AppendJoin(',', new double?[] { r.Parts, r.OnTime, r.Late, r.OnTimePercent, r.AverageDaysLate, r.OpenNow, r.OverdueNow }.Select(n => Words.Num(n))).Append(',')
                .Append(card.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(card.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .AppendJoin(',', new double?[] { r.OriginallyDue, r.OnTimeOriginal, r.LateOriginal, r.OnTimeOriginalPercent, r.Moved, r.Moves }.Select(n => Words.Num(n)))
                .Append('\n');
        return sb.ToString();
    }
}
