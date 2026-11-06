using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Files;
using Tide.Application.Indicators;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>An action as the editor edits it.</summary>
public sealed class ActionForm
{
    public Guid? Id { get; set; }
    public Guid EditVersion { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public ActionKind Kind { get; set; } = ActionKind.IocSweep;
    public Priority Priority { get; set; } = Priority.P3;
    public Guid TeamId { get; set; }
    public string? AssigneeId { get; set; }
    public string? AssigneeName { get; set; }

    /// <summary>Blank on a new action: the SLA target sets it.</summary>
    public DateOnly? DueDate { get; set; }
    public string? TicketRef { get; set; }
    public string? TicketUrl { get; set; }
    public Guid? ArticleId { get; set; }
    public Guid? ParentId { get; set; }

    /// <summary>On a new action raised for a past day's brief: that day (see <see cref="ActionItem.RaisedOn"/>).</summary>
    public DateOnly? RaisedOn { get; set; }

    /// <summary>On a new action: the playbook its checklist starts from (one for its kind, or for any kind).</summary>
    public Guid? PlaybookId { get; set; }

    public static ActionForm From(ActionItem a) => new()
    {
        Id = a.Id, EditVersion = a.EditVersion, Title = a.Title, Description = a.Description, Kind = a.Kind, Priority = a.Priority,
        TeamId = a.TeamId, AssigneeId = a.AssigneeId, AssigneeName = a.AssigneeName, DueDate = a.DueDate, TicketRef = a.TicketRef,
        TicketUrl = a.TicketUrl, ArticleId = a.ArticleId, ParentId = a.ParentId,
    };
}

/// <summary>A row on an action list.</summary>
public sealed record ActionRow(Guid Id, int Number, string Title, ActionKind Kind, Priority Priority, Guid TeamId, string TeamName,
    string? AssigneeId, string? AssigneeName, DateOnly? DueDate, ActionStatus Status, ActionOutcome Outcome, DateOnly? ClosedDate,
    Guid? ArticleId, string? ArticleTitle, Guid? ParentId, int Children, DateTimeOffset CreatedAtUtc,
    int? ParentNumber = null, string? PlaybookName = null, int ChecklistDone = 0, int ChecklistTotal = 0, DateOnly? RaisedOn = null,
    int Files = 0)
{
    public SlaState Sla(DateOnly today, WorkingDays? workingDays = null) => ActionSla.StateOf(Status, DueDate, ClosedDate, today, workingDays);
    public ActionUrgency Urgency(DateOnly today, WorkingDays? workingDays = null) => ActionSla.UrgencyOf(Status, DueDate, today, workingDays);
    public bool IsOpen => !ActionItem.IsClosedStatus(Status);
}

/// <summary>An overdue action the meeting looks at again: how late it is, and whether its team has no members (so
/// nobody sees it as theirs). <see cref="Tlp"/> is the TLP of the article it was raised from, if any: what it's marked at.</summary>
public sealed record OutstandingAction(ActionRow Row, int DaysLate, bool NoOneToOwn, TlpLevel? Tlp = null);

/// <summary>The overdue actions the meeting and the team email show (at most <see cref="ActionService.OutstandingSize"/>),
/// and how many are overdue in all (those above the edition's ceiling counted, not named).</summary>
public sealed record OpenActionsReview(IReadOnlyList<OutstandingAction> Rows, int Overdue)
{
    public static readonly OpenActionsReview None = new([], 0);
    public bool Any => Rows.Count > 0;

    /// <summary>The highest marking of the actions listed (null when none came from an article): counted in the edition's.</summary>
    public TlpLevel? Marking => Rows.Select(o => o.Tlp).Max();
}

/// <summary>Which actions a list shows.</summary>
public enum ActionView
{
    /// <summary>Open, assigned to me.</summary>
    Mine = 0,

    /// <summary>Open, owned by a team I'm on.</summary>
    MyTeams = 1,

    /// <summary>Every open action.</summary>
    Open = 2,

    /// <summary>Open and past due.</summary>
    Overdue = 3,

    /// <summary>Closed in the last 90 days.</summary>
    Closed = 4,
}

/// <summary>Which actions a list shows. <see cref="AssigneeId"/> narrows to one holder (<see cref="Unassigned"/>: nobody's).</summary>
public sealed record ActionFilter(ActionView View = ActionView.Open, Guid? TeamId = null, ActionKind? Kind = null, Guid? ArticleId = null,
    string? AssigneeId = null)
{
    public const string Unassigned = "none";
}

/// <summary>
/// The actions board: open actions and those closed in the last <see cref="ActionService.BoardClosedDays"/> days, a
/// column per status, and which of them the current user may move (the teams they work, or all for an analyst).
/// </summary>
public sealed record ActionBoard(IReadOnlyList<ActionRow> Cards, bool CanWorkAll, IReadOnlySet<Guid> WorkTeams)
{
    public bool CanMove(ActionRow card) => CanWorkAll || WorkTeams.Contains(card.TeamId);

    public IReadOnlyList<ActionRow> In(ActionStatus status) => Cards.Where(c => c.Status == status).ToList();
}

/// <summary>
/// Today's queue for someone who works actions: the open actions of the teams they're on that are theirs or nobody's
/// (<see cref="Rows"/>, at most <see cref="ActionService.TeamQueueSize"/>), how many of each there are in all, and
/// whether they're on a team at all.
/// </summary>
public sealed record TeamQueue(IReadOnlyList<ActionRow> Rows, int Mine, int Unassigned, bool OnATeam)
{
    /// <summary>
    /// The queue's order: the user's own first, then the unassigned ones (each by due date, the undated last, then
    /// priority and number); a colleague's are left out, as they're in hand.
    /// </summary>
    public static TeamQueue From(IEnumerable<ActionRow> teamRows, string userId, bool onATeam, int take)
    {
        var open = teamRows.Where(r => r.IsOpen).ToList();
        var mine = Sorted(open.Where(r => r.AssigneeId == userId)).ToList();
        var unassigned = Sorted(open.Where(r => string.IsNullOrEmpty(r.AssigneeId))).ToList();
        return new(mine.Concat(unassigned).Take(take).ToList(), mine.Count, unassigned.Count, onATeam);
    }

    private static IEnumerable<ActionRow> Sorted(IEnumerable<ActionRow> rows) =>
        rows.OrderBy(r => r.DueDate ?? DateOnly.MaxValue).ThenBy(r => r.Priority).ThenBy(r => r.Number);

    /// <summary>How many there are in all, shown or not.</summary>
    public int Total => Mine + Unassigned;
}

/// <summary>
/// An action with everything shown on its page. <see cref="CanComment"/> is wider than <see cref="CanWork"/>: anyone who
/// reads the team edition may comment on any action, with text; files on a comment stay with whoever works it.
/// </summary>
public sealed record ActionDetail(ActionItem Action, ActionRow? Parent, IReadOnlyList<ActionRow> Children, bool CanWork, bool CanEditAll,
    bool CanComment = false)
{
    /// <summary>The files on the action and its comments, newest first, and (for an administrator) the removed ones.</summary>
    public ActionFileList Files { get; init; } = ActionFileList.None;
}

/// <summary>One team's load: open, overdue, due soon, P1, closed in the last week, and open by assignee.</summary>
public sealed record TeamLoad(Team Team, int Open, int Overdue, int DueSoon, int Blocked, int ClosedLastWeek, int MetLastWeek,
    IReadOnlyList<AssigneeLoad> ByAssignee, int P1 = 0);

/// <summary>One holder's share of a team's open actions; a null id is the team's own (unassigned).</summary>
public sealed record AssigneeLoad(string? AssigneeId, string Assignee, int Open, int Overdue, int P1);

/// <summary>What a hunt can be built from: the action's article's indicators, and its story's (distinct).</summary>
public sealed record HuntSources(int ArticleIndicators, int StoryIndicators, int StoryArticles, int ActionIndicators = 0)
{
    /// <summary>What "Generate" builds from: the article's indicators and the action's own.</summary>
    public int Own => ArticleIndicators + ActionIndicators;
}

/// <summary>
/// Follow-up actions: created by analysts (from an article, or in the meeting), owned by a team, worked by its
/// members, and closed with an outcome. Each change leaves a readable line in the action's history as well as the
/// audit log.
/// <para>
/// Who may do what: anyone with the team edition or WorkActions reads actions. Analysts (WriteArticles) create and
/// edit any action. A person with WorkActions who is on the owning team works it: status, outcome, assignee, due
/// date, ticket, comments and hunts.
/// </para>
/// </summary>
public sealed class ActionService(IAppDbContextFactory dbs, ICurrentUser user, SlaSettings sla, OrgClock clock, HuntQueryBuilder queries,
    ScreenshotService files, Briefs.IMeetingsInProgress? meetings = null)
{
    public DateOnly Today => clock.Today;

    /// <summary>The working days, with the holidays, as the SLA counts them.</summary>
    public WorkingDays Days => clock.Days;

    // --- reading ------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ActionRow>> ListAsync(ActionFilter filter, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var q = Narrow(db, db.Actions.AsNoTracking(), filter);
        if (filter.View == ActionView.Closed)
        {
            var since = DateTimeOffset.UtcNow.AddDays(-90);
            q = q.Where(a => a.ClosedAtUtc != null && a.ClosedAtUtc >= since);
        }
        else q = q.WhereOpen();
        var rows = await Rows(db, q).ToListAsync(ct);
        return filter.View == ActionView.Closed
            ? rows.OrderByDescending(r => r.ClosedDate).ThenByDescending(r => r.Number).ToList()
            : rows.OrderBy(r => r.DueDate ?? DateOnly.MaxValue).ThenBy(r => r.Priority).ThenBy(r => r.Number).ToList();
    }

    /// <summary>How far back the board's closed columns reach.</summary>
    public const int BoardClosedDays = 14;

    /// <summary>
    /// The board for <paramref name="filter"/>'s holder, team, kind and assignee: open actions, and those closed in the
    /// last <see cref="BoardClosedDays"/> days. Its views are whose actions (mine, my teams', all); Overdue and Closed
    /// are list views, and show all.
    /// </summary>
    public async Task<ActionBoard> BoardAsync(ActionFilter filter, CancellationToken ct = default)
    {
        RequireRead(user);
        if (filter.View is ActionView.Overdue or ActionView.Closed) filter = filter with { View = ActionView.Open };
        await using var db = await dbs.CreateAsync(ct);
        var since = DateTimeOffset.UtcNow.AddDays(-BoardClosedDays);
        var q = Narrow(db, db.Actions.AsNoTracking(), filter)
            .Where(a => a.Status == ActionStatus.Open || a.Status == ActionStatus.InProgress || a.Status == ActionStatus.Blocked
                        || (a.ClosedAtUtc != null && a.ClosedAtUtc >= since));
        var rows = await Rows(db, q).ToListAsync(ct);
        // Open columns by due date then priority; closed ones most recently closed first.
        var cards = rows.Where(r => r.IsOpen).OrderBy(r => r.DueDate ?? DateOnly.MaxValue).ThenBy(r => r.Priority).ThenBy(r => r.Number)
            .Concat(rows.Where(r => !r.IsOpen).OrderByDescending(r => r.ClosedDate).ThenByDescending(r => r.Number)).ToList();
        var all = user.Has(Permission.WriteArticles);
        var teams = all || !user.Has(Permission.WorkActions)
            ? []
            : (await db.TeamMembers.AsNoTracking().Where(m => m.UserId == user.UserId).Select(m => m.TeamId).ToListAsync(ct)).ToHashSet();
        return new ActionBoard(cards, all, teams);
    }

    // The holder, team, kind, article and assignee parts of a filter; the caller says which statuses.
    private IQueryable<ActionItem> Narrow(IAppDbContext db, IQueryable<ActionItem> q, ActionFilter filter)
    {
        var today = clock.Today;
        switch (filter.View)
        {
            case ActionView.Mine:
                q = q.Where(a => a.AssigneeId == user.UserId);
                break;
            case ActionView.MyTeams:
                var mine = db.TeamMembers.Where(m => m.UserId == user.UserId).Select(m => m.TeamId);
                q = q.Where(a => mine.Contains(a.TeamId));
                break;
            case ActionView.Overdue:
                q = q.Where(a => a.DueDate != null && a.DueDate < today);
                break;
        }
        if (filter.TeamId is { } team) q = q.Where(a => a.TeamId == team);
        if (filter.Kind is { } kind) q = q.Where(a => a.Kind == kind);
        if (filter.ArticleId is { } article) q = q.Where(a => a.ArticleId == article);
        if (filter.AssigneeId == ActionFilter.Unassigned) q = q.Where(a => a.AssigneeId == null);
        else if (!string.IsNullOrEmpty(filter.AssigneeId)) q = q.Where(a => a.AssigneeId == filter.AssigneeId);
        return q;
    }

    /// <summary>How many open actions assigned to me are past due: one count, for the sidebar (no rows loaded).</summary>
    public async Task<int> MyOverdueCountAsync(CancellationToken ct = default)
    {
        RequireRead(user);
        var today = clock.Today;
        await using var db = await dbs.CreateAsync(ct);
        return await db.Actions.AsNoTracking().WhereOpen().CountAsync(a => a.AssigneeId == user.UserId && a.DueDate != null && a.DueDate < today, ct);
    }

    /// <summary>How many overdue actions the meeting's "Still open" slide and the team email show.</summary>
    public const int OutstandingSize = 8;

    /// <summary>
    /// The overdue actions the meeting looks at again, as of <paramref name="asOf"/> (the brief's day): P1 and P2 first,
    /// then those on a team with no members (nobody sees them as theirs), then the rest, longest overdue first. At most
    /// <see cref="OutstandingSize"/>, with the full overdue count. Actions not yet due aren't listed. An action is held at
    /// its article's TLP: one raised from an article above <paramref name="ceiling"/> (the team edition's) is counted but not
    /// listed, as the brief leaves that article out with its actions.
    /// </summary>
    public async Task<OpenActionsReview> OutstandingAsync(DateOnly asOf, TlpLevel ceiling, CancellationToken ct = default)
    {
        // A Visitor, from a seat in that day's live meeting: its open actions slide (MeetingAccess).
        if (!Briefs.MeetingAccess.SeatedOn(user, meetings, asOf)) RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        return await Outstanding(db, asOf, ceiling, ct);
    }

    /// <summary><see cref="OutstandingAsync"/> on a context the caller holds (the team email reads it when it is cut).</summary>
    internal static async Task<OpenActionsReview> Outstanding(IAppDbContext db, DateOnly asOf, TlpLevel ceiling, CancellationToken ct)
    {
        var q = db.Actions.AsNoTracking().WhereOpen().Where(a => a.DueDate != null && a.DueDate < asOf);
        var rows = await Rows(db, q).ToListAsync(ct);
        if (rows.Count == 0) return OpenActionsReview.None;
        var articleIds = rows.Select(r => r.ArticleId).OfType<Guid>().Distinct().ToList();
        var tlps = await db.Articles.AsNoTracking().Where(a => articleIds.Contains(a.Id)).Select(a => new { a.Id, a.Tlp })
            .ToDictionaryAsync(a => a.Id, a => a.Tlp, ct);
        TlpLevel? TlpOf(ActionRow r) => r.ArticleId is { } id && tlps.TryGetValue(id, out var t) ? t : null;
        var staffed = (await db.TeamMembers.AsNoTracking().Select(m => m.TeamId).Distinct().ToListAsync(ct)).ToHashSet();
        var ranked = rows.Where(r => TlpOf(r) is not { } t || t <= ceiling)
            .Select(r => new OutstandingAction(r, ActionSla.DaysLate(r.DueDate, asOf), !staffed.Contains(r.TeamId), TlpOf(r)))
            .OrderBy(o => o.Row.Priority <= Priority.P2 ? 0 : o.NoOneToOwn ? 1 : 2)
            .ThenByDescending(o => o.DaysLate).ThenBy(o => o.Row.Number)
            .Take(OutstandingSize).ToList();
        return new OpenActionsReview(ranked, rows.Count);
    }

    /// <summary>Whether a team has nobody in TIDE, so an action raised to it is on nobody's list.</summary>
    public async Task<bool> TeamHasNoMembersAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        return !await db.TeamMembers.AsNoTracking().AnyAsync(m => m.TeamId == teamId, ct);
    }

    /// <summary>How many actions Today's team queue shows.</summary>
    public const int TeamQueueSize = 8;

    /// <summary>Today's queue (<see cref="TeamQueue"/>): the current user's teams' open actions that are theirs or nobody's.</summary>
    public async Task<TeamQueue> TeamQueueAsync(CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var onATeam = await db.TeamMembers.AsNoTracking().AnyAsync(m => m.UserId == user.UserId, ct);
        if (!onATeam) return new([], 0, 0, false);
        var q = Narrow(db, db.Actions.AsNoTracking(), new ActionFilter(ActionView.MyTeams)).WhereOpen()
            .Where(a => a.AssigneeId == null || a.AssigneeId == user.UserId);
        return TeamQueue.From(await Rows(db, q).ToListAsync(ct), user.UserId, true, TeamQueueSize);
    }

    /// <summary>One action's list row, or null.</summary>
    public async Task<ActionRow?> RowAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        return await Rows(db, db.Actions.AsNoTracking().Where(a => a.Id == id)).FirstOrDefaultAsync(ct);
    }

    /// <summary>The actions raised from an article, open first.</summary>
    public async Task<IReadOnlyList<ActionRow>> ForArticleAsync(Guid articleId, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var rows = await Rows(db, db.Actions.AsNoTracking().Where(a => a.ArticleId == articleId)).ToListAsync(ct);
        return rows.OrderBy(r => r.IsOpen ? 0 : 1).ThenBy(r => r.Number).ToList();
    }

    /// <summary>
    /// Open actions that a new one would likely repeat: the same kind, for the same team, raised from the article or any
    /// other in its story. The editor warns; it doesn't refuse (a second sweep can be deliberate).
    /// </summary>
    public async Task<IReadOnlyList<ActionRow>> LikelyDuplicatesAsync(Guid articleId, ActionKind kind, Guid teamId, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var storyId = await db.Articles.AsNoTracking().Where(a => a.Id == articleId).Select(a => a.StoryId).FirstOrDefaultAsync(ct);
        var articleIds = storyId is { } sid
            ? await db.Articles.AsNoTracking().Where(a => a.StoryId == sid).Select(a => a.Id).ToListAsync(ct)
            : [articleId];
        return (await Rows(db, db.Actions.AsNoTracking().WhereOpen()
                .Where(a => a.ArticleId != null && articleIds.Contains(a.ArticleId.Value) && a.Kind == kind && a.TeamId == teamId))
            .ToListAsync(ct)).OrderBy(a => a.Number).ToList();
    }

    /// <summary>The actions raised from any of these articles (a brief's), by article.</summary>
    public async Task<ILookup<Guid, ActionRow>> ForArticlesAsync(IReadOnlyCollection<Guid> articleIds, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var rows = await Rows(db, db.Actions.AsNoTracking().Where(a => a.ArticleId != null && articleIds.Contains(a.ArticleId.Value))).ToListAsync(ct);
        return rows.OrderBy(r => r.Number).ToLookup(r => r.ArticleId!.Value);
    }

    /// <summary>These actions (a trend line's), newest first.</summary>
    public async Task<IReadOnlyList<ActionRow>> ListByIdAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        RequireRead(user);
        if (ids.Count == 0) return [];
        await using var db = await dbs.CreateAsync(ct);
        var list = ids.ToList();
        var rows = await Rows(db, db.Actions.AsNoTracking().Where(a => list.Contains(a.Id))).ToListAsync(ct);
        return rows.OrderByDescending(r => r.Number).ToList();
    }

    // The rows, each with its live files counted (on it and its comments), for the paperclip on lists, the board and the brief.
    private static IQueryable<ActionRow> Rows(IAppDbContext db, IQueryable<ActionItem> q) => q.Select(a => new ActionRow(
        a.Id, a.Number, a.Title, a.Kind, a.Priority, a.TeamId, a.Team!.Name, a.AssigneeId, a.AssigneeName, a.DueDate, a.Status, a.Outcome,
        a.ClosedDate, a.ArticleId, a.Article == null ? null : a.Article.Title, a.ParentId, db.Actions.Count(c => c.ParentId == a.Id), a.CreatedAtUtc,
        db.Actions.Where(p => p.Id == a.ParentId).Select(p => (int?)p.Number).FirstOrDefault(), a.PlaybookName,
        a.Checklist.Count(i => i.IsDone), a.Checklist.Count, a.RaisedOn,
        a.Files.Count(f => f.RemovedAtUtc == null) + a.Comments.SelectMany(c => c.Files).Count(f => f.RemovedAtUtc == null)));

    public async Task<ActionDetail?> GetAsync(int number, CancellationToken ct = default)
    {
        // A Visitor sees only the action the presenter has open for the room, while they're in the meeting (MeetingAccess).
        if (!Briefs.MeetingAccess.Seats(user, meetings).Any(s => s.Action == number)) RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Actions.AsNoTracking()
            .Include(x => x.Team).Include(x => x.Article).ThenInclude(x => x!.Indicators)
            .Include(x => x.Comments).ThenInclude(c => c.Files).Include(x => x.Events).Include(x => x.Hunts).Include(x => x.Checklist)
            .Include(x => x.Indicators).AsSplitQuery().FirstOrDefaultAsync(x => x.Number == number, ct);
        if (a is null) return null;
        a.Comments = a.Comments.OrderBy(c => c.AtUtc).ToList();
        a.Events = a.Events.OrderByDescending(e => e.AtUtc).ToList();
        a.Hunts = a.Hunts.OrderByDescending(h => h.CreatedAtUtc).ToList();
        a.Indicators = a.Indicators.OrderBy(i => i.AddedAtUtc).ThenBy(i => i.Value, StringComparer.OrdinalIgnoreCase).ToList();
        var parent = a.ParentId is { } pid ? await Rows(db, db.Actions.AsNoTracking().Where(x => x.Id == pid)).FirstOrDefaultAsync(ct) : null;
        var children = await Rows(db, db.Actions.AsNoTracking().Where(x => x.ParentId == a.Id)).ToListAsync(ct);
        var canWork = await CanWork(db, user, a.TeamId, ct);
        return new ActionDetail(a, parent, children.OrderBy(c => c.Number).ToList(), canWork, user.Has(Permission.WriteArticles),
            canWork || CanComment) { Files = await ActionFileService.ListAsync(db, user, a.Id, ct, a.Comments) };
    }

    /// <summary>The id and article of the action with this number, for whoever may read it as <see cref="GetAsync"/>
    /// allows; null when there's none. For a check that needs no more of the action (the API's file download).</summary>
    internal async Task<(Guid Id, Guid? ArticleId)?> FindReadableAsync(int number, CancellationToken ct = default)
    {
        if (!Briefs.MeetingAccess.Seats(user, meetings).Any(s => s.Action == number)) RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Actions.AsNoTracking().Where(x => x.Number == number).Select(x => new { x.Id, x.ArticleId }).FirstOrDefaultAsync(ct);
        return a is null ? null : (a.Id, a.ArticleId);
    }

    /// <summary>Per team: what's open, overdue and due soon, last week's closures, and who holds what.</summary>
    public async Task<IReadOnlyList<TeamLoad>> WorkloadAsync(CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var today = clock.Today;
        var weekAgo = ActionQueries.LastSevenDaysFrom(today);
        var teams = await db.Teams.AsNoTracking().Where(t => !t.IsRetired).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
        var rows = await db.Actions.AsNoTracking().WhereOpenOrClosedSince(weekAgo)
            .Select(a => new { a.TeamId, a.Status, a.DueDate, a.ClosedDate, a.AssigneeId, a.AssigneeName, a.Priority })
            .ToListAsync(ct);
        return teams.Select(t =>
        {
            var mine = rows.Where(r => r.TeamId == t.Id).ToList();
            var open = mine.Where(r => !ActionItem.IsClosedStatus(r.Status)).ToList();
            var closed = mine.Where(r => ActionItem.IsClosedStatus(r.Status)).ToList();
            return new TeamLoad(t, open.Count,
                open.Count(r => ActionSla.StateOf(r.Status, r.DueDate, null, today, clock.Days) == SlaState.Overdue),
                open.Count(r => ActionSla.StateOf(r.Status, r.DueDate, null, today, clock.Days) == SlaState.DueSoon),
                open.Count(r => r.Status == ActionStatus.Blocked),
                closed.Count(r => r.Status == ActionStatus.Done),
                closed.Count(r => ActionSla.StateOf(r.Status, r.DueDate, r.ClosedDate, today, clock.Days) == SlaState.Met),
                open.GroupBy(r => r.AssigneeId).OrderBy(g => g.Key is null ? 1 : 0).ThenByDescending(g => g.Count())
                    .Select(g => new AssigneeLoad(g.Key, g.Key is null ? "Unassigned" : g.First().AssigneeName ?? g.Key, g.Count(),
                        g.Count(r => ActionSla.StateOf(r.Status, r.DueDate, null, today, clock.Days) == SlaState.Overdue), g.Count(r => r.Priority == Priority.P1)))
                    .ToList(),
                open.Count(r => r.Priority == Priority.P1));
        }).ToList();
    }

    /// <summary>The due date the SLA targets suggest for a new action of this priority and kind, from today.</summary>
    public async Task<DateOnly?> SuggestedDueAsync(Priority priority, ActionKind kind, CancellationToken ct = default) =>
        ActionSla.DueDate(clock.Today, (await sla.GetAsync(ct)).DaysFor(priority, kind), clock.Days);

    // --- creating and editing -----------------------------------------------------------------------------------

    /// <summary>Creates an action. A blank due date takes the SLA target's. Returns its number.</summary>
    public async Task<int> CreateAsync(ActionForm form, CancellationToken ct = default)
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts can create actions.");
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbs.CreateAsync(ct);
            var a = new ActionItem();
            await Apply(db, a, form, ct);
            a.DueDate ??= ActionSla.DueDate(clock.Today, (await sla.GetAsync(ct)).DaysFor(a.Priority, a.Kind), clock.Days);
            if (form.RaisedOn is { } raised && raised != clock.Today)
            {
                if (raised > clock.Today) throw new ArgumentException("An action can't be raised for a day still to come.");
                if (raised < clock.Today.AddYears(-1)) throw new ArgumentException("That day is too far in the past.");
                a.RaisedOn = raised;
            }
            a.Number = (await db.Actions.MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
            var playbook = form.PlaybookId is { } pid ? await PlaybookFor(db, pid, a.Kind, ct) : null;
            db.Actions.Add(a);
            if (playbook is not null) db.ActionChecklistItems.AddRange(AddSteps(a, playbook));
            db.ActionEvents.Add(Event(user, a.Id, $"Created for {await TeamName(db, a.TeamId, ct)}{(a.DueDate is { } d ? $", due {Day(d)}" : "")}"
                + (a.RaisedOn is { } r ? $"; raised for {Day(r)}'s brief" : "")
                + (playbook is not null ? $", with the {playbook.Name} playbook's checklist." : ".")));
            try
            {
                await db.SaveChangesAsync(ct);
                return a.Number;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // Two actions created at once took the same number; take the next one.
            }
        }
    }

    /// <summary>
    /// A quick action from the meeting: a title, kind, team and (optional) due date, on an article, at the article's
    /// priority (see <see cref="QuickPriority"/>). A blank due date takes the SLA target's, a kind's own target first
    /// (ADR 0008). <paramref name="raisedOn"/> is a past day's brief being presented after the fact.
    /// </summary>
    public async Task<int> QuickAddAsync(Guid articleId, string title, ActionKind kind, Guid teamId, DateOnly? due, DateOnly? raisedOn = null,
        CancellationToken ct = default)
    {
        Priority? articlePriority;
        await using (var db = await dbs.CreateAsync(ct))
            articlePriority = await db.Articles.AsNoTracking().Where(a => a.Id == articleId).Select(a => a.Priority).FirstOrDefaultAsync(ct);
        return await CreateAsync(new ActionForm
        {
            Title = title, Kind = kind, TeamId = teamId, DueDate = due, ArticleId = articleId, Priority = QuickPriority(articlePriority, kind),
            RaisedOn = raisedOn,
        }, ct);
    }

    /// <summary>
    /// A meeting action's priority: its article's, except that telling people, asking a vendor and anything else are
    /// never above P2 (a P1 article's "tell the service desk" isn't a P1 action). An article with no priority (an
    /// informational one) leaves it to the kind: a sweep or a block P2, the rest P3.
    /// </summary>
    public static Priority QuickPriority(Priority? article, ActionKind kind) => article switch
    {
        null => kind is ActionKind.IocSweep or ActionKind.Block ? Priority.P2 : Priority.P3,
        Priority.P1 when kind is ActionKind.Awareness or ActionKind.VendorInquiry or ActionKind.Other => Priority.P2,
        { } p => p,
    };

    /// <summary>
    /// For each kind, the team that last had an action of that kind (still a team, not retired): the meeting's guess at
    /// who takes a new one.
    /// </summary>
    public async Task<IReadOnlyDictionary<ActionKind, Guid>> TeamsByKindAsync(CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var live = db.Actions.AsNoTracking().Where(a => !a.Team!.IsRetired);
        var latest = await live.GroupBy(a => a.Kind).Select(g => g.Max(a => a.Number)).ToListAsync(ct);
        return await live.Where(a => latest.Contains(a.Number)).ToDictionaryAsync(a => a.Kind, a => a.TeamId, ct);
    }

    /// <summary>Saves an edit. Refused from a stale copy, or for someone who can't work the action.</summary>
    public async Task SaveAsync(ActionForm form, CancellationToken ct = default)
    {
        if (form.Id is not { } id) throw new ArgumentException("That isn't a saved action.");
        await using var db = await dbs.CreateAsync(ct);
        var a = await WorkableAsync(db, id, ct);
        if (a.EditVersion != form.EditVersion) throw new StaleEditException("Someone else changed this action since you opened it. Reload to see their changes.");
        // A team member who isn't an analyst can hand the action to another team, but the article link and parent are
        // the analysts'.
        if (!user.Has(Permission.WriteArticles))
        {
            form.ArticleId = a.ArticleId;
            form.ParentId = a.ParentId;
        }
        var before = (a.Title, a.Kind, a.Priority, a.TeamId, a.AssigneeName, a.DueDate, a.TicketRef);
        await Apply(db, a, form, ct);
        var changes = new List<string>();
        if (before.Title != a.Title) changes.Add("title");
        if (before.Kind != a.Kind) changes.Add($"kind: {KindName(before.Kind)} → {KindName(a.Kind)}");
        if (before.Priority != a.Priority) changes.Add($"priority: {before.Priority} → {a.Priority}");
        if (before.TeamId != a.TeamId) changes.Add($"team: {await TeamName(db, before.TeamId, ct)} → {await TeamName(db, a.TeamId, ct)}");
        if (before.AssigneeName != a.AssigneeName) changes.Add($"assignee: {before.AssigneeName ?? "nobody"} → {a.AssigneeName ?? "nobody"}");
        if (before.DueDate != a.DueDate) changes.Add($"due: {(before.DueDate is { } b ? Day(b) : "none")} → {(a.DueDate is { } d ? Day(d) : "none")}");
        if (before.TicketRef != a.TicketRef) changes.Add($"ticket: {a.TicketRef ?? "none"}");
        a.EditVersion = Guid.NewGuid();
        if (changes.Count > 0) db.ActionEvents.Add(Event(user, a.Id, "Changed " + string.Join("; ", changes) + "."));
        await SaveEditAsync(db, ct);
    }

    // A save that moves the edit version, so one that lost the race to another change (the version moved under it) is
    // refused as a stale edit, as an article's is, rather than as a database error.
    private static async Task SaveEditAsync(IAppDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new StaleEditException("Someone else changed this action at the same moment. Reload to see their change, then try again.");
        }
    }

    /// <summary>Takes the action on (assigns it to me), starting it if it was open.</summary>
    public async Task TakeAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var a = await WorkableAsync(db, id, ct);
        a.AssigneeId = user.UserId;
        a.AssigneeName = user.DisplayName;
        var started = a.Status == ActionStatus.Open;
        if (started) a.Status = ActionStatus.InProgress;
        a.EditVersion = Guid.NewGuid();
        db.ActionEvents.Add(Event(user, a.Id, started ? "Took it on and started it." : "Took it on."));
        await SaveEditAsync(db, ct);
    }

    /// <summary>
    /// Gives the action to <paramref name="assigneeId"/> (a member of its team, or nobody when null) and sets when it's
    /// due, as the meeting does. A due date left null stays as it was.
    /// </summary>
    public Task AssignAsync(Guid id, string? assigneeId, DateOnly? due, CancellationToken ct = default) =>
        AssignAsync(id, keepAssignee: false, assigneeId, due, ct);

    /// <summary>
    /// Sets when the action is due, as the meeting does, leaving whoever holds it as it is now: a colleague may have given
    /// it to someone since the caller's copy was read, and naming that copy's assignee would take it back from them.
    /// </summary>
    public Task SetDueAsync(Guid id, DateOnly due, CancellationToken ct = default) => AssignAsync(id, keepAssignee: true, null, due, ct);

    private async Task AssignAsync(Guid id, bool keepAssignee, string? assigneeId, DateOnly? due, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        var a = await WorkableAsync(db, id, ct);
        if (keepAssignee) assigneeId = a.AssigneeId;
        // A new assignee must be on the team. The one it has already keeps it, team or not (an analyst who took it on).
        var name = a.AssigneeName;
        if (assigneeId is null) name = null;
        else if (assigneeId != a.AssigneeId)
        {
            name = await db.TeamMembers.Where(m => m.TeamId == a.TeamId && m.UserId == assigneeId).Select(m => m.DisplayName).FirstOrDefaultAsync(ct)
                   ?? throw new ArgumentException("That person isn't on the action's team.");
        }
        if (due != a.DueDate) CheckDue(due);
        var changes = new List<string>();
        if (a.AssigneeId != assigneeId) changes.Add($"assignee: {a.AssigneeName ?? "nobody"} → {name ?? "nobody"}");
        if (due is { } d && a.DueDate != d) changes.Add($"due: {(a.DueDate is { } b ? Day(b) : "none")} → {Day(d)}");
        if (changes.Count == 0) return;
        (a.AssigneeId, a.AssigneeName, a.DueDate) = (assigneeId, name, due ?? a.DueDate);
        a.EditVersion = Guid.NewGuid();
        db.ActionEvents.Add(Event(user, a.Id, "Changed " + string.Join("; ", changes) + " at the meeting."));
        await SaveEditAsync(db, ct);
    }

    /// <summary>
    /// Moves an action to a status. Closing it Done needs an outcome; a hunt or sweep closed Done needs one that says
    /// what was found. Reopening clears the closure.
    /// </summary>
    public async Task SetStatusAsync(Guid id, ActionStatus status, ActionOutcome outcome, string? note, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(status) || !Enum.IsDefined(outcome)) throw new ArgumentException("Choose a status and an outcome.");
        await using var db = await dbs.CreateAsync(ct);
        var a = await WorkableAsync(db, id, ct);
        ApplyStatus(db, a, status, outcome, note);
        await SaveEditAsync(db, ct);
    }

    /// <summary>
    /// Moves an action on the board from the column it was in (<paramref name="from"/>) to another. It's refused when the
    /// action has moved since the board was drawn, and otherwise follows <see cref="SetStatusAsync"/>'s rules: the same
    /// permission, and an outcome to close it Done (see <see cref="OutcomesFor"/>). A move to the column it's in does nothing.
    /// </summary>
    public async Task MoveAsync(Guid id, ActionStatus from, ActionStatus to, ActionOutcome outcome = ActionOutcome.None, string? note = null,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to) || !Enum.IsDefined(outcome)) throw new ArgumentException("Choose a status and an outcome.");
        await using var db = await dbs.CreateAsync(ct);
        var a = await WorkableAsync(db, id, ct);
        if (a.Status != from)
            throw new StaleEditException($"Someone else moved A-{a.Number} to {StatusName(a.Status)} in the meantime.");
        if (from == to) return;
        ApplyStatus(db, a, to, outcome, note);
        await SaveEditAsync(db, ct);
    }

    /// <summary>
    /// Changes a closed action's outcome and note without moving it, from the status it was at when the change began
    /// (<paramref name="at"/>). Refused, as a move is, when it has moved since: someone reopened it meanwhile, and saving
    /// the outcome would close it again over their change.
    /// </summary>
    public async Task ChangeOutcomeAsync(Guid id, ActionStatus at, ActionOutcome outcome, string? note, CancellationToken ct = default)
    {
        if (!ActionItem.IsClosedStatus(at) || !Enum.IsDefined(outcome)) throw new ArgumentException("Choose a status and an outcome.");
        await using var db = await dbs.CreateAsync(ct);
        var a = await WorkableAsync(db, id, ct);
        if (a.Status != at)
            throw new StaleEditException($"Someone else moved A-{a.Number} to {StatusName(a.Status)} in the meantime.");
        ApplyStatus(db, a, at, outcome, note);
        await SaveEditAsync(db, ct);
    }

    /// <summary>
    /// The outcomes an action of <paramref name="kind"/> may close with at <paramref name="status"/>: Done needs one of
    /// its kind's (<see cref="KindOutcomes"/>); Cancelled and Not applicable may have one, or none; an open status has
    /// none. <paramref name="current"/>, the action's outcome now, stays a choice even when its kind's don't include it,
    /// so an action closed before the choices narrowed keeps what it was closed with.
    /// </summary>
    public static IReadOnlyList<ActionOutcome> OutcomesFor(ActionKind kind, ActionStatus status, ActionOutcome current = ActionOutcome.None)
    {
        if (!ActionItem.IsClosedStatus(status)) return [ActionOutcome.None];
        var outcomes = KindOutcomes(kind).ToList();
        if (current != ActionOutcome.None && !outcomes.Contains(current)) outcomes.Insert(0, current);
        if (status != ActionStatus.Done) outcomes.Insert(0, ActionOutcome.None);
        return outcomes;
    }

    /// <summary>
    /// What an action of <paramref name="kind"/> can come to, Other always last: a hunt or sweep says what it found; a
    /// patch, a change or a block, whether it was made or needed; a notice, whether it went out (Remediated) or wasn't
    /// needed; a vendor question, its answer. An action of kind Other may close with any.
    /// </summary>
    public static IReadOnlyList<ActionOutcome> KindOutcomes(ActionKind kind) => kind switch
    {
        ActionKind.IocSweep or ActionKind.ThreatHunt => [ActionOutcome.NoHits, ActionOutcome.HitsEscalated, ActionOutcome.Other],
        ActionKind.PatchRemediate or ActionKind.ConfigChange =>
            [ActionOutcome.Remediated, ActionOutcome.NotAffected, ActionOutcome.RiskAccepted, ActionOutcome.Other],
        ActionKind.Block => [ActionOutcome.Blocked, ActionOutcome.NotAffected, ActionOutcome.RiskAccepted, ActionOutcome.Other],
        ActionKind.Awareness => [ActionOutcome.Remediated, ActionOutcome.NotAffected, ActionOutcome.Other],
        ActionKind.VendorInquiry => [ActionOutcome.Answered, ActionOutcome.NotAffected, ActionOutcome.Other],
        _ => Enum.GetValues<ActionOutcome>().Where(o => o != ActionOutcome.None).ToList(),
    };

    // A note on the history line (a hunt's result, closing it in the same step) goes before the status change.
    private void ApplyStatus(IAppDbContext db, ActionItem a, ActionStatus status, ActionOutcome outcome, string? note, string? lead = null)
    {
        if (status == ActionStatus.Done && outcome == ActionOutcome.None) throw new ArgumentException("Say what came of it: choose an outcome.");
        if (ActionItem.IsClosedStatus(status) && !OutcomesFor(a.Kind, status, a.Outcome).Contains(outcome))
            throw new ArgumentException(a.Kind is ActionKind.IocSweep or ActionKind.ThreatHunt
                ? "A hunt closes with what it found: no hits, or hits escalated."
                : $"A {KindName(a.Kind)} action closes with one of: {string.Join(", ", KindOutcomes(a.Kind).Select(OutcomeName))}.");
        var cleanNote = ArticleService.CleanOrNull(note, 2000, multiline: true);
        var was = a.Status;
        a.Status = status;
        if (ActionItem.IsClosedStatus(status))
        {
            a.Outcome = outcome;
            a.OutcomeNote = cleanNote;
            if (!ActionItem.IsClosedStatus(was))
            {
                a.ClosedAtUtc = DateTimeOffset.UtcNow;
                a.ClosedDate = clock.Today;
            }
        }
        else
        {
            a.Outcome = ActionOutcome.None;
            a.OutcomeNote = null;
            a.ClosedAtUtc = null;
            a.ClosedDate = null;
        }
        a.EditVersion = Guid.NewGuid();
        var text = (lead is null ? "" : lead + " ") + $"Status: {StatusName(was)} → {StatusName(status)}";
        if (a.Outcome != ActionOutcome.None) text += $" ({OutcomeName(a.Outcome)})";
        text += a.OutcomeNote is null ? "." : $": {a.OutcomeNote}";
        db.ActionEvents.Add(Event(user, a.Id, text));
    }

    /// <summary>
    /// Adds a comment, with screenshots and evidence files if any (<see cref="ScreenshotService.WriteAttachmentsAsync"/>
    /// checks them). A comment needs text or at least one file. Anyone who reads the team edition may comment on any
    /// action, so an owner on another team can ask a question or add what they found (ADR 0008, amended); files go only
    /// with a comment from whoever works it.
    /// </summary>
    public async Task AddCommentAsync(Guid id, string text, IReadOnlyList<ScreenshotUpload>? attachments = null, CancellationToken ct = default)
    {
        var clean = ArticleService.Clean(text, 8000, multiline: true);
        attachments ??= [];
        if (clean.Length == 0 && attachments.Count == 0) throw new ArgumentException("Write something or attach a file first.");
        await using var db = await dbs.CreateAsync(ct);
        var a = await FindAsync(db, id, ct);
        if (attachments.Count > 0) await RequireWork(db, user, a, ct);
        else await RequireComment(db, a, ct);
        var written = await files.WriteAttachmentsAsync(attachments, ct);
        var comment = new ActionComment { ActionId = id, Text = clean, AuthorId = user.UserId, AuthorName = user.DisplayName, AtUtc = DateTimeOffset.UtcNow };
        foreach (var ((file, isImage), i) in written.Select((w, i) => (w, i)))
        {
            db.Files.Add(file);
            var u = attachments[i];
            comment.Files.Add(new ActionCommentFile
            {
                FileId = file.Id, FileName = file.FileName, SizeBytes = file.SizeBytes, IsImage = isImage, SortOrder = i,
                RiskReason = FileRisk.Inspect(file.FileName, u.DeclaredType, u.Bytes).RiskReason,
            });
        }
        db.ActionComments.Add(comment);
        if (written.Count > 0)
        {
            var shots = written.Count(w => w.IsImage);
            var evidence = written.Count - shots;
            db.ActionEvents.Add(Event(user, a.Id, "Attached " + string.Join(" and ", new[]
            {
                shots > 0 ? $"{shots} screenshot{(shots == 1 ? "" : "s")}" : null,
                evidence > 0 ? $"{evidence} evidence file{(evidence == 1 ? "" : "s")}" : null,
            }.Where(s => s is not null)) + " to a comment."));
        }
        try { await db.SaveChangesAsync(ct); }
        catch
        {
            files.Discard(written.Select(w => w.File.Id));
            throw;
        }
    }

    /// <summary>Changes a comment: only its author, on whichever team (or an administrator). The action's history says so, and the audit
    /// log keeps the text before and after. <paramref name="editVersion"/> is the comment's
    /// <see cref="ActionComment.EditVersion"/> when its edit opened: if someone changed it since, the save is refused and
    /// says who (null skips the check). Returns the comment's version after the save.</summary>
    public async Task<Guid> EditCommentAsync(Guid commentId, string text, Guid? editVersion = null, CancellationToken ct = default)
    {
        var clean = ArticleService.Clean(text, 8000, multiline: true);
        await using var db = await dbs.CreateAsync(ct);
        var (comment, a) = await OwnCommentAsync(db, commentId, ct);
        if (editVersion is { } v && v != comment.EditVersion)
            throw new StaleEditException($"{comment.EditedByName ?? "Someone else"} changed this comment since you opened it.");
        if (clean.Length == 0 && !await db.ActionCommentFiles.AnyAsync(f => f.CommentId == commentId && f.RemovedAtUtc == null, ct))
            throw new ArgumentException("A comment can't be empty; delete it instead.");
        if (comment.Text == clean) return comment.EditVersion;
        comment.Text = clean;
        comment.EditedAtUtc = DateTimeOffset.UtcNow;
        comment.EditedByName = user.DisplayName;
        comment.EditVersion = Guid.NewGuid();
        db.ActionEvents.Add(Event(user, a.Id, $"{user.DisplayName} edited a comment from {comment.AtUtc.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture)}."));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new StaleEditException("Someone else changed this comment at the same moment.");
        }
        return comment.EditVersion;
    }

    /// <summary>Deletes a comment: only its author, on whichever team (or an administrator). Its files are kept on the
    /// action as removed files ("Its comment was deleted"), with their bytes, hashes and records, for an administrator to
    /// restore. The history says so; the audit log keeps it.</summary>
    public async Task DeleteCommentAsync(Guid commentId, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var (comment, a) = await OwnCommentAsync(db, commentId, ct);
        var attached = await db.ActionCommentFiles.Where(f => f.CommentId == commentId).ToListAsync(ct);
        await ActionFileService.KeepFilesOfDeletedCommentAsync(db, user, comment, attached, ct);
        db.ActionCommentFiles.RemoveRange(attached);
        db.ActionComments.Remove(comment);
        db.ActionEvents.Add(Event(user, a.Id, $"{user.DisplayName} deleted a comment by {comment.AuthorName}"
            + (attached.Count > 0 ? $"; its {attached.Count} file{(attached.Count == 1 ? " is" : "s are")} kept as removed." : ".")));
        await db.SaveChangesAsync(ct);
    }

    // --- checklist and playbooks --------------------------------------------------------------------------------

    /// <summary>An action's checklist, in order (for a board card, opened).</summary>
    public async Task<IReadOnlyList<ActionChecklistItem>> ChecklistAsync(Guid actionId, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        return await db.ActionChecklistItems.AsNoTracking().Where(i => i.ActionId == actionId).OrderBy(i => i.SortOrder).ToListAsync(ct);
    }

    /// <summary>Ticks or unticks a checklist item, for whoever works the action. The history says who did it.</summary>
    public async Task SetChecklistItemAsync(Guid itemId, bool done, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var item = await db.ActionChecklistItems.FirstOrDefaultAsync(i => i.Id == itemId, ct) ?? throw new KeyNotFoundException("That checklist item no longer exists.");
        var a = await db.Actions.FirstAsync(x => x.Id == item.ActionId, ct);
        await RequireWork(db, user, a, ct);
        if (item.IsDone == done) return;
        item.IsDone = done;
        item.DoneByName = done ? user.DisplayName : null;
        item.DoneAtUtc = done ? DateTimeOffset.UtcNow : null;
        db.ActionEvents.Add(Event(user, a.Id, $"{(done ? "Ticked" : "Unticked")} \"{item.Text}\"."));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Adds an item to the end of an action's checklist.</summary>
    public async Task AddChecklistItemAsync(Guid actionId, string text, CancellationToken ct = default)
    {
        var clean = ArticleService.Clean(text, ActionChecklistItem.MaxText);
        if (clean.Length == 0) throw new ArgumentException("Say what needs doing.");
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Actions.Include(x => x.Checklist).FirstOrDefaultAsync(x => x.Id == actionId, ct) ?? throw new KeyNotFoundException("That action no longer exists.");
        await RequireWork(db, user, a, ct);
        if (a.Checklist.Count >= ActionChecklistItem.MaxPerAction) throw new ArgumentException($"A checklist can have at most {ActionChecklistItem.MaxPerAction} items.");
        db.ActionChecklistItems.Add(new ActionChecklistItem { ActionId = a.Id, Text = clean, SortOrder = NextOrder(a) });
        db.ActionEvents.Add(Event(user, a.Id, $"Added \"{clean}\" to the checklist."));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Takes an item off an action's checklist.</summary>
    public async Task RemoveChecklistItemAsync(Guid itemId, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var item = await db.ActionChecklistItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return;
        var a = await db.Actions.FirstAsync(x => x.Id == item.ActionId, ct);
        await RequireWork(db, user, a, ct);
        db.ActionChecklistItems.Remove(item);
        db.ActionEvents.Add(Event(user, a.Id, $"Took \"{item.Text}\" off the checklist."));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adds a playbook's steps to an action's checklist: only the steps it doesn't already have (by text,
    /// ignoring case), at the end. The action takes the playbook's name if it had none. Returns how many were added.
    /// </summary>
    public async Task<int> ApplyPlaybookAsync(Guid actionId, Guid playbookId, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Actions.Include(x => x.Checklist).FirstOrDefaultAsync(x => x.Id == actionId, ct) ?? throw new KeyNotFoundException("That action no longer exists.");
        await RequireWork(db, user, a, ct);
        var playbook = await PlaybookFor(db, playbookId, a.Kind, ct);
        var added = AddSteps(a, playbook);
        db.ActionChecklistItems.AddRange(added);
        db.ActionEvents.Add(Event(user, a.Id, added.Count == 0
            ? $"Applied the {playbook.Name} playbook: its steps were all on the checklist already."
            : $"Applied the {playbook.Name} playbook: {added.Count} step{(added.Count == 1 ? "" : "s")} added to the checklist."));
        await db.SaveChangesAsync(ct);
        return added.Count;
    }

    private static async Task<Playbook> PlaybookFor(IAppDbContext db, Guid playbookId, ActionKind kind, CancellationToken ct)
    {
        var playbook = await db.Playbooks.AsNoTracking().Include(p => p.Steps).FirstOrDefaultAsync(p => p.Id == playbookId, ct)
                       ?? throw new KeyNotFoundException("That playbook no longer exists.");
        if (!playbook.IsActive) throw new ArgumentException($"The {playbook.Name} playbook is retired.");
        if (playbook.Kind is { } k && k != kind) throw new ArgumentException($"The {playbook.Name} playbook is for {KindName(k)} actions.");
        return playbook;
    }

    // Copies the playbook's steps the action doesn't have yet onto its checklist (within the checklist's limit).
    private static List<ActionChecklistItem> AddSteps(ActionItem a, Playbook playbook)
    {
        var have = new HashSet<string>(a.Checklist.Select(i => i.Text.Trim()), StringComparer.OrdinalIgnoreCase);
        var added = new List<ActionChecklistItem>();
        var order = NextOrder(a);
        foreach (var step in playbook.Steps.OrderBy(s => s.Order).Where(s => have.Add(s.Text.Trim())))
        {
            if (a.Checklist.Count + added.Count >= ActionChecklistItem.MaxPerAction) break;
            added.Add(new ActionChecklistItem { ActionId = a.Id, Text = step.Text, SortOrder = order++ });
        }
        a.PlaybookId ??= playbook.Id;
        a.PlaybookName ??= playbook.Name;
        return added;
    }

    private static int NextOrder(ActionItem a) => a.Checklist.Count == 0 ? 0 : a.Checklist.Max(i => i.SortOrder) + 1;

    private async Task<(ActionComment, ActionItem)> OwnCommentAsync(IAppDbContext db, Guid commentId, CancellationToken ct)
    {
        var comment = await db.ActionComments.FirstOrDefaultAsync(c => c.Id == commentId, ct) ?? throw new KeyNotFoundException("That comment no longer exists.");
        var a = await db.Actions.FirstAsync(x => x.Id == comment.ActionId, ct);
        // Whoever may comment may edit or delete their own comment, so an owner on another team isn't left unable to
        // correct what they wrote.
        await RequireComment(db, a, ct);
        if (comment.AuthorId != user.UserId && !user.Has(Permission.Administer))
            throw new ForbiddenException("Only whoever wrote a comment can change it.");
        return (comment, a);
    }

    // --- hunts --------------------------------------------------------------------------------------------------

    /// <summary>
    /// What a hunt can be built from: the indicators on the action's article, and on every article in that article's
    /// story (a later report often carries the indicators the first one lacked), and any added on the action itself.
    /// </summary>
    public async Task<HuntSources> HuntSourcesAsync(Guid actionId, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Actions.AsNoTracking().Include(x => x.Article).ThenInclude(x => x!.Indicators).Include(x => x.Indicators)
                    .FirstOrDefaultAsync(x => x.Id == actionId, ct)
                ?? throw new KeyNotFoundException("That action no longer exists.");
        var story = await StoryArticlesAsync(db, a, ct);
        var own = OwnIndicators(a);
        return new HuntSources(a.Article?.Indicators.Count ?? 0, Distinct(DistinctIndicators(story).Concat(own)).Count, story.Count, own.Count);
    }

    /// <summary>
    /// Generates a hunt query in <paramref name="dialect"/> from the source article's indicators, or (<paramref name="fromStory"/>)
    /// every article's in its story, with any added on the action itself. It looks back from a week before the earliest of
    /// those articles, at least 30 days.
    /// </summary>
    public async Task<HuntQuery> GenerateHuntAsync(Guid actionId, string dialect, bool fromStory = false, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Actions.Include(x => x.Article).ThenInclude(x => x!.Indicators).Include(x => x.Indicators).FirstOrDefaultAsync(x => x.Id == actionId, ct)
                ?? throw new KeyNotFoundException("That action no longer exists.");
        await RequireWork(db, user, a, ct);
        var sources = fromStory ? await StoryArticlesAsync(db, a, ct) : a.Article is { } own ? [own] : [];
        var indicators = Distinct(DistinctIndicators(sources).Concat(OwnIndicators(a)));
        if (indicators.Count == 0)
            throw new InvalidOperationException(fromStory ? "No article in the story has indicators to hunt for."
                : a.Article is null ? "The action has no indicators to hunt for. Add them on this card, or write a query by hand."
                : "The action's article has no indicators to hunt for. Add them on the article first, or build it from the story.");
        var days = HuntQueryBuilder.WindowDays(sources.Min(x => (DateTimeOffset?)(x.PublishedAtUtc ?? x.CreatedAtUtc)), DateTimeOffset.UtcNow);
        var text = await queries.BuildAsync(dialect, indicators, days, ct);
        var hunt = new HuntQuery
        {
            ActionId = a.Id, Dialect = dialect, GeneratedText = text, Text = text, CreatedAtUtc = DateTimeOffset.UtcNow, CreatedByName = user.DisplayName,
        };
        db.HuntQueries.Add(hunt);
        db.ActionEvents.Add(Event(user, a.Id, $"Generated a {HuntQueryBuilder.DialectName(dialect)} hunt query{(fromStory ? $" from the story's {sources.Count} articles" : "")}."));
        await db.SaveChangesAsync(ct);
        return hunt;
    }

    /// <summary>
    /// Adds a query the analyst wrote (a behavioural hunt, say, "php-fpm spawning sh") rather than one built from
    /// indicators. It's recorded like a generated one; with no generated text, it's shown as hand-written.
    /// </summary>
    public async Task<HuntQuery> AddHandWrittenHuntAsync(Guid actionId, string dialect, string text, CancellationToken ct = default)
    {
        var d = QueryDialects.Find(dialect) ?? throw new ArgumentException("Choose a query language.");
        var clean = HuntText((text ?? "").Replace("\r\n", "\n"));
        await using var db = await dbs.CreateAsync(ct);
        var a = await WorkableAsync(db, actionId, ct);
        var hunt = new HuntQuery
        {
            ActionId = a.Id, Dialect = d.Key, GeneratedText = "", Text = clean, CreatedAtUtc = DateTimeOffset.UtcNow, CreatedByName = user.DisplayName,
        };
        db.HuntQueries.Add(hunt);
        db.ActionEvents.Add(Event(user, a.Id, $"Added a hand-written {d.Name} hunt query."));
        await db.SaveChangesAsync(ct);
        return hunt;
    }

    /// <summary>
    /// Adds a query from the team's saved ones (<see cref="SavedQueryService"/>): its <c>{{days}}</c> filled with the window
    /// a query generated from the action's article would look back, and its <c>{{values}}</c> with the indicators on the
    /// article and the action (<see cref="HuntQueryBuilder.FromSaved"/>). It's recorded like a generated one, and names
    /// the saved query it came from. A retired saved query isn't added.
    /// </summary>
    public async Task<HuntQuery> AddSavedHuntAsync(Guid actionId, Guid savedQueryId, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Actions.Include(x => x.Article).ThenInclude(x => x!.Indicators).Include(x => x.Indicators).FirstOrDefaultAsync(x => x.Id == actionId, ct)
                ?? throw new KeyNotFoundException("That action no longer exists.");
        await RequireWork(db, user, a, ct);
        var saved = await db.SavedQueries.AsNoTracking().FirstOrDefaultAsync(q => q.Id == savedQueryId, ct)
                    ?? throw new KeyNotFoundException("That saved query no longer exists.");
        if (saved.IsRetired) throw new InvalidOperationException($"{saved.Name} is retired. Restore it on Saved queries to use it.");
        var d = QueryDialects.Find(saved.Dialect) ?? throw new ArgumentException("That saved query's language isn't one TIDE knows.");
        var days = HuntQueryBuilder.WindowDays(a.Article is { } article ? article.PublishedAtUtc ?? article.CreatedAtUtc : null, DateTimeOffset.UtcNow);
        var indicators = Distinct((a.Article?.Indicators ?? []).Concat(OwnIndicators(a)));
        var text = HuntText(HuntQueryBuilder.FromSaved(d, saved.Name, saved.Text, days, indicators));
        var hunt = new HuntQuery
        {
            ActionId = a.Id, Dialect = d.Key, GeneratedText = text, Text = text, CreatedAtUtc = DateTimeOffset.UtcNow, CreatedByName = user.DisplayName,
            SavedQueryId = saved.Id, SavedQueryName = saved.Name,
        };
        db.HuntQueries.Add(hunt);
        db.ActionEvents.Add(Event(user, a.Id, $"Added the saved {d.Name} hunt query \"{saved.Name}\"."));
        await db.SaveChangesAsync(ct);
        return hunt;
    }

    /// <summary>
    /// Adds indicators for the action's hunts, from its Hunt queries card, and says how many were new. An action raised
    /// from an article adds them to the article (as its editor would, so the brief, exports and other actions have them
    /// too), which takes writing articles as well as working the action; one with no article keeps them itself. Either
    /// way they're normalised and folded as an article's are, at most <see cref="IocExtractor.MaxIndicators"/>, and the
    /// action's history says so.
    /// </summary>
    public async Task<int> AddIndicatorsAsync(Guid actionId, IEnumerable<IndicatorForm> indicators, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var a = await WorkableAsync(db, actionId, ct);
        int added;
        if (a.ArticleId is { } articleId)
        {
            if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException(OnTheArticle);
            var article = await db.Articles.Include(x => x.Indicators).FirstOrDefaultAsync(x => x.Id == articleId, ct)
                          ?? throw new KeyNotFoundException("The action's article no longer exists.");
            added = ArticleService.AddIndicators(db, article, indicators);
            if (added > 0) db.ActionEvents.Add(Event(user, a.Id, $"Added {Indicators(added)} to the article."));
        }
        else
        {
            var wanted = ArticleService.WantedIndicators(indicators);
            var have = await db.ActionIndicators.Where(i => i.ActionId == a.Id).ToListAsync(ct);
            foreach (var existing in have) wanted.Remove($"{(int)existing.Type}|{existing.Value}");
            if (have.Count + wanted.Count > IocExtractor.MaxIndicators)
                throw new ArgumentException($"An action can carry at most {IocExtractor.MaxIndicators} indicators.");
            var now = DateTimeOffset.UtcNow;
            db.ActionIndicators.AddRange(wanted.Values.Select(f => new ActionIndicator
            {
                ActionId = a.Id, Type = f.Type, Value = f.Value, Note = f.Note, AddedAtUtc = now, AddedByName = user.DisplayName,
            }));
            added = wanted.Count;
            if (added > 0) db.ActionEvents.Add(Event(user, a.Id, $"Added {Indicators(added)}."));
        }
        if (added == 0) return 0;
        try { await db.SaveChangesAsync(ct); }
        // The article's edit version moved under this (its editor saved at the same moment).
        catch (DbUpdateConcurrencyException) { throw new StaleEditException("Someone saved the article at the same moment. Try again."); }
        return added;
    }

    /// <summary>What the Hunt queries card says to someone who works the action but can't add to its article.</summary>
    public const string OnTheArticle = "This action's indicators are kept on its article, and only analysts can add to an article. Ask one to add them there.";

    /// <summary>Removes an indicator added on the action itself (an article's are removed in its editor).</summary>
    public async Task RemoveIndicatorAsync(Guid indicatorId, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        if (await db.ActionIndicators.FirstOrDefaultAsync(i => i.Id == indicatorId, ct) is not { } indicator) return;
        var a = await WorkableAsync(db, indicator.ActionId, ct);
        db.ActionIndicators.Remove(indicator);
        db.ActionEvents.Add(Event(user, a.Id, $"Removed the indicator {indicator.Value}."));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { /* someone removed it first */ }
    }

    private static string Indicators(int n) => $"{n} indicator{(n == 1 ? "" : "s")}";

    // The action's own indicators as a hunt reads them (the builder works on an article's).
    private static List<ArticleIndicator> OwnIndicators(ActionItem a) =>
        a.Indicators.Select(i => new ArticleIndicator { Id = i.Id, Type = i.Type, Value = i.Value, Note = i.Note }).ToList();

    private static List<ArticleIndicator> Distinct(IEnumerable<ArticleIndicator> indicators) =>
        indicators.DistinctBy(i => (i.Type, i.Value.Trim().ToLowerInvariant())).ToList();

    // The action's article and the others in its story (just the article when it has no story), with their indicators.
    private static async Task<List<Article>> StoryArticlesAsync(IAppDbContext db, ActionItem a, CancellationToken ct)
    {
        if (a.Article is null) return [];
        if (a.Article.StoryId is not { } storyId) return [a.Article];
        return await db.Articles.AsNoTracking().Include(x => x.Indicators).Where(x => x.StoryId == storyId).ToListAsync(ct);
    }

    private static List<ArticleIndicator> DistinctIndicators(IEnumerable<Article> articles) => articles.SelectMany(x => x.Indicators)
        .DistinctBy(i => (i.Type, i.Value.Trim().ToLowerInvariant())).ToList();

    private static string HuntText(string? text)
    {
        var clean = (text ?? "").Trim();
        if (clean.Length == 0) throw new ArgumentException("The query is empty.");
        if (clean.Length > 20000) throw new ArgumentException("That query is too long.");
        return clean;
    }

    /// <summary>
    /// Saves the query as the analyst edited it. After its result is recorded it can still be edited, but is marked as
    /// edited since (the result was for the earlier text), and the action's history says so.
    /// </summary>
    public async Task SaveHuntTextAsync(Guid huntId, string text, CancellationToken ct = default)
    {
        var clean = HuntText(text);
        await using var db = await dbs.CreateAsync(ct);
        var (hunt, a) = await WorkableHuntAsync(db, huntId, ct);
        if (hunt.Text == clean) return;
        hunt.Text = clean;
        if (hunt.Result != HuntResult.NotRun)
        {
            hunt.EditedAfterResultAtUtc = DateTimeOffset.UtcNow;
            db.ActionEvents.Add(Event(user, a.Id, $"Edited the {HuntQueryBuilder.DialectName(hunt.Dialect)} hunt query after its result was recorded."));
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Records what a hunt found when it was run.</summary>
    public async Task RecordHuntResultAsync(Guid huntId, HuntResult result, int? count, string? notes, Guid? evidenceFileId, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(result) || result == HuntResult.NotRun) throw new ArgumentException("Choose hits or no hits.");
        if (count is < 0) throw new ArgumentException("The count can't be negative.");
        await using var db = await dbs.CreateAsync(ct);
        var (hunt, a) = await WorkableHuntAsync(db, huntId, ct);
        // Once recorded, a result stands (as CloseWithNoHitsAsync holds it): a second recording, from another tab or a
        // colleague at the same moment, would overwrite what the action may already have been closed with.
        if (hunt.Result != HuntResult.NotRun) throw new InvalidOperationException("That query's result is already recorded.");
        hunt.Result = result;
        hunt.ResultCount = result == HuntResult.NoHits ? 0 : count;
        hunt.Notes = ArticleService.CleanOrNull(notes, 2000, multiline: true);
        RanBy(hunt);
        if (evidenceFileId is { } fileId)
        {
            // Only a file stored as evidence and not yet used anywhere: never an article's screenshot, a frozen send's image or
            // a comment's file (deleting that comment would delete the bytes from under this hunt).
            if (await ScreenshotService.InUseAsync(db, fileId, hunt.Id, ct) || !await db.Files.AnyAsync(f => f.Id == fileId, ct)) throw new ArgumentException("That evidence file can't be used.");
            hunt.EvidenceFileId = fileId;
        }
        db.ActionEvents.Add(Event(user, a.Id, $"Ran the {HuntQueryBuilder.DialectName(hunt.Dialect)} hunt: {(result == HuntResult.NoHits ? "no hits" : $"hits{(count is { } c ? $" ({c})" : "")}")}."));
        // The action's version moves with it, so of two recordings of this query at once (or one and a "No hits, close"),
        // the second is refused as stale rather than overwriting the first: the check above alone can't see a save
        // that hasn't landed yet.
        a.EditVersion = Guid.NewGuid();
        await SaveEditAsync(db, ct);
    }

    /// <summary>
    /// Records a sweep's or hunt's query as run with no hits and closes the action Done (No hits), in one step and one
    /// line of history, which says how many days were searched and how many indicators the query left out. Refused when
    /// values were left out and <paramref name="checkedLeftOut"/> doesn't say they were checked another way, when another
    /// query on the action found hits or hasn't been run, and on an action that isn't an open sweep or hunt.
    /// </summary>
    public async Task CloseWithNoHitsAsync(Guid huntId, int windowDays, bool checkedLeftOut, CancellationToken ct = default)
    {
        if (windowDays is < 1 or > 3650) throw new ArgumentException("Say how many days the query searched.");
        await using var db = await dbs.CreateAsync(ct);
        var (hunt, a) = await WorkableHuntAsync(db, huntId, ct);
        if (a.Kind is not (ActionKind.IocSweep or ActionKind.ThreatHunt)) throw new InvalidOperationException("Only a sweep or a hunt closes with no hits.");
        if (!a.IsOpen) throw new InvalidOperationException($"A-{a.Number} is already closed.");
        if (hunt.Result != HuntResult.NotRun) throw new InvalidOperationException("That query's result is already recorded.");
        var others = await db.HuntQueries.Where(h => h.ActionId == a.Id && h.Id != hunt.Id).Select(h => h.Result).ToListAsync(ct);
        if (others.Contains(HuntResult.Hits)) throw new InvalidOperationException("Another query on this action found hits, so it can't close as no hits.");
        if (others.Contains(HuntResult.NotRun)) throw new InvalidOperationException("Another query on this action hasn't been run: record its result, or remove it, first.");
        var leftOut = HuntQueryBuilder.LeftOut(hunt.GeneratedText);
        if (leftOut > 0 && !checkedLeftOut)
            throw new ArgumentException($"The query left out {Values(leftOut)}: check them another way first, and say so.");
        var searched = $"{windowDays} day{(windowDays == 1 ? "" : "s")}";
        var missed = leftOut > 0 ? $"; {Values(leftOut)} not included, checked separately" : "";
        hunt.Result = HuntResult.NoHits;
        hunt.ResultCount = 0;
        hunt.Notes = $"Searched {searched}{missed}.";
        RanBy(hunt);
        ApplyStatus(db, a, ActionStatus.Done, ActionOutcome.NoHits, $"Searched {searched}{missed}.",
            $"Ran the {HuntQueryBuilder.DialectName(hunt.Dialect)} hunt: no hits.");
        await SaveEditAsync(db, ct);
    }

    private void RanBy(HuntQuery hunt)
    {
        hunt.RunById = user.UserId;
        hunt.RunByName = user.DisplayName;
        hunt.RunAtUtc = DateTimeOffset.UtcNow;
    }

    private static string Values(int n) => $"{n} value{(n == 1 ? "" : "s")}";

    public async Task RemoveHuntAsync(Guid huntId, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var (hunt, a) = await WorkableHuntAsync(db, huntId, ct);
        if (hunt.Result != HuntResult.NotRun) throw new InvalidOperationException("A hunt with a recorded result is kept.");
        db.HuntQueries.Remove(hunt);
        // As a recording does, the removal moves the action's version: a result recorded (or a "No hits, close") at the same
        // moment then refuses this as stale, rather than the recorded query, and what the action was closed on, going.
        a.EditVersion = Guid.NewGuid();
        await SaveEditAsync(db, ct);
    }

    // A hunt query and its action, for someone who may work the action.
    private async Task<(HuntQuery, ActionItem)> WorkableHuntAsync(IAppDbContext db, Guid huntId, CancellationToken ct)
    {
        var hunt = await db.HuntQueries.FirstOrDefaultAsync(h => h.Id == huntId, ct) ?? throw new KeyNotFoundException("That hunt query no longer exists.");
        var a = await db.Actions.FirstAsync(x => x.Id == hunt.ActionId, ct);
        await RequireWork(db, user, a, ct);
        return (hunt, a);
    }

    /// <summary>Whether the current user may work actions of <paramref name="teamId"/>.</summary>
    public async Task<bool> CanWorkAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        return await CanWork(db, user, teamId, ct);
    }

    // --- helpers ------------------------------------------------------------------------------------------------

    private async Task Apply(IAppDbContext db, ActionItem a, ActionForm form, CancellationToken ct)
    {
        var title = ArticleService.Clean(form.Title, ActionItem.MaxTitle);
        if (title.Length == 0) throw new ArgumentException("An action needs a title.");
        if (!Enum.IsDefined(form.Kind) || !Enum.IsDefined(form.Priority)) throw new ArgumentException("Choose a kind and a priority.");
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == form.TeamId, ct) ?? throw new ArgumentException("Choose a team.");
        if (team.IsRetired && team.Id != a.TeamId) throw new ArgumentException($"{team.Name} is retired; choose another team.");
        // Only a date being set is checked: one the action already has (overdue for over a year, say) doesn't stop
        // every other edit to it.
        if (form.DueDate != a.DueDate) CheckDue(form.DueDate);
        if (form.ArticleId is { } articleId && articleId != a.ArticleId && !await db.Articles.AnyAsync(x => x.Id == articleId, ct))
            throw new KeyNotFoundException("That article no longer exists.");
        if (form.ParentId is { } parentId && parentId != a.ParentId)
        {
            if (parentId == a.Id) throw new ArgumentException("An action can't be part of itself.");
            var parent = await db.Actions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == parentId, ct) ?? throw new KeyNotFoundException("The parent action no longer exists.");
            if (parent.ParentId is not null) throw new ArgumentException("Actions split one level deep: choose the top action as the parent.");
            if (await db.Actions.AnyAsync(x => x.ParentId == a.Id, ct))
                throw new ArgumentException("Actions split one level deep: this one already has parts, so it can't become a part.");
        }
        a.Title = title;
        a.Description = ArticleService.CleanOrNull(form.Description, 8000, multiline: true);
        a.Kind = form.Kind;
        a.Priority = form.Priority;
        a.TeamId = team.Id;
        a.AssigneeId = string.IsNullOrWhiteSpace(form.AssigneeId) ? null : form.AssigneeId.Trim();
        a.AssigneeName = a.AssigneeId is null ? null : ArticleService.Clean(form.AssigneeName ?? form.AssigneeId, 256);
        a.DueDate = form.DueDate;
        a.TicketRef = ArticleService.CleanOrNull(form.TicketRef, 100);
        a.TicketUrl = ArticleService.CheckUrl(form.TicketUrl, "The ticket link");
        a.ArticleId = form.ArticleId;
        a.ParentId = form.ParentId;
    }

    /// <summary>Whether <paramref name="who"/> may work actions of <paramref name="teamId"/>: an analyst or administrator, or a
    /// member of the team with WorkActions.</summary>
    internal static async Task<bool> CanWork(IAppDbContext db, ICurrentUser who, Guid teamId, CancellationToken ct) =>
        who.Has(Permission.WriteArticles)
        || (who.Has(Permission.WorkActions) && await db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.UserId == who.UserId, ct));

    internal static async Task RequireWork(IAppDbContext db, ICurrentUser who, ActionItem a, CancellationToken ct,
        string refusal = "Only analysts and members of the owning team can work this action.")
    {
        if (!await CanWork(db, who, a.TeamId, ct)) throw new ForbiddenException(refusal);
    }

    internal static async Task<ActionItem> FindAsync(IAppDbContext db, Guid id, CancellationToken ct) =>
        await db.Actions.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("That action no longer exists.");

    // The action (tracked), for someone who may work it.
    private async Task<ActionItem> WorkableAsync(IAppDbContext db, Guid id, CancellationToken ct)
    {
        var a = await FindAsync(db, id, ct);
        await RequireWork(db, user, a, ct);
        return a;
    }

    // Whoever reads the team edition and takes part in the team's work may comment on any action, and whoever works it
    // may too. A Viewer reads the team edition and does neither, so can't.
    private async Task RequireComment(IAppDbContext db, ActionItem a, CancellationToken ct)
    {
        if (!CanComment && !await CanWork(db, user, a.TeamId, ct)) throw new ForbiddenException("You don't have access to comment on actions.");
    }

    /// <summary>Whether the current user may comment, with text, on any action they can see (ADR 0008, amended): on the
    /// team edition, and working actions or writing articles (not a Viewer, who only reads).</summary>
    private bool CanComment => user.Has(Permission.ReadTeamEdition) && (user.Has(Permission.WorkActions) || user.Has(Permission.WriteArticles));

    /// <summary>Whether <paramref name="who"/> reads actions: with the team edition, or working them.</summary>
    internal static bool MayRead(ICurrentUser who) => who.Has(Permission.ReadTeamEdition) || who.Has(Permission.WorkActions);

    internal static void RequireRead(ICurrentUser who)
    {
        if (!MayRead(who)) throw new ForbiddenException("You don't have access to actions.");
    }

    /// <summary>The longest a history line is (its column). A longer one, such as a status change with a pasted outcome
    /// note of up to 2,000 characters, is clipped with an ellipsis; the full note stays on the action.</summary>
    public const int EventTextMax = 1000;

    internal static ActionEvent Event(ICurrentUser who, Guid actionId, string text) =>
        new() { ActionId = actionId, AtUtc = DateTimeOffset.UtcNow, ByName = who.DisplayName, Text = ClipEvent(text) };

    // Not cutting a surrogate pair in two.
    internal static string ClipEvent(string text)
    {
        if (text.Length <= EventTextMax) return text;
        var cut = EventTextMax - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }

    private static async Task<string> TeamName(IAppDbContext db, Guid teamId, CancellationToken ct) =>
        await db.Teams.Where(t => t.Id == teamId).Select(t => t.Name).FirstOrDefaultAsync(ct) ?? "a team";

    // A due date a year or more gone is a typo, from the form or the meeting.
    private void CheckDue(DateOnly? due)
    {
        if (due is { } d && d < clock.Today.AddYears(-1)) throw new ArgumentException("That due date is too far in the past.");
    }

    private static string Day(DateOnly d) => d.ToString("ddd MMM d", System.Globalization.CultureInfo.InvariantCulture);

    public static string KindName(ActionKind k) => k switch
    {
        ActionKind.IocSweep => "IOC sweep",
        ActionKind.ThreatHunt => "Threat hunt",
        ActionKind.PatchRemediate => "Patch / remediate",
        ActionKind.ConfigChange => "Config change",
        ActionKind.VendorInquiry => "Vendor inquiry",
        _ => k.ToString(),
    };

    public static string StatusName(ActionStatus s) => s switch
    {
        ActionStatus.InProgress => "In progress",
        ActionStatus.NotApplicable => "Not applicable",
        _ => s.ToString(),
    };

    public static string OutcomeName(ActionOutcome o) => o switch
    {
        ActionOutcome.NoHits => "No hits",
        ActionOutcome.HitsEscalated => "Hits found, escalated",
        ActionOutcome.RiskAccepted => "Risk accepted",
        ActionOutcome.NotAffected => "Not affected",
        ActionOutcome.None => "None",
        _ => o.ToString(),
    };
}
