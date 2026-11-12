using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Files;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

public class ActionServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public ActionServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<Guid> Team(string name = "Cyber Defense") => await _db.Teams().AddAsync(name, null, null);

    [Fact]
    public async Task Outstanding_PutsP1AndP2First_ThenTeamsWithNoMembers_ThenTheLongestLate_AtMostEight()
    {
        var staffed = await Team("Vulnerability Management");
        await _db.Teams().AddMemberAsync(staffed, "S-1-5-21-OWNER", "Owner");
        var empty = await Team("IAM");
        var svc = _db.Actions();
        var today = svc.Today;
        await svc.CreateAsync(new ActionForm { Title = "old P3, staffed", TeamId = staffed, Priority = Priority.P3, DueDate = today.AddDays(-20) });
        await svc.CreateAsync(new ActionForm { Title = "P3 on IAM", TeamId = empty, Priority = Priority.P3, DueDate = today.AddDays(-2) });
        await svc.CreateAsync(new ActionForm { Title = "P1 late", TeamId = staffed, Priority = Priority.P1, DueDate = today.AddDays(-1) });
        await svc.CreateAsync(new ActionForm { Title = "not yet due", TeamId = staffed, Priority = Priority.P1, DueDate = today.AddDays(3) });
        for (var i = 0; i < 8; i++) await svc.CreateAsync(new ActionForm { Title = $"filler {i}", TeamId = staffed, Priority = Priority.P4, DueDate = today.AddDays(-1) });

        var open = await svc.OutstandingAsync(today, TlpLevel.AmberStrict);

        open.Overdue.Should().Be(11, "everything past due counts, shown or not");
        open.Rows.Should().HaveCount(ActionService.OutstandingSize);
        open.Rows.Select(o => o.Row.Title).Take(3).Should().Equal("P1 late", "P3 on IAM", "old P3, staffed");
        open.Rows[1].NoOneToOwn.Should().BeTrue();
        open.Rows[2].DaysLate.Should().Be(20);
        open.Rows.Should().NotContain(o => o.Row.Title == "not yet due");
        (await svc.TeamHasNoMembersAsync(empty)).Should().BeTrue();
        (await svc.TeamHasNoMembersAsync(staffed)).Should().BeFalse();
    }

    [Fact]
    public async Task Outstanding_AnActionSomeoneHolds_IsTheirs_EvenOnATeamWithNoMembers()
    {
        var empty = await Team("Fraud Operations");
        var svc = _db.Actions();
        var today = svc.Today;
        var held = await svc.CreateAsync(new ActionForm { Title = "Held on an empty team", TeamId = empty, DueDate = today.AddDays(-2) });
        await svc.CreateAsync(new ActionForm { Title = "Nobody's on an empty team", TeamId = empty, DueDate = today.AddDays(-2) });
        var form = ActionForm.From((await svc.GetAsync(held))!.Action);
        (form.AssigneeId, form.AssigneeName) = ("dev:sam", "Sam");
        await svc.SaveAsync(form);

        var rows = (await svc.OutstandingAsync(today, TlpLevel.AmberStrict)).Rows;
        rows.Single(o => o.Row.Title == "Held on an empty team").NoOneToOwn.Should().BeFalse("Sam holds it, as the action page says");
        rows.Single(o => o.Row.Title == "Nobody's on an empty team").NoOneToOwn.Should().BeTrue();
    }

    [Fact]
    public async Task Outstanding_LeavesOutAnActionRaisedFromAnArticleAboveTheCeiling_AndDoesntCountIt()
    {
        var team = await Team();
        var svc = _db.Actions();
        var today = svc.Today;
        var red = await _db.Articles().CreateAsync("Partner RED report", "Partner", null, TlpLevel.Red);
        var amber = await _db.Articles().CreateAsync("ISAC note", "ISAC", null, TlpLevel.Amber);
        await svc.CreateAsync(new ActionForm { Title = "Block 203.0.113.7", TeamId = team, ArticleId = red, DueDate = today.AddDays(-3) });
        await svc.CreateAsync(new ActionForm { Title = "Reset partner keys", TeamId = team, ArticleId = amber, DueDate = today.AddDays(-2) });
        await svc.CreateAsync(new ActionForm { Title = "Rotate the VPN cert", TeamId = team, DueDate = today.AddDays(-1) });

        var atTeam = await svc.OutstandingAsync(today, TlpLevel.AmberStrict);
        atTeam.Rows.Select(o => o.Row.Title).Should().BeEquivalentTo(new[] { "Reset partner keys", "Rotate the VPN cert" });
        atTeam.Overdue.Should().Be(2, "the withheld one isn't counted either: the count would tell the room it exists");
        atTeam.Marking.Should().Be(TlpLevel.Amber, "an action is marked at its article's TLP");

        var atRed = await svc.OutstandingAsync(today, TlpLevel.Red);
        atRed.Rows.Should().HaveCount(3).And.Contain(o => o.Row.Title == "Block 203.0.113.7" && o.Tlp == TlpLevel.Red);
        atRed.Marking.Should().Be(TlpLevel.Red);

        var atClear = await svc.OutstandingAsync(today, TlpLevel.Clear);
        atClear.Rows.Should().ContainSingle().Which.Row.Title.Should().Be("Rotate the VPN cert");
        atClear.Marking.Should().BeNull("an action raised from no article carries no marking of its own");
    }

    private async Task<Guid> ArticleWithIndicators()
    {
        var svc = _db.Articles();
        var id = await svc.CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await svc.GetAsync(id))!);
        form.Bluf = "Exploited.";
        form.Indicators = [new() { Type = IndicatorType.IpAddress, Value = "203.0.113.45" }, new() { Type = IndicatorType.Domain, Value = "bad.example.net" }];
        await svc.SaveAsync(form);
        return id;
    }

    [Fact]
    public async Task Create_TakesTheSlaDueDate_AndNumbersInOrder()
    {
        var team = await Team();
        var svc = _db.Actions();

        var first = await svc.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.Block, Priority = Priority.P1, TeamId = team });
        var second = await svc.CreateAsync(new ActionForm { Title = "Patch", Kind = ActionKind.PatchRemediate, Priority = Priority.P3, TeamId = team, DueDate = new DateOnly(2030, 1, 1) });

        second.Should().Be(first + 1);
        var a = (await svc.GetAsync(first))!.Action;
        a.DueDate.Should().Be(ActionSla.DueDate(_db.Clock.Today, 1), "P1 gets one business day by default");
        a.Events.Single().Text.Should().StartWith("Created for Cyber Defense");
        (await svc.GetAsync(second))!.Action.DueDate.Should().Be(new DateOnly(2030, 1, 1), "a date given wins over the SLA");
    }

    [Fact]
    public async Task Sla_AnAdministeredKindTargetWins()
    {
        await _db.Sla().SaveAsync(new Dictionary<Priority, int?>(), new Dictionary<ActionKind, int?> { [ActionKind.IocSweep] = 2 });
        (await _db.Actions().SuggestedDueAsync(Priority.P4, ActionKind.IocSweep)).Should().Be(ActionSla.DueDate(_db.Clock.Today, 2));
        (await _db.Actions().SuggestedDueAsync(Priority.P4, ActionKind.Block)).Should().Be(ActionSla.DueDate(_db.Clock.Today, 20));
    }

    [Fact]
    public async Task Status_DoneNeedsAnOutcome_AHuntNeedsWhatItFound_ReopenClears()
    {
        var team = await Team();
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team });
        var id = (await svc.GetAsync(n))!.Action.Id;

        var noOutcome = () => svc.SetStatusAsync(id, ActionStatus.Done, ActionOutcome.None, null);
        await noOutcome.Should().ThrowAsync<ArgumentException>();
        var wrongOutcome = () => svc.SetStatusAsync(id, ActionStatus.Done, ActionOutcome.Remediated, null);
        await wrongOutcome.Should().ThrowAsync<ArgumentException>().WithMessage("*no hits*");

        await svc.SetStatusAsync(id, ActionStatus.Done, ActionOutcome.NoHits, "30 days of proxy logs");
        var done = (await svc.GetAsync(n))!.Action;
        done.ClosedDate.Should().Be(_db.Clock.Today);
        done.OutcomeNote.Should().Be("30 days of proxy logs");
        done.Events.Select(e => e.Text).Should().Contain("Status: Open → Done (No hits): 30 days of proxy logs", "the note is in the history too");
        ActionSla.StateOf(done, _db.Clock.Today).Should().Be(SlaState.Met);

        await svc.SetStatusAsync(id, ActionStatus.InProgress, ActionOutcome.None, null);
        var reopened = (await svc.GetAsync(n))!.Action;
        reopened.ClosedDate.Should().BeNull();
        reopened.Outcome.Should().Be(ActionOutcome.None);
        reopened.Events.Select(e => e.Text).Should().Contain(t => t.StartsWith("Status: Done → In progress"));
    }

    [Fact]
    public async Task ALongOutcomeNote_IsClippedInTheHistory_AndKeptWholeOnTheAction()
    {
        var team = await Team();
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Patch", Kind = ActionKind.PatchRemediate, TeamId = team });
        var id = (await svc.GetAsync(n))!.Action.Id;
        var note = string.Concat(Enumerable.Repeat("Patched the gateways; ", 100))[..2000];

        // TestDb refuses a string longer than its column, as SQL Server does: this save failed there before.
        await svc.SetStatusAsync(id, ActionStatus.Done, ActionOutcome.Remediated, note);
        await svc.MoveAsync(id, ActionStatus.Done, ActionStatus.InProgress);
        await svc.MoveAsync(id, ActionStatus.InProgress, ActionStatus.Done, ActionOutcome.Remediated, note);

        var done = (await svc.GetAsync(n))!.Action;
        done.OutcomeNote.Should().Be(note, "the note itself is kept whole");
        var lines = done.Events.Where(e => e.Text.StartsWith("Status:", StringComparison.Ordinal) && e.Text.Contains("→ Done")).ToList();
        lines.Should().HaveCount(2).And.OnlyContain(e => e.Text.Length == ActionService.EventTextMax && e.Text.EndsWith('…'));
    }

    [Fact]
    public async Task TheTestDatabase_RefusesAStringLongerThanItsColumn_AsSqlServerDoes()
    {
        await using var db = _db.Context();
        db.ActionEvents.Add(new ActionEvent { ActionId = Guid.NewGuid(), ByName = "x", Text = new string('x', ActionService.EventTextMax + 1) });
        await FluentActions.Invoking(() => db.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*Text*1001*1000*");
    }

    [Fact]
    public async Task TheTestDatabase_IsTheMigratedOne_AndRefusesARowWhoseParentIsMissing()
    {
        // Each test's database is a copy of one migrated once per run (TestDb): it must be as if migrated here, foreign
        // keys enforced (a per-connection setting in SQLite, not one the copy carries) as SQL Server enforces them.
        await using var db = _db.Context();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await db.Database.GetAppliedMigrationsAsync()).Should().Equal(db.Database.GetMigrations());
        db.ActionEvents.Add(new ActionEvent { ActionId = Guid.NewGuid(), ByName = "x", Text = "x" });
        (await FluentActions.Invoking(() => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>()).WithInnerException<Microsoft.Data.Sqlite.SqliteException>().WithMessage("*FOREIGN KEY*");
    }

    [Fact]
    public async Task Permissions_TeamMembersWorkTheirTeamsActions_OthersCant()
    {
        var team = await Team();
        var other = await Team("Vulnerability Management");
        var svc = _db.Actions();
        var mine = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Ours", TeamId = team })))!.Action.Id;
        var theirs = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Theirs", TeamId = other })))!.Action.Id;
        await _db.Teams().AddMemberAsync(team, "S-1-5-21-OWNER", "Owner");

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        _db.User.UserId = "S-1-5-21-OWNER";
        await svc.TakeAsync(mine);
        await svc.AddCommentAsync(mine, "Looking now.");
        (await svc.ListAsync(new ActionFilter(ActionView.Mine))).Select(r => r.Title).Should().Equal("Ours");
        (await svc.ListAsync(new ActionFilter(ActionView.MyTeams))).Select(r => r.Title).Should().Equal("Ours");

        var foreign = () => svc.SetStatusAsync(theirs, ActionStatus.InProgress, ActionOutcome.None, null);
        await foreign.Should().ThrowAsync<ForbiddenException>();
        var create = () => svc.CreateAsync(new ActionForm { Title = "x", TeamId = team });
        await create.Should().ThrowAsync<ForbiddenException>("only analysts create actions");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var read = () => svc.ListAsync(new ActionFilter());
        await read.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Board_Move_ChangesTheStatus_RefusesAStaleColumn_AndClosingFollowsTheOutcomeRules()
    {
        var team = await Team();
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team });
        var id = (await svc.GetAsync(n))!.Action.Id;

        await svc.MoveAsync(id, ActionStatus.Open, ActionStatus.InProgress);
        var moved = (await svc.GetAsync(n))!.Action;
        moved.Status.Should().Be(ActionStatus.InProgress);
        moved.Events.Select(e => e.Text).Should().Contain("Status: Open → In progress.");

        var stale = () => svc.MoveAsync(id, ActionStatus.Open, ActionStatus.Blocked);
        await stale.Should().ThrowAsync<StaleEditException>("the board showed it in Open, but it's moved since");
        (await svc.GetAsync(n))!.Action.Status.Should().Be(ActionStatus.InProgress);

        var noOutcome = () => svc.MoveAsync(id, ActionStatus.InProgress, ActionStatus.Done);
        await noOutcome.Should().ThrowAsync<ArgumentException>();
        var wrongOutcome = () => svc.MoveAsync(id, ActionStatus.InProgress, ActionStatus.Done, ActionOutcome.Remediated);
        await wrongOutcome.Should().ThrowAsync<ArgumentException>().WithMessage("*no hits*");
        ActionService.OutcomesFor(ActionKind.IocSweep, ActionStatus.Done).Should().Equal(ActionOutcome.NoHits, ActionOutcome.HitsEscalated, ActionOutcome.Other);

        await svc.MoveAsync(id, ActionStatus.InProgress, ActionStatus.Done, ActionOutcome.NoHits, "Clean");
        var done = (await svc.GetAsync(n))!.Action;
        done.Outcome.Should().Be(ActionOutcome.NoHits);
        done.ClosedDate.Should().Be(_db.Clock.Today);

        var counts = await svc.ViewCountsAsync(new ActionFilter(ActionView.Overdue), closedDays: ActionService.BoardClosedDays);
        counts.ClosedAs(ActionStatus.Done).Should().Be(1, "a recently closed action is counted under the board, and Overdue shows all there");
        (await svc.BoardAsync(new ActionFilter(ActionView.Overdue))).Cards.Should().BeEmpty("it isn't drawn");
        var open = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Another", Kind = ActionKind.Block, TeamId = team })))!.Action.Number;
        var board = await svc.BoardAsync(new ActionFilter(ActionView.Overdue));
        board.Cards.Single().Number.Should().Be(open);
        board.CanMove(board.Cards[0]).Should().BeTrue("an analyst works every action");

        await svc.MoveAsync(id, ActionStatus.Done, ActionStatus.Cancelled);
        (await svc.GetAsync(n))!.Action.Outcome.Should().Be(ActionOutcome.None, "cancelling needs no outcome");
    }

    [Fact]
    public async Task Board_Move_IsOnlyForAnalystsAndTheOwningTeam()
    {
        var team = await Team();
        var other = await Team("Vulnerability Management");
        var svc = _db.Actions();
        var mine = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Ours", TeamId = team })))!.Action.Id;
        var theirs = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Theirs", TeamId = other })))!.Action.Id;
        await _db.Teams().AddMemberAsync(team, "S-1-5-21-OWNER", "Owner");

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        _db.User.UserId = "S-1-5-21-OWNER";
        var board = await svc.BoardAsync(new ActionFilter());
        board.Cards.Where(board.CanMove).Select(c => c.Title).Should().Equal("Ours");

        await svc.MoveAsync(mine, ActionStatus.Open, ActionStatus.Blocked);
        (await svc.GetAsync((await svc.RowAsync(mine))!.Number))!.Action.Status.Should().Be(ActionStatus.Blocked);
        var foreign = () => svc.MoveAsync(theirs, ActionStatus.Open, ActionStatus.InProgress);
        await foreign.Should().ThrowAsync<ForbiddenException>();
        (await svc.RowAsync(theirs))!.Status.Should().Be(ActionStatus.Open);

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var reader = () => svc.MoveAsync(mine, ActionStatus.Blocked, ActionStatus.Open);
        await reader.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Save_FromAStaleCopy_IsRefused_AndChangesAreInTheHistory()
    {
        var team = await Team();
        var other = await Team("Vulnerability Management");
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Sweep", TeamId = team });
        var form = ActionForm.From((await svc.GetAsync(n))!.Action);
        var stale = ActionForm.From((await svc.GetAsync(n))!.Action);

        form.TeamId = other;
        form.Priority = Priority.P1;
        await svc.SaveAsync(form);
        // Another team in the editor is a hand-off (EP-86): its own line, beside the other changes'.
        var history = (await svc.GetAsync(n))!.Action.Events.Select(e => e.Text).ToList();
        history.Should().Contain(t => t.Contains("priority: P3 → P1"))
            .And.Contain($"Handed from Cyber Defense to Vulnerability Management: {ActionService.EditorHandoffReason}");

        stale.Title = "Something else";
        var act = () => svc.SaveAsync(stale);
        await act.Should().ThrowAsync<StaleEditException>();
    }

    [Fact]
    public async Task Create_ForAPastDaysBrief_RecordsThatDay_ButTheSlaStartsToday()
    {
        var team = await Team();
        var svc = _db.Actions();
        var today = _db.Clock.Today;
        var pastDay = WorkingDays.Weekdays.Previous(WorkingDays.Weekdays.Previous(today));
        var plain = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Now", Kind = ActionKind.Block, Priority = Priority.P2, TeamId = team })))!.Action;

        var n = await svc.QuickAddAsync(await ArticleWithIndicators(), "Block the C2", ActionKind.Block, team, null, raisedOn: pastDay);

        var a = (await svc.GetAsync(n))!.Action;
        a.RaisedOn.Should().Be(pastDay);
        a.DueDate.Should().Be(plain.DueDate, "the SLA runs from when it was recorded, so it isn't overdue at once");
        a.Events.Should().Contain(e => e.Text.Contains("raised for"));
        var future = () => svc.CreateAsync(new ActionForm { Title = "Later", TeamId = team, RaisedOn = today.AddDays(3) });
        await future.Should().ThrowAsync<ArgumentException>();
        (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Today", TeamId = team, RaisedOn = today })))!.Action.RaisedOn
            .Should().BeNull("today is the default");
    }

    [Fact]
    public async Task Parts_SplitOneLevelDeep()
    {
        var team = await Team();
        var svc = _db.Actions();
        var parent = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Remediate", Kind = ActionKind.PatchRemediate, TeamId = team })))!.Action;
        var childN = await svc.CreateAsync(new ActionForm { Title = "Patch servers", TeamId = team, ParentId = parent.Id });
        var child = (await svc.GetAsync(childN))!;
        child.Parent!.Number.Should().Be(parent.Number);
        (await svc.GetAsync(parent.Number))!.Children.Select(c => c.Number).Should().Equal(childN);

        var grandchild = () => svc.CreateAsync(new ActionForm { Title = "Too deep", TeamId = team, ParentId = child.Action.Id });
        await grandchild.Should().ThrowAsync<ArgumentException>().WithMessage("*one level*");

        // Nor can an action with parts become a part.
        var other = await svc.CreateAsync(new ActionForm { Title = "Another", TeamId = team });
        var form = ActionForm.From((await svc.GetAsync(parent.Number))!.Action);
        form.ParentId = (await svc.GetAsync(other))!.Action.Id;
        var nest = () => svc.SaveAsync(form);
        await nest.Should().ThrowAsync<ArgumentException>().WithMessage("*already has parts*");
    }

    [Fact]
    public async Task Hunt_FromTheStory_UsesEveryArticlesIndicators_AndLooksBackFurtherForAnOldStory()
    {
        var team = await Team();
        var articles = _db.Articles();
        // The action hangs off the first report, which has no indicators; a later one in the story has them.
        var first = await articles.CreateAsync("PHP2Shell disclosed", "Vendor", null, TlpLevel.Clear);
        var later = await ArticleWithIndicators();
        var story = await _db.Stories().CreateAsync("PHP2Shell", null);
        await _db.Stories().LinkAsync(first, story);
        await _db.Stories().LinkAsync(later, story);
        await using (var ctx = _db.Context())
        {
            (await ctx.Articles.SingleAsync(x => x.Id == first)).PublishedAtUtc = DateTimeOffset.UtcNow.AddDays(-60);
            await ctx.SaveChangesAsync();
        }
        var svc = _db.Actions();
        var action = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Hunt", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = first })))!.Action;

        (await svc.HuntSourcesAsync(action.Id) with { Own = [], Story = [], Days = HuntQueryBuilder.DefaultDays, StoryDays = HuntQueryBuilder.DefaultDays }).Should().Be(new HuntSources(0, 2, 2));
        var own = () => svc.GenerateHuntAsync(action.Id, "kql");
        await own.Should().ThrowAsync<InvalidOperationException>().WithMessage("*build it from the story*");

        var hunt = await svc.GenerateHuntAsync(action.Id, "kql", fromStory: true);

        hunt.Text.Should().Contain("\"203.0.113.45\"").And.Contain("ago(67d)", "a week before the story's first report, 60 days ago");
        (await svc.GetAsync(action.Number))!.Action.Events.Should().Contain(e => e.Text.Contains("from the story's 2 articles"));
    }

    [Fact]
    public async Task Comments_AnOwnerOnAnotherTeam_CanComment_AndEditOrDeleteTheirOwn_ButNotAttachOrWork()
    {
        var other = await Team("Vulnerability Management");
        var svc = _db.Actions();
        var action = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Patch the gateways", TeamId = other })))!.Action;

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        _db.User.UserId = "S-1-5-21-OWNER";
        _db.User.DisplayName = "Owner Elsewhere";

        var detail = (await svc.GetAsync(action.Number))!;
        detail.CanWork.Should().BeFalse();
        detail.CanComment.Should().BeTrue("anyone who reads the team edition may comment (ADR 0008, amended)");

        await svc.AddCommentAsync(action.Id, "We saw this on two SOC hosts too.");
        var comment = (await svc.GetAsync(action.Number))!.Action.Comments.Single();
        comment.AuthorName.Should().Be("Owner Elsewhere");

        await svc.EditCommentAsync(comment.Id, "We saw this on three SOC hosts too.");
        (await svc.GetAsync(action.Number))!.Action.Comments.Single().Text.Should().Be("We saw this on three SOC hosts too.");

        var attach = () => svc.AddCommentAsync(action.Id, "Evidence", [new ScreenshotUpload("hosts.png", TestDb.Png)]);
        await attach.Should().ThrowAsync<ForbiddenException>("files on a comment stay with whoever works the action");
        var status = () => svc.SetStatusAsync(action.Id, ActionStatus.InProgress, ActionOutcome.None, null);
        await status.Should().ThrowAsync<ForbiddenException>("status stays with the owning team");

        await svc.DeleteCommentAsync(comment.Id);
        (await svc.GetAsync(action.Number))!.Action.Comments.Should().BeEmpty();
    }

    [Fact]
    public async Task Comments_SomeoneElsesComment_StaysTheirs_ForAnOwnerOnAnotherTeam()
    {
        var other = await Team("Vulnerability Management");
        var svc = _db.Actions();
        var action = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Patch", TeamId = other })))!.Action;
        await svc.AddCommentAsync(action.Id, "Analyst's note.");
        var comment = (await svc.GetAsync(action.Number))!.Action.Comments.Single();

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        _db.User.UserId = "S-1-5-21-OWNER";
        var edit = () => svc.EditCommentAsync(comment.Id, "Changed.");
        await edit.Should().ThrowAsync<ForbiddenException>();
        var delete = () => svc.DeleteCommentAsync(comment.Id);
        await delete.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Comments_TheOwningTeamStillAttaches_AndAReaderCantComment()
    {
        var team = await Team();
        var svc = _db.Actions();
        var action = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Sweep", TeamId = team })))!.Action;
        await _db.Teams().AddMemberAsync(team, "S-1-5-21-OWNER", "Owner");

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        _db.User.UserId = "S-1-5-21-OWNER";
        await svc.AddCommentAsync(action.Id, "Proxy hits", [new ScreenshotUpload("hits.png", TestDb.Png)]);
        (await svc.GetAsync(action.Number))!.Action.Comments.Single().Files.Should().ContainSingle();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        _db.User.UserId = "S-1-5-21-READER";
        var comment = () => svc.AddCommentAsync(action.Id, "Me too.");
        await comment.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Comments_CanBeEditedAndDeleted_ByTheirAuthorOnly_AndTheHistorySaysSo()
    {
        var team = await Team();
        var svc = _db.Actions();
        var action = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Patch", TeamId = team })))!.Action;
        await svc.AddCommentAsync(action.Id, "Pached the gateways.");
        var comment = (await svc.GetAsync(action.Number))!.Action.Comments.Single();

        await svc.EditCommentAsync(comment.Id, "Patched the gateways.");

        var after = (await svc.GetAsync(action.Number))!.Action;
        after.Comments.Single().Text.Should().Be("Patched the gateways.");
        after.Comments.Single().EditedAtUtc.Should().NotBeNull();
        after.Events.Should().Contain(e => e.Text.Contains("edited a comment"));

        _db.User.UserId = "S-someone-else";
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        var notTheirs = () => svc.DeleteCommentAsync(comment.Id);
        await notTheirs.Should().ThrowAsync<ForbiddenException>();

        _db.User.UserId = "S-1-5-21-TEST-1";
        await svc.DeleteCommentAsync(comment.Id);
        var gone = (await svc.GetAsync(action.Number))!.Action;
        gone.Comments.Should().BeEmpty();
        gone.Events.Should().Contain(e => e.Text.Contains("deleted a comment"));
    }

    [Fact]
    public async Task LikelyDuplicates_AreOpenActionsOfTheSameKindAndTeam_AnywhereInTheStory()
    {
        var team = await Team();
        var other = await _db.Teams().AddAsync("Vulnerability Management", null, null);
        var first = await ArticleWithIndicators();
        var second = await _db.Articles().CreateAsync("Follow-up", null, null, TlpLevel.Clear);
        var story = await _db.Stories().CreateAsync("Edge VPN", null);
        await _db.Stories().LinkAsync(first, story);
        await _db.Stories().LinkAsync(second, story);
        var svc = _db.Actions();
        var sweep = await svc.CreateAsync(new ActionForm { Title = "IOC sweep", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = first });
        await svc.CreateAsync(new ActionForm { Title = "Patch", Kind = ActionKind.PatchRemediate, TeamId = team, ArticleId = first });
        await svc.CreateAsync(new ActionForm { Title = "Their sweep", Kind = ActionKind.IocSweep, TeamId = other, ArticleId = first });

        var dupes = await svc.LikelyDuplicatesAsync(second, ActionKind.IocSweep, team);

        dupes.Select(d => d.Number).Should().Equal(sweep);
    }

    [Fact]
    public async Task Hunt_CanBeWrittenByHand_AndIsMarkedSo()
    {
        var team = await Team();
        var svc = _db.Actions();
        var action = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Behavioural hunt", TeamId = team })))!.Action;

        var hunt = await svc.AddHandWrittenHuntAsync(action.Id, "kql", "DeviceProcessEvents | where InitiatingProcessFileName == \"php-fpm\" and FileName == \"sh\"");

        hunt.GeneratedText.Should().BeEmpty("that's how a hand-written query is told apart");
        hunt.Text.Should().StartWith("DeviceProcessEvents");
        var empty = () => svc.AddHandWrittenHuntAsync(action.Id, "kql", "  ");
        await empty.Should().ThrowAsync<ArgumentException>();
        var unknown = () => svc.AddHandWrittenHuntAsync(action.Id, "sql", "select 1");
        await unknown.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Hunt_IsGeneratedFromTheArticlesIndicators_EditedAndItsResultRecordedWithEvidence()
    {
        var team = await Team();
        var article = await ArticleWithIndicators();
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = article });
        var action = (await svc.GetAsync(n))!.Action;

        var hunt = await svc.GenerateHuntAsync(action.Id, "kql");
        hunt.Text.Should().Contain("\"203.0.113.45\"").And.Contain("\"bad.example.net\"").And.Contain("DeviceNetworkEvents");

        await svc.SaveHuntTextAsync(hunt.Id, hunt.Text + "| take 100");
        var evidence = await _db.Screenshots().StoreEvidenceAsync(new ScreenshotUpload("result.png", TestDb.Png));
        await svc.RecordHuntResultAsync(hunt.Id, HuntResult.Hits, 3, "Two hosts", evidence);
        (await _db.Screenshots().OpenAttachmentAsync(evidence))!.Value.FileName.Should().Be("result.png", "the screenshot dialog downloads it");

        var saved = (await svc.GetAsync(n))!.Action.Hunts.Single();
        saved.Text.Should().EndWith("| take 100");
        saved.GeneratedText.Should().NotEndWith("| take 100", "what was generated is kept beside what was run");
        saved.Result.Should().Be(HuntResult.Hits);
        saved.ResultCount.Should().Be(3);
        saved.RunByName.Should().Be(_db.User.DisplayName);
        (await _db.Screenshots().OpenAsync(evidence)).Should().NotBeNull("the team sees a hunt's evidence");

        var remove = () => svc.RemoveHuntAsync(hunt.Id);
        await remove.Should().ThrowAsync<InvalidOperationException>("a hunt with a result is kept");
        saved.EditedAfterResultAtUtc.Should().BeNull("edited before its result");
    }

    [Fact]
    public async Task AHuntQuery_EditedAfterItsResult_IsMarked_AndTheHistorySaysSo()
    {
        var team = await Team();
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Hunt", Kind = ActionKind.ThreatHunt, TeamId = team });
        var hunt = await svc.AddHandWrittenHuntAsync((await svc.GetAsync(n))!.Action.Id, "kql", "DeviceProcessEvents");
        await svc.RecordHuntResultAsync(hunt.Id, HuntResult.NoHits, null, null, null);

        await svc.SaveHuntTextAsync(hunt.Id, "DeviceProcessEvents");
        (await svc.GetAsync(n))!.Action.Hunts.Single().EditedAfterResultAtUtc.Should().BeNull("the text didn't change");

        await svc.SaveHuntTextAsync(hunt.Id, "DeviceProcessEvents | take 10");
        var a = (await svc.GetAsync(n))!.Action;
        a.Hunts.Single().Should().Match<HuntQuery>(h => h.Text == "DeviceProcessEvents | take 10" && h.EditedAfterResultAtUtc != null && h.Result == HuntResult.NoHits);
        a.Events.Select(e => e.Text).Should().Contain("Edited the Microsoft Sentinel / Defender (KQL) hunt query after its result was recorded.");
    }

    [Fact]
    public async Task Hunt_RefusesAnArticlesScreenshotAsEvidence()
    {
        var team = await Team();
        var article = await ArticleWithIndicators();
        var shot = (await _db.Screenshots().AddAsync(article, [new ScreenshotUpload("a.png", TestDb.Png)])).Single();
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = article });
        var hunt = await svc.GenerateHuntAsync((await svc.GetAsync(n))!.Action.Id, "xql");

        var act = () => svc.RecordHuntResultAsync(hunt.Id, HuntResult.NoHits, null, null, shot.FileId);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Workload_CountsOpenOverdueAndClosures()
    {
        var team = await Team();
        var svc = _db.Actions();
        var today = _db.Clock.Today;
        await svc.CreateAsync(new ActionForm { Title = "late", TeamId = team, DueDate = today.AddDays(-10) });
        await svc.CreateAsync(new ActionForm { Title = "fine", TeamId = team, DueDate = today.AddDays(30), AssigneeId = "u1", AssigneeName = "Robin" });
        var done = await svc.CreateAsync(new ActionForm { Title = "done", Kind = ActionKind.PatchRemediate, TeamId = team, DueDate = today.AddDays(30) });
        await svc.SetStatusAsync((await svc.GetAsync(done))!.Action.Id, ActionStatus.Done, ActionOutcome.Remediated, null);

        var load = (await svc.WorkloadAsync()).Single();
        load.Open.Should().Be(2);
        load.Overdue.Should().Be(1);
        load.ClosedLastWeek.Should().Be(1);
        load.MetLastWeek.Should().Be(1);
        load.ByAssignee.Select(x => x.Assignee).Should().Equal("Robin", "Unassigned");
        var robin = load.ByAssignee[0].AssigneeId!;
        (await svc.ListAsync(new ActionFilter(ActionView.Open, AssigneeId: robin))).Should().OnlyContain(r => r.AssigneeId == robin);
        (await svc.ListAsync(new ActionFilter(ActionView.Open, AssigneeId: ActionFilter.Unassigned))).Should().OnlyContain(r => r.AssigneeId == null).And.NotBeEmpty();
    }

    [Fact]
    public async Task Filters_HeldBy_InternalOrExternal_AndRaisedByMe_NarrowTheList_AndTheHoldersAreCounted()
    {
        var team = await Team();
        var outside = await _db.Teams().AddExternalAsync("Hosting Provider", "noc@provider.example", confirmOutside: true);
        var svc = _db.Actions();
        var me = _db.User.UserId;
        await svc.CreateAsync(new ActionForm { Title = "mine, internal", TeamId = team, AssigneeId = "u1", AssigneeName = "Robin" });
        await svc.CreateAsync(new ActionForm { Title = "mine, external", TeamId = outside });
        var them = _db.User.UserId = "S-1-5-21-SOMEONE-ELSE";
        await _db.Actions().CreateAsync(new ActionForm { Title = "theirs, internal", TeamId = team, AssigneeId = "u2", AssigneeName = "Alex" });
        await _db.Actions().CreateAsync(new ActionForm { Title = "theirs, also Robin's", TeamId = team, AssigneeId = "u1", AssigneeName = "Robin" });
        _db.User.UserId = me;

        async Task<IEnumerable<string>> Titles(ActionFilter f) => (await svc.ListAsync(f)).Select(r => r.Title).Order();
        (await Titles(new ActionFilter(ActionView.Open) { Teams = ActionTeams.External })).Should().Equal("mine, external");
        (await Titles(new ActionFilter(ActionView.Open) { Teams = ActionTeams.Internal })).Should().Equal("mine, internal", "theirs, also Robin's", "theirs, internal");
        (await Titles(new ActionFilter(ActionView.Open) { RaisedByMe = true })).Should().Equal("mine, external", "mine, internal");
        (await Titles(new ActionFilter(ActionView.Open, AssigneeId: "u1") { RaisedByMe = true })).Should().ContainSingle().Which.Should().Be("mine, internal", "they combine");
        (await svc.BoardAsync(new ActionFilter(ActionView.Open) { RaisedByMe = true, Teams = ActionTeams.External })).Cards.Select(c => c.Title).Should().Equal("mine, external");

        (await svc.HoldersAsync()).Should().Equal(new ActionHolder("u2", "Alex", 1), new ActionHolder("u1", "Robin", 2));
        them.Should().NotBe(me);
    }

    [Fact]
    public async Task MyOverdueCount_IsMyOpenActionsPastDue_Only()
    {
        var team = await Team();
        var svc = _db.Actions();
        var today = _db.Clock.Today;
        var me = _db.User.UserId;
        await svc.CreateAsync(new ActionForm { Title = "mine, late", TeamId = team, DueDate = today.AddDays(-3), AssigneeId = me, AssigneeName = "Me" });
        await svc.CreateAsync(new ActionForm { Title = "mine, due today", TeamId = team, DueDate = today, AssigneeId = me, AssigneeName = "Me" });
        await svc.CreateAsync(new ActionForm { Title = "theirs, late", TeamId = team, DueDate = today.AddDays(-3), AssigneeId = "u1", AssigneeName = "Robin" });
        await svc.CreateAsync(new ActionForm { Title = "nobody's, late", TeamId = team, DueDate = today.AddDays(-3) });
        var done = await svc.CreateAsync(new ActionForm { Title = "mine, late, done", Kind = ActionKind.PatchRemediate, TeamId = team,
            DueDate = today.AddDays(-3), AssigneeId = me, AssigneeName = "Me" });
        await svc.SetStatusAsync((await svc.GetAsync(done))!.Action.Id, ActionStatus.Done, ActionOutcome.Remediated, null);

        (await svc.MyOverdueCountAsync()).Should().Be(1, "only my open action past its due date; due today isn't overdue yet");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var reader = () => _db.Actions().MyOverdueCountAsync();
        await reader.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Teams_RetireDeletesAnUnusedTeam_AndRetiresAUsedOne()
    {
        var unused = await Team("Unused");
        var used = await Team("Used");
        await _db.Actions().CreateAsync(new ActionForm { Title = "x", TeamId = used });

        await _db.Teams().SetRetiredAsync(unused, true);
        await _db.Teams().SetRetiredAsync(used, true);

        var all = await _db.Teams().ListAsync(includeRetired: true);
        all.Select(t => (t.Name, t.IsRetired)).Should().Equal(("Used", true));
        var assign = () => _db.Actions().CreateAsync(new ActionForm { Title = "y", TeamId = used });
        await assign.Should().ThrowAsync<ArgumentException>().WithMessage("*retired*");
    }

    [Fact]
    public async Task QueryTemplates_AreAdministered_AndMustSayWhereTheValuesGo()
    {
        var q = _db.Queries();
        var bad = () => q.SaveTemplateAsync("kql", HuntValueKind.Domain, "DnsEvents | where Name == 'x'");
        await bad.Should().ThrowAsync<ArgumentException>().WithMessage("*{{values}}*");

        await q.SaveTemplateAsync("kql", HuntValueKind.Domain, "DnsEvents | where Name in~ ({{values}})");
        var text = await q.BuildAsync("kql", [new ArticleIndicator { Type = IndicatorType.Domain, Value = "bad.example.net" }]);
        text.Should().Contain("DnsEvents | where Name in~ (\"bad.example.net\")");

        await q.ResetTemplateAsync("kql", HuntValueKind.Domain);
        (await q.TemplatesAsync()).Single(r => r.Dialect.Key == "kql" && r.Kind == HuntValueKind.Domain).Saved.Should().BeNull();

        // A kind with no built-in template can be given one, and any kind can be turned off.
        var rows = await q.TemplatesAsync();
        rows.Should().HaveCount(QueryDialects.All.Count * Enum.GetValues<HuntValueKind>().Length, "every kind is listed for every language");
        await q.SaveTemplateAsync("fql", HuntValueKind.Url, "#event_simpleName=DnsRequest | in(field=DomainName, values=[{{values}}])");
        (await q.BuildAsync("fql", [new ArticleIndicator { Type = IndicatorType.Url, Value = "https://x.example" }])).Should().Contain("values=[\"https://x.example\"]");
        await q.SaveTemplateAsync("fql", HuntValueKind.Url, "");
        (await q.TemplatesAsync()).Single(r => r.Dialect.Key == "fql" && r.Kind == HuntValueKind.Url).IsOff.Should().BeTrue();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        var analyst = () => q.SaveTemplateAsync("kql", HuntValueKind.Domain, "{{values}}");
        await analyst.Should().ThrowAsync<ForbiddenException>();
    }

    // An article at a priority: a threat at P1, say, or an informational one (no priority).
    private async Task<Guid> ArticleAt(Priority? priority)
    {
        var svc = _db.Articles();
        var id = await svc.CreateAsync("GitLab account takeover", "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await svc.GetAsync(id))!);
        form.Bluf = "Exploited.";
        form.Classification = priority is null ? Classification.Informational : Classification.Threat;
        form.Priority = priority;
        await svc.SaveAsync(form);
        return id;
    }

    [Fact]
    public async Task QuickAdd_TakesTheArticlesPriority_CappedForANotice_AndTheDueDateStillFollowsTheSla()
    {
        var team = await Team();
        await _db.Sla().SaveAsync(new Dictionary<Priority, int?> { [Priority.P1] = 1, [Priority.P2] = 3, [Priority.P3] = 10 },
            new Dictionary<ActionKind, int?> { [ActionKind.IocSweep] = 5 });
        var svc = _db.Actions();
        var p1 = await ArticleAt(Priority.P1);
        var info = await ArticleAt(null);

        var patch = (await svc.GetAsync(await svc.QuickAddAsync(p1, "Patch GitLab", ActionKind.PatchRemediate, team, null)))!.Action;
        patch.Priority.Should().Be(Priority.P1);
        patch.DueDate.Should().Be(ActionSla.DueDate(_db.Clock.Today, 1));

        var notice = (await svc.GetAsync(await svc.QuickAddAsync(p1, "Tell the service desk", ActionKind.Awareness, team, null)))!.Action;
        notice.Priority.Should().Be(Priority.P2, "telling people is never above P2");
        notice.DueDate.Should().Be(ActionSla.DueDate(_db.Clock.Today, 3));

        var sweep = (await svc.GetAsync(await svc.QuickAddAsync(p1, "Sweep for the IOCs", ActionKind.IocSweep, team, null)))!.Action;
        sweep.Priority.Should().Be(Priority.P1);
        sweep.DueDate.Should().Be(ActionSla.DueDate(_db.Clock.Today, 5), "a kind's own SLA target still wins (ADR 0008)");

        var fromInfo = (await svc.GetAsync(await svc.QuickAddAsync(info, "Block the domain", ActionKind.Block, team, null)))!.Action;
        fromInfo.Priority.Should().Be(Priority.P2, "an informational article has no priority, so the kind's stands");
    }

    [Fact]
    public async Task TeamsByKind_IsTheTeamThatLastHadThatKind()
    {
        var soc = await Team("SOC");
        var vm = await Team("Vulnerability Management");
        var svc = _db.Actions();
        await svc.CreateAsync(new ActionForm { Title = "a", Kind = ActionKind.PatchRemediate, TeamId = soc });
        await svc.CreateAsync(new ActionForm { Title = "b", Kind = ActionKind.PatchRemediate, TeamId = vm });
        await svc.CreateAsync(new ActionForm { Title = "c", Kind = ActionKind.IocSweep, TeamId = soc });

        var byKind = await svc.TeamsByKindAsync();
        byKind[ActionKind.PatchRemediate].Should().Be(vm);
        byKind[ActionKind.IocSweep].Should().Be(soc);
        byKind.Should().NotContainKey(ActionKind.Awareness);
    }

    [Fact]
    public async Task Closing_OffersTheKindsOutcomes_ButKeepsOneItWasClosedWith()
    {
        var team = await Team();
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Patch", Kind = ActionKind.PatchRemediate, TeamId = team });
        var id = (await svc.GetAsync(n))!.Action.Id;

        var wrong = () => svc.SetStatusAsync(id, ActionStatus.Done, ActionOutcome.Answered, null);
        await wrong.Should().ThrowAsync<ArgumentException>().WithMessage("*Remediated, Not affected, Risk accepted, Other*");

        // Closed as Answered before the choices narrowed: moving it to Cancelled may keep that.
        await using (var db = _db.Context())
        {
            var a = await db.Actions.SingleAsync(x => x.Id == id);
            (a.Status, a.Outcome, a.ClosedDate) = (ActionStatus.Done, ActionOutcome.Answered, _db.Clock.Today);
            await db.SaveChangesAsync();
        }
        await svc.SetStatusAsync(id, ActionStatus.Cancelled, ActionOutcome.Answered, null);
        (await svc.GetAsync(n))!.Action.Outcome.Should().Be(ActionOutcome.Answered);
    }

    [Fact]
    public async Task NoHitsClose_RecordsTheHuntAndClosesTheAction_InOneLineOfHistory()
    {
        var team = await Team();
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = await ArticleWithIndicators() });
        var hunt = await svc.GenerateHuntAsync((await svc.GetAsync(n))!.Action.Id, "kql");
        var before = (await svc.GetAsync(n))!.Action.Events.Count;

        await svc.CloseWithNoHitsAsync(hunt.Id, 30, checkedLeftOut: false);

        var a = (await svc.GetAsync(n))!.Action;
        a.Status.Should().Be(ActionStatus.Done);
        a.Outcome.Should().Be(ActionOutcome.NoHits);
        a.ClosedDate.Should().Be(_db.Clock.Today);
        a.Hunts.Single().Result.Should().Be(HuntResult.NoHits);
        a.Hunts.Single().RunByName.Should().Be(_db.User.DisplayName);
        a.Events.Should().HaveCount(before + 1, "one step, one line");
        a.Events.First().Text.Should().Be("Ran the Microsoft Sentinel / Defender (KQL) hunt: no hits. Status: Open → Done (No hits): Searched 30 days.");

        var again = () => svc.CloseWithNoHitsAsync(hunt.Id, 30, false);
        await again.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task NoHitsClose_NeedsTheLeftOutValuesChecked_AndSaysSo()
    {
        var team = await Team();
        var svc = _db.Actions();
        var articles = _db.Articles();
        var article = await ArticleWithIndicators();
        var form = ArticleForm.From((await articles.GetAsync(article))!);
        form.Indicators = [.. form.Indicators, new() { Type = IndicatorType.Url, Value = "https://evil.example/drop" }];
        await articles.SaveAsync(form);
        var n = await svc.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = article });
        var hunt = await svc.GenerateHuntAsync((await svc.GetAsync(n))!.Action.Id, "fql");
        HuntQueryBuilder.LeftOut(hunt.GeneratedText).Should().Be(1, "CrowdStrike has no URL template");

        var unchecked_ = () => svc.CloseWithNoHitsAsync(hunt.Id, 45, checkedLeftOut: false);
        await unchecked_.Should().ThrowAsync<ArgumentException>().WithMessage("*left out 1 value*");
        var noWindow = () => svc.CloseWithNoHitsAsync(hunt.Id, 0, true);
        await noWindow.Should().ThrowAsync<ArgumentException>();
        (await svc.GetAsync(n))!.Action.Status.Should().Be(ActionStatus.Open);

        await svc.CloseWithNoHitsAsync(hunt.Id, 45, checkedLeftOut: true);
        var a = (await svc.GetAsync(n))!.Action;
        a.Events.First().Text.Should().EndWith("Searched 45 days; 1 value not included, checked separately.");
        a.OutcomeNote.Should().Be("Searched 45 days; 1 value not included, checked separately.");
    }

    [Fact]
    public async Task NoHitsClose_IsRefused_WhenAnotherQueryFoundHitsOrIsntRun_OrTheActionIsntAHunt()
    {
        var team = await Team();
        var svc = _db.Actions();
        var article = await ArticleWithIndicators();
        var n = await svc.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = article });
        var id = (await svc.GetAsync(n))!.Action.Id;
        var first = await svc.GenerateHuntAsync(id, "kql");
        var second = await svc.AddHandWrittenHuntAsync(id, "xql", "dataset = xdr_data | filter action_remote_ip = \"203.0.113.45\"");

        var unrun = () => svc.CloseWithNoHitsAsync(second.Id, 30, false);
        await unrun.Should().ThrowAsync<InvalidOperationException>().WithMessage("*hasn't been run*");
        await svc.RecordHuntResultAsync(first.Id, HuntResult.Hits, 2, null, null);
        var hits = () => svc.CloseWithNoHitsAsync(second.Id, 30, false);
        await hits.Should().ThrowAsync<InvalidOperationException>().WithMessage("*found hits*");
        (await svc.GetAsync(n))!.Action.IsOpen.Should().BeTrue();

        var block = await svc.CreateAsync(new ActionForm { Title = "Block", Kind = ActionKind.Block, TeamId = team, ArticleId = article });
        var blockHunt = await svc.AddHandWrittenHuntAsync((await svc.GetAsync(block))!.Action.Id, "kql", "x");
        var notAHunt = () => svc.CloseWithNoHitsAsync(blockHunt.Id, 30, false);
        await notAHunt.Should().ThrowAsync<InvalidOperationException>();
    }
}

public class ReminderTests : IDisposable
{
    private readonly TestDb _db = new();

    public ReminderTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task Person(string sid, string email)
    {
        await using var ctx = _db.Context();
        ctx.Users.Add(new AppUser { Sid = sid, DisplayName = sid, Email = email, RolesCsv = "ActionOwner", LastSeenUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task DueSoon_GoesToTheAssignee_OrTheTeamMailbox_OnceEach_BatchedPerRecipient()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, "defense@example.org");
        await Person("S-ROBIN", "robin@example.org");
        var today = _db.Clock.Today;
        var svc = _db.Actions();
        await svc.CreateAsync(new ActionForm { Title = "Robin first", TeamId = team, DueDate = today, AssigneeId = "S-ROBIN", AssigneeName = "Robin" });
        await svc.CreateAsync(new ActionForm { Title = "Robin second", TeamId = team, DueDate = WorkingDays.Weekdays.Next(today), AssigneeId = "S-ROBIN", AssigneeName = "Robin" });
        await svc.CreateAsync(new ActionForm { Title = "Nobody", TeamId = team, DueDate = today });
        await svc.CreateAsync(new ActionForm { Title = "Later", TeamId = team, DueDate = today.AddDays(20) });

        (await _db.Reminders().RunAsync(ReminderKind.DueSoon)).Should().Be(2);

        _db.Email.Sent.Should().HaveCount(2);
        var robin = _db.Email.Sent.Single(m => m.To.Contains("robin@example.org"));
        robin.Subject.Should().Contain("2 actions due soon");
        robin.Html.Should().Contain("Robin first").And.Contain("Robin second").And.NotContain("Nobody").And.Contain("https://tide.example.org/actions/");
        _db.Email.Sent.Single(m => m.To.Contains("defense@example.org")).Html.Should().Contain("Nobody").And.NotContain("Later");

        (await _db.Reminders().RunAsync(ReminderKind.DueSoon)).Should().Be(0, "each reminder goes once");
    }

    [Fact]
    public async Task Overdue_FallsBackToTheMembers_AndARelayFailureIsTriedAgain()
    {
        var team = await _db.Teams().AddAsync("Vulnerability Management", null, null);
        await Person("S-SAM", "sam@example.org");
        await _db.Teams().AddMemberAsync(team, "S-SAM", "Sam");
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch it", TeamId = team, DueDate = _db.Clock.Today.AddDays(-3) });

        _db.Email.Result = new(Application.Distribution.EmailSendStatus.Failed, "down");
        (await _db.Reminders().RunAsync(ReminderKind.Overdue)).Should().Be(0);
        _db.Email.Result = new(Application.Distribution.EmailSendStatus.Sent);
        (await _db.Reminders().RunAsync(ReminderKind.Overdue)).Should().Be(1, "nothing was marked while the relay failed");
        _db.Email.Sent.Last().To.Should().Equal("sam@example.org");
        _db.Email.Sent.Last().Subject.Should().Contain("1 overdue action");
    }

    [Fact]
    public async Task DueSoon_AScanStoppedPartway_KeepsTheMarksOfWhatWent_AndDoesntEmailThoseAgain()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, "defense@example.org");
        await Person("S-ROBIN", "robin@example.org");
        var today = _db.Clock.Today;
        await _db.Actions().CreateAsync(new ActionForm { Title = "Robin's", TeamId = team, DueDate = today, AssigneeId = "S-ROBIN", AssigneeName = "Robin" });
        await _db.Actions().CreateAsync(new ActionForm { Title = "The team's", TeamId = team, DueDate = today });
        // The server shuts down after the first email went.
        _db.Email.Fault = _ => _db.Email.Sent.Count >= 1 ? new OperationCanceledException() : null;

        var stopped = () => _db.Reminders().RunAsync(ReminderKind.DueSoon);
        await stopped.Should().ThrowAsync<OperationCanceledException>();
        var first = _db.Email.Sent.Single().To.Single();

        _db.Email.Fault = null;
        (await _db.Reminders().RunAsync(ReminderKind.DueSoon)).Should().Be(1, "only the one that didn't go");
        _db.Email.Sent.Should().HaveCount(2);
        _db.Email.Sent[1].To.Single().Should().NotBe(first);
    }

    [Fact]
    public async Task Overdue_ToEachMember_AScanStoppedPartway_StillReachesTheMembersNotYetEmailed()
    {
        var team = await _db.Teams().AddAsync("Vulnerability Management", null, null);
        foreach (var (sid, mail) in new[] { ("S-A", "a@example.org"), ("S-B", "b@example.org"), ("S-C", "c@example.org") })
        {
            await Person(sid, mail);
            await _db.Teams().AddMemberAsync(team, sid, sid);
        }
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch it", TeamId = team, DueDate = _db.Clock.Today.AddDays(-3) });
        _db.Email.Fault = _ => _db.Email.Sent.Count >= 1 ? new OperationCanceledException() : null;

        var stopped = () => _db.Reminders().RunAsync(ReminderKind.Overdue);
        await stopped.Should().ThrowAsync<OperationCanceledException>();
        _db.Email.Fault = null;
        (await _db.Reminders().RunAsync(ReminderKind.Overdue)).Should().Be(2, "the two members not reached yet");
        (await _db.Reminders().RunAsync(ReminderKind.Overdue)).Should().Be(0);

        _db.Email.Sent.SelectMany(m => m.To).Should().BeEquivalentTo(["a@example.org", "b@example.org", "c@example.org"]);
    }

    [Fact]
    public async Task RunDue_AScanThatFails_DoesntStopTheScansAfterIt()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, "defense@example.org");
        // 09:45 local on a Monday.
        var clock = new OrgClock(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(2026, 10, 26, 13, 45, 0, TimeSpan.Zero)));
        await _db.Actions().CreateAsync(new ActionForm { Title = "Soon", TeamId = team, DueDate = clock.Today });
        await _db.Actions().CreateAsync(new ActionForm { Title = "Late", TeamId = team, DueDate = clock.Today.AddDays(-5) });
        var options = new ReminderOptions { DueSoon = { Enabled = true }, Overdue = { Enabled = true } };
        _db.Email.Fault = m => m.Subject.Contains("due soon", StringComparison.Ordinal) ? new InvalidOperationException("relay bug") : null;
        var log = new ReminderLog();

        await _db.Reminders(options, clock, log).RunDueAsync();

        _db.Email.Sent.Should().ContainSingle().Which.Subject.Should().Contain("overdue");
        log.Last(ReminderKind.DueSoon)!.Error.Should().Contain("relay bug");
    }

    [Fact]
    public async Task Reminders_AreMarkedWithTheirArticlesTlp_InTheSubjectAndBesideEachAction()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, "defense@example.org");
        var restricted = await _db.Articles().CreateAsync("Partner report", null, null, TlpLevel.AmberStrict);
        var open = await _db.Articles().CreateAsync("Public advisory", null, null, TlpLevel.Clear);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Hunt the partner's IOCs", TeamId = team, DueDate = _db.Clock.Today, ArticleId = restricted });
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch the advisory's CVE", TeamId = team, DueDate = _db.Clock.Today, ArticleId = open });

        (await _db.Reminders().RunAsync(ReminderKind.DueSoon)).Should().Be(1);
        (await _db.Reminders().RunAsync(ReminderKind.Digest)).Should().Be(1);

        foreach (var mail in _db.Email.Sent)
        {
            mail.Subject.Should().EndWith("[TLP:AMBER+STRICT]", "the highest marking it lists");
            mail.Html.Should().Contain("TLP:CLEAR").And.Contain("TLP:AMBER+STRICT");
            mail.Text.Should().Contain("TLP:AMBER+STRICT");
        }
    }

    [Fact]
    public async Task Reminders_ForActionsRaisedOnTheirOwn_CarryNoMarking()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, "defense@example.org");
        await _db.Actions().CreateAsync(new ActionForm { Title = "Review firewall rules", TeamId = team, DueDate = _db.Clock.Today });

        (await _db.Reminders().RunAsync(ReminderKind.DueSoon)).Should().Be(1);

        _db.Email.Sent.Single().Subject.Should().NotContain("TLP:");
        _db.Email.Sent.Single().Html.Should().NotContain("TLP:");
    }

    [Fact]
    public async Task Digest_GoesOncePerTeamPerWeek()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, "defense@example.org");
        await _db.Actions().CreateAsync(new ActionForm { Title = "Open one", TeamId = team, DueDate = _db.Clock.Today.AddDays(-1) });

        (await _db.Reminders().RunAsync(ReminderKind.Digest)).Should().Be(1);
        _db.Email.Sent.Single().Html.Should().Contain("Open one").And.Contain("1</strong> overdue");
        (await _db.Reminders().RunAsync(ReminderKind.Digest)).Should().Be(0);
    }

    [Fact]
    public async Task TheLastSevenDays_AreTodayAndTheSixBefore_InWorkloadAndTheDigest()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, "defense@example.org");
        var svc = _db.Actions();
        var today = _db.Clock.Today;
        foreach (var (title, ago) in new[] { ("Closed six days ago", 6), ("Closed seven days ago", 7) })
        {
            var n = await svc.CreateAsync(new ActionForm { Title = title, Kind = ActionKind.PatchRemediate, TeamId = team, DueDate = today.AddDays(30) });
            var id = (await svc.GetAsync(n))!.Action.Id;
            await svc.SetStatusAsync(id, ActionStatus.Done, ActionOutcome.Remediated, null);
            await using var ctx = _db.Context();
            await ctx.Actions.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ClosedDate, today.AddDays(-ago)));
        }

        (await svc.WorkloadAsync()).Single().ClosedLastWeek.Should().Be(1, "seven days back is the eighth day");
        (await _db.Reminders().RunAsync(ReminderKind.Digest)).Should().Be(1);
        _db.Email.Sent.Single().Html.Should().Contain("Closed six days ago").And.NotContain("Closed seven days ago");
    }

    [Fact]
    public async Task BriefNotReady_NudgesTheAnalyst_OrTheFallback_AndNotOnceReady()
    {
        // 09:30 local on a Monday, before the 10:30 meeting.
        var monday = new DateTimeOffset(2026, 10, 26, 13, 45, 0, TimeSpan.Zero);
        var clock = new OrgClock(Options.Create(new OrgOptions()), new TestClock(monday));
        var options = new ReminderOptions { BriefNotReady = { Enabled = true, Time = "09:30", Recipients = ["lead@example.org"] } };

        await _db.Reminders(options, clock).RunDueAsync();
        _db.Email.Sent.Single().To.Should().Equal("lead@example.org");
        _db.Email.Sent.Single().Html.Should().Contain("no brief for today");

        await _db.Reminders(options, clock).RunDueAsync();
        _db.Email.Sent.Should().HaveCount(1, "once a day");
    }

    [Fact]
    public async Task RunDue_SkipsScansThatAreOff_OrNotYetDue()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, "defense@example.org");
        await _db.Actions().CreateAsync(new ActionForm { Title = "Late", TeamId = team, DueDate = _db.Clock.Today.AddDays(-5) });
        var early = new OrgClock(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero)));   // 06:00 local

        await _db.Reminders(new ReminderOptions(), early).RunDueAsync();
        await _db.Reminders(new ReminderOptions { Overdue = { Enabled = true } }, early).RunDueAsync();

        _db.Email.Sent.Should().BeEmpty("one scan is off, and the other's time hasn't come");
    }

    [Fact]
    public async Task RunNow_IsForAdministrators()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        var act = () => _db.Reminders().RunNowAsync(ReminderKind.Digest);
        await act.Should().ThrowAsync<ForbiddenException>();
    }
}
