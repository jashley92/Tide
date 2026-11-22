using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Several actions changed at once from the Actions list (<see cref="ActionService.ManyAsync"/>): each checked, changed and
/// recorded as that one action alone would be, independently, so a stale or forbidden one is refused, named and left as
/// it was while the rest go ahead; one reason asked for all; no hits only on a query that was run.
/// </summary>
public class ManyActionsTests : IDisposable
{
    private readonly TestDb _db = new();

    public ManyActionsTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private string Me => _db.User.UserId;

    private async Task<Guid> Team(string name, bool member = true)
    {
        var id = await _db.Teams().AddAsync(name, null, null);
        if (member) await _db.Teams().AddMemberAsync(id, Me, "Test Analyst");
        return id;
    }

    private async Task<int> Raise(Guid team, string title, ActionKind kind = ActionKind.Block) =>
        await _db.Actions().CreateAsync(new ActionForm { Title = title, TeamId = team, Kind = kind });

    // The rows as the list read them, with their edit versions.
    private async Task<IReadOnlyList<ActionRow>> Rows(params int[] numbers)
    {
        await using var ctx = _db.Context();
        var ids = await ctx.Actions.Where(a => numbers.Contains(a.Number)).Select(a => a.Id).ToListAsync();
        return await _db.Actions().ListByIdAsync(ids);
    }

    private static Dictionary<Guid, Guid> Versions(IEnumerable<ActionRow> rows) => rows.ToDictionary(r => r.Id, r => r.EditVersion);

    private Task<ManyResult> Many(IReadOnlyList<ActionRow> rows, ManyOp op) =>
        _db.Actions().ManyAsync([.. rows.Select(r => r.Id)], op, Versions(rows));

    private async Task<ActionItem> Action(int number)
    {
        await using var ctx = _db.Context();
        return await ctx.Actions.AsNoTracking().Include(a => a.Events).SingleAsync(a => a.Number == number);
    }

    [Fact]
    public async Task OneThatChangedSinceTheListWasRead_IsRefusedAndNamed_AndTheRestAreDone()
    {
        var team = await Team("Cyber Defense");
        int[] numbers = [await Raise(team, "One"), await Raise(team, "Two"), await Raise(team, "Three"), await Raise(team, "Four"), await Raise(team, "Five")];
        var rows = await Rows(numbers);
        var stale = rows.Single(r => r.Number == numbers[2]);
        // A colleague moves it after the list was read.
        await _db.Actions().MoveAsync(stale.Id, ActionStatus.Open, ActionStatus.Tracking);

        var result = await Many(rows, ManyOp.MoveTo(ActionStatus.Blocked));

        result.Done.Should().BeEquivalentTo(numbers.Where(n => n != stale.Number));
        result.Refused.Should().ContainSingle().Which.Should().Be(new ManyRefusal(stale.Id, stale.Number, $"A-{stale.Number} changed since you looked"));
        result.Text.Should().Be($"4 done, 1 refused: A-{stale.Number} changed since you looked");
        (await Action(stale.Number)).Status.Should().Be(ActionStatus.Tracking, "a stale one is left as the colleague left it");
        foreach (var n in result.Done) (await Action(n)).Status.Should().Be(ActionStatus.Blocked, "no all-or-nothing: the others are independent");
    }

    [Fact]
    public async Task OneThePersonMayNotWork_IsRefusedForThatAction_AsItWouldBeAlone()
    {
        var mine = await Team("Vulnerability Management");
        var theirs = await Team("Network Engineering", member: false);
        int[] ours = [await Raise(mine, "Patch the gateways"), await Raise(mine, "Patch the firewalls")];
        var other = await Raise(theirs, "Block the domain");
        var rows = await Rows([.. ours, other]);

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };   // an action owner on one team only
        var result = await Many(rows, ManyOp.Take());

        result.Done.Should().BeEquivalentTo(ours);
        result.Refused.Should().ContainSingle().Which.Why.Should().Be($"A-{other}: Only analysts and members of the owning team can work this action");
        foreach (var n in ours) (await Action(n)).AssigneeId.Should().Be(Me);
        (await Action(other)).AssigneeId.Should().BeNull();
    }

    [Fact]
    public async Task RecordNoHitsAndClose_ClosesOnlySweepsWhoseQueriesWereRun_AndNamesTheOthers()
    {
        var team = await Team("Cyber Defense");
        var svc = _db.Actions();
        var ran = await Raise(team, "Sweep ran clean", ActionKind.IocSweep);
        var unrun = await Raise(team, "Sweep not run", ActionKind.IocSweep);
        var noQuery = await Raise(team, "Hunt with no query", ActionKind.ThreatHunt);
        var block = await Raise(team, "Block it");
        var rows = await Rows(ran, unrun, noQuery, block);
        var hunt = await svc.AddHandWrittenHuntAsync(rows.Single(r => r.Number == ran).Id, "kql", "DeviceNetworkEvents");
        await svc.RecordHuntResultAsync(hunt.Id, HuntResult.NoHits, 0, null, null);
        await svc.AddHandWrittenHuntAsync(rows.Single(r => r.Number == unrun).Id, "kql", "DeviceNetworkEvents");
        rows = await Rows(ran, unrun, noQuery, block);   // the list read after the queries were kept

        var result = await Many(rows, ManyOp.NoHits());

        result.Done.Should().Equal(ran);
        var closed = await Action(ran);
        (closed.Status, closed.Outcome).Should().Be((ActionStatus.Done, ActionOutcome.NoHits));
        result.Refused.Select(r => (r.Number, r.Why)).Should().BeEquivalentTo(new[]
        {
            (unrun, $"A-{unrun}: A query on this action hasn't been run: record its result, or remove it, first"),
            (noQuery, $"A-{noQuery}: No query on this action has been kept and run, so it can't close as no hits yet. Copy or keep one on its Hunt queries card, then use No hits, close there"),
            (block, $"A-{block} isn't a sweep or a hunt, so it doesn't close with no hits"),
        }, "no hits needs a query that was run (#243), for each one");
        foreach (var n in new[] { unrun, noQuery, block }) (await Action(n)).Status.Should().Be(ActionStatus.Open);
    }

    [Fact]
    public async Task CancellingThem_AsksTheReasonOnce_AndEachOnesHistoryKeepsIt()
    {
        var team = await Team("Cyber Defense");
        int[] numbers = [await Raise(team, "One"), await Raise(team, "Two"), await Raise(team, "Three")];
        var rows = await Rows(numbers);

        var result = await Many(rows, ManyOp.MoveTo(ActionStatus.Cancelled, "Raised twice from the same advisory"));

        result.Text.Should().Be("3 done");
        foreach (var n in numbers)
        {
            var a = await Action(n);
            a.Status.Should().Be(ActionStatus.Cancelled);
            a.OutcomeNote.Should().Be("Raised twice from the same advisory");
            a.Events.Should().ContainSingle(e => e.Text == "Status: Open → Cancelled: Raised twice from the same advisory");
        }

        // Done says what came of each, so it's closed one at a time.
        var done = () => Many(rows, ManyOp.MoveTo(ActionStatus.Done));
        await done.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EachActionGetsOneHistoryLine_AndItsOwnAuditEntry()
    {
        var team = await Team("Cyber Defense");
        int[] numbers = [await Raise(team, "One"), await Raise(team, "Two"), await Raise(team, "Three")];
        var rows = await Rows(numbers);
        var before = new Dictionary<Guid, (int Lines, int Audited)>();
        foreach (var r in rows) before[r.Id] = ((await Action(r.Number)).Events.Count, await Audited(r.Id));

        (await Many(rows, ManyOp.Take())).Text.Should().Be("3 done");

        foreach (var r in rows)
        {
            var a = await Action(r.Number);
            a.Events.Should().HaveCount(before[r.Id].Lines + 1, "one line each, not one for the lot");
            a.Events.OrderBy(e => e.AtUtc).Last().Text.Should().Be("Took it on and started it.");
            (await Audited(r.Id)).Should().Be(before[r.Id].Audited + 1, "each is its own save, audited as it would be alone");
        }
    }

    // How many times the audit log has the action changed.
    private async Task<int> Audited(Guid id)
    {
        await using var ctx = _db.Context();
        var key = id.ToString();
        return await ctx.AuditLog.CountAsync(e => e.EntityType == nameof(ActionItem) && e.EntityKey.Contains(key) && e.Action == "Modified");
    }

    [Fact]
    public async Task AssigningThem_GivesEachToThePerson_WhereTheyreOnItsTeam_AndSetDueKeepsTheHolder()
    {
        const string colleague = "S-1-5-21-COLLEAGUE";
        var ours = await Team("Cyber Defense");
        var other = await Team("Network Engineering");
        await _db.Teams().AddMemberAsync(ours, colleague, "Cal Colleague");
        var onOurs = await Raise(ours, "Ours");
        var onOther = await Raise(other, "Other");
        var rows = await Rows(onOurs, onOther);

        var result = await Many(rows, ManyOp.AssignTo(colleague));
        result.Done.Should().Equal(onOurs);
        result.Refused.Single().Why.Should().Be($"A-{onOther}: That person isn't on the action's team");
        (await Action(onOurs)).Events.Should().Contain(e => e.Text == "Changed assignee: nobody → Cal Colleague.");

        var due = _db.Clock.Today.AddDays(5);
        (await Many(await Rows(onOurs, onOther), ManyOp.DueOn(due))).Text.Should().Be("2 done");
        var held = await Action(onOurs);
        (held.AssigneeId, held.DueDate).Should().Be((colleague, due));
    }

    [Fact]
    public async Task HandingThemOff_MovesEachToTheTeam_WithTheOneReason_AndRefusesOneAlreadyThere()
    {
        var from = await Team("Cyber Defense");
        var to = await Team("Network Engineering");
        var one = await Raise(from, "One");
        var two = await Raise(from, "Two");
        var there = await Raise(to, "Already there");

        var result = await Many(await Rows(one, two, there), ManyOp.HandTo(to, "Their firewall"));

        result.Done.Should().Equal(one, two);
        result.Refused.Single().Why.Should().StartWith($"A-{there} is ");
        foreach (var n in new[] { one, two })
        {
            var a = await Action(n);
            (a.TeamId, a.HandoffReason).Should().Be((to, "Their firewall"));
            a.Events.Should().ContainSingle(e => e.Text.StartsWith("Handed from Cyber Defense to Network Engineering: Their firewall"));
        }
    }

    [Fact]
    public async Task AtMostAPageAtOnce_AndAMissingVersionIsStale()
    {
        var team = await Team("Cyber Defense");
        var n = await Raise(team, "One");
        var row = (await Rows(n)).Single();
        var tooMany = () => _db.Actions().ManyAsync([.. Enumerable.Range(0, ActionService.ListPageSize + 1).Select(_ => Guid.NewGuid())], ManyOp.Take(), new Dictionary<Guid, Guid>());
        await tooMany.Should().ThrowAsync<ArgumentException>();

        var result = await _db.Actions().ManyAsync([row.Id], ManyOp.Take(), new Dictionary<Guid, Guid>());
        result.Refused.Single().Why.Should().Be(ActionService.ChangedSince(n));
    }
}
