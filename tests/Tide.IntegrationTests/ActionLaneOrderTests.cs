using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// The board's lane order: the shared manual order people set by dragging (placed cards first, then the rest by due
/// date), or by due date, or by priority, each sorted in the database. Placing a card renumbers its whole lane from 1; a
/// move to another lane takes it out of the order, so it goes to the end of the new lane's.
/// </summary>
public class ActionLaneOrderTests : IDisposable
{
    private TestDb _db = new();

    public ActionLaneOrderTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    // Five open actions with due dates and priorities that put them in a different order under each sort, and one in
    // progress (another lane). Numbered from 1, written straight in. By due date: 4, 3, 5, 1, 2.
    private async Task<Dictionary<int, Guid>> Seed()
    {
        var team = await _db.Teams().AddAsync("Lane team", null, null);
        var today = _db.Clock.Today;
        (int Number, int? Due, Priority Priority, ActionStatus Status)[] rows =
        [
            (1, 3, Priority.P3, ActionStatus.Open),
            (2, null, Priority.P1, ActionStatus.Open),
            (3, 1, Priority.P3, ActionStatus.Open),
            (4, 1, Priority.P1, ActionStatus.Open),
            (5, 2, Priority.P2, ActionStatus.Open),
            (7, 1, Priority.P2, ActionStatus.InProgress),
        ];
        await using var ctx = _db.Context();
        var made = new Dictionary<int, Guid>();
        foreach (var (number, due, priority, status) in rows)
        {
            var a = new ActionItem
            {
                Number = number, Title = $"Lane card {number}", TeamId = team, Kind = ActionKind.Block, Priority = priority,
                Status = status, DueDate = due is { } d ? today.AddDays(d) : null,
            };
            ctx.Actions.Add(a);
            made[number] = a.Id;
        }
        await ctx.SaveChangesAsync();
        return made;
    }

    private async Task<IEnumerable<int>> Lane(ActionBoardSort sort = ActionBoardSort.Manual, ActionStatus status = ActionStatus.Open) =>
        (await _db.Actions().BoardAsync(new ActionFilter(ActionView.Open), sort)).In(status).Select(c => c.Number);

    private async Task<Dictionary<int, int>> Ranks()
    {
        await using var ctx = _db.Context();
        return await ctx.Actions.ToDictionaryAsync(a => a.Number, a => a.LaneRank);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task EachSort_OrdersTheLane_InTheDatabase(string provider)
    {
        _db = _db.On(provider);
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var made = await Seed();
        await using (var ctx = _db.Context())
        {
            await ctx.Actions.Where(a => a.Id == made[1]).ExecuteUpdateAsync(s => s.SetProperty(a => a.LaneRank, 2));
            await ctx.Actions.Where(a => a.Id == made[2]).ExecuteUpdateAsync(s => s.SetProperty(a => a.LaneRank, 1));
        }

        (await Lane(ActionBoardSort.Due)).Should().Equal([4, 3, 5, 1, 2], "no due date last, then due date, priority, number");
        (await Lane(ActionBoardSort.Priority)).Should().Equal([4, 2, 5, 3, 1], "priority, then due date with none last");
        (await Lane(ActionBoardSort.Manual)).Should().Equal([2, 1, 4, 3, 5], "the placed cards by their place, then the rest by due date");
        (await Lane()).Should().Equal([2, 1, 4, 3, 5], "manual is the default");
    }

    [Fact]
    public async Task Place_PutsTheCardBeforeAnother_OrAtTheEnd_WritingOnlyItsRow_UntilTheLaneNeedsNumbering()
    {
        var made = await Seed();
        var svc = _db.Actions();
        (await Lane()).Should().Equal([4, 3, 5, 1, 2], "nothing placed yet: by due date");

        // At the top, above unplaced cards: the card alone takes a place.
        await svc.PlaceAsync(made[2], ActionStatus.Open, new LanePlace(made[4]));
        (await Lane()).Should().Equal([2, 4, 3, 5, 1]);
        var ranks = await Ranks();
        ranks[2].Should().Be(1024);
        new[] { 4, 3, 5, 1, 7 }.Select(n => ranks[n]).Should().AllBeEquivalentTo(0, "no other row is written");

        // A card raised since is unplaced: under the placed ones, among the unplaced by its due date.
        await using (var ctx = _db.Context())
        {
            ctx.Actions.Add(new ActionItem
            {
                Number = 6, Title = "Lane card 6", TeamId = (await ctx.Actions.FirstAsync()).TeamId, Kind = ActionKind.Block,
                Priority = Priority.P1, DueDate = _db.Clock.Today,
            });
            await ctx.SaveChangesAsync();
        }
        (await Lane()).Should().Equal([2, 6, 4, 3, 5, 1]);
        (await Lane(ActionBoardSort.Due)).Should().Equal([6, 4, 3, 5, 1, 2], "the other sorts ignore the places");

        // At the end, under unplaced cards: they'd sort after it, so the lane is numbered from the top.
        await svc.PlaceAsync(made[1], ActionStatus.Open, new LanePlace(null));
        (await Lane()).Should().Equal([2, 6, 4, 3, 5, 1], "null is the end of the lane");
        ranks = await Ranks();
        new[] { 2, 6, 4, 3, 5, 1 }.Select(n => ranks[n]).Should().Equal([1024, 2048, 3072, 4096, 5120, 6144]);

        // Between two placed cards: halfway, its row alone.
        await svc.PlaceAsync(made[1], ActionStatus.Open, new LanePlace(made[4]));
        (await Lane()).Should().Equal([2, 6, 1, 4, 3, 5]);
        ranks = await Ranks();
        ranks[1].Should().Be(2560);
        new[] { 2, 6, 4, 3, 5 }.Select(n => ranks[n]).Should().Equal([1024, 2048, 3072, 4096, 5120], "the others keep theirs");

        await svc.PlaceAsync(made[3], ActionStatus.Open, new LanePlace(made[7]));
        (await Lane()).Should().Equal([2, 6, 1, 4, 5, 3], "a card that isn't in the lane is no place: the end");
        await svc.PlaceAsync(made[3], ActionStatus.Open, new LanePlace(made[3]));
        (await Lane()).Should().Equal([2, 6, 1, 4, 5, 3], "nor is the card itself");
    }

    [Fact]
    public async Task Place_AddsNoHistoryLine_LeavesTheEditVersion_AndIsntAudited()
    {
        var made = await Seed();
        Guid version;
        int events, audited;
        DateTimeOffset? modified;
        await using (var ctx = _db.Context())
        {
            var row = await ctx.Actions.Where(a => a.Id == made[2]).Select(a => new { a.EditVersion, a.ModifiedAtUtc }).SingleAsync();
            (version, modified, events, audited) = (row.EditVersion, row.ModifiedAtUtc, await ctx.ActionEvents.CountAsync(), await ctx.AuditLog.CountAsync());
        }

        await _db.Actions().PlaceAsync(made[2], ActionStatus.Open, new LanePlace(made[4]));

        await using var after = _db.Context();
        var now = await after.Actions.Where(a => a.Id == made[2]).Select(a => new { a.EditVersion, a.ModifiedAtUtc }).SingleAsync();
        now.EditVersion.Should().Be(version, "an edit form opened before still saves");
        now.ModifiedAtUtc.Should().Be(modified, "the board's arrangement isn't a change to the action");
        (await after.ActionEvents.CountAsync()).Should().Be(events, "a place isn't a change to the action");
        (await after.AuditLog.CountAsync()).Should().Be(audited, "nor is it audited");
    }

    [Fact]
    public async Task AMove_TakesTheCardOutOfTheOrder_ToTheEndOfItsNewLane_OrToTheDropsPlace()
    {
        var made = await Seed();
        var svc = _db.Actions();
        await svc.PlaceAsync(made[2], ActionStatus.Open, new LanePlace(made[4]));
        await svc.PlaceAsync(made[7], ActionStatus.InProgress, new LanePlace(null));
        (await Ranks())[7].Should().Be(1024);

        await svc.MoveAsync(made[2], ActionStatus.Open, ActionStatus.InProgress);
        (await Ranks())[2].Should().Be(0, "it left its place with its lane");
        (await Lane(status: ActionStatus.InProgress)).Should().Equal([7, 2], "after the placed cards of its new lane");

        await svc.MoveAsync(made[2], ActionStatus.InProgress, ActionStatus.Open);
        (await Lane()).Should().Equal([4, 3, 5, 1, 2], "back with the unplaced cards, by due date");

        // Dropped at a place in the new lane: moved and placed in one save.
        await svc.MoveAsync(made[2], ActionStatus.Open, ActionStatus.InProgress, place: new LanePlace(made[7]));
        (await Lane(status: ActionStatus.InProgress)).Should().Equal([2, 7]);
    }

    [Fact]
    public async Task Place_IsRefused_ForAClosedStatus_AMovedCard_OrSomeoneWhoCantWorkIt()
    {
        var made = await Seed();
        var svc = _db.Actions();

        await FluentActions.Awaiting(() => svc.PlaceAsync(made[1], ActionStatus.Done, new LanePlace(null))).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => svc.PlaceAsync(made[1], ActionStatus.InProgress, new LanePlace(null))).Should().ThrowAsync<StaleEditException>("it's in Open now");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Awaiting(() => svc.PlaceAsync(made[1], ActionStatus.Open, new LanePlace(null))).Should().ThrowAsync<ForbiddenException>();
        (await Ranks()).Values.Should().AllSatisfy(r => r.Should().Be(0), "nothing was placed");
    }
}
