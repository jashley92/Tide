using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>Bug round 1, actions: an old due date no longer blocks every edit, a team named by a card rule is retired
/// rather than failing to delete, a hunt's result is recorded once, and an action's row carries the day it was raised for.</summary>
public class ActionsRound1BugTests : IDisposable
{
    private readonly TestDb _db = new();

    public ActionsRound1BugTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    // An action whose due date is now more than a year gone: created as any is, then its date moved back in the database
    // (as time would have moved it).
    private async Task<(ActionItem Action, Guid Team)> LongOverdue()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        await _db.Teams().AddMemberAsync(team, "S-1-5-21-OWNER", "Owner");
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Retire the old VPN", TeamId = team, DueDate = svc.Today });
        await using (var ctx = _db.Context())
        {
            (await ctx.Actions.SingleAsync(a => a.Number == n)).DueDate = svc.Today.AddYears(-1).AddDays(-30);
            await ctx.SaveChangesAsync();
        }
        return ((await svc.GetAsync(n))!.Action, team);
    }

    [Fact]
    public async Task AnActionOverdueForOverAYear_CanStillBeEdited_AndReassignedAtTheMeeting()
    {
        var (action, _) = await LongOverdue();
        var svc = _db.Actions();
        var old = action.DueDate;

        var form = ActionForm.From(action);
        form.TicketRef = "CHG0012345";
        await svc.SaveAsync(form);
        await svc.AssignAsync(action.Id, "S-1-5-21-OWNER", old);

        var saved = (await svc.GetAsync(action.Number))!.Action;
        saved.TicketRef.Should().Be("CHG0012345");
        saved.AssigneeName.Should().Be("Owner");
        saved.DueDate.Should().Be(old, "the date it had is kept, not refused");
    }

    [Fact]
    public async Task ADueDateSetMoreThanAYearBack_IsStillRefused()
    {
        var (action, _) = await LongOverdue();
        var svc = _db.Actions();
        var form = ActionForm.From(action);
        form.DueDate = svc.Today.AddYears(-2);

        var edit = () => svc.SaveAsync(form);
        await edit.Should().ThrowAsync<ArgumentException>().WithMessage("*too far in the past*");
        var meeting = () => svc.AssignAsync(action.Id, null, svc.Today.AddYears(-2));
        await meeting.Should().ThrowAsync<ArgumentException>().WithMessage("*too far in the past*");
    }

    [Fact]
    public async Task RetiringATeamThatOwnsNoAction_ButACardRuleNames_RetiresIt_RatherThanFailing()
    {
        var teams = _db.Teams();
        var named = await teams.AddAsync("IAM", null, null);
        var unnamed = await teams.AddAsync("Network", null, null);
        await _db.CardRules().SaveAsync(new ActionCardRule { Name = "IAM's", TeamId = named });

        await teams.SetRetiredAsync(named, true);
        await teams.SetRetiredAsync(unnamed, true);

        var all = await teams.ListAsync(includeRetired: true);
        all.Should().ContainSingle(t => t.Id == named).Which.IsRetired.Should().BeTrue("the rule still names it");
        all.Should().NotContain(t => t.Id == unnamed, "a team nothing refers to is deleted, as before");
        (await _db.CardRules().ListAsync()).Should().ContainSingle(r => r.TeamId == named);
    }

    [Fact]
    public async Task AHuntsResult_IsRecordedOnce()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Hunt", Kind = ActionKind.ThreatHunt, TeamId = team });
        var action = (await svc.GetAsync(n))!.Action;
        var hunt = await svc.AddHandWrittenHuntAsync(action.Id, "kql", "DeviceProcessEvents | where FileName == \"sh\"");
        await svc.CloseWithNoHitsAsync(hunt.Id, 30, checkedLeftOut: false);

        var again = () => svc.RecordHuntResultAsync(hunt.Id, HuntResult.Hits, 4, "Four hosts", null);

        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already recorded*");
        var saved = (await svc.GetAsync(n))!.Action;
        saved.Hunts.Single().Result.Should().Be(HuntResult.NoHits, "the action was closed on it");
        saved.Outcome.Should().Be(ActionOutcome.NoHits);
    }

    [Fact]
    public async Task AnActionsRow_CarriesTheDayItWasRaisedFor()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var svc = _db.Actions();
        var pastDay = svc.Today.AddDays(-9);
        var raised = await svc.CreateAsync(new ActionForm { Title = "For last week's brief", TeamId = team, RaisedOn = pastDay });
        var now = await svc.CreateAsync(new ActionForm { Title = "Today's", TeamId = team });

        var rows = await svc.ListAsync(new ActionFilter(ActionView.Open));

        rows.Single(r => r.Number == raised).RaisedOn.Should().Be(pastDay, "Trends lists it as raised on the day it counts on");
        rows.Single(r => r.Number == now).RaisedOn.Should().BeNull();
    }
}
