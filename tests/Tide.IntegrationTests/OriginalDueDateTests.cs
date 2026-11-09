using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>
/// An action's first due date is kept, and every later move counted, wherever the date is set: creating it (a date given
/// or the SLA target's), the editor, Record an update, the meeting, a split's parts and the meeting's quick add. A
/// hand-off, or an edit that keeps the date, moves nothing.
/// </summary>
public class OriginalDueDateTests
{
    private static TestDb Db(string provider = TestDb.Sqlite)
    {
        var db = new TestDb(provider: provider);
        db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        return db;
    }

    private static async Task<ActionItem> Read(TestDb db, int number)
    {
        await using var c = db.Context();
        return await c.Actions.AsNoTracking().SingleAsync(a => a.Number == number);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheFirstDate_IsSetOnce_AndEachMoveIsCounted_WhereverTheDateMoves(string provider)
    {
        using var db = Db(provider);
        var team = await db.Teams().AddAsync("Network Engineering", null, null);
        var other = await db.Teams().AddAsync("Service Desk", null, null);
        var svc = db.Actions();
        var today = svc.Today;
        var first = today.AddDays(5);

        var number = await svc.CreateAsync(new ActionForm { Title = "Patch the edge VPN", TeamId = team, DueDate = first });
        var a = await Read(db, number);
        (a.OriginalDueDate, a.DueDateMoves).Should().Be((first, 0));

        // The editor, keeping the date: nothing moves.
        var form = ActionForm.From(a);
        form.Title = "Patch the edge VPN appliances";
        await svc.SaveAsync(form);
        (await Read(db, number)).DueDateMoves.Should().Be(0, "an edit that keeps the date doesn't move it");

        // The editor, moving it.
        form = ActionForm.From(await Read(db, number));
        form.DueDate = today.AddDays(9);
        await svc.SaveAsync(form);
        a = await Read(db, number);
        (a.DueDate, a.OriginalDueDate, a.DueDateMoves).Should().Be((today.AddDays(9), first, 1));

        // Record an update, moving it with a reason.
        await svc.RecordUpdateAsync(new TeamUpdate
        {
            ActionId = a.Id, From = a.Status, Status = a.Status, DueDate = today.AddDays(14), DueReason = "their change window",
        });
        a = await Read(db, number);
        (a.DueDate, a.OriginalDueDate, a.DueDateMoves).Should().Be((today.AddDays(14), first, 2));

        // The meeting's date picker, moving it earlier: a move either way counts.
        await svc.SetDueAsync(a.Id, today.AddDays(7));
        a = await Read(db, number);
        (a.DueDate, a.OriginalDueDate, a.DueDateMoves).Should().Be((today.AddDays(7), first, 3));

        // The meeting's assignment, its date left as it is, and a hand-off: neither moves it.
        await svc.AssignAsync(a.Id, null, null);
        await db.Handoffs().HandOffAsync(a.Id, other, "The fix is theirs");
        a = await Read(db, number);
        (a.DueDate, a.OriginalDueDate, a.DueDateMoves, a.TeamId).Should().Be((today.AddDays(7), first, 3, other));

        // Cleared in the editor, then given one again: each is a move, and the first date stays.
        form = ActionForm.From(a);
        form.DueDate = null;
        await svc.SaveAsync(form);
        form = ActionForm.From(await Read(db, number));
        form.DueDate = today.AddDays(20);
        await svc.SaveAsync(form);
        a = await Read(db, number);
        (a.DueDate, a.OriginalDueDate, a.DueDateMoves).Should().Be((today.AddDays(20), first, 5));
    }

    [Fact]
    public async Task ABlankDate_TakesTheSlaTarget_AsItsFirst_AndAPartOrAQuickAction_StartsFromItsOwn()
    {
        using var db = Db();
        var team = await db.Teams().AddAsync("Cyber Defense", null, null);
        var network = await db.Teams().AddAsync("Network Engineering", null, null);
        var svc = db.Actions();
        var today = svc.Today;

        var sla = await svc.CreateAsync(new ActionForm { Title = "SLA date", TeamId = team, Priority = Priority.P2 });
        var a = await Read(db, sla);
        a.DueDate.Should().NotBeNull("a blank date takes the SLA target's");
        (a.OriginalDueDate, a.DueDateMoves).Should().Be((a.DueDate, 0));

        // A part takes the parent's date (or its own) as its first: a part's commitment is its own.
        await svc.SetDueAsync(a.Id, today.AddDays(30));
        var parts = await db.Handoffs().SplitAsync(a.Id, [new SplitPart(network), new SplitPart(team, today.AddDays(12))]);
        var inherited = await Read(db, parts[0]);
        (inherited.DueDate, inherited.OriginalDueDate, inherited.DueDateMoves).Should().Be((today.AddDays(30), today.AddDays(30), 0));
        var own = await Read(db, parts[1]);
        (own.OriginalDueDate, own.DueDateMoves).Should().Be((today.AddDays(12), 0));

        var article = await db.Articles().CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);
        var quick = await svc.QuickAddAsync(article, "Block the C2 domains", ActionKind.Block, team, today.AddDays(2));
        var q = await Read(db, quick);
        (q.OriginalDueDate, q.DueDateMoves).Should().Be((today.AddDays(2), 0));
    }

    [Fact]
    public void TheDate_IsKeptBySettingIt_NotByLoadingIt()
    {
        var a = new ActionItem();
        (a.OriginalDueDate, a.DueDateMoves).Should().Be((null, 0), "no date yet");
        a.DueDate = new DateOnly(2026, 11, 10);
        (a.OriginalDueDate, a.DueDateMoves).Should().Be((new DateOnly(2026, 11, 10), 0), "the first date given is the original");
        a.DueDate = new DateOnly(2026, 11, 10);
        a.DueDateMoves.Should().Be(0, "setting the date it has isn't a move");
        a.DueDate = new DateOnly(2026, 11, 3);
        (a.OriginalDueDate, a.DueDateMoves).Should().Be((new DateOnly(2026, 11, 10), 1));
        ActionPage.MovedNote(a).Should().Be("Originally due Tue Nov 10 · moved 1 time");
        a.DueDate = new DateOnly(2026, 11, 24);
        ActionPage.MovedNote(a).Should().Be("Originally due Tue Nov 10 · moved 2 times");
        ActionPage.MovedNote(new ActionItem { DueDate = new DateOnly(2026, 11, 10) }).Should().BeNull("a date that never moved says nothing");
    }
}
