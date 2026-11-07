using FluentAssertions;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>The action page's first things, as pure rules: its primary button, its Next line, the tools in its drawer and
/// the one it opens on, and its "What's happened" feed.</summary>
public class ActionGuideTests
{
    private static readonly DateOnly Mon = new(2026, 11, 23), Wed = new(2026, 11, 25), Fri = new(2026, 11, 27);

    private static PrimaryFacts Facts(ActionStatus status = ActionStatus.InProgress, ActionKind kind = ActionKind.Block, bool canWork = true,
        bool assigned = true, bool external = false, int openParts = 0, int emails = 0, bool waiting = false) =>
        new(status, kind, canWork, assigned, external, openParts, emails, waiting);

    private static LineageNode Part(int n, string team, ActionStatus status, DateOnly? due) =>
        new(Guid.NewGuid(), n, "Part " + n, team, false, status, due, null, Mon, LineageRules.StateOf(status, due, Wed));

    [Fact]
    public void ThePrimary_IsNone_ForAClosedAction_OrAViewerWhoCantWorkIt()
    {
        ActionGuide.Primary(Facts(canWork: false, assigned: false)).Should().Be(PrimaryStep.None, "a read-only viewer gets no button they can't use");
        foreach (var closed in ActionItem.ClosedStatuses)
            ActionGuide.Primary(Facts(closed, ActionKind.Outreach)).Should().Be(PrimaryStep.None);
        ActionGuide.Label(PrimaryStep.None).Should().BeEmpty();
    }

    [Fact]
    public void AnOutreachAction_WritesItsFirstEmail_ThenRecordsTheReply_WhateverElseApplies()
    {
        ActionGuide.Primary(Facts(ActionStatus.Open, ActionKind.Outreach, assigned: false)).Should().Be(PrimaryStep.WriteEmail, "before taking it");
        ActionGuide.Primary(Facts(ActionStatus.Tracking, ActionKind.Outreach, emails: 1, waiting: true)).Should().Be(PrimaryStep.RecordReply);
        ActionGuide.Primary(Facts(ActionStatus.InProgress, ActionKind.Outreach, emails: 2)).Should().Be(PrimaryStep.CloseWithOutcome,
            "every email answered: what's left is to close it");
        ActionGuide.Label(PrimaryStep.WriteEmail).Should().Be("Write the email");
        ActionGuide.Label(PrimaryStep.RecordReply).Should().Be("Record the reply");
    }

    [Fact]
    public void WorkFollowedRatherThanDone_RecordsAnUpdate()
    {
        ActionGuide.Primary(Facts(ActionStatus.Tracking)).Should().Be(PrimaryStep.RecordUpdate);
        ActionGuide.Primary(Facts(ActionStatus.Open, assigned: false, external: true)).Should().Be(PrimaryStep.RecordUpdate,
            "an external team has no members, so nobody takes it");
        ActionGuide.Primary(Facts(ActionStatus.InProgress, openParts: 2)).Should().Be(PrimaryStep.RecordUpdate);
        ActionGuide.Label(PrimaryStep.RecordUpdate).Should().Be("Record an update");
    }

    [Fact]
    public void AnOpenActionNobodyHas_IsTaken_OtherwiseTheStatusMoveThatFits()
    {
        ActionGuide.Primary(Facts(ActionStatus.Open, assigned: false)).Should().Be(PrimaryStep.TakeIt);
        ActionGuide.Primary(Facts(ActionStatus.Blocked, assigned: false)).Should().Be(PrimaryStep.TakeIt);
        ActionGuide.Primary(Facts(ActionStatus.Open)).Should().Be(PrimaryStep.StartWork);
        ActionGuide.Primary(Facts(ActionStatus.Blocked)).Should().Be(PrimaryStep.Unblock);
        ActionGuide.Primary(Facts(ActionStatus.InProgress)).Should().Be(PrimaryStep.CloseWithOutcome);
        new[] { PrimaryStep.TakeIt, PrimaryStep.StartWork, PrimaryStep.Unblock, PrimaryStep.CloseWithOutcome }.Select(ActionGuide.Label)
            .Should().Equal("Take it", "Start work", "Unblock", "Close with outcome");
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
        var next = ActionGuide.Next(new NextFacts(ActionStatus.Tracking, Fri, Wed, true, parts, EscalatedOn: Wed))!;
        next.Kind.Should().Be(NextKind.PartLate, "the part that's late comes before the escalation it caused");
        next.Text.Should().Be("Branch IT is 3 days late on A-15. 1 more part overdue.");
        next.Part.Should().Be(15);
        next.Links.Should().Equal((NextLink.ChasePart, "Chase them"), (NextLink.PartReply, "Record their reply"));

        ActionGuide.Next(new NextFacts(ActionStatus.Tracking, Fri, Wed, true, [parts[0], parts[1]]))!.Text
            .Should().Be("Hosting provider is 1 day late on A-14.");
    }

    [Fact]
    public void AnEmailPastItsFollowUp_AsksForTheReply_ButNotBefore_OrWithTheReminderOff()
    {
        var waiting = new WaitingEmail("psirt@vendor.example", Mon, Wed);
        var next = ActionGuide.Next(new NextFacts(ActionStatus.Open, Fri, Wed, true, Waiting: waiting))!;
        next.Kind.Should().Be(NextKind.NoReply);
        next.Text.Should().Be("No reply from psirt@vendor.example since Nov 23; its follow-up was due Nov 25.");
        next.Links.Select(l => l.Link).Should().Equal(NextLink.RecordReply, NextLink.WriteEmail);

        ActionGuide.Next(new NextFacts(ActionStatus.Open, Fri, Wed, true, Waiting: waiting with { FollowUpOn = Fri })).Should().BeNull();
        ActionGuide.Next(new NextFacts(ActionStatus.Open, Fri, Wed, true, Waiting: waiting with { FollowUpOn = null })).Should().BeNull();
    }

    [Fact]
    public void Escalated_ThenBlocked_ThenOverdue_EachWithTheirLinks()
    {
        var escalated = ActionGuide.Next(new NextFacts(ActionStatus.Blocked, Mon, Wed, true, EscalatedOn: Wed))!;
        escalated.Kind.Should().Be(NextKind.Escalated);
        escalated.Text.Should().Be("Escalated on Nov 25: 2 days late.");
        escalated.Links.Select(l => l.Link).Should().Equal(NextLink.RecordUpdate, NextLink.HandOff);
        ActionGuide.Next(new NextFacts(ActionStatus.Open, Fri, Wed, true, EscalatedOn: Wed))!.Text.Should().Be("Escalated on Nov 25: due Nov 27.");
        ActionGuide.Next(new NextFacts(ActionStatus.Open, null, Wed, true, EscalatedOn: Wed))!.Text.Should().Be("Escalated on Nov 25: no due date.");

        var blocked = ActionGuide.Next(new NextFacts(ActionStatus.Blocked, Mon, Wed, true))!;
        blocked.Kind.Should().Be(NextKind.Blocked, "blocked says more than late");
        blocked.Text.Should().Be("It's blocked.");
        blocked.Links.Select(l => l.Link).Should().Equal(NextLink.Comment, NextLink.HandOff);

        var overdue = ActionGuide.Next(new NextFacts(ActionStatus.InProgress, Mon, Wed, true))!;
        overdue.Kind.Should().Be(NextKind.Overdue);
        overdue.Text.Should().Be("It's 2 days late: it was due Nov 23.");
        overdue.Links.Should().Equal((NextLink.RecordUpdate, "Record an update"), (NextLink.Edit, "Move the due date"));
    }

    [Fact]
    public void NothingNeedsDoing_OnTrack_Closed_OrForAViewerWhoCantWorkIt()
    {
        ActionGuide.Next(new NextFacts(ActionStatus.InProgress, Fri, Wed, true)).Should().BeNull();
        ActionGuide.Next(new NextFacts(ActionStatus.InProgress, Wed, Wed, true)).Should().BeNull("due today isn't late yet");
        ActionGuide.Next(new NextFacts(ActionStatus.Done, Mon, Wed, true)).Should().BeNull();
        ActionGuide.Next(new NextFacts(ActionStatus.Blocked, Mon, Wed, false)).Should().BeNull();
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
        Enum.GetValues<ActionTool>().Select(ActionGuide.Key).Should().Equal("checklist", "timeline", "hunt", "outreach");
        Enum.GetValues<ActionTool>().Select(ActionGuide.Name).Should().Equal("Checklist", "Timeline", "Hunt queries", "Outreach");
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
