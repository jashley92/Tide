using System.Text;
using FluentAssertions;
using Tide.Application.Actions;
using Tide.Application.Files;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>Playbooks and checklists, card rules, and files on actions' comments.</summary>
public class PlaybookTests : IDisposable
{
    private readonly TestDb _db = new();

    public PlaybookTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private Task<Guid> Team(string name = "Cyber Defense") => _db.Teams().AddAsync(name, null, null);

    private Task<Guid> SweepPlaybook(params string[] steps) => _db.Playbooks().SaveAsync(new PlaybookForm
    {
        Name = "Ransomware sweep", Kind = ActionKind.IocSweep, Steps = steps.Length > 0 ? [.. steps] : ["Pull the indicators", "Sweep proxy logs", "Sweep EDR"],
    });

    [Fact]
    public async Task Create_FromAPlaybook_CopiesItsStepsAsTheChecklist_AndApplyingAddsOnlyWhatsMissing()
    {
        var team = await Team();
        var playbook = await SweepPlaybook();
        var svc = _db.Actions();

        var n = await svc.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team, PlaybookId = playbook });
        var a = (await svc.GetAsync(n))!.Action;
        a.PlaybookName.Should().Be("Ransomware sweep");
        a.Checklist.OrderBy(i => i.SortOrder).Select(i => i.Text).Should().Equal("Pull the indicators", "Sweep proxy logs", "Sweep EDR");
        a.Events.Should().Contain(e => e.Text.Contains("with the Ransomware sweep playbook's checklist"));

        // A later edit to the playbook doesn't reach the action; applying it again adds only the new step.
        await _db.Playbooks().SaveAsync(new PlaybookForm
        {
            Id = playbook, Name = "Ransomware sweep", Kind = ActionKind.IocSweep, Steps = ["Pull the indicators", "sweep proxy logs", "Sweep DNS logs"],
        });
        (await svc.ChecklistAsync(a.Id)).Should().HaveCount(3);
        (await svc.ApplyPlaybookAsync(a.Id, playbook)).Should().Be(1, "the steps it has, by text ignoring case, aren't added twice");
        (await svc.ChecklistAsync(a.Id)).Select(i => i.Text).Should().EndWith("Sweep DNS logs");

        var patch = await svc.CreateAsync(new ActionForm { Title = "Patch", Kind = ActionKind.PatchRemediate, TeamId = team });
        var wrongKind = async () => await svc.ApplyPlaybookAsync((await svc.GetAsync(patch))!.Action.Id, playbook);
        await wrongKind.Should().ThrowAsync<ArgumentException>().WithMessage("*for IOC sweep actions*");
        (await _db.Playbooks().ForKindAsync(ActionKind.PatchRemediate)).Should().BeEmpty();
    }

    [Fact]
    public async Task Playbooks_AreForAdministrators_AndOneInUseIsRetiredNotDeleted()
    {
        var team = await Team();
        var used = await SweepPlaybook();
        var unused = await _db.Playbooks().SaveAsync(new PlaybookForm { Name = "Spare", Steps = ["One"] });
        await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team, PlaybookId = used });

        await _db.Playbooks().RemoveAsync(used);
        await _db.Playbooks().RemoveAsync(unused);
        var all = await _db.Playbooks().ListAsync(includeRetired: true);
        all.Should().ContainSingle().Which.IsActive.Should().BeFalse();
        var retired = () => _db.Actions().CreateAsync(new ActionForm { Title = "Again", Kind = ActionKind.IocSweep, TeamId = team, PlaybookId = used });
        await retired.Should().ThrowAsync<ArgumentException>().WithMessage("*retired*");

        var empty = () => _db.Playbooks().SaveAsync(new PlaybookForm { Name = "Nothing", Steps = ["  "] });
        await empty.Should().ThrowAsync<ArgumentException>();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        var analyst = () => _db.Playbooks().SaveAsync(new PlaybookForm { Name = "Mine", Steps = ["x"] });
        await analyst.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Checklist_IsTickedByWhoeverWorksTheAction_AndTheHistorySaysSo()
    {
        var team = await Team();
        var other = await Team("Vulnerability Management");
        var svc = _db.Actions();
        var playbook = await SweepPlaybook();
        var ours = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Ours", Kind = ActionKind.IocSweep, TeamId = team, PlaybookId = playbook })))!.Action;
        var theirs = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Theirs", Kind = ActionKind.IocSweep, TeamId = other, PlaybookId = playbook })))!.Action;
        await _db.Teams().AddMemberAsync(team, "S-1-5-21-OWNER", "Owner");

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        _db.User.UserId = "S-1-5-21-OWNER";
        _db.User.DisplayName = "Owner";
        var first = ours.Checklist.OrderBy(i => i.SortOrder).First();
        await svc.SetChecklistItemAsync(first.Id, true);
        await svc.AddChecklistItemAsync(ours.Id, "Tell the SOC");
        var board = await svc.BoardAsync(new ActionFilter());
        var card = board.Cards.Single(c => c.Id == ours.Id);
        (card.ChecklistDone, card.ChecklistTotal).Should().Be((1, 4));

        var after = (await svc.GetAsync(ours.Number))!.Action;
        after.Checklist.Single(i => i.Id == first.Id).DoneByName.Should().Be("Owner");
        after.Events.Select(e => e.Text).Should().Contain(["Ticked \"Pull the indicators\".", "Added \"Tell the SOC\" to the checklist."]);

        var foreign = () => svc.SetChecklistItemAsync(theirs.Checklist.First().Id, true);
        await foreign.Should().ThrowAsync<ForbiddenException>();
        var foreignAdd = () => svc.AddChecklistItemAsync(theirs.Id, "Sneak");
        await foreignAdd.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task CardRules_MatchEveryConditionTheySet_AndAPartShowsItsParent()
    {
        var team = await Team();
        var other = await Team("Vulnerability Management");
        var svc = _db.Actions();
        var parent = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Remediate", Priority = Priority.P1, TeamId = team, DueDate = _db.Clock.Today.AddDays(-2) })))!.Action;
        await svc.CreateAsync(new ActionForm { Title = "Patch the VPN", Priority = Priority.P3, TeamId = other, ParentId = parent.Id, DueDate = _db.Clock.Today.AddDays(30) });

        var rules = _db.CardRules();
        await rules.SaveAsync(new ActionCardRule { Name = "P2+ overdue", Colour = CardColour.Red, PriorityAtLeast = Priority.P2, Sla = CardRuleSla.Overdue });
        await rules.SaveAsync(new ActionCardRule { Name = "Part", Colour = CardColour.Purple, IsPart = true, ShowAsFilter = false });
        var everything = () => rules.SaveAsync(new ActionCardRule { Name = "All" });
        await everything.Should().ThrowAsync<ArgumentException>().WithMessage("*at least one condition*");

        var list = await rules.ListAsync();
        var cards = (await svc.BoardAsync(new ActionFilter())).Cards;
        var top = cards.Single(c => c.Title == "Remediate");
        var part = cards.Single(c => c.Title == "Patch the VPN");
        ActionRules.For(list, top, _db.Clock.Today).Select(r => r.Name).Should().Equal("P2+ overdue");
        ActionRules.For(list, part, _db.Clock.Today).Select(r => r.Name).Should().Equal("Part");
        part.ParentNumber.Should().Be(parent.Number);
        top.Children.Should().Be(1);

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        var analyst = () => rules.SaveAsync(new ActionCardRule { Name = "Mine", IsPart = true });
        await analyst.Should().ThrowAsync<ForbiddenException>();
        (await rules.ListAsync()).Should().HaveCount(2, "everyone who reads actions reads the rules");
    }

    [Fact]
    public async Task Playbooks_AreOrderedByTheAdministrator_NamedOnce_AndKeptToTheirLimit()
    {
        var pb = _db.Playbooks();
        var sweep = await SweepPlaybook();
        var any = await pb.SaveAsync(new PlaybookForm { Name = "Close-out", Description = "For any action.", Steps = ["Tell the requester"] });
        (await pb.ForKindAsync(ActionKind.IocSweep)).Select(p => p.Id).Should().Equal([sweep, any], "the kind's own come before those for any kind");

        await pb.MoveAsync(any, -1);
        (await pb.ListAsync()).Select(p => p.Id).Should().Equal(any, sweep);
        await pb.MoveAsync(any, -1);   // already first: nothing moves
        (await pb.ListAsync()).Select(p => p.Id).Should().Equal(any, sweep);

        var duplicate = () => pb.SaveAsync(new PlaybookForm { Name = "Close-out", Steps = ["x"] });
        await duplicate.Should().ThrowAsync<ArgumentException>().WithMessage("*already a playbook*");
        var noName = () => pb.SaveAsync(new PlaybookForm { Name = " ", Steps = ["x"] });
        await noName.Should().ThrowAsync<ArgumentException>();
        var tooLong = () => pb.SaveAsync(new PlaybookForm { Name = "Long", Steps = [.. Enumerable.Range(0, Playbook.MaxSteps + 1).Select(i => $"Step {i}")] });
        await tooLong.Should().ThrowAsync<ArgumentException>();
        var badKind = () => pb.SaveAsync(new PlaybookForm { Name = "Odd", Kind = (ActionKind)99, Steps = ["x"] });
        await badKind.Should().ThrowAsync<ArgumentException>();
        var gone = () => pb.SaveAsync(new PlaybookForm { Id = Guid.NewGuid(), Name = "Gone", Steps = ["x"] });
        await gone.Should().ThrowAsync<KeyNotFoundException>();

        var form = PlaybookForm.From((await pb.ListAsync()).Single(p => p.Id == any));
        form.Steps.Should().Equal("Tell the requester");
        form.Description.Should().Be("For any action.");
        await pb.RemoveAsync(Guid.NewGuid());   // an unknown one is no error
    }

    [Fact]
    public async Task Checklist_ItemsAreTakenOff_ApplyingTwiceAddsNothing_AndEmptyItemsAreRefused()
    {
        var team = await Team();
        var svc = _db.Actions();
        var playbook = await SweepPlaybook("One", "Two");
        var a = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team, PlaybookId = playbook })))!.Action;

        (await svc.ApplyPlaybookAsync(a.Id, playbook)).Should().Be(0);
        var item = a.Checklist.Single(i => i.Text == "One");
        await svc.SetChecklistItemAsync(item.Id, true);
        await svc.SetChecklistItemAsync(item.Id, true);   // already ticked: no second history line
        await svc.SetChecklistItemAsync(item.Id, false);
        await svc.RemoveChecklistItemAsync(item.Id);
        await svc.RemoveChecklistItemAsync(item.Id);   // already gone: no error
        (await svc.ChecklistAsync(a.Id)).Select(i => i.Text).Should().Equal("Two");

        var events = (await svc.GetAsync(a.Number))!.Action.Events.Select(e => e.Text).ToList();
        events.Should().Contain("Applied the Ransomware sweep playbook: its steps were all on the checklist already.");
        events.Count(e => e == "Ticked \"One\".").Should().Be(1);
        events.Should().Contain(["Unticked \"One\".", "Took \"One\" off the checklist."]);

        var blank = () => svc.AddChecklistItemAsync(a.Id, "   ");
        await blank.Should().ThrowAsync<ArgumentException>();
        var missing = () => svc.SetChecklistItemAsync(Guid.NewGuid(), true);
        await missing.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task CardRules_EachCondition_AndTheirOrderDescriptionAndRemoval()
    {
        var team = await Team();
        var other = await Team("Vulnerability Management");
        var today = _db.Clock.Today;
        ActionRow Row(ActionKind kind = ActionKind.IocSweep, Priority prio = Priority.P3, Guid? teamId = null, string? assignee = "S-1",
            DateOnly? due = null, ActionStatus status = ActionStatus.Open, Guid? parent = null, int checklist = 0) =>
            new(Guid.NewGuid(), 1, "x", kind, prio, teamId ?? team, "Team", assignee, null, due, status, ActionOutcome.None, null,
                null, null, parent, 0, DateTimeOffset.UtcNow, ChecklistTotal: checklist);

        ActionRules.Matches(new ActionCardRule { Kind = ActionKind.Block }, Row(), today).Should().BeFalse();
        ActionRules.Matches(new ActionCardRule { Kind = ActionKind.IocSweep }, Row(), today).Should().BeTrue();
        ActionRules.Matches(new ActionCardRule { PriorityAtLeast = Priority.P2 }, Row(prio: Priority.P1), today).Should().BeTrue();
        ActionRules.Matches(new ActionCardRule { PriorityAtLeast = Priority.P2 }, Row(prio: Priority.P3), today).Should().BeFalse();
        ActionRules.Matches(new ActionCardRule { TeamId = other }, Row(), today).Should().BeFalse();
        ActionRules.Matches(new ActionCardRule { Status = ActionStatus.Blocked }, Row(status: ActionStatus.Blocked), today).Should().BeTrue();
        ActionRules.Matches(new ActionCardRule { Status = ActionStatus.Blocked }, Row(), today).Should().BeFalse();
        ActionRules.Matches(new ActionCardRule { Unassigned = true }, Row(), today).Should().BeFalse();
        ActionRules.Matches(new ActionCardRule { Unassigned = true }, Row(assignee: null), today).Should().BeTrue();
        ActionRules.Matches(new ActionCardRule { HasChecklist = true }, Row(), today).Should().BeFalse();
        ActionRules.Matches(new ActionCardRule { HasChecklist = true }, Row(checklist: 3), today).Should().BeTrue();
        ActionRules.Matches(new ActionCardRule { IsPart = true }, Row(), today).Should().BeFalse();
        ActionRules.Matches(new ActionCardRule { Sla = CardRuleSla.DueSoon }, Row(due: today), today).Should().BeTrue();
        ActionRules.Matches(new ActionCardRule { Sla = CardRuleSla.DueSoon }, Row(due: today.AddDays(-1)), today).Should().BeFalse();
        ActionRules.Matches(new ActionCardRule { Sla = CardRuleSla.OverdueOrDueSoon }, Row(due: today.AddDays(-1)), today).Should().BeTrue();
        ActionRules.Matches(new ActionCardRule { Sla = CardRuleSla.OverdueOrDueSoon }, Row(due: today.AddDays(60)), today).Should().BeFalse();
        ActionRules.For([new ActionCardRule { Kind = ActionKind.IocSweep, IsActive = false }], Row(), today).Should().BeEmpty("a rule turned off matches nothing");

        var described = new ActionCardRule
        {
            Kind = ActionKind.IocSweep, PriorityAtLeast = Priority.P2, TeamId = other, Status = ActionStatus.Open, Sla = CardRuleSla.Overdue,
            Unassigned = true, HasChecklist = true, IsPart = true,
        };
        ActionRules.Describe(described, id => id == other ? "Vulnerability Management" : null)
            .Should().Be("IOC sweep · P2 or higher · Vulnerability Management · Open · overdue · unassigned · has a checklist · a part of another action");
        ActionRules.Describe(new ActionCardRule { PriorityAtLeast = Priority.P1, TeamId = Guid.NewGuid(), Sla = CardRuleSla.DueSoon }, _ => null)
            .Should().Be("P1 · a removed team · due soon");
        ActionRules.Describe(new ActionCardRule { Sla = CardRuleSla.OverdueOrDueSoon }, _ => null).Should().Be("overdue or due soon");

        var rules = _db.CardRules();
        var first = await rules.SaveAsync(new ActionCardRule { Name = "Sweeps", Kind = ActionKind.IocSweep, TeamId = team });
        var second = await rules.SaveAsync(new ActionCardRule { Name = "Blocked", Status = ActionStatus.Blocked, Colour = CardColour.Amber });
        await rules.MoveAsync(second, -1);
        (await rules.ListAsync()).Select(r => r.Id).Should().Equal(second, first);
        await rules.MoveAsync(second, 5);   // past the end: nothing moves
        await rules.SaveAsync(new ActionCardRule { Id = first, Name = "IOC sweeps", Kind = ActionKind.IocSweep, IsActive = false });
        (await rules.ListAsync()).Should().ContainSingle().Which.Id.Should().Be(second);
        (await rules.ListAsync(includeInactive: true)).Single(r => r.Id == first).Name.Should().Be("IOC sweeps");
        var badTeam = () => rules.SaveAsync(new ActionCardRule { Name = "Ghost", TeamId = Guid.NewGuid() });
        await badTeam.Should().ThrowAsync<ArgumentException>();
        var badColour = () => rules.SaveAsync(new ActionCardRule { Name = "Odd", Colour = (CardColour)42, IsPart = true });
        await badColour.Should().ThrowAsync<ArgumentException>();
        var noName = () => rules.SaveAsync(new ActionCardRule { Name = "", IsPart = true });
        await noName.Should().ThrowAsync<ArgumentException>();
        await rules.DeleteAsync(first);
        await rules.DeleteAsync(first);   // already gone: no error
        (await rules.ListAsync(includeInactive: true)).Select(r => r.Id).Should().Equal(second);
    }

    [Fact]
    public async Task Attachments_KeepToTheirLimits_AndACommentWithFilesCanLoseItsText()
    {
        var team = await Team();
        var svc = _db.Actions();
        var a = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Sweep", TeamId = team })))!.Action;

        // The test store's screenshot limit is 1 MB: a bigger image is refused, though it's within the evidence limit.
        var bigImage = TestDb.Png.Concat(new byte[1024 * 1024 + 1]).ToArray();
        var big = () => svc.AddCommentAsync(a.Id, "x", [new ScreenshotUpload("big.png", bigImage)]);
        await big.Should().ThrowAsync<ArgumentException>().WithMessage("*screenshots*");
        var tooMany = () => svc.AddCommentAsync(a.Id, "x", [.. Enumerable.Range(0, ActionCommentFile.MaxPerComment + 1).Select(i => new ScreenshotUpload($"{i}.png", TestDb.Png))]);
        await tooMany.Should().ThrowAsync<ArgumentException>();
        _db.Store.Files.Should().BeEmpty();
        _db.Screenshots().MaxAttachmentBytes.Should().Be(25L * 1024 * 1024);

        await svc.AddCommentAsync(a.Id, "Proxy hits", [new ScreenshotUpload("", Encoding.UTF8.GetBytes("raw"))]);
        var comment = (await svc.GetAsync(a.Number))!.Action.Comments.Single();
        comment.Files.Single().FileName.Should().Be("evidence", "a file with no usable name still gets one");
        await svc.EditCommentAsync(comment.Id, "");
        (await svc.GetAsync(a.Number))!.Action.Comments.Single().Text.Should().BeEmpty();

        await svc.AddCommentAsync(a.Id, "Words only");
        var wordsOnly = (await svc.GetAsync(a.Number))!.Action.Comments.Single(c => c.Text == "Words only");
        var emptied = () => svc.EditCommentAsync(wordsOnly.Id, " ");
        await emptied.Should().ThrowAsync<ArgumentException>().WithMessage("*delete it instead*");
    }

    [Fact]
    public async Task Comments_TakeScreenshotsAndEvidenceFiles_ServedOnlyAsTheyShouldBe_AndDeletedWithTheComment()
    {
        var team = await Team();
        var svc = _db.Actions();
        var action = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team })))!.Action;
        var log = Encoding.UTF8.GetBytes("2026-10-28 10:01 203.0.113.45 GET /");

        await svc.AddCommentAsync(action.Id, "", [new ScreenshotUpload("hits.png", TestDb.Png), new ScreenshotUpload("../proxy extract.log", log)]);

        var after = (await svc.GetAsync(action.Number))!.Action;
        var comment = after.Comments.Single();
        comment.Text.Should().BeEmpty("files alone make a comment");
        var shot = comment.Files.Single(f => f.IsImage);
        var evidence = comment.Files.Single(f => !f.IsImage);
        evidence.FileName.Should().Be("proxy extract.log", "the name keeps its extension but loses any path");
        after.Events.Should().Contain(e => e.Text == "Attached 1 screenshot and 1 evidence file to a comment.");

        var shots = _db.Screenshots();
        (await shots.OpenAsync(shot.FileId)).Should().NotBeNull("a screenshot is shown inline");
        (await shots.OpenAsync(evidence.FileId)).Should().BeNull("evidence is never served inline");
        var download = await shots.OpenAttachmentAsync(evidence.FileId);
        download!.Value.FileName.Should().Be("proxy extract.log");

        var nothing = () => svc.AddCommentAsync(action.Id, "  ");
        await nothing.Should().ThrowAsync<ArgumentException>();
        var empty = () => svc.AddCommentAsync(action.Id, "x", [new ScreenshotUpload("empty.bin", [])]);
        await empty.Should().ThrowAsync<ArgumentException>();
        _db.Store.Files.Should().HaveCount(2, "a refused comment leaves no file behind");

        await svc.DeleteCommentAsync(comment.Id);
        _db.Store.Files.Should().BeEmpty();
        (await shots.OpenAttachmentAsync(evidence.FileId)).Should().BeNull();
    }
}
