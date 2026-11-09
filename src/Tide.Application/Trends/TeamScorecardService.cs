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
    public double? OnTimePercent => OnTime + Late == 0 ? null : Math.Round(100.0 * OnTime / (OnTime + Late), 1);

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
    public double? OnTimeOriginalPercent => OnTimeOriginal + LateOriginal == 0 ? null
        : Math.Round(100.0 * OnTimeOriginal / (OnTimeOriginal + LateOriginal), 1);
}

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
