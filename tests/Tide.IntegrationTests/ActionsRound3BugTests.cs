using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Files;
using Tide.Application.Org;
using Tide.Application.Rotation;
using Tide.Application.Trends;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.TestSupport;

namespace Tide.IntegrationTests;

/// <summary>Bug round 3, actions: a refused hunt recording leaves no evidence file behind, and two recordings at once
/// can't both land; a period still to come has no backlog; and a swap or cover approved after its days began is refused.</summary>
public class ActionsRound3BugTests : IDisposable
{
    private readonly TestDb _db = new();

    public ActionsRound3BugTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<(ActionService Svc, int Number, ActionItem Action)> HuntAction()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Hunt", Kind = ActionKind.ThreatHunt, TeamId = team });
        return (svc, n, (await svc.GetAsync(n))!.Action);
    }

    [Fact]
    public async Task EvidenceForARefusedRecording_IsDiscarded_ButEvidenceAHuntHoldsIsKept()
    {
        var (svc, _, action) = await HuntAction();
        var hunt = await svc.AddHandWrittenHuntAsync(action.Id, "kql", "DeviceProcessEvents | take 1");
        var shots = _db.Screenshots();
        await svc.RecordHuntResultAsync(hunt.Id, HuntResult.NoHits, null, null, null);

        // A colleague recorded it first: the panel had already stored the screenshot, and nothing holds it.
        var orphan = await shots.StoreEvidenceAsync(new ScreenshotUpload("late.png", TestDb.Png));
        await FluentActions.Invoking(() => svc.RecordHuntResultAsync(hunt.Id, HuntResult.Hits, 2, null, orphan))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*already recorded*");
        await shots.DiscardEvidenceAsync(orphan);
        _db.Store.Files.Should().NotContainKey(orphan, "the bytes went");
        await using (var ctx = _db.Context()) (await ctx.Files.AnyAsync(f => f.Id == orphan)).Should().BeFalse("and so did its row");

        // A file a hunt holds stays, whoever asks.
        var other = await svc.AddHandWrittenHuntAsync(action.Id, "kql", "DeviceFileEvents | take 1");
        var held = await shots.StoreEvidenceAsync(new ScreenshotUpload("result.png", TestDb.Png));
        await svc.RecordHuntResultAsync(other.Id, HuntResult.Hits, 1, null, held);
        await shots.DiscardEvidenceAsync(held);
        _db.Store.Files.Should().ContainKey(held);
        (await shots.OpenAttachmentAsync(held)).Should().NotBeNull();
    }

    [Fact]
    public async Task TwoRecordingsOfAQueryAtOnce_TheSecondIsRefused_NotWrittenOverTheFirst()
    {
        var (svc, n, action) = await HuntAction();
        var hunt = await svc.AddHandWrittenHuntAsync(action.Id, "kql", "DeviceProcessEvents | take 1");

        // A colleague's "No hits, close" lands between this recording's check and its save (the colleague's save moves
        // the action's version, as every recording now does).
        _db.BeforeSave = _ =>
        {
            _db.BeforeSave = null;
            using var ctx = _db.PlainContext();
            ctx.HuntQueries.Where(h => h.Id == hunt.Id).ExecuteUpdate(u => u.SetProperty(h => h.Result, HuntResult.NoHits).SetProperty(h => h.ResultCount, 0));
            ctx.Actions.Where(a => a.Id == action.Id).ExecuteUpdate(u => u.SetProperty(a => a.Status, ActionStatus.Done)
                .SetProperty(a => a.Outcome, ActionOutcome.NoHits).SetProperty(a => a.EditVersion, Guid.NewGuid()));
        };
        await FluentActions.Invoking(() => svc.RecordHuntResultAsync(hunt.Id, HuntResult.Hits, 4, "Four hosts", null))
            .Should().ThrowAsync<StaleEditException>();

        var saved = (await svc.GetAsync(n))!.Action;
        saved.Hunts.Single().Result.Should().Be(HuntResult.NoHits, "the action was closed on it, and hits used to overwrite that");
        saved.Outcome.Should().Be(ActionOutcome.NoHits);
    }

    [Fact]
    public async Task APeriodNotStarted_HasNothingOpenOrOverdueAtItsEnd()
    {
        var team = await _db.Teams().AddAsync("Vulnerability Management", null, null);
        await _db.Actions().CreateAsync(new ActionForm
        {
            Title = "Patch the VPN", Kind = ActionKind.PatchRemediate, TeamId = team, DueDate = _db.Clock.Today.AddDays(-3),
        });
        var now = Period.Of(PeriodKind.Month, _db.Clock.Today);
        var report = await _db.Trends().ReportAsync(now.Next);

        report.Find("actions:open-at-end")!.Current.Should().Be(0, "next month hasn't started: today's backlog isn't in it");
        report.Find("actions:overdue-at-end")!.Current.Should().Be(0);
        report.Find("actions:open-at-end")!.Previous.Should().Be(1, "this month to date still counts it");
        report.Find("actions:overdue-at-end")!.Previous.Should().Be(1);
    }

}
