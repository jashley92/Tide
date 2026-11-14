using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// An action's holder is a member of its team, whichever way it's given: the editor (create and save), the meeting's
/// assign and a meeting's quick add. A holder from before the rule, who isn't on the team, keeps the action until it
/// changes hands; an external team has no members, so its actions are nobody's.
/// </summary>
public class ActionAssigneeRuleTests : IDisposable
{
    private readonly TestDb _db = new();

    public ActionAssigneeRuleTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<Guid> TeamWith(string name, params (string Id, string Name)[] members)
    {
        var team = await _db.Teams().AddAsync(name, null, null);
        foreach (var (id, who) in members) await _db.Teams().AddMemberAsync(team, id, who);
        return team;
    }

    private async Task<ActionForm> Form(int number) => ActionForm.From((await _db.Actions().GetAsync(number))!.Action);

    // An action held by someone not on its team, as one assigned before the rule was.
    private async Task<int> HeldByAnOutsider(Guid team)
    {
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = "From before the rule", TeamId = team });
        await using var ctx = _db.PlainContext();
        await ctx.Actions.Where(a => a.Number == number)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AssigneeId, "dev:outsider").SetProperty(a => a.AssigneeName, "Outsider"));
        return number;
    }

    [Fact]
    public async Task Creating_TakesOnlyAMemberOfTheTeam_UnderTheNameTheTeamKnows()
    {
        var team = await TeamWith("Vulnerability Management", ("dev:robin", "Robin Member"));
        var svc = _db.Actions();

        var refused = () => svc.CreateAsync(new ActionForm { Title = "Patch", TeamId = team, AssigneeId = "dev:stranger", AssigneeName = "Stranger" });
        await refused.Should().ThrowAsync<ArgumentException>().WithMessage("*isn't on the action's team*");

        var number = await svc.CreateAsync(new ActionForm { Title = "Patch", TeamId = team, AssigneeId = "dev:robin", AssigneeName = "Someone else entirely" });
        var a = (await svc.GetAsync(number))!.Action;
        (a.AssigneeId, a.AssigneeName).Should().Be(("dev:robin", "Robin Member"), "the name is the team's, not what the form said");

        var nobodys = await svc.CreateAsync(new ActionForm { Title = "Unassigned", TeamId = team });
        (await svc.GetAsync(nobodys))!.Action.AssigneeId.Should().BeNull();
    }

    [Fact]
    public async Task AnExternalTeamsAction_CantBeAssigned()
    {
        var external = await _db.Teams().AddExternalAsync("Managed SOC", "soc@example.org");
        var refused = () => _db.Actions().CreateAsync(new ActionForm { Title = "Block", TeamId = external, AssigneeId = "dev:robin", AssigneeName = "Robin" });
        await refused.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SavingAnEdit_ToAHolderNotOnTheTeam_IsRefused_AndToAMember_Goes()
    {
        var team = await TeamWith("Cyber Defense", ("dev:robin", "Robin"), ("dev:alex", "Alex"));
        var svc = _db.Actions();
        var number = await svc.CreateAsync(new ActionForm { Title = "Sweep", TeamId = team, AssigneeId = "dev:robin" });

        var form = await Form(number);
        (form.AssigneeId, form.AssigneeName) = ("dev:stranger", "Stranger");
        await svc.Invoking(s => s.SaveAsync(form)).Should().ThrowAsync<ArgumentException>().WithMessage("*isn't on the action's team*");
        (await svc.GetAsync(number))!.Action.AssigneeId.Should().Be("dev:robin", "nothing was saved");

        form = await Form(number);
        (form.AssigneeId, form.AssigneeName) = ("dev:alex", "Alex");
        await svc.SaveAsync(form);
        (await svc.GetAsync(number))!.Action.AssigneeName.Should().Be("Alex");
    }

    [Fact]
    public async Task AHolderFromBeforeTheRule_IsStillShown_AndAnEditThatKeepsThemSaves()
    {
        var team = await TeamWith("Cyber Defense", ("dev:robin", "Robin"));
        var number = await HeldByAnOutsider(team);
        var svc = _db.Actions();

        var form = await Form(number);
        (form.AssigneeId, form.AssigneeName).Should().Be(("dev:outsider", "Outsider"), "the editor starts from who holds it");
        form.Title = "From before the rule, retitled";
        await svc.SaveAsync(form);

        var a = (await svc.GetAsync(number))!.Action;
        (a.Title, a.AssigneeId, a.AssigneeName).Should().Be(("From before the rule, retitled", "dev:outsider", "Outsider"));

        // Given back to the team, they can't be given it again from the editor.
        form = await Form(number);
        (form.AssigneeId, form.AssigneeName) = (null, null);
        await svc.SaveAsync(form);
        form = await Form(number);
        (form.AssigneeId, form.AssigneeName) = ("dev:outsider", "Outsider");
        await svc.Invoking(s => s.SaveAsync(form)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MovingToAnotherTeam_KeepsTheHolderOnlyIfTheyreOnIt()
    {
        var from = await TeamWith("Cyber Defense", ("dev:robin", "Robin"), ("dev:alex", "Alex"));
        var to = await TeamWith("Vulnerability Management", ("dev:robin", "Robin"));
        var svc = _db.Actions();

        var kept = await svc.CreateAsync(new ActionForm { Title = "Robin's", TeamId = from, AssigneeId = "dev:robin" });
        var form = await Form(kept);
        form.TeamId = to;
        await svc.SaveAsync(form);
        (await svc.GetAsync(kept))!.Action.AssigneeId.Should().Be("dev:robin", "Robin is on both teams");

        var dropped = await svc.CreateAsync(new ActionForm { Title = "Alex's", TeamId = from, AssigneeId = "dev:alex" });
        form = await Form(dropped);
        form.TeamId = to;
        await svc.SaveAsync(form);
        var a = (await svc.GetAsync(dropped))!.Action;
        (a.TeamId, a.AssigneeId).Should().Be((to, (string?)null), "Alex isn't on the new team: it's that team's to give out");

        var outsider = await HeldByAnOutsider(from);
        form = await Form(outsider);
        form.TeamId = to;
        await svc.SaveAsync(form);
        (await svc.GetAsync(outsider))!.Action.AssigneeId.Should().BeNull("a holder from before the rule doesn't follow it to a new team either");
    }

    [Fact]
    public async Task AQuickAddInTheMeeting_IsTheRaisersOnlyWhenTheyreOnTheTeam()
    {
        var team = await TeamWith("Cyber Defense", ("dev:robin", "Robin"));
        var article = await _db.Articles().CreateAsync("Edge VPN");
        var svc = _db.Actions();

        var notOnIt = await svc.QuickAddAsync(article, "Patch the VPN", ActionKind.PatchRemediate, team, null);
        (await svc.GetAsync(notOnIt.Number))!.Action.AssigneeId.Should().BeNull("the analyst raising it isn't on the team");
        notOnIt.Yours.Should().BeFalse("the caller is told it was left with the team");

        await _db.Teams().AddMemberAsync(team, _db.User.UserId, _db.User.DisplayName);
        var onIt = await svc.QuickAddAsync(article, "Sweep for the C2", ActionKind.IocSweep, team, null);
        (await svc.GetAsync(onIt.Number))!.Action.AssigneeId.Should().Be(_db.User.UserId);
        onIt.Yours.Should().BeTrue();
    }

    // Whether the actions list offers the reader Take it on the action.
    private async Task<bool> ListOffersTake(Guid id)
    {
        var page = await _db.Actions().PageAsync(new ActionFilter());
        return page.Workers.CanTake(page.Rows.Single(r => r.Id == id));
    }

    [Fact]
    public async Task TakingItOn_IsForAMemberOfTheTeam_UnderTheNameTheTeamKnows()
    {
        var team = await TeamWith("Cyber Defense", ("dev:robin", "Robin"));
        var svc = _db.Actions();
        var number = await svc.CreateAsync(new ActionForm { Title = "Sweep", TeamId = team });
        var id = (await svc.GetAsync(number))!.Action.Id;

        // The analyst may work any action, but isn't on this team: no Take it, and the service refuses it.
        var detail = (await svc.GetAsync(number))!;
        (detail.CanWork, detail.OnTeam).Should().Be((true, false), "an analyst works every team's actions");
        (await ListOffersTake(id)).Should().BeFalse("the list offers Take it only to the team's members");
        await svc.Invoking(s => s.TakeAsync(id)).Should().ThrowAsync<StaleEditException>()
            .WithMessage($"You're not on Cyber Defense, so you can't take A-{number} on.");
        (await svc.GetAsync(number))!.Action.AssigneeId.Should().BeNull("nothing was taken");

        await _db.Teams().AddMemberAsync(team, _db.User.UserId, "Tester On The Team");
        (await svc.GetAsync(number))!.OnTeam.Should().BeTrue();
        (await ListOffersTake(id)).Should().BeTrue();
        await svc.TakeAsync(id);
        var a = (await svc.GetAsync(number))!.Action;
        (a.AssigneeId, a.AssigneeName, a.Status).Should().Be((_db.User.UserId, "Tester On The Team", ActionStatus.InProgress));
    }

    [Fact]
    public async Task HandedToAnotherTeam_ItsNoLongerAFormerMembersToTake()
    {
        var from = await TeamWith("Cyber Defense", (_db.User.UserId, "Tester"));
        var to = await TeamWith("Vulnerability Management", ("dev:robin", "Robin"));
        var svc = _db.Actions();
        var number = await svc.CreateAsync(new ActionForm { Title = "Patch", TeamId = from });
        var id = (await svc.GetAsync(number))!.Action.Id;
        (await svc.GetAsync(number))!.OnTeam.Should().BeTrue();

        await _db.Handoffs().HandOffAsync(id, to, "Theirs to patch");

        (await svc.GetAsync(number))!.OnTeam.Should().BeFalse();
        (await ListOffersTake(id)).Should().BeFalse();
        var board = await svc.BoardAsync(new ActionFilter());
        board.CanTake(board.Cards.Single(c => c.Id == id)).Should().BeFalse("nor does the board");
        await svc.Invoking(s => s.TakeAsync(id)).Should().ThrowAsync<StaleEditException>().WithMessage("You're not on Vulnerability Management*");
    }

    [Fact]
    public async Task AHolderFromBeforeTheRule_KeepsTheAction_ThoughTheyCantTakeAnother()
    {
        var team = await TeamWith("Cyber Defense", ("dev:robin", "Robin"));
        var held = await HeldByAnOutsider(team);
        _db.User.UserId = "dev:outsider";
        var svc = _db.Actions();
        var a = (await svc.GetAsync(held))!.Action;
        a.AssigneeId.Should().Be("dev:outsider", "they keep what they hold");
        await svc.SetStatusAsync(a.Id, ActionStatus.InProgress, ActionOutcome.None, null);
        (await svc.GetAsync(held))!.Action.AssigneeId.Should().Be("dev:outsider", "working it doesn't take it from them");

        var other = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Another", TeamId = team })))!.Action.Id;
        await svc.Invoking(s => s.TakeAsync(other)).Should().ThrowAsync<StaleEditException>().WithMessage("You're not on Cyber Defense*");
    }
}
