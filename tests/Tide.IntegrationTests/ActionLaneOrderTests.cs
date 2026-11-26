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
    public async Task Place_PutsTheCardBeforeAnother_OrAtTheEnd_AndRenumbersTheLane()
    {
        var made = await Seed();
        var svc = _db.Actions();
        (await Lane()).Should().Equal([4, 3, 5, 1, 2], "nothing placed yet: by due date");

        await svc.PlaceAsync(made[2], ActionStatus.Open, made[4]);
        (await Lane()).Should().Equal([2, 4, 3, 5, 1]);
        var ranks = await Ranks();
        new[] { 2, 4, 3, 5, 1 }.Select(n => ranks[n]).Should().Equal([1, 2, 3, 4, 5], "the whole lane is numbered from the top");
        ranks[7].Should().Be(0, "another lane is left alone");

        // A card raised since is unplaced: under the placed ones, whatever its due date.
        await using (var ctx = _db.Context())
        {
            ctx.Actions.Add(new ActionItem
            {
                Number = 6, Title = "Lane card 6", TeamId = (await ctx.Actions.FirstAsync()).TeamId, Kind = ActionKind.Block,
                Priority = Priority.P1, DueDate = _db.Clock.Today,
            });
            await ctx.SaveChangesAsync();
        }
        (await Lane()).Should().Equal([2, 4, 3, 5, 1, 6]);
        (await Lane(ActionBoardSort.Due)).Should().Equal([6, 4, 3, 5, 1, 2], "the other sorts ignore the places");

        await svc.PlaceAsync(made[1], ActionStatus.Open, null);
        (await Lane()).Should().Equal([2, 4, 3, 5, 6, 1], "null is the end of the lane");
        ranks = await Ranks();
        new[] { 2, 4, 3, 5, 6, 1 }.Select(n => ranks[n]).Should().Equal([1, 2, 3, 4, 5, 6], "a second placement renumbers 1..n again");

        await svc.PlaceAsync(made[3], ActionStatus.Open, made[7]);
        (await Lane()).Should().Equal([2, 4, 5, 6, 1, 3], "a card that isn't in the lane is no place: the end");
        await svc.PlaceAsync(made[3], ActionStatus.Open, made[3]);
        (await Lane()).Should().Equal([2, 4, 5, 6, 1, 3], "nor is the card itself");
    }

    [Fact]
    public async Task Place_AddsNoHistoryLine_AndLeavesTheEditVersion()
    {
        var made = await Seed();
        Guid version;
        int events;
        await using (var ctx = _db.Context())
            (version, events) = (await ctx.Actions.Where(a => a.Id == made[2]).Select(a => a.EditVersion).SingleAsync(), await ctx.ActionEvents.CountAsync());

        await _db.Actions().PlaceAsync(made[2], ActionStatus.Open, made[4]);

        await using var after = _db.Context();
        (await after.Actions.Where(a => a.Id == made[2]).Select(a => a.EditVersion).SingleAsync()).Should().Be(version, "an edit form opened before still saves");
        (await after.ActionEvents.CountAsync()).Should().Be(events, "a place isn't a change to the action");
    }

    [Fact]
    public async Task AMove_TakesTheCardOutOfTheOrder_ToTheEndOfItsNewLane()
    {
        var made = await Seed();
        var svc = _db.Actions();
        await svc.PlaceAsync(made[2], ActionStatus.Open, made[4]);
        await svc.PlaceAsync(made[7], ActionStatus.InProgress, null);
        (await Ranks())[7].Should().Be(1);

        await svc.MoveAsync(made[2], ActionStatus.Open, ActionStatus.InProgress);
        (await Ranks())[2].Should().Be(0, "it left its place with its lane");
        (await Lane(status: ActionStatus.InProgress)).Should().Equal([7, 2], "after the placed cards of its new lane");

        await svc.MoveAsync(made[2], ActionStatus.InProgress, ActionStatus.Open);
        (await Lane()).Should().Equal([4, 3, 5, 1, 2], "back with the unplaced cards, by due date");
    }

    [Fact]
    public async Task Place_IsRefused_ForAClosedStatus_AMovedCard_OrSomeoneWhoCantWorkIt()
    {
        var made = await Seed();
        var svc = _db.Actions();

        await FluentActions.Awaiting(() => svc.PlaceAsync(made[1], ActionStatus.Done, null)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => svc.PlaceAsync(made[1], ActionStatus.InProgress, null)).Should().ThrowAsync<StaleEditException>("it's in Open now");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Awaiting(() => svc.PlaceAsync(made[1], ActionStatus.Open, null)).Should().ThrowAsync<ForbiddenException>();
        (await Ranks()).Values.Should().AllSatisfy(r => r.Should().Be(0), "nothing was placed");
    }
}
