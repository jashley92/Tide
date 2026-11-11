using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Content;
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

    /// <summary>Why it goes ahead though it takes its team over its active limit (a team that warns asks for one).</summary>
    public string? OverLimitReason { get; set; }

    /// <summary>On an edit: the team it was on when the edit opened, and why it goes to another (a team change is a hand-off).</summary>
    public Guid FromTeamId { get; set; }
    public string? HandoffReason { get; set; }

    /// <summary>On an edit that changes the team, of an action whose reminders are off ("Don't remind"): turn them back on
    /// for the new team. By default they stay as they are.</summary>
    public bool TurnRemindersOn { get; set; }

    public static ActionForm From(ActionItem a) => new()
    {
        Id = a.Id, EditVersion = a.EditVersion, Title = a.Title, Description = a.Description, Kind = a.Kind, Priority = a.Priority,
        TeamId = a.TeamId, FromTeamId = a.TeamId, AssigneeId = a.AssigneeId, AssigneeName = a.AssigneeName, DueDate = a.DueDate, TicketRef = a.TicketRef,
        TicketUrl = a.TicketUrl, ArticleId = a.ArticleId, ParentId = a.ParentId,
    };
}

/// <summary>A row on an action list.</summary>
public sealed record ActionRow(Guid Id, int Number, string Title, ActionKind Kind, Priority Priority, Guid TeamId, string TeamName,
    string? AssigneeId, string? AssigneeName, DateOnly? DueDate, ActionStatus Status, ActionOutcome Outcome, DateOnly? ClosedDate,
    Guid? ArticleId, string? ArticleTitle, Guid? ParentId, int Children, DateTimeOffset CreatedAtUtc,
    int? ParentNumber = null, string? PlaybookName = null, int ChecklistDone = 0, int ChecklistTotal = 0, DateOnly? RaisedOn = null,
    int Files = 0, bool TeamExternal = false, Guid? HandedFromTeamId = null, int ChildrenClosed = 0, int ChildrenOverdue = 0,
    DateOnly? ChildrenNextDue = null, DateOnly? EscalatedForDue = null, DateTimeOffset? EscalatedAtUtc = null,
    bool ArticleKev = false, DateTimeOffset? InStatusSinceUtc = null)
{
    // The board fills ArticleKev (its article names a CVE on CISA's KEV list). InStatusSinceUtc is when it moved to the
    // status it's in (ActionItem.StatusChangedAtUtc).
    public SlaState Sla(DateOnly today, WorkingDays? workingDays = null) => ActionSla.StateOf(Status, DueDate, ClosedDate, today, workingDays);
    public ActionUrgency Urgency(DateOnly today, WorkingDays? workingDays = null) => ActionSla.UrgencyOf(Status, DueDate, today, workingDays);
    public bool IsOpen => !ActionItem.IsClosedStatus(Status);

    /// <summary>Whether it's escalated now (<see cref="ActionItem.EscalatedFor"/>).</summary>
    public bool Escalated => ActionItem.EscalatedFor(Status, DueDate, EscalatedForDue);

    /// <summary>When it was escalated, while it still is; null otherwise (the badge's).</summary>
    public DateTimeOffset? EscalatedSince => Escalated ? EscalatedAtUtc : null;

    /// <summary>A parent's parts in a line: "3 of 5 done · 1 overdue · next due Nov 20" (empty with none).</summary>
    public string Rollup => ActionRollup.Text(Children, ChildrenClosed, ChildrenOverdue, ChildrenNextDue);

    /// <summary>How many of its parts are still open.</summary>
    public int ChildrenOpen => Children - ChildrenClosed;

    /// <summary>Whether someone who <paramref name="canWork"/> it may take it on from a list: nobody has it, and taking
    /// is allowed (<see cref="ActionGuide.CanTake"/>, the action page's rule).</summary>
    public bool TakeableBy(bool canWork) => AssigneeId is null && ActionGuide.CanTake(new ActionFacts(Status, Kind, canWork, Assigned: false, TeamExternal));
}

/// <summary>A deleted action on the administrators' list: who deleted it, when and why, and whether its parent is deleted
/// too (it's restored after its parent).</summary>
public sealed record DeletedAction(Guid Id, int Number, string Title, string TeamName, ActionStatus Status, DateTimeOffset DeletedAtUtc,
    string? DeletedByName, string? Reason, bool ParentDeleted);

/// <summary>An overdue action the meeting looks at again: how late it is, and whether its team has no members (so
/// nobody sees it as theirs). <see cref="Tlp"/> is the TLP of the article it was raised from, if any: what it's marked at.</summary>
public sealed record OutstandingAction(ActionRow Row, int DaysLate, bool NoOneToOwn, TlpLevel? Tlp = null)
{
    /// <summary>"3 days late", or "not yet due" for an escalated one that isn't (the meeting's and the email's words).</summary>
    public string Late => ActionSla.LateText(DaysLate) ?? "not yet due";
}

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

    /// <summary>Words in the title or the article's title, or an action's number ("A-12", "12").</summary>
    public string? Search { get; init; }

    /// <summary>Any of these priorities ("P1 or P2", <c>priority=p1,p2</c> in the address); none for any priority.</summary>
    public IReadOnlyList<Priority>? Priorities { get; init; }

    public ActionDue? Due { get; init; }

    /// <summary>One status (the Actions page offers the open ones: Open, In progress, Blocked).</summary>
    public ActionStatus? Status { get; init; }

    /// <summary>An administrator's card rule shown as a filter: the actions it matches (the list's; the board matches
    /// its cards itself, to count each rule's).</summary>
    public Guid? RuleId { get; init; }

    /// <summary>The parts of one action, by its number ("Part of A-12", <c>parent=12</c> in the address).</summary>
    public int? ParentNumber { get; init; }

    /// <summary>Only internal teams' actions, or only external teams' (<c>teams=internal|external</c>); null for both.</summary>
    public ActionTeams? Teams { get; init; }

    /// <summary>Only the actions the person reading raised (created), wherever they are now (<c>raised=me</c>).</summary>
    public bool RaisedByMe { get; init; }
}

/// <summary>Which teams' actions, as a filter.</summary>
public enum ActionTeams
{
    /// <summary>Teams in TIDE, with members.</summary>
    Internal = 0,

    /// <summary>Teams that work outside TIDE (<see cref="Team.IsExternal"/>).</summary>
    External = 1,
}

/// <summary>Someone who holds open actions, for the Held by filter: their user id, name, and how many.</summary>
public sealed record ActionHolder(string Id, string Name, int Open);

/// <summary>When an action is due, as a filter.</summary>
public enum ActionDue
{
    /// <summary>Open and past due.</summary>
    Overdue = 0,

    /// <summary>Due from today to the end of the business week (the organization's working days, with its holidays).</summary>
    ThisWeek = 1,

    /// <summary>No due date.</summary>
    None = 2,
}

/// <summary>What the actions list is sorted by. <see cref="Closed"/> is most recently closed first, the closed view's own.</summary>
public enum ActionSort
{
    Due = 0,
    Number = 1,
    Title = 2,
    Kind = 3,
    Team = 4,
    Status = 5,
    Closed = 6,
}

/// <summary>What the actions list is grouped under, so each group's rows come together across its pages.</summary>
public enum ActionGrouping
{
    None = 0,
    Kind = 1,

    /// <summary>When it's due (<see cref="ActionUrgency"/>).</summary>
    Urgency = 2,
}

/// <summary>A page of the actions list: its rows, how many match in all, which page it is (the last, when the one asked
/// for is past the end), and how many matching actions each group holds (named as its heading is) when grouped.
/// <see cref="Workers"/> says which rows the reader may work, so the list offers Take it where taking is allowed.</summary>
public sealed record ActionListPage(IReadOnlyList<ActionRow> Rows, int Total, int Page, IReadOnlyDictionary<string, int> Groups)
{
    public ActionWorkers Workers { get; init; } = ActionWorkers.None;
}

/// <summary>Which teams' actions the reader works: all of them (an analyst), or the teams they're on with WorkActions
/// (ActionService.CanWork's rule, read once for a whole list or board).</summary>
public sealed record ActionWorkers(bool All, IReadOnlySet<Guid> Teams)
{
    public static readonly ActionWorkers None = new(false, new HashSet<Guid>());

    public bool CanWork(Guid teamId) => All || Teams.Contains(teamId);

    /// <summary>Whether the reader may take this row on (<see cref="ActionRow.TakeableBy"/>).</summary>
    public bool CanTake(ActionRow row) => row.TakeableBy(CanWork(row.TeamId));
}

/// <summary>
/// How many actions each view tab shows under the same filters (Mine lets go of Held by, as choosing it does): the list's
/// open actions, or on the board its cards, Closed lane included. And for an empty Mine to point at, how many open actions
/// are the reader's (<see cref="MineOpen"/>) and how many of the reader's teams' nobody has (<see cref="MyTeamsUnowned"/>).
/// </summary>
public sealed record ActionViewCounts(int Mine, int MyTeams, int Open, int Overdue, int Closed, int MyTeamsUnowned, int MineOpen)
{
    public int For(ActionView view) => view switch
    {
        ActionView.Mine => Mine,
        ActionView.MyTeams => MyTeams,
        ActionView.Overdue => Overdue,
        ActionView.Closed => Closed,
        _ => Open,
    };
}

/// <summary>
/// The actions board: open actions and those closed in the last <see cref="ClosedDays"/> days, a column per status, and
/// which of them the current user may move (the teams they work, or all for an analyst).
/// </summary>
public sealed record ActionBoard(IReadOnlyList<ActionRow> Cards, bool CanWorkAll, IReadOnlySet<Guid> WorkTeams)
{
    /// <summary>How far back the closed columns reach.</summary>
    public int ClosedDays { get; init; } = ActionService.BoardClosedDays;

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

    /// <summary>The escalated actions that are theirs to chase (<see cref="ActionService.EscalatedAsync"/>), when asked for.</summary>
    public IReadOnlyList<ActionRow> Escalated { get; init; } = [];
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

    /// <summary>The team it was last handed from (by <see cref="ActionItem.HandedByName"/>, why: <see cref="ActionItem.HandoffReason"/>), or null.</summary>
    public string? HandedFrom { get; init; }

    /// <summary>Its parts' progress ("3 of 5 done · 1 overdue · next due Nov 20"), empty with none.</summary>
    public string Rollup { get; init; } = "";

    /// <summary>Where it came from and went, for an action that has parts or is one; null otherwise.</summary>
    public ActionLineage? Lineage { get; init; }

    /// <summary>How many emails it has (an Outreach action's, read with it into <see cref="ActionItem.Outreach"/>, newest first).</summary>
    public int Emails => Action.Outreach.Count;

    /// <summary>The email waiting longest for a reply, or null (<see cref="OutreachState.Oldest"/>).</summary>
    public OutreachMessage? Waiting => new OutreachState(Action.Outreach, null).Oldest;
}

/// <summary>One team's load: open, overdue, due soon, P1, closed in the last week, and open by assignee (none for an external
/// team, which has no members); and, for a team with a limit, how many are active against it.</summary>
public sealed record TeamLoad(Team Team, int Open, int Overdue, int DueSoon, int Blocked, int ClosedLastWeek, int MetLastWeek,
    IReadOnlyList<AssigneeLoad> ByAssignee, int P1 = 0, TeamActive? Active = null);

/// <summary>One holder's share of a team's open actions; a null id is the team's own (unassigned).</summary>
public sealed record AssigneeLoad(string? AssigneeId, string Assignee, int Open, int Overdue, int P1);

/// <summary>
/// What a hunt can be built from, each indicator counted once: <paramref name="Indicators"/>, what Generate hunts for (the
/// action's article's and any added on the action); <paramref name="StoryIndicators"/>, what building from the story hunts
/// for (every one of its <paramref name="StoryArticles"/> articles', with the action's own).
/// </summary>
public sealed record HuntSources(int Indicators, int StoryIndicators, int StoryArticles);

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
    ScreenshotService files, ArticleService articles, Briefs.IMeetingsInProgress? meetings = null, HandoffNotices? notices = null)
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
            var since = DateTimeOffset.UtcNow.AddDays(-ClosedListDays);
            q = q.Where(a => a.ClosedAtUtc != null && a.ClosedAtUtc >= since);
        }
        else q = q.WhereOpen();
        var rows = await RowsAsync(db, q, ct);
        return filter.View == ActionView.Closed
            ? rows.OrderByDescending(r => r.ClosedDate).ThenByDescending(r => r.Number).ToList()
            : rows.OrderBy(r => r.DueDate ?? DateOnly.MaxValue).ThenBy(r => r.Priority).ThenBy(r => r.Number).ToList();
    }

    /// <summary>How far back the Recently closed view reaches.</summary>
    public const int ClosedListDays = 90;

    /// <summary>Rows on a page of the actions list.</summary>
    public const int ListPageSize = 50;

    /// <summary>
    /// A page of the list for <paramref name="filter"/> (open actions, or those closed in the last 90 days), sorted and
    /// grouped in the database, with how many match in all and in each group. A page past the end is the last page.
    /// </summary>
    public async Task<ActionListPage> PageAsync(ActionFilter filter, ActionSort sort = ActionSort.Due, bool descending = false,
        ActionGrouping group = ActionGrouping.None, int page = 1, int size = ListPageSize, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var q = await ListQuery(db, filter, ct);
        var total = await q.CountAsync(ct);
        size = Math.Clamp(size, 1, 500);
        page = Math.Clamp(page, 1, Math.Max(1, (total + size - 1) / size));
        var today = clock.Today;
        var urgency = UrgencyOf(today, ActionSla.EndOfBusinessWeek(today, clock.Days));
        IReadOnlyDictionary<string, int> groups = group switch
        {
            ActionGrouping.Kind => (await q.GroupBy(a => a.Kind).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
                .ToDictionary(g => KindName(g.Key), g => g.N),
            ActionGrouping.Urgency => (await q.Select(urgency).GroupBy(u => u).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
                .ToDictionary(g => ActionSla.UrgencyName((ActionUrgency)g.Key), g => g.N),
            _ => new Dictionary<string, int>(),
        };
        var rows = await RowsAsync(db, Ordered(q, sort, descending, group, urgency).Skip((page - 1) * size).Take(size), ct);
        return new ActionListPage(Readable(rows), total, page, groups) { Workers = await WorkersAsync(db, ct) };
    }

    // Which teams' actions the reader works (CanWork's rule, for every row at once): all for an analyst, none without
    // WorkActions, otherwise the teams they're on.
    private async Task<ActionWorkers> WorkersAsync(IAppDbContext db, CancellationToken ct)
    {
        if (user.Has(Permission.WriteArticles)) return new ActionWorkers(true, new HashSet<Guid>());
        if (!user.Has(Permission.WorkActions)) return ActionWorkers.None;
        return new ActionWorkers(false, (await db.TeamMembers.AsNoTracking().Where(m => m.UserId == user.UserId).Select(m => m.TeamId).ToListAsync(ct)).ToHashSet());
    }

    // An article's title (and what's read from it) is shown only to someone who may open the article: whoever reads the
    // team edition, as the article pages require. Someone who only works actions sees the action without it.
    private List<ActionRow> Readable(List<ActionRow> rows) =>
        user.Has(Permission.ReadTeamEdition) ? rows : [.. rows.Select(r => r with { ArticleTitle = null })];

    /// <summary>
    /// How many actions each view tab shows under <paramref name="filter"/>'s other filters (and the card
    /// <paramref name="rule"/> chosen as one, already read), in one grouped query: each count is the total that tab's
    /// <see cref="PageAsync"/> would give, or with <paramref name="closedDays"/>, the cards its <see cref="BoardAsync"/>
    /// would draw, those closed in that many days included. Mine lets go of Held by, as choosing the tab does.
    /// </summary>
    public async Task<ActionViewCounts> ViewCountsAsync(ActionFilter filter, ActionCardRule? rule = null, int? closedDays = null, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var (me, held, today) = (user.UserId, filter.AssigneeId, clock.Today);
        var closedSince = DateTimeOffset.UtcNow.AddDays(-ClosedListDays);
        // The board's Closed lane (BoardAsync's window); the list has none. Within the closed list's 90 days, as the board's
        // longest reach (BoardOlderDays) is.
        var boardSince = closedDays is { } days ? BoardClosedSince(days) : DateTimeOffset.MaxValue;
        var q = Narrow(db, db.Actions.AsNoTracking(), filter with { View = ActionView.Open, AssigneeId = null });
        if (rule is not null) q = WhereRule(q, rule);
        var open = ActionItem.OpenStatuses;
        // Each action once, with whether it's on one of the reader's teams (a membership is one row per team and person).
        var rows = from a in q.WhereOpenOrClosedSinceUtc(closedSince)
                   join m in db.TeamMembers.Where(m => m.UserId == me) on a.TeamId equals m.TeamId into ms
                   from m in ms.DefaultIfEmpty()
                   select new
                   {
                       Open = open.Contains(a.Status),
                       // On the board: a card, open or in its Closed lane (BoardAsync's WhereOpenOrClosedSinceUtc).
                       Shown = open.Contains(a.Status) || a.ClosedAtUtc >= boardSince,
                       Closed = a.ClosedAtUtc != null && a.ClosedAtUtc >= closedSince,
                       Mine = a.AssigneeId == me,
                       Nobodys = a.AssigneeId == null,
                       Held = held == null || (held == ActionFilter.Unassigned ? a.AssigneeId == null : a.AssigneeId == held),
                       Team = m != null,
                       Late = a.DueDate != null && a.DueDate < today,
                   };
        var c = await rows.GroupBy(_ => 1).Select(g => new
        {
            Mine = g.Count(x => x.Shown && x.Mine),
            MyTeams = g.Count(x => x.Shown && x.Team && x.Held),
            Open = g.Count(x => x.Shown && x.Held),
            Overdue = g.Count(x => x.Open && x.Late && x.Held),
            Closed = g.Count(x => x.Closed && x.Held),
            Unowned = g.Count(x => x.Open && x.Team && x.Nobodys),
            MineOpen = g.Count(x => x.Open && x.Mine),
        }).FirstOrDefaultAsync(ct);
        return c is null ? new(0, 0, 0, 0, 0, 0, 0) : new(c.Mine, c.MyTeams, c.Open, c.Overdue, c.Closed, c.Unowned, c.MineOpen);
    }

    /// <summary>How many of the list's actions for <paramref name="filter"/> each rule matches (its own rule left out): the
    /// counts beside the rules offered as filters.</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> RuleCountsAsync(ActionFilter filter, IEnumerable<ActionCardRule> rules, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var q = await ListQuery(db, filter with { RuleId = null }, ct);
        var counts = new Dictionary<Guid, int>();
        foreach (var rule in rules) counts[rule.Id] = await WhereRule(q, rule).CountAsync(ct);
        return counts;
    }

    // The list's actions: the filter's, open (or for Closed, closed in the last 90 days), and its rule's.
    private async Task<IQueryable<ActionItem>> ListQuery(IAppDbContext db, ActionFilter filter, CancellationToken ct)
    {
        var q = Narrow(db, db.Actions.AsNoTracking(), filter);
        if (filter.View == ActionView.Closed)
        {
            var since = DateTimeOffset.UtcNow.AddDays(-ClosedListDays);
            q = q.Where(a => a.ClosedAtUtc != null && a.ClosedAtUtc >= since);
        }
        else q = q.WhereOpen();
        if (filter.RuleId is { } ruleId)
        {
            // A rule taken away meanwhile filters nothing out, as it no longer shows as a filter.
            var rule = await db.ActionCardRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ruleId, ct);
            if (rule is not null) q = WhereRule(q, rule);
        }
        return q;
    }

    // Which urgency group an action is under (ActionSla.UrgencyOf), as the database works it out to group and sort by.
    private static Expression<Func<ActionItem, int>> UrgencyOf(DateOnly today, DateOnly weekEnd) => a =>
        a.Status == ActionStatus.Blocked ? (int)ActionUrgency.Blocked
        : a.DueDate == null || a.DueDate > weekEnd ? (int)ActionUrgency.Later
        : a.DueDate < today ? (int)ActionUrgency.Overdue
        : a.DueDate == today ? (int)ActionUrgency.DueToday
        : (int)ActionUrgency.ThisWeek;

    // The list's order: its group first, so a group's rows come together across pages, then the sort, then the number, so
    // the order is stable and an action is on one page only. Blanks (no due date) go last either way, as the table's own
    // sort puts them.
    private static IQueryable<ActionItem> Ordered(IQueryable<ActionItem> q, ActionSort sort, bool descending, ActionGrouping group,
        Expression<Func<ActionItem, int>> urgency)
    {
        IOrderedQueryable<ActionItem>? o = null;
        void By<TKey>(Expression<Func<ActionItem, TKey>> key, bool desc = false) =>
            o = o is null ? (desc ? q.OrderByDescending(key) : q.OrderBy(key)) : (desc ? o.ThenByDescending(key) : o.ThenBy(key));
        if (group == ActionGrouping.Kind) By(a => a.Kind);
        else if (group == ActionGrouping.Urgency) By(urgency);
        switch (sort)
        {
            case ActionSort.Closed:
                By(a => a.ClosedDate, desc: true);
                By(a => a.Number, desc: true);
                return o!;
            case ActionSort.Number: By(a => a.Number, descending); break;
            // Letters in any case together, as the table sorts them: ToLower runs in the database (lower(), LOWER()), never
            // in .NET, so no culture applies.
#pragma warning disable CA1304, CA1311
            case ActionSort.Title: By(a => a.Title.ToLower(), descending); break;
            case ActionSort.Kind: By(a => a.Kind, descending); break;
            case ActionSort.Team: By(a => a.Team!.Name.ToLower(), descending); break;
#pragma warning restore CA1304, CA1311
            case ActionSort.Status: By(a => a.Status, descending); break;
            default:
                By(a => a.DueDate == null);
                By(a => a.DueDate, descending);
                By(a => a.Priority);
                break;
        }
        By(a => a.Number);
        return o!;
    }

    // The actions an administrator's card rule matches (ActionRules.Matches), in the database.
    private IQueryable<ActionItem> WhereRule(IQueryable<ActionItem> q, ActionCardRule rule)
    {
        var today = clock.Today;
        var soon = clock.Days.Next(today);   // due by the next working day is due soon (ActionSla.StateOf)
        if (rule.Kind is { } kind) q = q.Where(a => a.Kind == kind);
        if (rule.PriorityAtLeast is { } p) q = q.Where(a => a.Priority <= p);
        if (rule.TeamId is { } team) q = q.Where(a => a.TeamId == team);
        if (rule.Status is { } status) q = q.Where(a => a.Status == status);
        if (rule.Unassigned) q = q.Where(a => a.AssigneeId == null);
        if (rule.HasChecklist) q = q.Where(a => a.Checklist.Any());
        if (rule.IsPart) q = q.Where(a => a.ParentId != null);
        return rule.Sla switch
        {
            CardRuleSla.Overdue => q.WhereOpen().Where(a => a.DueDate != null && a.DueDate < today),
            CardRuleSla.DueSoon => q.WhereOpen().Where(a => a.DueDate != null && a.DueDate >= today && a.DueDate <= soon),
            CardRuleSla.OverdueOrDueSoon => q.WhereOpen().Where(a => a.DueDate != null && a.DueDate <= soon),
            _ => q,
        };
    }

    /// <summary>How far back the board's closed columns reach at first.</summary>
    public const int BoardClosedDays = 7;

    /// <summary>How far back they reach once "Show older" is chosen.</summary>
    public const int BoardOlderDays = 30;

    /// <summary>
    /// The board for <paramref name="filter"/>'s holder, team, kind, assignee and the rest: open actions, and those closed
    /// in the last <paramref name="closedDays"/> days (<see cref="BoardClosedDays"/>, or up to <see cref="BoardOlderDays"/>).
    /// Its views are whose actions (mine, my teams', all); Overdue and Closed are list views, and show all.
    /// </summary>
    public async Task<ActionBoard> BoardAsync(ActionFilter filter, int closedDays = BoardClosedDays, CancellationToken ct = default)
    {
        RequireRead(user);
        if (filter.View is ActionView.Overdue or ActionView.Closed) filter = filter with { View = ActionView.Open };
        closedDays = Math.Clamp(closedDays, 1, BoardOlderDays);
        await using var db = await dbs.CreateAsync(ct);
        var since = BoardClosedSince(closedDays);
        var q = Narrow(db, db.Actions.AsNoTracking(), filter).WhereOpenOrClosedSinceUtc(since);
        var rows = await CardsAsync(db, q, ct);
        // Open columns by due date then priority; closed ones most recently closed first.
        var cards = rows.Where(r => r.IsOpen).OrderBy(r => r.DueDate ?? DateOnly.MaxValue).ThenBy(r => r.Priority).ThenBy(r => r.Number)
            .Concat(rows.Where(r => !r.IsOpen).OrderByDescending(r => r.ClosedDate).ThenByDescending(r => r.Number)).ToList();
        var workers = await WorkersAsync(db, ct);
        return new ActionBoard(cards, workers.All, workers.Teams) { ClosedDays = closedDays };
    }

    // How far back the board's Closed lane reaches: the last closedDays days, a day to BoardOlderDays.
    private static DateTimeOffset BoardClosedSince(int closedDays) => DateTimeOffset.UtcNow.AddDays(-Math.Clamp(closedDays, 1, BoardOlderDays));

    // The board's cards: the rows, with whether each one's article names a CVE on CISA's KEV list, in one read for all the
    // cards, on the cards' own query (so no list of ids is sent).
    private async Task<List<ActionRow>> CardsAsync(IAppDbContext db, IQueryable<ActionItem> q, CancellationToken ct)
    {
        var rows = Readable(await RowsAsync(db, q, ct));
        if (rows.Count == 0) return rows;
        var kev = new HashSet<Guid>();
        if (user.Has(Permission.ReadTeamEdition) && rows.Any(r => r.ArticleId is not null))
        {
            var articles = q.Where(a => a.ArticleId != null).Select(a => a.ArticleId!.Value);
            kev = (await db.ArticleCves.AsNoTracking().Where(c => articles.Contains(c.ArticleId))
                .Where(c => db.CveRecords.Any(r => r.CveId == c.CveId && r.IsKev)).Select(c => c.ArticleId).Distinct().ToListAsync(ct)).ToHashSet();
        }
        return kev.Count == 0 ? rows : [.. rows.Select(r => r.ArticleId is { } article && kev.Contains(article) ? r with { ArticleKev = true } : r)];
    }

    // The holder, team, kind, article, assignee, words, priority, due date and status parts of a filter; the caller says
    // which statuses (open, or recently closed) and applies the rule.
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
        if (filter.Teams is { } side)
        {
            var external = side == ActionTeams.External;
            q = q.Where(a => a.Team!.IsExternal == external);
        }
        if (filter.RaisedByMe) q = q.Where(a => a.CreatedBy == user.UserId);
        if (filter.Kind is { } kind) q = q.Where(a => a.Kind == kind);
        if (filter.ArticleId is { } article) q = q.Where(a => a.ArticleId == article);
        if (filter.ParentNumber is { } parent)
        {
            var parentId = db.Actions.Where(p => p.Number == parent).Select(p => (Guid?)p.Id);
            q = q.Where(a => a.ParentId != null && parentId.Contains(a.ParentId));
        }
        if (filter.AssigneeId == ActionFilter.Unassigned) q = q.Where(a => a.AssigneeId == null);
        else if (!string.IsNullOrEmpty(filter.AssigneeId)) q = q.Where(a => a.AssigneeId == filter.AssigneeId);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // The words anywhere in the title or the article's (LIKE: the same match on SQLite and SQL Server, wildcards
            // literal), or a whole number as the action's.
            var words = filter.Search.Trim();
            var like = Search.SearchText.Contains(words);
            var number = Search.SearchText.ActionNumber(words) ?? -1;
            q = q.Where(a => a.Number == number || EF.Functions.Like(a.Title, like, Search.SearchText.Escape)
                             || (a.Article != null && EF.Functions.Like(a.Article.Title, like, Search.SearchText.Escape)));
        }
        if (filter.Priorities is { Count: > 0 } priorities)
        {
            var any = priorities.ToArray();
            q = q.Where(a => any.Contains(a.Priority));
        }
        if (filter.Status is { } status) q = q.Where(a => a.Status == status);
        switch (filter.Due)
        {
            case ActionDue.Overdue:
                q = q.WhereOpen().Where(a => a.DueDate != null && a.DueDate < today);
                break;
            case ActionDue.ThisWeek:
                var weekEnd = ActionSla.EndOfBusinessWeek(today, clock.Days);
                q = q.Where(a => a.DueDate != null && a.DueDate >= today && a.DueDate <= weekEnd);
                break;
            case ActionDue.None:
                q = q.Where(a => a.DueDate == null);
                break;
        }
        return q;
    }

    /// <summary>Everyone who holds an open action, by name, with how many: the Held by filter's people.</summary>
    public async Task<IReadOnlyList<ActionHolder>> HoldersAsync(CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var held = await db.Actions.AsNoTracking().WhereOpen().Where(a => a.AssigneeId != null)
            .GroupBy(a => a.AssigneeId!).Select(g => new { Id = g.Key, Name = g.Max(a => a.AssigneeName), Open = g.Count() }).ToListAsync(ct);
        return [.. held.Select(h => new ActionHolder(h.Id, h.Name ?? h.Id, h.Open)).OrderByName(h => h.Name)];
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
    /// The overdue actions the meeting looks at again, as of <paramref name="asOf"/> (the brief's day), with any the
    /// escalation ladder flagged, overdue or not: the escalated first, then P1 and P2, then those on a team with no members
    /// (nobody sees them as theirs), then the rest, longest overdue first. At most <see cref="OutstandingSize"/>, with the
    /// full count. Other actions not yet due aren't listed. An action is held at
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
        // Two reads, each on its own index ((Status, DueDate), and the escalated one), rather than one OR that uses neither.
        var overdue = await Rows(db, db.Actions.AsNoTracking().WhereOpen().Where(a => a.DueDate != null && a.DueDate < asOf)).ToListAsync(ct);
        var escalated = await Rows(db, db.Actions.AsNoTracking().WhereEscalated()).ToListAsync(ct);
        var rows = overdue.Concat(escalated).DistinctBy(r => r.Id).ToList();
        if (rows.Count == 0) return OpenActionsReview.None;
        var articleIds = rows.Select(r => r.ArticleId).OfType<Guid>().Distinct().ToList();
        var tlps = await db.Articles.AsNoTracking().Where(a => articleIds.Contains(a.Id)).Select(a => new { a.Id, a.Tlp })
            .ToDictionaryAsync(a => a.Id, a => a.Tlp, ct);
        TlpLevel? TlpOf(ActionRow r) => r.ArticleId is { } id && tlps.TryGetValue(id, out var t) ? t : null;
        var nobodys = (await db.Teams.AsNoTracking().Where(NoOneToOwn).Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        var ranked = rows.Where(r => TlpOf(r) is not { } t || t <= ceiling)
            .Select(r => new OutstandingAction(r, ActionSla.DaysLate(r.DueDate, asOf), nobodys.Contains(r.TeamId), TlpOf(r)))
            .OrderBy(o => o.Row.Escalated ? 0 : o.Row.Priority <= Priority.P2 ? 1 : o.NoOneToOwn ? 2 : 3)
            .ThenByDescending(o => o.DaysLate).ThenBy(o => o.Row.Number)
            .Take(OutstandingSize).ToList();
        return new OpenActionsReview(ranked, rows.Count);
    }

    /// <summary>A team nobody in TIDE owns work on: no members, and not external (an external team's reminders follow
    /// its work). The meeting ranks its overdue actions up; an action raised to it on the action page says so.</summary>
    private static readonly Expression<Func<Team, bool>> NoOneToOwn = t => !t.IsExternal && !t.Members.Any();

    /// <summary>Whether a team has nobody in TIDE, so an action raised to it is on nobody's list (<see cref="NoOneToOwn"/>).</summary>
    public async Task<bool> TeamHasNoMembersAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        return await db.Teams.AsNoTracking().Where(t => t.Id == teamId).AnyAsync(NoOneToOwn, ct);
    }

    /// <summary>How many actions Today's team queue shows.</summary>
    public const int TeamQueueSize = 8;

    /// <summary>Today's queue (<see cref="TeamQueue"/>): the current user's teams' open actions that are theirs or nobody's.
    /// Its <see cref="TeamQueue.Escalated"/> too, read on the same context, so Today's Escalated card opens none of its own.
    /// With the ladder off (<paramref name="ladderOn"/> false) it's read only if anything is still flagged, a look at the
    /// escalated index alone.</summary>
    public async Task<TeamQueue> TeamQueueAsync(bool ladderOn = false, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        IReadOnlyList<ActionRow> mineEscalated = ladderOn || await db.Actions.WhereEscalated().AnyAsync(ct) ? await EscalatedRowsAsync(db, ct) : [];
        var onATeam = await db.TeamMembers.AsNoTracking().AnyAsync(m => m.UserId == user.UserId, ct);
        if (!onATeam) return new([], 0, 0, false) { Escalated = mineEscalated };
        var q = Narrow(db, db.Actions.AsNoTracking(), new ActionFilter(ActionView.MyTeams)).WhereOpen()
            .Where(a => a.AssigneeId == null || a.AssigneeId == user.UserId);
        return TeamQueue.From(await RowsAsync(db, q, ct), user.UserId, true, TeamQueueSize) with { Escalated = mineEscalated };
    }

    /// <summary>How many escalated actions Today lists.</summary>
    public const int EscalatedSize = 8;

    /// <summary>
    /// Today's Escalated list: the open actions the escalation ladder flagged that are the current user's to chase: assigned
    /// to them, on a team of theirs, or a part of an action assigned to them. Soonest due first, at most
    /// <see cref="EscalatedSize"/>.
    /// </summary>
    public async Task<IReadOnlyList<ActionRow>> EscalatedAsync(CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        return await EscalatedRowsAsync(db, ct);
    }

    private async Task<IReadOnlyList<ActionRow>> EscalatedRowsAsync(IAppDbContext db, CancellationToken ct)
    {
        var me = user.UserId;
        var q = db.Actions.AsNoTracking().WhereEscalated().Where(a => a.AssigneeId == me
            || db.TeamMembers.Any(m => m.TeamId == a.TeamId && m.UserId == me)
            || db.Actions.Any(p => p.Id == a.ParentId && p.AssigneeId == me));
        return (await RowsAsync(db, q, ct)).OrderBy(r => r.DueDate ?? DateOnly.MaxValue).ThenBy(r => r.Priority).ThenBy(r => r.Number)
            .Take(EscalatedSize).ToList();
    }

    /// <summary>One action's list row, or null.</summary>
    public async Task<ActionRow?> RowAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        return (await RowsAsync(db, db.Actions.AsNoTracking().Where(a => a.Id == id), ct)).FirstOrDefault();
    }

    /// <summary>The actions raised from an article, open first.</summary>
    public async Task<IReadOnlyList<ActionRow>> ForArticleAsync(Guid articleId, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var rows = await RowsAsync(db, db.Actions.AsNoTracking().Where(a => a.ArticleId == articleId), ct);
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
        var rows = await RowsAsync(db, db.Actions.AsNoTracking().Where(a => a.ArticleId != null && articleIds.Contains(a.ArticleId.Value)), ct);
        return rows.OrderBy(r => r.Number).ToLookup(r => r.ArticleId!.Value);
    }

    /// <summary>These actions (a trend line's), newest first.</summary>
    public async Task<IReadOnlyList<ActionRow>> ListByIdAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        RequireRead(user);
        if (ids.Count == 0) return [];
        await using var db = await dbs.CreateAsync(ct);
        var list = ids.ToList();
        var rows = await RowsAsync(db, db.Actions.AsNoTracking().Where(a => list.Contains(a.Id)), ct);
        return rows.OrderByDescending(r => r.Number).ToList();
    }

    // The rows, each with its live files counted (on it and its comments), for the paperclip on lists, the board and the brief.
    private static IQueryable<ActionRow> Rows(IAppDbContext db, IQueryable<ActionItem> q) => q.Select(a => new ActionRow(
        a.Id, a.Number, a.Title, a.Kind, a.Priority, a.TeamId, a.Team!.Name, a.AssigneeId, a.AssigneeName, a.DueDate, a.Status, a.Outcome,
        a.ClosedDate, a.ArticleId, a.Article == null ? null : a.Article.Title, a.ParentId, db.Actions.Count(c => c.ParentId == a.Id), a.CreatedAtUtc,
        db.Actions.Where(p => p.Id == a.ParentId).Select(p => (int?)p.Number).FirstOrDefault(), a.PlaybookName,
        a.Checklist.Count(i => i.IsDone), a.Checklist.Count, a.RaisedOn,
        a.Files.Count(f => f.RemovedAtUtc == null) + a.Comments.SelectMany(c => c.Files).Count(f => f.RemovedAtUtc == null),
        a.Team!.IsExternal, a.HandedFromTeamId, 0, 0, null, a.EscalatedForDue, a.EscalatedAtUtc, false, a.StatusChangedAtUtc));

    // The rows, and each parent's rollup ("3 of 5 done · 1 overdue · next due Nov 20") from one grouped read of their parts.
    private async Task<List<ActionRow>> RowsAsync(IAppDbContext db, IQueryable<ActionItem> q, CancellationToken ct)
    {
        var rows = await Rows(db, q).ToListAsync(ct);
        var parents = rows.Where(r => r.Children > 0).Select(r => r.Id).ToList();
        if (parents.Count == 0) return rows;
        var rollups = await RollupsAsync(db, parents, clock.Today, ct);
        return [.. rows.Select(r => rollups.TryGetValue(r.Id, out var x) ? r with { ChildrenClosed = x.Closed, ChildrenOverdue = x.Overdue, ChildrenNextDue = x.NextDue } : r)];
    }

    private sealed record PartsRollup(int Total, int Closed, int Overdue, DateOnly? NextDue)
    {
        public string Text => ActionRollup.Text(Total, Closed, Overdue, NextDue);
    }

    // How the parts of each of these parents stand today: one query, grouped by parent.
    private static async Task<Dictionary<Guid, PartsRollup>> RollupsAsync(IAppDbContext db, List<Guid> parents, DateOnly today, CancellationToken ct) =>
        (await db.Actions.AsNoTracking().Where(c => c.ParentId != null && parents.Contains(c.ParentId.Value)).GroupBy(c => c.ParentId!.Value)
            .Select(g => new
            {
                g.Key, Total = g.Count(), Closed = g.Count(c => ActionItem.ClosedStatuses.Contains(c.Status)),
                Overdue = g.Count(c => !ActionItem.ClosedStatuses.Contains(c.Status) && c.DueDate != null && c.DueDate < today),
                Next = g.Min(c => ActionItem.ClosedStatuses.Contains(c.Status) ? null : c.DueDate),
            })
            .ToListAsync(ct)).ToDictionary(g => g.Key, g => new PartsRollup(g.Total, g.Closed, g.Overdue, g.Next));

    public async Task<ActionDetail?> GetAsync(int number, CancellationToken ct = default)
    {
        // A Visitor sees only the action the presenter has open for the room, while they're in the meeting (MeetingAccess).
        if (!Briefs.MeetingAccess.Seats(user, meetings).Any(s => s.Action == number)) RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Actions.AsNoTracking()
            .Include(x => x.Team).Include(x => x.Article).ThenInclude(x => x!.Indicators).Include(x => x.Article).ThenInclude(x => x!.Cves)
            .Include(x => x.Comments).ThenInclude(c => c.Files).Include(x => x.Events).Include(x => x.Hunts).Include(x => x.Checklist)
            .Include(x => x.Indicators).Include(x => x.Outreach).AsSplitQuery().FirstOrDefaultAsync(x => x.Number == number, ct);
        if (a is null) return null;
        a.Comments = a.Comments.OrderBy(c => c.AtUtc).ToList();
        a.Events = a.Events.OrderByDescending(e => e.AtUtc).ToList();
        a.Hunts = a.Hunts.OrderByDescending(h => h.CreatedAtUtc).ToList();
        a.Outreach = a.Outreach.OrderByDescending(m => m.SentAtUtc).ToList();
        a.Checklist = InOrder(a.Checklist);
        a.Indicators = a.Indicators.OrderBy(i => i.AddedAtUtc).ThenBy(i => i.Value, StringComparer.OrdinalIgnoreCase).ToList();
        var parent = a.ParentId is { } pid ? await Rows(db, db.Actions.AsNoTracking().Where(x => x.Id == pid)).FirstOrDefaultAsync(ct) : null;
        var children = (await Rows(db, db.Actions.AsNoTracking().Where(x => x.ParentId == a.Id)).ToListAsync(ct)).OrderBy(c => c.Number).ToList();
        var canWork = await CanWork(db, user, a.TeamId, ct);
        var handedFrom = a.HandedFromTeamId is { } from ? await TeamName(db, from, ct) : null;
        var lineage = children.Count > 0 ? ParentLineage(a, handedFrom, children)
            : parent is not null ? await PartLineageAsync(db, a, parent, ct) : null;
        return new ActionDetail(a, parent, children, canWork, user.Has(Permission.WriteArticles), canWork || CanComment)
        {
            Files = await ActionFileService.ListAsync(db, user, a.Id, ct, a.Comments), HandedFrom = handedFrom,
            Rollup = lineage?.Parent.Id == a.Id ? lineage.Rollup : "", Lineage = lineage,
        };
    }

    // A parent's lineage, from what its page has read already: itself, its article and its parts.
    private ActionLineage ParentLineage(ActionItem a, string? handedFrom, IReadOnlyList<ActionRow> children) =>
        Lineage(a.Article is { } art ? Article(art.Id, art.Title, art.Cves.Select(c => c.CveId)) : null,
            Node(a.Id, a.Number, a.Title, a.Team?.Name ?? "", a.Team?.IsExternal == true, a.Status, a.DueDate, a.ClosedDate, a.CreatedAtUtc, handedFrom),
            children, a.Id);

    // A part's lineage: its parent (read already), and the parent's parts, itself among them, in one more read; the article
    // is the part's own when it shares the parent's, as a split part does.
    private async Task<ActionLineage> PartLineageAsync(IAppDbContext db, ActionItem a, ActionRow parent, CancellationToken ct)
    {
        var siblings = (await Rows(db, db.Actions.AsNoTracking().Where(x => x.ParentId == parent.Id)).ToListAsync(ct)).OrderBy(c => c.Number).ToList();
        var handedFrom = parent.HandedFromTeamId is { } from ? await TeamName(db, from, ct) : null;
        LineageArticle? article = null;
        if (parent.ArticleId is { } articleId)
        {
            article = a.Article is { } own && own.Id == articleId ? Article(own.Id, own.Title, own.Cves.Select(c => c.CveId))
                : await db.Articles.AsNoTracking().Where(x => x.Id == articleId)
                    .Select(x => new { x.Id, x.Title, Cves = x.Cves.Select(c => c.CveId).ToList() }).FirstOrDefaultAsync(ct) is { } other
                    ? Article(other.Id, other.Title, other.Cves) : null;
        }
        var p = parent;
        return Lineage(article, Node(p.Id, p.Number, p.Title, p.TeamName, p.TeamExternal, p.Status, p.DueDate, p.ClosedDate, p.CreatedAtUtc, handedFrom),
            siblings, a.Id);
    }

    private ActionLineage Lineage(LineageArticle? article, LineageNode parent, IReadOnlyList<ActionRow> parts, Guid current) =>
        new(article, parent, [.. parts.Select(c => Node(c.Id, c.Number, c.Title, c.TeamName, c.TeamExternal, c.Status, c.DueDate, c.ClosedDate, c.CreatedAtUtc, null))],
            current, ActionRollup.Of([.. parts.Select(c => (c.Status, c.DueDate))], clock.Today));

    private static LineageArticle Article(Guid id, string title, IEnumerable<string> cves) =>
        new(id, title, cves.Order(StringComparer.OrdinalIgnoreCase).ToList());

    // Its state is worked out here, once, by the organization's calendar: the flow and the timeline both read it.
    private LineageNode Node(Guid id, int number, string title, string team, bool external, ActionStatus status, DateOnly? due,
        DateOnly? closed, DateTimeOffset created, string? handedFrom) =>
        new(id, number, title, team, external, status, due, closed, clock.DayOf(created),
            LineageRules.StateOf(status, due, clock.Today, clock.Days), handedFrom);

    /// <summary>An action's details (Markdown), or null: a new part starts from its parent's.</summary>
    public async Task<string?> DescriptionAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        return await db.Actions.AsNoTracking().Where(a => a.Id == id).Select(a => a.Description).FirstOrDefaultAsync(ct);
    }

    /// <summary>A team's active actions against its limit ("4 of 5 active"), or null when it has none.</summary>
    public async Task<TeamActive?> TeamActiveAsync(Guid teamId, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        return await ActiveLimits.OfAsync(db, teamId, ct);
    }

    /// <summary>
    /// Leaves an action out of its external team's reminder emails, or puts it back (<paramref name="off"/> false). For
    /// whoever works it; it matters only while its team is external, so it's refused on another's.
    /// </summary>
    public async Task SetNoRemindersAsync(Guid id, bool off, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var a = await WorkableAsync(db, id, ct);
        if (a.NoReminders == off) return;
        if (!await db.Teams.AnyAsync(t => t.Id == a.TeamId && t.IsExternal, ct))
            throw new InvalidOperationException("Only an external team's actions are emailed to it, so there's nothing to turn off.");
        a.NoReminders = off;
        a.EditVersion = Guid.NewGuid();
        db.ActionEvents.Add(Event(user, a.Id, off ? "Turned off its reminders to the external team." : "Turned its reminders to the external team back on."));
        await SaveEditAsync(db, ct);
    }

    // --- deleting and restoring ---------------------------------------------------------------------------------

    /// <summary>The administrators' list of deleted actions, where each can be restored.</summary>
    public const string DeletedActionsHref = "admin/deleted-actions";

    /// <summary>Whether the current user may delete actions at all: an analyst or an administrator (each action is then
    /// held to <see cref="CanWork"/> too).</summary>
    public bool MayDelete => user.Has(Permission.WriteArticles) || user.Has(Permission.Administer);

    /// <summary>
    /// Deletes an action raised by mistake or twice: it's kept, with its number, history and files, but left out of
    /// every read (the database's query filter). For an analyst or an administrator who may work it, with a reason;
    /// refused for a parent with open parts. Cancelled or Not applicable is for work that won't be done, not this.
    /// Audited, with a line in its history, and heard on the actions feed as any change to an action is.
    /// </summary>
    public async Task DeleteAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        if (!MayDelete) throw new ForbiddenException("Only analysts and administrators can delete an action.");
        var why = ArticleService.CleanOrNull(reason, ActionItem.MaxDeleteReason, multiline: true)
                  ?? throw new ArgumentException("Say why it's deleted: a mistake, or a duplicate of which action.");
        await using var db = await dbs.CreateAsync(ct);
        var a = await FindAsync(db, id, ct);
        await RequireWork(db, user, a, ct, "Only analysts and administrators who can edit this action can delete it.");
        var openParts = await db.Actions.WhereOpen().CountAsync(c => c.ParentId == a.Id, ct);
        if (openParts > 0)
            throw new InvalidOperationException($"A-{a.Number} has {Words.Plural(openParts, "open part")}: delete or close its parts first.");
        (a.DeletedAtUtc, a.DeletedById, a.DeletedByName, a.DeleteReason) = (DateTimeOffset.UtcNow, user.UserId, user.DisplayName, why);
        a.EditVersion = Guid.NewGuid();
        db.ActionEvents.Add(Event(user, a.Id, $"Deleted it: {why}"));
        await SaveEditAsync(db, ct);
    }

    /// <summary>The deleted actions, the latest deleted first, with who deleted each, when and why. Administrators only.</summary>
    public async Task<IReadOnlyList<DeletedAction>> DeletedAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        // IgnoreQueryFilters reaches the whole query, its subqueries too, so the parent's deletion is asked for outright.
        return await db.Actions.AsNoTracking().IgnoreQueryFilters([ActionItem.DeletedFilter]).Where(a => a.DeletedAtUtc != null)
            .OrderByDescending(a => a.DeletedAtUtc).ThenByDescending(a => a.Number)
            .Select(a => new DeletedAction(a.Id, a.Number, a.Title, a.Team!.Name, a.Status, a.DeletedAtUtc!.Value, a.DeletedByName,
                a.DeleteReason, db.Actions.Any(p => p.Id == a.ParentId && p.DeletedAtUtc != null)))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Brings a deleted action back, as it was, its number and history included. Administrators only; audited, with a
    /// line in its history. A part whose parent is deleted waits for the parent.
    /// </summary>
    public async Task<int> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Actions.IgnoreQueryFilters([ActionItem.DeletedFilter]).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("That action no longer exists.");
        if (a.DeletedAtUtc is not { } deleted) return a.Number;
        if (a.ParentId is { } parentId
            && await db.Actions.IgnoreQueryFilters([ActionItem.DeletedFilter]).Where(p => p.Id == parentId && p.DeletedAtUtc != null)
                .Select(p => (int?)p.Number).FirstOrDefaultAsync(ct) is { } parent)
            throw new InvalidOperationException($"It's a part of A-{parent}, which is deleted too: restore A-{parent} first.");
        var line = $"Restored it (deleted {Day(clock.DayOf(deleted))} by {a.DeletedByName ?? "someone"}: {a.DeleteReason}).";
        (a.DeletedAtUtc, a.DeletedById, a.DeletedByName, a.DeleteReason) = (null, null, null, null);
        a.EditVersion = Guid.NewGuid();
        db.ActionEvents.Add(Event(user, a.Id, line));
        await SaveEditAsync(db, ct);
        return a.Number;
    }

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can see and restore deleted actions.");
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
        var rows = await db.Actions.AsNoTracking().WhereOpenOrClosedSince(weekAgo)
            .Select(a => new { a.TeamId, a.Status, a.DueDate, a.ClosedDate, a.AssigneeId, a.AssigneeName, a.Priority })
            .ToListAsync(ct);
        // A retired team is listed while it still has open actions (as on Team commitments): retiring one leaves its
        // actions on it, and they'd otherwise be on no team's card, overdue ones and all.
        var holding = rows.Where(r => !ActionItem.IsClosedStatus(r.Status)).Select(r => r.TeamId).ToHashSet();
        var teams = (await db.Teams.AsNoTracking().ToListAsync(ct)).InOrder(t => t.SortOrder, t => t.Name)
            .Where(t => !t.IsRetired || holding.Contains(t.Id)).ToList();
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
                open.Where(_ => !t.IsExternal).GroupBy(r => r.AssigneeId).OrderBy(g => g.Key is null ? 1 : 0).ThenByDescending(g => g.Count())
                    .Select(g => new AssigneeLoad(g.Key, g.Key is null ? "Unassigned" : g.First().AssigneeName ?? g.Key, g.Count(),
                        g.Count(r => ActionSla.StateOf(r.Status, r.DueDate, null, today, clock.Days) == SlaState.Overdue), g.Count(r => r.Priority == Priority.P1)))
                    .ToList(),
                open.Count(r => r.Priority == Priority.P1),
                t.ActiveLimit is { } limit ? new TeamActive(t.Id, t.Name, open.Count(r => ActionItem.IsActiveStatus(r.Status)), limit, t.LimitMode) : null);
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
        var made = await AddActionsAsync(dbs, user, async db =>
        {
            var a = new ActionItem();
            await Apply(db, a, form, ct);
            a.DueDate ??= ActionSla.DueDate(clock.Today, (await sla.GetAsync(ct)).DaysFor(a.Priority, a.Kind), clock.Days);
            if (form.RaisedOn is { } raised && raised != clock.Today)
            {
                if (raised > clock.Today) throw new ArgumentException("An action can't be raised for a day still to come.");
                if (raised < clock.Today.AddYears(-1)) throw new ArgumentException("That day is too far in the past.");
                a.RaisedOn = raised;
            }
            var over = await ActiveLimits.CheckChangeAsync(db, (null, null), (a.TeamId, a.Status), null, form.OverLimitReason, null, ct);
            var playbook = form.PlaybookId is { } pid ? await PlaybookFor(db, pid, a.Kind, ct) : null;
            if (playbook is not null) db.ActionChecklistItems.AddRange(AddSteps(a, playbook));
            return [new NewAction(a, $"Created for {await TeamName(db, a.TeamId, ct)}{(a.DueDate is { } d ? $", due {Day(d)}" : "")}"
                + (a.RaisedOn is { } r ? $"; raised for {Day(r)}'s brief" : "")
                + (playbook is not null ? $", with the {playbook.Name} playbook's checklist." : ".") + over)];
        }, null, ct);
        return made[0];
    }

    /// <summary>A new action and the first line of its history.</summary>
    internal sealed record NewAction(ActionItem Action, string Line);

    /// <summary>
    /// Adds new actions (one raised, or a split's parts): numbers them from the next free number, writes each one's first
    /// history line, gives a part of a parent with no article the parent's own indicators, and saves.
    /// <paramref name="build"/> makes them, checked, on the context given; <paramref name="numbered"/>, if any, then sees
    /// their numbers before the save (a split's line on its parent). Two creates at once can take the same number: the
    /// second's save fails on the unique index and it's built and numbered again, three times at most.
    /// </summary>
    internal static async Task<IReadOnlyList<int>> AddActionsAsync(IAppDbContextFactory dbs, ICurrentUser user,
        Func<IAppDbContext, Task<IReadOnlyList<NewAction>>> build, Action<IAppDbContext, IReadOnlyList<ActionItem>>? numbered, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbs.CreateAsync(ct);
            var made = await build(db);
            // Deleted ones included: a number is never reused.
            var number = (await db.Actions.IgnoreQueryFilters([ActionItem.DeletedFilter]).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
            foreach (var (a, line) in made)
            {
                a.Number = number++;
                db.Actions.Add(a);
                if (a.ParentId is { } parentId && a.ArticleId is null) await CopyParentIndicatorsAsync(db, user, parentId, a.Id, ct);
                db.ActionEvents.Add(Event(user, a.Id, line));
            }
            numbered?.Invoke(db, [.. made.Select(m => m.Action)]);
            try
            {
                await db.SaveChangesAsync(ct);
                return [.. made.Select(m => m.Action.Number)];
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // Two actions created at once took the same number; take the next one.
            }
        }
    }

    /// <summary>
    /// A quick action from the meeting: a title, kind, team and (optional) due date, on an article, at the article's
    /// priority (see <see cref="QuickPriority"/>), assigned to whoever raises it. Internal teams only: an external
    /// team's work is a part split off an action, so it has a parent to follow it up. A blank due date takes the SLA
    /// target's, a kind's own target first (ADR 0008). <paramref name="raisedOn"/> is a past day's brief being presented after the fact;
    /// <paramref name="overLimitReason"/> is why it goes ahead over a team's limit that warns (<see cref="ActiveLimits"/>).
    /// </summary>
    public async Task<int> QuickAddAsync(Guid articleId, string title, ActionKind kind, Guid teamId, DateOnly? due, DateOnly? raisedOn = null,
        string? overLimitReason = null, CancellationToken ct = default)
    {
        Priority? articlePriority;
        await using (var db = await dbs.CreateAsync(ct))
        {
            if (await db.Teams.AsNoTracking().AnyAsync(t => t.Id == teamId && t.IsExternal, ct))
                throw new ArgumentException("An external team's work is split off an action as a part. Raise it for one of your teams, then split it.");
            articlePriority = await db.Articles.AsNoTracking().Where(a => a.Id == articleId).Select(a => a.Priority).FirstOrDefaultAsync(ct);
        }
        return await CreateAsync(new ActionForm
        {
            Title = title, Kind = kind, TeamId = teamId, DueDate = due, ArticleId = articleId, Priority = QuickPriority(articlePriority, kind),
            RaisedOn = raisedOn, OverLimitReason = overLimitReason,
            // Whoever raises it in the meeting holds it, so it's on someone's list from the start; they can hand it on.
            AssigneeId = user.UserId, AssigneeName = user.DisplayName,
        }, ct);
    }

    /// <summary>
    /// A meeting action's priority: its article's, except that telling people, asking someone and anything else are
    /// never above P2 (a P1 article's "tell the service desk" isn't a P1 action). An article with no priority (an
    /// informational one) leaves it to the kind: a sweep or a block P2, the rest P3.
    /// </summary>
    public static Priority QuickPriority(Priority? article, ActionKind kind) => article switch
    {
        null => kind is ActionKind.IocSweep or ActionKind.Block ? Priority.P2 : Priority.P3,
        Priority.P1 when kind is ActionKind.Awareness or ActionKind.Outreach or ActionKind.Other => Priority.P2,
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
        // Another team in the editor is a hand-off, as the Hand off button's is: who and why are recorded, an assignee who
        // isn't on that team goes, its limit holds, and it's told.
        HandoffService.Handover? handover = null;
        if (form.TeamId != a.TeamId)
        {
            var to = await HandoffService.ReceivingTeamAsync(db, form.TeamId, ct);
            if (form.AssigneeId is { } held && to.Members.All(m => m.UserId != held)) (form.AssigneeId, form.AssigneeName) = (null, null);
            handover = await HandoffService.HandOverAsync(db, user, a, to,
                string.IsNullOrWhiteSpace(form.HandoffReason) ? EditorHandoffReason : form.HandoffReason, form.OverLimitReason, form.TurnRemindersOn, ct);
        }
        var before = (a.Title, a.Kind, a.Priority, a.AssigneeName, a.DueDate, a.TicketRef);
        await Apply(db, a, form, ct);
        var changes = new List<string>();
        if (before.Title != a.Title) changes.Add("title");
        if (before.Kind != a.Kind) changes.Add($"kind: {KindName(before.Kind)} → {KindName(a.Kind)}");
        if (before.Priority != a.Priority) changes.Add($"priority: {before.Priority} → {a.Priority}");
        if (before.AssigneeName != a.AssigneeName) changes.Add($"assignee: {before.AssigneeName ?? "nobody"} → {a.AssigneeName ?? "nobody"}");
        if (before.DueDate != a.DueDate) changes.Add($"due: {(before.DueDate is { } b ? Day(b) : "none")} → {(a.DueDate is { } d ? Day(d) : "none")}");
        if (before.TicketRef != a.TicketRef) changes.Add($"ticket: {a.TicketRef ?? "none"}");
        a.EditVersion = Guid.NewGuid();
        if (changes.Count > 0) db.ActionEvents.Add(Event(user, a.Id, "Changed " + string.Join("; ", changes) + "."));
        await SaveEditAsync(db, ct);
        if (handover is not null && notices is not null) await notices.SendAsync(db, a, handover, user.DisplayName, ct);
    }

    /// <summary>The reason a team change in the editor is recorded with when none is given.</summary>
    public const string EditorHandoffReason = "Changed the team in the editor";

    // A part raised from a parent with no article takes the parent's own indicators, so its hunts start from them.
    internal static async Task CopyParentIndicatorsAsync(IAppDbContext db, ICurrentUser user, Guid parentId, Guid childId, CancellationToken ct)
    {
        var parent = await db.Actions.AsNoTracking().Where(p => p.Id == parentId).Select(p => new { p.ArticleId }).FirstOrDefaultAsync(ct);
        if (parent is null || parent.ArticleId is not null) return;
        var now = DateTimeOffset.UtcNow;
        var theirs = await db.ActionIndicators.AsNoTracking().Where(i => i.ActionId == parentId).ToListAsync(ct);
        db.ActionIndicators.AddRange(theirs.Select(i => new ActionIndicator
        {
            ActionId = childId, Type = i.Type, Value = i.Value, Note = i.Note, AddedAtUtc = now, AddedByName = user.DisplayName,
        }));
    }

    // A save that moves the edit version, so one that lost the race to another change (the version moved under it) is
    // refused as a stale edit, as an article's is, rather than as a database error.
    internal static async Task SaveEditAsync(IAppDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new StaleEditException("Someone else changed this action at the same moment. Reload to see their change, then try again.");
        }
    }

    /// <summary>Takes the action on (assigns it to me), starting it if it was open. Held to <see cref="ActionGuide.CanTake"/>
    /// as the action is now, so one closed or handed to an external team since the caller's copy was read is refused as
    /// stale. From a list that showed it as nobody's (<paramref name="onlyIfNobodys"/>), it's also refused when someone
    /// else has taken it since.</summary>
    public async Task TakeAsync(Guid id, bool onlyIfNobodys = false, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var a = await WorkableAsync(db, id, ct);
        var external = await db.Teams.Where(t => t.Id == a.TeamId).Select(t => t.IsExternal).FirstOrDefaultAsync(ct);
        if (!ActionGuide.CanTake(new ActionFacts(a.Status, a.Kind, CanWork: true, Assigned: a.AssigneeId is not null, external)))
            throw new StaleEditException(external
                ? $"A-{a.Number} is with an external team now, so nobody here takes it on."
                : $"Someone else moved A-{a.Number} to {StatusName(a.Status)} in the meantime.");
        // Taken from a list that showed it as nobody's: someone who took it meanwhile keeps it.
        if (onlyIfNobodys && a.AssigneeId is not null && a.AssigneeId != user.UserId)
            throw new StaleEditException($"{a.AssigneeName ?? "Someone"} took A-{a.Number} on in the meantime.");
        a.AssigneeId = user.UserId;
        a.AssigneeName = user.DisplayName;
        var started = a.Status == ActionStatus.Open;
        var to = started ? ActionStatus.InProgress : a.Status;
        // Open to In progress stays active, so it never adds to the team's count; held to the rule all the same, so a
        // change to what Take does can't slip past the limit.
        var over = await ActiveLimits.CheckChangeAsync(db, (a.TeamId, a.Status), (a.TeamId, to), a.Id, null, null, ct);
        a.Status = to;
        a.EditVersion = Guid.NewGuid();
        db.ActionEvents.Add(Event(user, a.Id, (started ? "Took it on and started it." : "Took it on.") + over));
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
    public async Task SetStatusAsync(Guid id, ActionStatus status, ActionOutcome outcome, string? note, string? overLimitReason = null,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(status) || !Enum.IsDefined(outcome)) throw new ArgumentException("Choose a status and an outcome.");
        await using var db = await dbs.CreateAsync(ct);
        var a = await WorkableAsync(db, id, ct);
        var over = await OverLimit(db, a, status, overLimitReason, ct);
        var line = ApplyStatus(db, a, status, outcome, note);
        line.Text = ClipEvent(line.Text + over);
        await SaveEditAsync(db, ct);
    }

    // A move or change begun from the status the page showed (at): refused when someone has moved it since.
    private static void RequireAt(ActionItem a, ActionStatus at)
    {
        if (a.Status != at) throw new StaleEditException($"Someone else moved A-{a.Number} to {StatusName(a.Status)} in the meantime.");
    }

    // A move into an active status from one that isn't (Tracking, or reopening a closed one) adds to the team's active count.
    private static Task<string> OverLimit(IAppDbContext db, ActionItem a, ActionStatus to, string? reason, CancellationToken ct) =>
        ActiveLimits.CheckChangeAsync(db, (a.TeamId, a.Status), (a.TeamId, to), a.Id, reason, null, ct);

    /// <summary>
    /// Moves an action on the board from the column it was in (<paramref name="from"/>) to another. It's refused when the
    /// action has moved since the board was drawn, and otherwise follows <see cref="SetStatusAsync"/>'s rules: the same
    /// permission, and an outcome to close it Done (see <see cref="OutcomesFor"/>). A move to the column it's in does nothing.
    /// </summary>
    public async Task MoveAsync(Guid id, ActionStatus from, ActionStatus to, ActionOutcome outcome = ActionOutcome.None, string? note = null,
        string? overLimitReason = null, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to) || !Enum.IsDefined(outcome)) throw new ArgumentException("Choose a status and an outcome.");
        await using var db = await dbs.CreateAsync(ct);
        var a = await WorkableAsync(db, id, ct);
        RequireAt(a, from);
        if (from == to) return;
        var over = await OverLimit(db, a, to, overLimitReason, ct);
        var line = ApplyStatus(db, a, to, outcome, note);
        line.Text = ClipEvent(line.Text + over);
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
        RequireAt(a, at);
        ApplyStatus(db, a, at, outcome, note);
        await SaveEditAsync(db, ct);
    }

    /// <summary>
    /// Records what the owning team said outside TIDE, in one save and one history line ("Update from Network Engineering:
    /// …"): the reply kept as a comment quoting it, a move to <see cref="TeamUpdate.Status"/> by the rules a move follows
    /// (its team's active limit, an outcome to close it Done), and a new due date with its reason. Who may: whoever works
    /// the action. Refused, as a move is, when the action moved since the dialog opened, and when there's nothing to record.
    /// </summary>
    public async Task RecordUpdateAsync(TeamUpdate update, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(update.Status) || !Enum.IsDefined(update.Outcome)) throw new ArgumentException("Choose a status and an outcome.");
        var reply = ArticleService.CleanOrNull(update.Reply, TeamUpdates.MaxReply, multiline: true);
        var note = ArticleService.CleanOrNull(update.Note, TeamUpdates.MaxNote, multiline: true);
        await using var db = await dbs.CreateAsync(ct);
        var a = await WorkableAsync(db, update.ActionId, ct);
        RequireAt(a, update.From);
        var team = await TeamName(db, a.TeamId, ct);
        (DateOnly?, DateOnly, string)? due = null;
        if (update.DueDate is { } newDue && newDue != a.DueDate)
        {
            CheckDue(newDue);
            var why = ArticleService.CleanOrNull(update.DueReason, TeamUpdates.MaxNote) ?? throw new ArgumentException("Say why the due date moves.");
            due = (a.DueDate, newDue, why);
            a.DueDate = newDue;
        }
        var moves = update.Status != a.Status;
        if (reply is null && note is null && due is null && !moves) throw new ArgumentException("Paste their reply, write a note, or change its status or due date first.");
        var closing = moves && ActionItem.IsClosedStatus(update.Status);
        var lead = TeamUpdates.Line(team, closing ? null : note, due, reply is not null);
        if (moves)
        {
            var over = await OverLimit(db, a, update.Status, update.OverLimitReason, ct);
            var line = ApplyStatus(db, a, update.Status, update.Outcome, closing ? note : null, lead);
            line.Text = ClipEvent(line.Text + over);
        }
        else
        {
            a.EditVersion = Guid.NewGuid();
            db.ActionEvents.Add(Event(user, a.Id, lead));
        }
        if (reply is not null) db.ActionComments.Add(NewComment(a.Id, TeamUpdates.ReplyComment(team, reply)));
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
    /// needed; a question put to someone (Outreach), its answer. An action of kind Other may close with any.
    /// </summary>
    public static IReadOnlyList<ActionOutcome> KindOutcomes(ActionKind kind) => kind switch
    {
        ActionKind.IocSweep or ActionKind.ThreatHunt => [ActionOutcome.NoHits, ActionOutcome.HitsEscalated, ActionOutcome.Other],
        ActionKind.PatchRemediate or ActionKind.ConfigChange =>
            [ActionOutcome.Remediated, ActionOutcome.NotAffected, ActionOutcome.RiskAccepted, ActionOutcome.Other],
        ActionKind.Block => [ActionOutcome.Blocked, ActionOutcome.NotAffected, ActionOutcome.RiskAccepted, ActionOutcome.Other],
        ActionKind.Awareness => [ActionOutcome.Remediated, ActionOutcome.NotAffected, ActionOutcome.Other],
        ActionKind.Outreach => [ActionOutcome.Answered, ActionOutcome.NotAffected, ActionOutcome.Other],
        _ => Enum.GetValues<ActionOutcome>().Where(o => o != ActionOutcome.None).ToList(),
    };

    // A note on the history line (a hunt's result, closing it in the same step) goes before the status change. Returns the
    // line, for a caller with more to say after it (that it went over its team's limit, and why).
    internal ActionEvent ApplyStatus(IAppDbContext db, ActionItem a, ActionStatus status, ActionOutcome outcome, string? note, string? lead = null)
    {
        if (status == ActionStatus.Done && outcome == ActionOutcome.None) throw new ArgumentException("Say what came of it: choose an outcome.");
        if (ActionItem.IsClosedStatus(status) && !OutcomesFor(a.Kind, status, a.Outcome).Contains(outcome))
            throw new ArgumentException(a.Kind is ActionKind.IocSweep or ActionKind.ThreatHunt
                ? "A hunt closes with what it found: no hits, or hits escalated."
                : $"{Words.AOrAn(KindName(a.Kind))} {KindName(a.Kind)} action closes with one of: {string.Join(", ", KindOutcomes(a.Kind).Select(OutcomeName))}.");
        var cleanNote = ArticleService.CleanOrNull(note, 2000, multiline: true);
        var was = a.Status;
        a.Status = status;
        if (ActionItem.IsClosedStatus(status))
        {
            a.Outcome = outcome;
            a.OutcomeNote = cleanNote;
            if (!ActionItem.IsClosedStatus(was))
            {
                // The same moment the status setter just recorded: closed, and in its Closed lane, at one time.
                a.ClosedAtUtc = a.StatusChangedAtUtc;
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
        var line = Event(user, a.Id, text);
        db.ActionEvents.Add(line);
        return line;
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
        var comment = NewComment(id, clean);
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

    // A comment by whoever is signed in, now.
    private ActionComment NewComment(Guid actionId, string text) =>
        new() { ActionId = actionId, Text = text, AuthorId = user.UserId, AuthorName = user.DisplayName, AtUtc = DateTimeOffset.UtcNow };

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
        return InOrder(await db.ActionChecklistItems.AsNoTracking().Where(i => i.ActionId == actionId).ToListAsync(ct));
    }

    /// <summary>
    /// A checklist in its order: by place, then when each was added, then id. A place is the highest so far plus one, so
    /// two items added at the same moment share one; without the rest they could swap between loads. Sorted here, as
    /// <c>AdminLists.InOrder</c> is: SQL Server and .NET order Guids differently.
    /// </summary>
    private static List<ActionChecklistItem> InOrder(IEnumerable<ActionChecklistItem> items) =>
        items.OrderBy(i => i.SortOrder).ThenBy(i => i.AddedAtUtc).ThenBy(i => i.Id).ToList();

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
    /// The action's edit version doesn't move: a checklist change isn't a change to the form someone may have open.
    /// </summary>
    /// <remarks>
    /// Who may and which playbook are read first; then the read of the checklist and the save run in one serializable
    /// transaction (<see cref="SerializedChanges.SerializedAsync{T}"/>), so two people applying the same playbook at the
    /// same moment don't both find its steps missing and add each one twice: the second waits and finds them there.
    /// </remarks>
    public async Task<int> ApplyPlaybookAsync(Guid actionId, Guid playbookId, CancellationToken ct = default)
    {
        Playbook playbook;
        await using (var check = await dbs.CreateAsync(ct))
        {
            var found = await FindAsync(check, actionId, ct);
            await RequireWork(check, user, found, ct);
            playbook = await PlaybookFor(check, playbookId, found.Kind, ct);
        }
        return await dbs.SerializedAsync(async db =>
        {
            var a = await db.Actions.Include(x => x.Checklist).FirstOrDefaultAsync(x => x.Id == actionId, ct) ?? throw new KeyNotFoundException("That action no longer exists.");
            var added = AddSteps(a, playbook);
            db.ActionChecklistItems.AddRange(added);
            db.ActionEvents.Add(Event(user, a.Id, added.Count == 0
                ? $"Applied the {playbook.Name} playbook: its steps were all on the checklist already."
                : $"Applied the {playbook.Name} playbook: {added.Count} step{(added.Count == 1 ? "" : "s")} added to the checklist."));
            return added.Count;
        }, ct);
    }

    private static async Task<Playbook> PlaybookFor(IAppDbContext db, Guid playbookId, ActionKind kind, CancellationToken ct)
    {
        var playbook = await db.Playbooks.AsNoTracking().Include(p => p.Steps).FirstOrDefaultAsync(p => p.Id == playbookId, ct)
                       ?? throw new KeyNotFoundException("That playbook no longer exists.");
        if (playbook.IsRetired) throw new ArgumentException($"The {playbook.Name} playbook is retired.");
        if (playbook.Kind is { } k && k != kind) throw new ArgumentException($"The {playbook.Name} playbook is for {KindName(k)} actions.");
        return playbook;
    }

    // Copies the playbook's steps the action doesn't have yet onto its checklist. All or none: when they don't all fit
    // within the checklist's limit, it's refused, rather than some added and the rest dropped unsaid (or, on a full
    // checklist, the history saying they were all there already).
    private static List<ActionChecklistItem> AddSteps(ActionItem a, Playbook playbook)
    {
        var have = new HashSet<string>(a.Checklist.Select(i => i.Text.Trim()), StringComparer.OrdinalIgnoreCase);
        var order = NextOrder(a);
        var added = playbook.Steps.OrderBy(s => s.Order).Where(s => have.Add(s.Text.Trim()))
            .Select(step => new ActionChecklistItem { ActionId = a.Id, Text = step.Text, SortOrder = order++ }).ToList();
        var room = Math.Max(0, ActionChecklistItem.MaxPerAction - a.Checklist.Count);
        if (added.Count > room)
            throw new ArgumentException($"A checklist can have at most {ActionChecklistItem.MaxPerAction} items, and this one has room for "
                + (room == 0 ? "no more items" : $"{room} more item{(room == 1 ? "" : "s")}")
                + $"; the {playbook.Name} playbook would add {added.Count} steps. Take some items off first.");
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
    /// What a hunt can be built from: the indicators on the action's article and any added on the action itself (what
    /// Generate uses), and those with every article's in that article's story (a later report often carries the
    /// indicators the first one lacked).
    /// </summary>
    public async Task<HuntSources> HuntSourcesAsync(Guid actionId, CancellationToken ct = default)
    {
        RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var a = await HuntActionAsync(db, actionId, tracked: false, ct);
        var own = await HuntInputsAsync(db, a, fromStory: false, ct);
        var story = await HuntInputsAsync(db, a, fromStory: true, ct);
        return new HuntSources(own.Indicators.Count, story.Indicators.Count, story.Articles);
    }

    /// <summary>
    /// Generates a hunt query in <paramref name="dialect"/> from the source article's indicators, or (<paramref name="fromStory"/>)
    /// every article's in its story, with any added on the action itself, over the window <see cref="HuntInputsAsync"/> sets.
    /// </summary>
    public async Task<HuntQuery> GenerateHuntAsync(Guid actionId, string dialect, bool fromStory = false, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var a = await HuntActionAsync(db, actionId, tracked: false, ct);
        await RequireWork(db, user, a, ct);
        var inputs = await HuntInputsAsync(db, a, fromStory, ct);
        if (inputs.Indicators.Count == 0)
            throw new InvalidOperationException(fromStory ? "No article in the story has indicators to hunt for."
                : a.Article is null ? "The action has no indicators to hunt for. Add them on this card, or write a query by hand."
                : "The action's article has no indicators to hunt for. Add them on the article first, or build it from the story.");
        var text = await GeneratedTextAsync(dialect, inputs, ct);
        var hunt = new HuntQuery
        {
            ActionId = a.Id, Dialect = dialect, GeneratedText = text, Text = text, CreatedAtUtc = DateTimeOffset.UtcNow, CreatedByName = user.DisplayName,
        };
        db.HuntQueries.Add(hunt);
        db.ActionEvents.Add(Event(user, a.Id, $"Generated a {HuntQueryBuilder.DialectName(dialect)} hunt query{(fromStory ? $" from the story's {inputs.Articles} articles" : "")}."));
        await db.SaveChangesAsync(ct);
        return hunt;
    }

    /// <summary>
    /// The query <see cref="GenerateHuntAsync"/> would save for the action in <paramref name="dialect"/>, from its
    /// article's indicators and its own, without saving it: the action's page shows it as a preview when a sweep or hunt
    /// opens. It writes nothing (no hunt query, no history line), and is for whoever may generate one: anyone else is
    /// refused as Generate refuses them. Null when the action has no indicators to hunt for.
    /// </summary>
    public async Task<string?> PreviewHuntAsync(Guid actionId, string dialect, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var a = await HuntActionAsync(db, actionId, tracked: false, ct);
        await RequireWork(db, user, a, ct);
        var inputs = await HuntInputsAsync(db, a, fromStory: false, ct);
        return inputs.Indicators.Count == 0 ? null : await GeneratedTextAsync(dialect, inputs, ct);
    }

    // A generated query's text, the one saved and its preview alike: one builder, the same templates and the same limit.
    private async Task<string> GeneratedTextAsync(string dialect, HuntInputs inputs, CancellationToken ct) =>
        Filled(await queries.BuildAsync(dialect, inputs.Indicators, inputs.Days, ct));

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
    /// Adds a query from the team's saved ones (<see cref="SavedQueryService"/>): its <c>{{days}}</c> and <c>{{values}}</c>
    /// filled with the window and indicators a query generated from the action would have (<see cref="HuntInputsAsync"/>,
    /// <see cref="HuntQueryBuilder.FromSaved"/>). It's recorded like a generated one, and names the saved query it came
    /// from. A retired saved query isn't added.
    /// </summary>
    public async Task<HuntQuery> AddSavedHuntAsync(Guid actionId, Guid savedQueryId, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var a = await HuntActionAsync(db, actionId, tracked: false, ct);
        await RequireWork(db, user, a, ct);
        var saved = await db.SavedQueries.AsNoTracking().FirstOrDefaultAsync(q => q.Id == savedQueryId, ct)
                    ?? throw new KeyNotFoundException("That saved query no longer exists.");
        if (saved.IsRetired) throw new InvalidOperationException($"{saved.Name} is retired. Restore it in the Library, on Saved queries, to use it.");
        var d = QueryDialects.Find(saved.Dialect) ?? throw new ArgumentException("That saved query's language isn't one TIDE knows.");
        var inputs = await HuntInputsAsync(db, a, fromStory: false, ct);
        // TIDE wrote all of it (the saved query is held to its own limit), so however long the indicators make it, it's added.
        var filled = HuntQueryBuilder.FromSaved(d, saved.Name, saved.Text, inputs.Days, inputs.Indicators);
        var text = HuntText(Filled(filled), filled.Length);
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
    /// from an article adds them to the article (<see cref="ArticleService.AddIndicators"/>, as its editor would, so the
    /// brief, exports and other actions have them too), which takes writing articles as well as working the action; one
    /// with no article keeps them itself. Either way they're added by the same rule
    /// (<see cref="ArticleService.NewIndicators"/>), and the action's history says so.
    /// </summary>
    public async Task<int> AddIndicatorsAsync(Guid actionId, IEnumerable<IndicatorForm> indicators, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var a = await HuntActionAsync(db, actionId, tracked: true, ct);
        await RequireWork(db, user, a, ct);
        int added;
        if (a.ArticleId is not null)
        {
            var article = a.Article ?? throw new KeyNotFoundException("The action's article no longer exists.");
            added = articles.AddIndicators(db, article, indicators, OnTheArticle);
            if (added > 0) db.ActionEvents.Add(Event(user, a.Id, $"Added {Words.Plural(added, "indicator")} to the article."));
        }
        else
        {
            var wanted = ArticleService.NewIndicators(indicators, a.Indicators.Select(i => (i.Type, i.Value)), "An action");
            var now = DateTimeOffset.UtcNow;
            db.ActionIndicators.AddRange(wanted.Select(f => new ActionIndicator
            {
                ActionId = a.Id, Type = f.Type, Value = f.Value, Note = f.Note, AddedAtUtc = now, AddedByName = user.DisplayName,
            }));
            added = wanted.Count;
            if (added > 0) db.ActionEvents.Add(Event(user, a.Id, $"Added {Words.Plural(added, "indicator")}."));
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

    /// <summary>
    /// The action with what its hunts are built from: its article's indicators and its own. The two collections are read
    /// as separate queries, so the rows don't multiply. <paramref name="tracked"/> when the article is written to.
    /// </summary>
    internal static async Task<ActionItem> HuntActionAsync(IAppDbContext db, Guid actionId, bool tracked, CancellationToken ct)
    {
        var query = db.Actions.Include(x => x.Article).ThenInclude(x => x!.Indicators).Include(x => x.Indicators).AsSplitQuery();
        return await (tracked ? query : query.AsNoTracking()).FirstOrDefaultAsync(x => x.Id == actionId, ct)
               ?? throw new KeyNotFoundException("That action no longer exists.");
    }

    /// <summary>
    /// What a hunt on <paramref name="a"/> searches for: its article's indicators, or (<paramref name="fromStory"/>) every
    /// article's in the article's story, with any added on the action, each once; and how far back, from a week before the
    /// earliest of those articles (<see cref="HuntQueryBuilder.WindowDays"/>). Generate and a saved query both use it, so
    /// they look back the same way.
    /// </summary>
    internal static async Task<HuntInputs> HuntInputsAsync(IAppDbContext db, ActionItem a, bool fromStory, CancellationToken ct)
    {
        List<Article> sources = fromStory ? await StoryArticlesAsync(db, a, ct) : a.Article is { } own ? [own] : [];
        var indicators = HuntIndicators(sources.SelectMany(x => x.Indicators), a.Indicators);
        var days = HuntQueryBuilder.WindowDays(sources.Min(x => (DateTimeOffset?)(x.PublishedAtUtc ?? x.CreatedAtUtc)), DateTimeOffset.UtcNow);
        return new HuntInputs(indicators, days, sources.Count);
    }

    internal sealed record HuntInputs(List<ArticleIndicator> Indicators, int Days, int Articles);

    /// <summary>
    /// Articles' indicators and the action's <paramref name="own"/> merged as a hunt searches them, each once. An outreach
    /// email passes only those its template's ceiling lets through.
    /// </summary>
    internal static List<ArticleIndicator> HuntIndicators(IEnumerable<ArticleIndicator> articles, IEnumerable<ActionIndicator> own) =>
        // The action's own as a hunt reads them (the builder works on an article's).
        Distinct(articles.Concat(own.Select(i => new ArticleIndicator { Id = i.Id, Type = i.Type, Value = i.Value, Note = i.Note })));

    private static List<ArticleIndicator> Distinct(IEnumerable<ArticleIndicator> indicators) =>
        indicators.DistinctBy(i => (i.Type, i.Value.Trim().ToLowerInvariant())).ToList();

    // The action's article and the others in its story (just the article when it has no story), with their indicators.
    private static async Task<List<Article>> StoryArticlesAsync(IAppDbContext db, ActionItem a, CancellationToken ct)
    {
        if (a.Article is null) return [];
        if (a.Article.StoryId is not { } storyId) return [a.Article];
        return await db.Articles.AsNoTracking().Include(x => x.Indicators).Where(x => x.StoryId == storyId).ToListAsync(ct);
    }

    // A query's text as it's kept. What TIDE wrote (<paramref name="written"/> characters: a generated query, or a saved one
    // filled in, both of which grow with the indicators) doesn't count against what a person may write
    // (HuntQuery.MaxWritten), only what they add to it, so a sweep over hundreds of hashes is added and its edit saved,
    // while a hand-written query is held to that. Nothing is longer than the column (HuntQuery.MaxText).
    private static string HuntText(string? text, int written = 0)
    {
        var clean = (text ?? "").Trim();
        if (clean.Length == 0) throw new ArgumentException("The query is empty.");
        if (clean.Length > Math.Min(HuntQuery.MaxText, HuntQuery.MaxWritten + written)) throw new ArgumentException("That query is too long.");
        return clean;
    }

    // A query TIDE wrote, refused only past the column: one that long is too many indicators for one query.
    private static string Filled(string text) => text.Length <= HuntQuery.MaxText ? text
        : throw new InvalidOperationException("That's too many indicators for one query. Build it from the article rather than the story, or split the hunt.");

    /// <summary>
    /// Saves the query as the analyst edited it. After its result is recorded it can still be edited, but is marked as
    /// edited since (the result was for the earlier text), and the action's history says so.
    /// </summary>
    public async Task SaveHuntTextAsync(Guid huntId, string text, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var (hunt, a) = await WorkableHuntAsync(db, huntId, ct);
        var clean = HuntText(text, hunt.GeneratedText.Length);
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
            // A deleted part counts: restoring it mustn't make parts two levels deep.
            if (await db.Actions.IgnoreQueryFilters([ActionItem.DeletedFilter]).AnyAsync(x => x.ParentId == a.Id, ct))
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

    internal static string Day(DateOnly d) => d.ToString("ddd MMM d", System.Globalization.CultureInfo.InvariantCulture);

    public static string KindName(ActionKind k) => k switch
    {
        ActionKind.IocSweep => "IOC sweep",
        ActionKind.ThreatHunt => "Threat hunt",
        ActionKind.PatchRemediate => "Patch / remediate",
        ActionKind.ConfigChange => "Config change",
        _ => k.ToString(),
    };

    /// <summary>
    /// The kind a stored or typed name stands for (its code name, as an SLA setting's key or a trends line has it), ignoring
    /// case, or null. A kind's name before a rename (<see cref="FormerKind"/>) still reads as the kind, so a bookmark, a server's
    /// <c>Actions:Sla:Kind</c> value or a configuration bundle from before the rename keeps working.
    /// </summary>
    public static ActionKind? KindNamed(string? name) =>
        FormerKind(name?.Trim())
        ?? (Domain.Common.EnumNames.TryParseExact(name?.Trim() ?? "", out ActionKind k, ignoreCase: true) ? k : null);

    /// <summary>What <see cref="ActionKind.Outreach"/> was called, and stored as in setting keys, before the rename (ADR 0019).</summary>
    public const string FormerOutreachName = "VendorInquiry";

    // Kinds' code names before a rename, and the kind each is now. The kind names read anywhere (KindNamed, the SLA
    // setting keys, the trends lines) go through this one list, so a rename is added here once.
    private static readonly (string Name, ActionKind Kind)[] FormerKinds = [(FormerOutreachName, ActionKind.Outreach)];

    /// <summary>
    /// The kind <paramref name="name"/>, a kind's code name before a rename, is now, or null. Compared whole, as given (not
    /// trimmed), ignoring case unless <paramref name="comparison"/> says otherwise.
    /// </summary>
    public static ActionKind? FormerKind(string? name, StringComparison comparison = StringComparison.OrdinalIgnoreCase) =>
        name is null ? null : Array.Find(FormerKinds, f => string.Equals(f.Name, name, comparison)) is { Name: not null } former ? former.Kind : null;

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
