using Tide.Application.Content;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>
/// The flows an action's page offers, from its primary button, its More menu and its Next line. Each is a flow the page
/// has anyway; the page runs every one through one dispatcher, and <c>?do=</c> names one by its <see cref="ActionGuide.Key"/>.
/// </summary>
public enum ActionFlow
{
    /// <summary>Nothing to do: no primary button, or a link that only opens another action.</summary>
    None = 0,

    /// <summary>The Outreach tool's "Write the email".</summary>
    WriteEmail,

    /// <summary>The Outreach tool's "Record the reply", for the email waiting longest.</summary>
    RecordReply,

    RecordUpdate,
    TakeIt,

    /// <summary>Open to In progress.</summary>
    StartWork,

    /// <summary>Blocked to In progress.</summary>
    Unblock,

    /// <summary>To Done, which asks for the outcome first.</summary>
    CloseWithOutcome,

    HandOff,
    SplitToTeams,

    /// <summary>The new-action form, prefilled from this one as its parent.</summary>
    SplitOffPart,

    Edit,

    /// <summary>The status badge's choices.</summary>
    ChangeStatus,

    /// <summary>The comment box.</summary>
    Comment,

    /// <summary>Delete it (a mistake or a duplicate), asking why.</summary>
    Delete,
}

/// <summary>The tools in the action page's side drawer, each an existing panel.</summary>
public enum ActionTool { Checklist = 0, Timeline, Hunt, Outreach }

/// <summary>What the page's Next line is about. The order is the order of importance.</summary>
public enum NextKind { PartLate = 0, NoReply, Escalated, Blocked, Overdue, Unowned, RunQuery, AwaitingResult }

/// <summary>What the page knows about an action and who's looking, to choose its primary button and More.</summary>
/// <param name="CanWork">Whether the viewer may work it (ActionDetail.CanWork).</param>
/// <param name="Assigned">Whether someone has it.</param>
/// <param name="External">Whether its team works outside TIDE (no members, so nobody takes it).</param>
/// <param name="OpenParts">How many of its parts are still open.</param>
/// <param name="Emails">How many emails it has (an Outreach action's), sent or replied.</param>
/// <param name="Waiting">Whether one of them is waiting for a reply.</param>
/// <param name="Mine">Whether the viewer has it.</param>
/// <param name="CanEditAll">Whether the viewer is an analyst (ActionDetail.CanEditAll), who splits.</param>
/// <param name="TopLevel">Whether it isn't a part, so it can be split.</param>
/// <param name="CanDelete">Whether the viewer may delete it: an analyst or administrator who may work it.</param>
public sealed record ActionFacts(ActionStatus Status, ActionKind Kind, bool CanWork, bool Assigned, bool External,
    int OpenParts = 0, int Emails = 0, bool Waiting = false, bool Mine = false, bool CanEditAll = false, bool TopLevel = true, bool CanDelete = false)
{
    public bool IsOpen => !ActionItem.IsClosedStatus(Status);
}

/// <summary>An email waiting for its reply, as the Next line names it: to whom, the day it went, and the day its
/// follow-up reminder goes (null when the reminder is off).</summary>
public sealed record WaitingEmail(string To, DateOnly SentOn, DateOnly? FollowUpOn);

/// <summary>What the page knows about an action on <paramref name="Today"/>, to choose its Next line.</summary>
/// <param name="Sla">Its standing against its due date (ActionSla.StateOf): Overdue is what's late.</param>
/// <param name="Parts">The parts of the action whose page this is (none on a part's page, or with none).</param>
/// <param name="EscalatedOn">The day it was escalated, while it still is.</param>
/// <param name="Hunt">Where its query stands, on a sweep or a hunt; null on every other kind.</param>
public sealed record NextFacts(ActionStatus Status, SlaState Sla, DateOnly? Due, DateOnly Today, bool CanWork,
    IReadOnlyList<LineageNode>? Parts = null, WaitingEmail? Waiting = null, DateOnly? EscalatedOn = null, HuntFacts? Hunt = null);

/// <summary>Where a sweep's or hunt's query stands for whoever is looking, for its Next line.</summary>
/// <param name="Takeable">Nobody has the action, and the viewer may take it (<see cref="ActionGuide.CanTake"/>).</param>
/// <param name="Mine">The viewer has it.</param>
/// <param name="OnAction">A query is saved on the action and has no result yet. Stored, so the line reads the same
/// after a reload.</param>
/// <param name="HasIndicators">There are indicators to build a query from (the preview, or Generate).</param>
/// <param name="HasResult">A query's result is recorded, so the query is no longer what's next.</param>
public sealed record HuntFacts(bool Takeable, bool Mine, bool OnAction, bool HasIndicators = false, bool HasResult = false);

/// <summary>A link on the Next line: a flow, in words that fit the line. With <paramref name="On"/> it goes to that
/// action's page, running the flow there (with <see cref="ActionFlow.None"/>, only opening it).</summary>
public sealed record NextLink(ActionFlow Flow, string Label, int? On = null);

/// <summary>The page's Next line: what needs doing, in words, and the links that do it.</summary>
public sealed record NextStep(NextKind Kind, string Text, IReadOnlyList<NextLink> Links);

/// <summary>
/// What an action's page puts first: its primary button (<see cref="Primary"/>), what's under More (<see cref="More"/>),
/// its Next line (<see cref="Next"/>) and which tools its drawer has (<see cref="Tools"/>). Kept pure, so the rules are
/// tested on their own; the page maps each flow to one it already has.
/// </summary>
public static class ActionGuide
{
    /// <summary>The one rule for taking an action: open, someone else's or nobody's, and on a team with members.</summary>
    public static bool CanTake(ActionFacts f) => f.CanWork && f.IsOpen && !f.Mine && !f.External;

    /// <summary>
    /// The primary button, by the first rule that applies: nothing for a closed action or a viewer who can't work it;
    /// an Outreach action with no email yet writes one; an email waiting records its reply; work followed rather than
    /// done here (Tracking, an external team's, or a parent with open parts) records an update; an open action nobody
    /// has is taken; otherwise the status move that fits: Open starts, Blocked unblocks, and In progress closes with
    /// an outcome.
    /// </summary>
    public static ActionFlow Primary(ActionFacts f)
    {
        if (!f.CanWork || !f.IsOpen) return ActionFlow.None;
        if (f.Kind == ActionKind.Outreach && f.Emails == 0) return ActionFlow.WriteEmail;
        if (f.Waiting) return ActionFlow.RecordReply;
        if (f.Status == ActionStatus.Tracking || f.External || f.OpenParts > 0) return ActionFlow.RecordUpdate;
        if (!f.Assigned && CanTake(f)) return ActionFlow.TakeIt;
        return f.Status switch
        {
            ActionStatus.Open => ActionFlow.StartWork,
            ActionStatus.Blocked => ActionFlow.Unblock,
            _ => ActionFlow.CloseWithOutcome,
        };
    }

    /// <summary>
    /// Everything else the viewer may do, in the menu's order: take it (by <see cref="CanTake"/>) and record an update
    /// when neither is the primary; hand off; split to teams (open) and split off a part, for an analyst on a top-level
    /// action; edit; change status; last, delete, for an analyst or administrator who may work it (a mistake or a
    /// duplicate). Nothing for a viewer who can't work it or split it.
    /// </summary>
    public static IReadOnlyList<ActionFlow> More(ActionFacts f)
    {
        var primary = Primary(f);
        var more = new List<ActionFlow>();
        if (CanTake(f) && primary != ActionFlow.TakeIt) more.Add(ActionFlow.TakeIt);
        if (f.CanWork && primary != ActionFlow.RecordUpdate) more.Add(ActionFlow.RecordUpdate);
        if (f.CanWork) more.Add(ActionFlow.HandOff);
        if (f.CanEditAll && f.TopLevel)
        {
            if (f.IsOpen) more.Add(ActionFlow.SplitToTeams);
            more.Add(ActionFlow.SplitOffPart);
        }
        if (f.CanWork) more.AddRange([ActionFlow.Edit, ActionFlow.ChangeStatus]);
        if (f.CanDelete) more.Add(ActionFlow.Delete);
        return more;
    }

    /// <summary>A flow's words on a button or menu item.</summary>
    public static string Label(ActionFlow s) => s switch
    {
        ActionFlow.WriteEmail => "Write the email",
        ActionFlow.RecordReply => "Record the reply",
        ActionFlow.RecordUpdate => "Record an update",
        ActionFlow.TakeIt => "Take it",
        ActionFlow.StartWork => "Start work",
        ActionFlow.Unblock => "Unblock",
        ActionFlow.CloseWithOutcome => "Close with outcome",
        ActionFlow.HandOff => "Hand off…",
        ActionFlow.SplitToTeams => "Split to teams…",
        ActionFlow.SplitOffPart => "Split off a part",
        ActionFlow.Edit => "Edit",
        ActionFlow.ChangeStatus => "Change status…",
        ActionFlow.Comment => "Comment",
        ActionFlow.Delete => "Delete…",
        _ => "",
    };

    /// <summary>A flow's Bootstrap icon.</summary>
    public static string Icon(ActionFlow s) => s switch
    {
        ActionFlow.WriteEmail => "bi-envelope-paper",
        ActionFlow.RecordReply => "bi-reply",
        ActionFlow.RecordUpdate => "bi-envelope-check",
        ActionFlow.TakeIt => "bi-person-check",
        ActionFlow.StartWork or ActionFlow.Unblock => "bi-play-fill",
        ActionFlow.CloseWithOutcome => "bi-check2-circle",
        ActionFlow.HandOff => "bi-box-arrow-right",
        ActionFlow.SplitToTeams => "bi-diagram-3",
        ActionFlow.SplitOffPart => "bi-plus-lg",
        ActionFlow.Edit => "bi-pencil",
        ActionFlow.ChangeStatus => "bi-arrow-left-right",
        ActionFlow.Comment => "bi-chat-left-text",
        ActionFlow.Delete => "bi-trash",
        _ => "",
    };

    /// <summary>A flow's or tool's key in the address (<c>?do=recordupdate</c>, <c>?tool=hunt</c>).</summary>
    public static string Key<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    /// <summary>The flow or tool an address names by its <see cref="Key{T}"/> (in any case), or null for none or nonsense.</summary>
    public static T? Parse<T>(string? key) where T : struct, Enum =>
        key?.Trim() is { Length: > 0 } k && char.IsLetter(k[0]) && Enum.TryParse<T>(k, true, out var v) ? v : null;

    /// <summary>
    /// The Next line, or null when nothing needs doing (or the viewer can't work it, or it's closed). Only the most
    /// important one is said, in this order: a part overdue (the latest of them, with how many more), an email past its
    /// follow-up day with no reply, escalated, blocked, then past its own due date (its SLA Overdue). After those, a
    /// sweep or hunt with no result yet says where its query stands: nobody has it (and whether a query is already on
    /// it); it's the viewer's, to copy and run; a query is saved on it, to run and record.
    /// </summary>
    public static NextStep? Next(NextFacts f)
    {
        if (!f.CanWork || ActionItem.IsClosedStatus(f.Status)) return null;
        var late = (f.Parts ?? []).Where(p => p.IsOpen && p.State == LineageState.Overdue)
            .OrderByDescending(p => ActionSla.DaysLate(p.Due, f.Today)).ThenBy(p => p.Number).ToList();
        if (late.Count > 0)
        {
            var p = late[0];
            var more = late.Count > 1 ? $" {Words.Plural(late.Count - 1, "more part")} overdue." : "";
            return new NextStep(NextKind.PartLate, $"{p.Team} is {ActionSla.LateText(p.Due, f.Today)} on A-{p.Number}.{more}",
                [new(ActionFlow.None, "Chase them", p.Number), new(ActionFlow.RecordUpdate, "Record their reply", p.Number)]);
        }
        if (f.Waiting is { FollowUpOn: { } followUp } w && followUp <= f.Today)
            return new NextStep(NextKind.NoReply,
                $"No reply from {w.To} since {Words.MonthDay(w.SentOn)}; its follow-up was due {Words.MonthDay(followUp)}.",
                [new(ActionFlow.RecordReply, "Record the reply"), new(ActionFlow.WriteEmail, "Write a follow-up")]);
        if (f.EscalatedOn is { } escalated)
        {
            var due = ActionSla.LateText(f.Due, f.Today) ?? (f.Due is { } d ? "due " + Words.MonthDay(d) : "no due date");
            return new NextStep(NextKind.Escalated, $"Escalated on {Words.MonthDay(escalated)}: {due}.",
                [new(ActionFlow.RecordUpdate, "Record an update"), new(ActionFlow.HandOff, "Hand off")]);
        }
        if (f.Status == ActionStatus.Blocked)
            return new NextStep(NextKind.Blocked, "It's blocked.",
                [new(ActionFlow.Comment, "Say what it's waiting on"), new(ActionFlow.HandOff, "Hand off")]);
        if (f.Sla == SlaState.Overdue && f.Due is { } was)
            return new NextStep(NextKind.Overdue, $"It's {ActionSla.LateText(was, f.Today)}: it was due {Words.MonthDay(was)}.",
                [new(ActionFlow.RecordUpdate, "Record an update"), new(ActionFlow.Edit, "Move the due date")]);
        if (f.Hunt is { HasResult: false } hunt)
        {
            if (hunt.Takeable)
                return new NextStep(NextKind.Unowned,
                    hunt.OnAction ? "Nobody has it yet. Take it, then run the query that's on the action." : "Nobody has it yet.",
                    [new(ActionFlow.TakeIt, "Take it")]);
            if (hunt.Mine && hunt.OnAction)
                return new NextStep(NextKind.AwaitingResult, "Run the query that's on the action, then record what it found.", []);
            if (hunt.Mine)
                return new NextStep(NextKind.RunQuery,
                    hunt.HasIndicators ? "Copy the query and run it in your SIEM." : "Add indicators or write a query by hand, then run it in your SIEM.", []);
        }
        return null;
    }

    /// <summary>A tool's tab name.</summary>
    public static string Name(ActionTool t) => t == ActionTool.Hunt ? "Hunt queries" : t.ToString();

    /// <summary>Whether the action whose page this is has a parts timeline: it's the parent in its lineage, and a part has
    /// a due date (what <see cref="RemediationTimeline.Build"/> needs to draw one).</summary>
    public static bool HasTimeline(ActionLineage? lineage, Guid actionId) =>
        lineage is not null && lineage.Parent.Id == actionId && lineage.Parts.Any(p => p.Due is not null);

    /// <summary>
    /// The drawer's tools, in order: the checklist always; the timeline on a parent whose parts have a due date; hunt
    /// queries for a sweep or hunt, or when it has any; outreach for an Outreach action, or when it has emails.
    /// </summary>
    public static IReadOnlyList<ActionTool> Tools(ActionKind kind, bool timeline, int hunts, int emails)
    {
        // A sweep's or hunt's query is its work, so its tab comes first.
        var tools = new List<ActionTool> { ActionTool.Checklist };
        if (timeline) tools.Add(ActionTool.Timeline);
        if (IsHunt(kind)) tools.Insert(0, ActionTool.Hunt);
        else if (hunts > 0) tools.Add(ActionTool.Hunt);
        if (emails > 0 || kind == ActionKind.Outreach) tools.Add(ActionTool.Outreach);
        return tools;
    }

    /// <summary>Whether the action is a sweep or a hunt. Its query is the work: its tools sit in the page's wide column,
    /// above what's happened (a query wraps badly in the side column, where every other kind's stay), the query is
    /// previewed when the page opens, and the Next line follows it.</summary>
    public static bool IsHunt(ActionKind kind) => kind is ActionKind.IocSweep or ActionKind.ThreatHunt;

    /// <summary>
    /// The tool the drawer shows: the one the address asks for (<paramref name="asked"/>, a <see cref="Key{T}"/>) when it's
    /// there, else the kind's own (outreach for Outreach, hunt queries for a sweep or hunt), else the checklist.
    /// </summary>
    public static ActionTool Opening(string? asked, ActionKind kind, IReadOnlyList<ActionTool> tools)
    {
        if (Parse<ActionTool>(asked) is { } t && tools.Contains(t)) return t;
        var own = kind switch
        {
            ActionKind.Outreach => ActionTool.Outreach,
            ActionKind.IocSweep or ActionKind.ThreatHunt => ActionTool.Hunt,
            _ => ActionTool.Checklist,
        };
        return tools.Contains(own) ? own : ActionTool.Checklist;
    }
}
