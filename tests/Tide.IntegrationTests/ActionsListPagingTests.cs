using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// The Actions list a page at a time, on both databases: the count of every match, pages that meet without a gap or a
/// repeat under every sort (ties broken by the number), the filters with the pages, and a page's read cut in the database
/// rather than after reading every action.
/// </summary>
public class ActionsListPagingTests : IDisposable
{
    private TestDb _db = new();

    public ActionsListPagingTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private const int Size = ActionService.ListPageSize;

    // 130 actions with plenty of ties under every sort: three due dates (one blank), two priorities, titles that differ only
    // in case, two kinds, two teams and three open statuses. Numbered from 1, written straight in.
    private async Task<(Guid Alpha, Guid Bravo, List<ActionItem> Made)> Seed(int count = 130)
    {
        var alpha = await _db.Teams().AddAsync("alpha team", null, null);
        var bravo = await _db.Teams().AddAsync("Bravo team", null, null);
        var today = _db.Clock.Today;
        var made = new List<ActionItem>();
        await using var ctx = _db.Context();
        for (var n = 1; n <= count; n++)
        {
            var a = new ActionItem
            {
                Number = n,
                Title = (n % 3) switch { 0 => "patch the vpn", 1 => "Patch the VPN", _ => "Block the hashes" },
                TeamId = n % 2 == 0 ? alpha : bravo,
                Kind = n % 4 < 2 ? ActionKind.Block : ActionKind.IocSweep,
                Priority = n % 5 == 0 ? Priority.P1 : Priority.P3,
                Status = (n % 3) switch { 0 => ActionStatus.Open, 1 => ActionStatus.InProgress, _ => ActionStatus.Blocked },
                DueDate = (n % 3) switch { 0 => null, 1 => today.AddDays(2), _ => today.AddDays(-1) },
            };
            ctx.Actions.Add(a);
            made.Add(a);
        }
        await ctx.SaveChangesAsync();
        return (alpha, bravo, made);
    }

    // Every page of the list for this filter and sort, read one after another as the pager turns them.
    private async Task<(List<ActionRow> Rows, List<int> Totals)> AllPages(ActionFilter filter, ActionSort sort = ActionSort.Due, bool descending = false)
    {
        var (rows, totals) = (new List<ActionRow>(), new List<int>());
        for (var page = 1; ; page++)
        {
            var read = await _db.Actions().PageAsync(filter, sort, descending, page: page);
            read.Page.Should().Be(page);
            read.Rows.Count.Should().BeLessThanOrEqualTo(Size, "a page is at most a page of rows");
            rows.AddRange(read.Rows);
            totals.Add(read.Total);
            if (page * Size >= read.Total) return (rows, totals);
        }
    }

    public static TheoryData<string, ActionSort, bool> SortsOnEachProvider
    {
        get
        {
            var rows = new TheoryData<string, ActionSort, bool>();
            foreach (var provider in ((IEnumerable<object[]>)TestDb.Providers).Select(r => (string)r[0]))
                foreach (var sort in new[] { ActionSort.Due, ActionSort.Number, ActionSort.Title, ActionSort.Kind, ActionSort.Team, ActionSort.Status })
                    foreach (var descending in new[] { false, true })
                        rows.Add(provider, sort, descending);
            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(SortsOnEachProvider))]
    public async Task EverySort_PagesMeetWithoutAGapOrARepeat_InOneStableOrder_TiesByNumber(string provider, ActionSort sort, bool descending)
    {
        _db = _db.On(provider);
        var (alpha, _, made) = await Seed();
        var teamName = (ActionItem a) => a.TeamId == alpha ? "alpha team" : "bravo team";

        var (rows, totals) = await AllPages(new ActionFilter(ActionView.Open), sort, descending);

        totals.Should().AllBeEquivalentTo(130, "each page counts every match");
        rows.Should().HaveCount(130);
        rows.Select(r => r.Id).Should().OnlyHaveUniqueItems("an action is on one page only, whatever the ties");
        // The order the table promises: the column (letters in any case together), then the number, so ties never move
        // an action from one page to the next between reads. A blank due date goes last either way.
        var expected = sort switch
        {
            ActionSort.Number => descending ? made.OrderByDescending(a => a.Number) : made.OrderBy(a => a.Number),
            ActionSort.Title => (descending ? made.OrderByDescending(a => a.Title.ToLowerInvariant(), StringComparer.Ordinal)
                : made.OrderBy(a => a.Title.ToLowerInvariant(), StringComparer.Ordinal)).ThenBy(a => a.Number),
            ActionSort.Kind => (descending ? made.OrderByDescending(a => a.Kind) : made.OrderBy(a => a.Kind)).ThenBy(a => a.Number),
            ActionSort.Team => (descending ? made.OrderByDescending(teamName, StringComparer.Ordinal) : made.OrderBy(teamName, StringComparer.Ordinal)).ThenBy(a => a.Number),
            ActionSort.Status => (descending ? made.OrderByDescending(a => a.Status) : made.OrderBy(a => a.Status)).ThenBy(a => a.Number),
            _ => (descending
                    ? made.OrderBy(a => a.DueDate is null).ThenByDescending(a => a.DueDate)
                    : made.OrderBy(a => a.DueDate is null).ThenBy(a => a.DueDate))
                .ThenBy(a => a.Priority).ThenBy(a => a.Number),
        };
        rows.Select(r => r.Number).Should().Equal(expected.Select(a => a.Number));

        var again = await AllPages(new ActionFilter(ActionView.Open), sort, descending);
        again.Rows.Select(r => r.Id).Should().Equal(rows.Select(r => r.Id), "reading the pages again gives the same order");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ThePageBoundaries_FiftyFiftyThenTheRest_AndAPagePastTheEndIsTheLast(string provider)
    {
        _db = _db.On(provider);
        await Seed();
        var svc = _db.Actions();
        var filter = new ActionFilter(ActionView.Open);

        var pages = new List<ActionListPage>();
        foreach (var p in new[] { 1, 2, 3 }) pages.Add(await svc.PageAsync(filter, ActionSort.Number, page: p));
        pages.Select(p => p.Rows.Count).Should().Equal(50, 50, 30);
        pages.Select(p => (p.Rows[0].Number, p.Rows[^1].Number)).Should().Equal((1, 50), (51, 100), (101, 130));
        pages.Should().OnlyContain(p => p.Total == 130);

        (await svc.PageAsync(filter, ActionSort.Number, page: 4)).Should().Match<ActionListPage>(p => p.Page == 3 && p.Rows[0].Number == 101, "past the end is the last page");
        (await svc.PageAsync(filter, ActionSort.Number, page: 0)).Page.Should().Be(1, "before the start is the first");
        var none = await svc.PageAsync(filter with { Search = "nothing is called this" });
        (none.Total, none.Page, none.Rows.Count).Should().Be((0, 1, 0), "no match is one empty page");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheFilters_NarrowTheCountAndEveryPage_TogetherWithThePages(string provider)
    {
        _db = _db.On(provider);
        var (alpha, _, made) = await Seed(330);
        var filter = new ActionFilter(ActionView.Open, TeamId: alpha, Kind: ActionKind.Block) { Priorities = [Priority.P3], Status = ActionStatus.InProgress };
        var matching = made.Where(a => a.TeamId == alpha && a.Kind == ActionKind.Block && a.Priority == Priority.P3 && a.Status == ActionStatus.InProgress)
            .Select(a => a.Number).Order().ToList();
        matching.Count.Should().BeGreaterThan(0);

        var (rows, totals) = await AllPages(filter, ActionSort.Number);
        totals.Should().AllBeEquivalentTo(matching.Count);
        rows.Select(r => r.Number).Should().Equal(matching);

        // Words and a team, past one page: 330 actions, a third of them titled "Block the hashes", half of those alpha's.
        var words = new ActionFilter(ActionView.Open, TeamId: alpha) { Search = "HASHES" };
        var expected = made.Where(a => a.TeamId == alpha && a.Title == "Block the hashes").Select(a => a.Number).Order().ToList();
        expected.Count.Should().BeGreaterThan(Size, "the filtered list still runs to a second page");
        var (found, counted) = await AllPages(words, ActionSort.Number);
        counted.Should().HaveCount(2).And.AllBeEquivalentTo(expected.Count);
        found.Select(r => r.Number).Should().Equal(expected);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task RecentlyClosed_PagesMostRecentFirst_TiesOnTheDayByNumber(string provider)
    {
        _db = _db.On(provider);
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var today = _db.Clock.Today;
        await using (var ctx = _db.Context())
        {
            for (var n = 1; n <= 75; n++)
                ctx.Actions.Add(new ActionItem
                {
                    Number = n, Title = $"Closed {n}", TeamId = team, Status = ActionStatus.Done,
                    ClosedAtUtc = DateTimeOffset.UtcNow.AddDays(-(n % 3)), ClosedDate = today.AddDays(-(n % 3)),
                });
            await ctx.SaveChangesAsync();
        }

        var (rows, totals) = await AllPages(new ActionFilter(ActionView.Closed), ActionSort.Closed);

        totals.Should().AllBeEquivalentTo(75);
        rows.Select(r => r.Number).Should().Equal(Enumerable.Range(1, 75).OrderBy(n => n % 3).ThenByDescending(n => n));
        (await _db.Actions().ListAsync(new ActionFilter(ActionView.Closed))).Select(r => r.Number).Should().Equal(rows.Select(r => r.Number),
            "the whole list (the API's) is in the order the pages are");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task APage_ReadsOnlyItsOwnRows_CutAndSortedInTheDatabase(string provider)
    {
        _db = _db.On(provider);
        await Seed(260);
        var svc = _db.Actions();

        _db.Commands.Record = true;
        _db.Commands.Reset();
        var page = await svc.PageAsync(new ActionFilter(ActionView.Open), ActionSort.Title, page: 3);
        var sql = _db.Commands.Sql;
        _db.Commands.Record = false;

        page.Rows.Should().HaveCount(Size);
        page.Total.Should().Be(260);
        // Every read of the actions table either counts, groups, or is cut to the page (LIMIT/OFFSET on SQLite, OFFSET ...
        // FETCH on SQL Server): none reads every action to sort and cut them here.
        var reads = sql.Where(s => s.Contains("Actions", StringComparison.Ordinal) && s.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)).ToList();
        reads.Should().NotBeEmpty();
        reads.Should().OnlyContain(s => s.Contains("COUNT(", StringComparison.OrdinalIgnoreCase) || s.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase)
            || s.Contains("LIMIT", StringComparison.Ordinal) || s.Contains("FETCH", StringComparison.Ordinal), "a page reads a page of actions, not all of them");
        reads.Should().Contain(s => s.Contains("ORDER BY", StringComparison.Ordinal)
            && (s.Contains("lower(", StringComparison.OrdinalIgnoreCase)), "the title sort runs in the database, in any case");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheWholeList_IsInThePagesOrder_SortedInTheDatabase(string provider)
    {
        _db = _db.On(provider);
        await Seed();
        var (rows, _) = await AllPages(new ActionFilter(ActionView.Open));

        _db.Commands.Record = true;
        _db.Commands.Reset();
        var all = await _db.Actions().ListAsync(new ActionFilter(ActionView.Open));
        var sql = _db.Commands.Sql;
        _db.Commands.Record = false;

        all.Select(r => r.Number).Should().Equal(rows.Select(r => r.Number), "the API's list and the brief's are in the list's default order");
        sql.Should().Contain(s => s.Contains("Actions", StringComparison.Ordinal) && s.Contains("ORDER BY", StringComparison.Ordinal));
        (await _db.Actions().BoardAsync(new ActionFilter(ActionView.Open))).Cards.Select(c => c.Number).Should().Equal(rows.Select(r => r.Number),
            "the board's cards, by due date then priority, as before");
    }
}
