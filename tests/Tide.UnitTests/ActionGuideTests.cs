using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>The action page's first things, as pure rules: its primary button, its More menu, its Next line, the tools in
/// its drawer and the one it opens on, the flows the address names, and its "What's happened" feed.</summary>
public class ActionGuideTests
{
    private static readonly DateOnly Mon = new(2026, 11, 23), Wed = new(2026, 11, 25), Fri = new(2026, 11, 27);

    private static ActionFacts Facts(ActionStatus status = ActionStatus.InProgress, ActionKind kind = ActionKind.Block, bool canWork = true,
        bool assigned = true, bool external = false, int openParts = 0, int emails = 0, bool waiting = false, bool mine = false,
        bool canEditAll = false, bool topLevel = true) =>
        new(status, kind, canWork, assigned, external, openParts, emails, waiting, mine, canEditAll, topLevel);

    // The Next line's facts on Wednesday, its SLA worked out as the page does.
    private static NextFacts On(ActionStatus status, DateOnly? due, bool canWork = true, IReadOnlyList<LineageNode>? parts = null,
        WaitingEmail? waiting = null, DateOnly? escalated = null) =>
        new(status, ActionSla.StateOf(status, due, null, Wed), due, Wed, canWork, parts, waiting, escalated);

    private static LineageNode Part(int n, string team, ActionStatus status, DateOnly? due) =>
        new(Guid.NewGuid(), n, "Part " + n, team, false, status, due, null, Mon, LineageRules.StateOf(status, due, Wed));

    [Fact]
    public void ThePrimary_IsNone_ForAClosedAction_OrAViewerWhoCantWorkIt()
    {
        ActionGuide.Primary(Facts(canWork: false, assigned: false)).Should().Be(ActionFlow.None, "a read-only viewer gets no button they can't use");
        foreach (var closed in ActionItem.ClosedStatuses)
            ActionGuide.Primary(Facts(closed, ActionKind.Outreach)).Should().Be(ActionFlow.None);
        ActionGuide.Label(ActionFlow.None).Should().BeEmpty();
        ActionGuide.Icon(ActionFlow.None).Should().BeEmpty();
    }

    [Fact]
    public void AnOutreachAction_WritesItsFirstEmail_ThenRecordsTheReply_WhateverElseApplies()
    {
        ActionGuide.Primary(Facts(ActionStatus.Open, ActionKind.Outreach, assigned: false)).Should().Be(ActionFlow.WriteEmail, "before taking it");
        ActionGuide.Primary(Facts(ActionStatus.Tracking, ActionKind.Outreach, emails: 1, waiting: true)).Should().Be(ActionFlow.RecordReply);
        ActionGuide.Primary(Facts(ActionStatus.InProgress, ActionKind.Outreach, emails: 2)).Should().Be(ActionFlow.CloseWithOutcome,
            "every email answered: what's left is to close it");
        ActionGuide.Label(ActionFlow.WriteEmail).Should().Be("Write the email");
        ActionGuide.Label(ActionFlow.RecordReply).Should().Be("Record the reply");
    }

    [Fact]
    public void WorkFollowedRatherThanDone_RecordsAnUpdate()
    {
        ActionGuide.Primary(Facts(ActionStatus.Tracking)).Should().Be(ActionFlow.RecordUpdate);
        ActionGuide.Primary(Facts(ActionStatus.Open, assigned: false, external: true)).Should().Be(ActionFlow.RecordUpdate,
            "an external team has no members, so nobody takes it");
        ActionGuide.Primary(Facts(ActionStatus.InProgress, openParts: 2)).Should().Be(ActionFlow.RecordUpdate);
        ActionGuide.Label(ActionFlow.RecordUpdate).Should().Be("Record an update");
    }

    [Fact]
    public void AnOpenActionNobodyHas_IsTaken_OtherwiseTheStatusMoveThatFits()
    {
        ActionGuide.Primary(Facts(ActionStatus.Open, assigned: false)).Should().Be(ActionFlow.TakeIt);
        ActionGuide.Primary(Facts(ActionStatus.Blocked, assigned: false)).Should().Be(ActionFlow.TakeIt);
        ActionGuide.Primary(Facts(ActionStatus.Open)).Should().Be(ActionFlow.StartWork);
        ActionGuide.Primary(Facts(ActionStatus.Blocked)).Should().Be(ActionFlow.Unblock);
        ActionGuide.Primary(Facts(ActionStatus.InProgress)).Should().Be(ActionFlow.CloseWithOutcome);
        new[] { ActionFlow.TakeIt, ActionFlow.StartWork, ActionFlow.Unblock, ActionFlow.CloseWithOutcome }.Select(ActionGuide.Label)
            .Should().Equal("Take it", "Start work", "Unblock", "Close with outcome");
    }

    [Fact]
    public void More_HoldsEverythingElse_InOrder_TakingByTheSameRuleAsThePrimary()
    {
        ActionGuide.More(Facts(ActionStatus.Open, assigned: false, canEditAll: true)).Should().Equal(
            [ActionFlow.RecordUpdate, ActionFlow.HandOff, ActionFlow.SplitToTeams, ActionFlow.SplitOffPart, ActionFlow.Edit, ActionFlow.ChangeStatus],
            "Take it is the primary, so it isn't in More too");
        ActionGuide.More(Facts(ActionStatus.Open)).Should().Equal(
            [ActionFlow.TakeIt, ActionFlow.RecordUpdate, ActionFlow.HandOff, ActionFlow.Edit, ActionFlow.ChangeStatus], "someone else has it");
        ActionGuide.More(Facts(ActionStatus.Open, mine: true)).Should().NotContain(ActionFlow.TakeIt, "it's yours already");
        ActionGuide.More(Facts(ActionStatus.Tracking, external: true)).Should().NotContain([ActionFlow.TakeIt, ActionFlow.RecordUpdate],
            "nobody takes an external team's work, and Record an update is the primary");
        ActionGuide.More(Facts(ActionStatus.Done, canEditAll: true)).Should().Equal(
            [ActionFlow.RecordUpdate, ActionFlow.HandOff, ActionFlow.SplitOffPart, ActionFlow.Edit, ActionFlow.ChangeStatus],
            "a closed action isn't taken or split to teams, but a part can still be split off");
        ActionGuide.More(Facts(canEditAll: true, topLevel: false)).Should().NotContain([ActionFlow.SplitToTeams, ActionFlow.SplitOffPart], "a part isn't split");
        ActionGuide.More(Facts(canWork: false)).Should().BeEmpty("a read-only viewer has nothing under More");
        ActionGuide.More(Facts(canWork: false, canEditAll: true)).Should().Equal(ActionFlow.SplitToTeams, ActionFlow.SplitOffPart);

        ActionGuide.CanTake(Facts(ActionStatus.Open)).Should().BeTrue();
        ActionGuide.CanTake(Facts(ActionStatus.Done)).Should().BeFalse();
        Enum.GetValues<ActionFlow>().Where(f => f != ActionFlow.None)
            .Should().OnlyContain(f => ActionGuide.Label(f).Length > 0 && ActionGuide.Icon(f).StartsWith("bi-"), "every flow has words and an icon");
        new[] { ActionFlow.HandOff, ActionFlow.SplitToTeams, ActionFlow.SplitOffPart, ActionFlow.Edit, ActionFlow.ChangeStatus, ActionFlow.Comment }
            .Select(ActionGuide.Label).Should().Equal("Hand off…", "Split to teams…", "Split off a part", "Edit", "Change status…", "Comment");
    }

    [Fact]
    public void TheAddress_NamesAFlowOrToolByItsKey_InAnyCase_AndNonsenseNamesNothing()
    {
        ActionGuide.Key(ActionFlow.RecordUpdate).Should().Be("recordupdate");
        ActionGuide.Parse<ActionFlow>("recordupdate").Should().Be(ActionFlow.RecordUpdate);
        ActionGuide.Parse<ActionFlow>(" RecordUpdate ").Should().Be(ActionFlow.RecordUpdate);
        ActionGuide.Parse<ActionFlow>("3").Should().BeNull("a number isn't a name");
        ActionGuide.Parse<ActionFlow>("nonsense").Should().BeNull();
        ActionGuide.Parse<ActionFlow>(null).Should().BeNull();
        ActionGuide.Parse<ActionFlow>("  ").Should().BeNull();
    }

    [Fact]
    public void TheNextLine_NamesTheLatestOverduePart_WithHowManyMore_AndLinksToChaseAndRecordTheirReply()
    {
        var parts = new[]
        {
            Part(13, "Network Engineering", ActionStatus.Done, Mon),
            Part(14, "Hosting provider", ActionStatus.Tracking, Mon.AddDays(1)),
            Part(15, "Branch IT", ActionStatus.Open, Mon.AddDays(-1)),
        };
        var next = ActionGuide.Next(On(ActionStatus.Tracking, Fri, parts: parts, escalated: Wed))!;
        next.Kind.Should().Be(NextKind.PartLate, "the part that's late comes before the escalation it caused");
        next.Text.Should().Be("Branch IT is 3 days late on A-15. 1 more part overdue.");
        next.Links.Should().Equal(new NextLink(ActionFlow.None, "Chase them", 15), new NextLink(ActionFlow.RecordUpdate, "Record their reply", 15));

        ActionGuide.Next(On(ActionStatus.Tracking, Fri, parts: [parts[0], parts[1]]))!.Text.Should().Be("Hosting provider is 1 day late on A-14.");
    }

    [Fact]
    public void AnEmailPastItsFollowUp_AsksForTheReply_ButNotBefore_OrWithTheReminderOff()
    {
        var waiting = new WaitingEmail("psirt@vendor.example", Mon, Wed);
        var next = ActionGuide.Next(On(ActionStatus.Open, Fri, waiting: waiting))!;
        next.Kind.Should().Be(NextKind.NoReply);
        next.Text.Should().Be("No reply from psirt@vendor.example since Nov 23; its follow-up was due Nov 25.");
        next.Links.Should().Equal(new NextLink(ActionFlow.RecordReply, "Record the reply"), new NextLink(ActionFlow.WriteEmail, "Write a follow-up"));

        ActionGuide.Next(On(ActionStatus.Open, Fri, waiting: waiting with { FollowUpOn = Fri })).Should().BeNull();
        ActionGuide.Next(On(ActionStatus.Open, Fri, waiting: waiting with { FollowUpOn = null })).Should().BeNull();
    }

    [Fact]
    public void Escalated_ThenBlocked_ThenOverdue_EachWithTheirLinks()
    {
        var escalated = ActionGuide.Next(On(ActionStatus.Blocked, Mon, escalated: Wed))!;
        escalated.Kind.Should().Be(NextKind.Escalated);
        escalated.Text.Should().Be("Escalated on Nov 25: 2 days late.");
        escalated.Links.Select(l => l.Flow).Should().Equal(ActionFlow.RecordUpdate, ActionFlow.HandOff);
        escalated.Links.Should().OnlyContain(l => l.On == null, "they're this page's own");
        ActionGuide.Next(On(ActionStatus.Open, Fri, escalated: Wed))!.Text.Should().Be("Escalated on Nov 25: due Nov 27.");
        ActionGuide.Next(On(ActionStatus.Open, null, escalated: Wed))!.Text.Should().Be("Escalated on Nov 25: no due date.");

        var blocked = ActionGuide.Next(On(ActionStatus.Blocked, Mon))!;
        blocked.Kind.Should().Be(NextKind.Blocked, "blocked says more than late");
        blocked.Text.Should().Be("It's blocked.");
        blocked.Links.Select(l => l.Flow).Should().Equal(ActionFlow.Comment, ActionFlow.HandOff);

        var overdue = ActionGuide.Next(On(ActionStatus.InProgress, Mon))!;
        overdue.Kind.Should().Be(NextKind.Overdue);
        overdue.Text.Should().Be("It's 2 days late: it was due Nov 23.");
        overdue.Links.Should().Equal(new NextLink(ActionFlow.RecordUpdate, "Record an update"), new NextLink(ActionFlow.Edit, "Move the due date"));
    }

    [Fact]
    public void NothingNeedsDoing_OnTrack_Closed_OrForAViewerWhoCantWorkIt()
    {
        ActionGuide.Next(On(ActionStatus.InProgress, Fri)).Should().BeNull();
        ActionGuide.Next(On(ActionStatus.InProgress, Wed)).Should().BeNull("due today isn't late yet");
        ActionGuide.Next(On(ActionStatus.Done, Mon)).Should().BeNull();
        ActionGuide.Next(On(ActionStatus.Blocked, Mon, canWork: false)).Should().BeNull();
        ActionGuide.Next(new NextFacts(ActionStatus.InProgress, SlaState.OnTrack, Mon, Wed, true)).Should().BeNull("late is the SLA's Overdue, not the dates");
    }

    [Fact]
    public void ASweepOrHunt_SaysNobodyHasIt_ThenToRunItsQuery_ThenToRecordWhatItFound()
    {
        NextStep? Next(HuntFacts hunt, ActionStatus status = ActionStatus.Open, DateOnly? due = null, bool canWork = true) =>
            ActionGuide.Next(On(status, due ?? Fri, canWork) with { Hunt = hunt });

        var unowned = Next(new HuntFacts(Takeable: true, Mine: false, OnAction: false, HasIndicators: true))!;
        unowned.Kind.Should().Be(NextKind.Unowned);
        unowned.Text.Should().Be("Nobody has it yet.");
        unowned.Links.Should().Equal(new NextLink(ActionFlow.TakeIt, "Take it"));
        // A query kept on it and nobody to run it: both are said.
        var kept = Next(new HuntFacts(Takeable: true, Mine: false, OnAction: true))!;
        (kept.Kind, kept.Text).Should().Be((NextKind.Unowned, "Nobody has it yet. Take it, then run the query that's on the action."));
        kept.Links.Should().Equal(new NextLink(ActionFlow.TakeIt, "Take it"));

        var mine = Next(new HuntFacts(false, Mine: true, OnAction: false, HasIndicators: true))!;
        (mine.Kind, mine.Text).Should().Be((NextKind.RunQuery, "Copy the query and run it in your SIEM."));
        mine.Links.Should().BeEmpty("Copy is on the query, just below");
        Next(new HuntFacts(false, Mine: true, OnAction: false))!.Text.Should().Be("Add indicators or write a query by hand, then run it in your SIEM.");

        // A saved query with no result yet is a stored fact: the line reads the same after a reload.
        var saved = Next(new HuntFacts(false, Mine: true, OnAction: true, HasIndicators: true))!;
        (saved.Kind, saved.Text).Should().Be((NextKind.AwaitingResult, "Run the query that's on the action, then record what it found."));

        Next(new HuntFacts(false, Mine: true, OnAction: true, HasResult: true)).Should().BeNull("a result is recorded: the query isn't what's next");
        Next(new HuntFacts(false, Mine: false, OnAction: true)).Should().BeNull("a colleague has it");
        Next(new HuntFacts(true, false, true), canWork: false).Should().BeNull();
        Next(new HuntFacts(true, false, true), ActionStatus.Done).Should().BeNull();
        Next(new HuntFacts(true, false, true), ActionStatus.Blocked)!.Kind.Should().Be(NextKind.Blocked, "what's wrong comes before what's routine");
        Next(new HuntFacts(false, true, true), due: Mon)!.Kind.Should().Be(NextKind.Overdue);
        ActionGuide.Next(On(ActionStatus.Open, Fri)).Should().BeNull("another kind of action has no hunt to speak of");
    }

    [Fact]
    public void OnlyASweepOrAHunt_IsAHunt()
    {
        foreach (var kind in Enum.GetValues<ActionKind>())
            ActionGuide.IsHunt(kind).Should().Be(kind is ActionKind.IocSweep or ActionKind.ThreatHunt);
    }

    [Fact]
    public void TheDrawer_HasTheChecklistAlways_AndEachOtherToolWhereItBelongs()
    {
        ActionGuide.Tools(ActionKind.Block, false, 0, 0).Should().Equal(ActionTool.Checklist);
        ActionGuide.Tools(ActionKind.PatchRemediate, true, 0, 0).Should().Equal(ActionTool.Checklist, ActionTool.Timeline);
        ActionGuide.Tools(ActionKind.IocSweep, false, 0, 0).Should().Equal(ActionTool.Checklist, ActionTool.Hunt);
        ActionGuide.Tools(ActionKind.Block, false, 2, 0).Should().Equal([ActionTool.Checklist, ActionTool.Hunt], "it has hunts, whatever its kind");
        ActionGuide.Tools(ActionKind.Outreach, false, 0, 0).Should().Equal(ActionTool.Checklist, ActionTool.Outreach);
        ActionGuide.Tools(ActionKind.Other, true, 1, 1).Should().Equal(ActionTool.Checklist, ActionTool.Timeline, ActionTool.Hunt, ActionTool.Outreach);
        Enum.GetValues<ActionTool>().Select(t => ActionGuide.Key(t)).Should().Equal("checklist", "timeline", "hunt", "outreach");
        Enum.GetValues<ActionTool>().Select(ActionGuide.Name).Should().Equal("Checklist", "Timeline", "Hunt queries", "Outreach");
    }

    [Fact]
    public void OnlyAParentWithADatedPart_HasATimeline()
    {
        LineageNode Node(Guid id, DateOnly? due) => new(id, 1, "x", "Team", false, ActionStatus.Open, due, null, Mon, LineageState.OnTrack);
        var parent = Guid.NewGuid();
        var dated = new ActionLineage(null, Node(parent, null), [Node(Guid.NewGuid(), Fri)], parent, "");
        var undated = dated with { Parts = [Node(Guid.NewGuid(), null)] };
        ActionGuide.HasTimeline(dated, parent).Should().BeTrue();
        ActionGuide.HasTimeline(undated, parent).Should().BeFalse("no part has a due date");
        ActionGuide.HasTimeline(dated, dated.Parts[0].Id).Should().BeFalse("a part's page shows its parent's lineage, not a timeline");
        ActionGuide.HasTimeline(null, parent).Should().BeFalse();
    }

    [Fact]
    public void TheDrawer_OpensOnTheToolAsked_ElseTheKindsOwn_ElseTheChecklist()
    {
        var all = ActionGuide.Tools(ActionKind.Other, true, 1, 1);
        ActionGuide.Opening(null, ActionKind.Outreach, ActionGuide.Tools(ActionKind.Outreach, false, 0, 0)).Should().Be(ActionTool.Outreach);
        ActionGuide.Opening(null, ActionKind.IocSweep, ActionGuide.Tools(ActionKind.IocSweep, false, 0, 0)).Should().Be(ActionTool.Hunt);
        ActionGuide.Opening(null, ActionKind.ThreatHunt, ActionGuide.Tools(ActionKind.ThreatHunt, true, 0, 0)).Should().Be(ActionTool.Hunt);
        ActionGuide.Opening(null, ActionKind.PatchRemediate, ActionGuide.Tools(ActionKind.PatchRemediate, true, 0, 0)).Should().Be(ActionTool.Checklist);
        ActionGuide.Opening(" Timeline ", ActionKind.Other, all).Should().Be(ActionTool.Timeline, "the address's case and spaces don't matter");
        ActionGuide.Opening("timeline", ActionKind.IocSweep, ActionGuide.Tools(ActionKind.IocSweep, false, 0, 0)).Should().Be(ActionTool.Hunt,
            "a tool it hasn't got falls back to the kind's own");
        ActionGuide.Opening("nonsense", ActionKind.Block, all).Should().Be(ActionTool.Checklist);
        ActionGuide.Opening(null, ActionKind.Outreach, [ActionTool.Checklist]).Should().Be(ActionTool.Checklist);
    }

    private static ActionComment Comment(DateTimeOffset at, string text = "") => new() { Id = Guid.NewGuid(), AtUtc = at, Text = text };

    private static ActionEvent Line(DateTimeOffset at, string text = "") => new() { Id = Guid.NewGuid(), AtUtc = at, Text = text };

    [Fact]
    public void TheFeed_MergesCommentsAndHistory_OldestFirst_TheLineBeforeTheCommentSavedWithIt()
    {
        var t = new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
        ActionComment first = Comment(t), reply = Comment(t.AddHours(2), "They patched it");
        ActionEvent made = Line(t.AddMinutes(-5), "Created."), update = Line(t.AddHours(2), "Update from Branch IT"), late = Line(t.AddHours(3));

        var feed = ActionFeed.Of([reply, first], [late, update, made]);
        feed.Select(e => (object?)e.Comment ?? e.Event).Should().Equal(made, first, update, reply, late);
        feed.Select(e => e.IsComment).Should().Equal(false, true, false, true, false);
        feed[0].At.Should().Be(made.AtUtc);
    }

    [Fact]
    public void TheFeed_ShowsTheLatestTen_FoldsTheRest_AndNeverFoldsAwayACommentBeingEdited()
    {
        var t = new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
        var comments = Enumerable.Range(0, 14).Select(i => Comment(t.AddMinutes(i))).ToList();
        var feed = ActionFeed.Of(comments, []);

        var window = ActionFeed.Window(feed, all: false);
        window.Earlier.Should().Be(4);
        window.Shown.Should().HaveCount(ActionFeed.Latest).And.Equal(feed.Skip(4));

        ActionFeed.Window(feed, all: true).Should().BeEquivalentTo(new FeedWindow(feed, 0));
        ActionFeed.Window(feed, all: false, keep: comments[2].Id).Earlier.Should().Be(2, "the edit stays open on the page");
        ActionFeed.Window(feed, all: false, keep: comments[12].Id).Earlier.Should().Be(4);
        ActionFeed.Window(feed, all: false, keep: Guid.NewGuid()).Earlier.Should().Be(4, "a comment that's gone keeps nothing");
        ActionFeed.Window(feed.Take(3).ToList(), all: false).Should().BeEquivalentTo(new FeedWindow(feed.Take(3).ToList(), 0));
    }
}
