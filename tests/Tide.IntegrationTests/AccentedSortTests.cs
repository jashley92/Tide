using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Content;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Lists sorted ignoring case put accented letters where SQL Server's collation does, on SQLite too. SQLite's own lower()
/// left "É" capital, so "Égout" sorted apart from "éclair", and its byte order put both after "Zebra".
/// </summary>
public class AccentedSortTests : IDisposable
{
    private static readonly string[] Titles = ["Zebra", "éclair", "apple", "Égout"];
    private static readonly string[] Expected = ["apple", "éclair", "Égout", "Zebra"];

    private TestDb _db = new();

    public AccentedSortTests() => Admin();

    private void Admin() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task Actions(Action<ActionItem, int> set)
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        await using var ctx = _db.Context();
        for (var i = 0; i < Titles.Length; i++)
        {
            var a = new ActionItem { Number = i + 1, Title = $"Action {i + 1}", TeamId = team };
            set(a, i);
            ctx.Actions.Add(a);
        }
        await ctx.SaveChangesAsync();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheActionsList_SortsAccentedTitles_AsSqlServerDoes(string provider)
    {
        _db = _db.On(provider);
        await Actions((a, i) => a.Title = Titles[i]);

        var asc = await _db.Actions().PageAsync(new ActionFilter(ActionView.Open), ActionSort.Title);
        var desc = await _db.Actions().PageAsync(new ActionFilter(ActionView.Open), ActionSort.Title, descending: true);

        asc.Rows.Select(r => r.Title).Should().Equal(Expected);
        desc.Rows.Select(r => r.Title).Should().Equal(Expected.Reverse());
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheActionsList_SortsAccentedTeams_AsSqlServerDoes(string provider)
    {
        _db = _db.On(provider);
        var teams = new List<Guid>();
        foreach (var name in Titles) teams.Add(await _db.Teams().AddAsync(name, null, null));
        await Actions((a, i) => a.TeamId = teams[i]);

        var page = await _db.Actions().PageAsync(new ActionFilter(ActionView.Open), ActionSort.Team);

        page.Rows.Select(r => r.Title).Should().Equal(Expected.Select(t => $"Action {Array.IndexOf(Titles, t) + 1}"));
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheArticleList_SortsAccentedTitles_AsSqlServerDoes(string provider)
    {
        _db = _db.On(provider);
        foreach (var title in Titles) await _db.Articles().CreateAsync(title, "Vendor", null, TlpLevel.Clear);

        var page = await _db.Articles().PageAsync(null, null, Tide.Application.Articles.ArticleSort.Title, descending: false, take: 10);

        page.Rows.Select(r => r.Title).Should().Equal(Expected);
    }

    [Fact]
    public async Task ANameListRead_AndOneSortedInMemory_AgreeOnAccents()
    {
        foreach (var name in Titles) await _db.Teams().AddAsync(name, null, null);
        await using var ctx = _db.Context();

        var read = await ctx.Teams.AsNoTracking().OrderByName(t => t.Name).Select(t => t.Name).ToListAsync();

        read.Should().Equal(Expected);
        Titles.OrderByName(t => t).Should().Equal(Expected, "a list sorted in memory puts a name where the database does");
    }

    [Fact]
    public async Task AContextOpeningItsOwnConnection_SortsAccentsToo()
    {
        // The app's contexts open their connection from a connection string, not one handed in as most tests' are.
        _db.Dispose();
        _db = new TestDb(connectionPerContext: true);
        Admin();
        await Actions((a, i) => a.Title = Titles[i]);

        var page = await _db.Actions().PageAsync(new ActionFilter(ActionView.Open), ActionSort.Title);

        page.Rows.Select(r => r.Title).Should().Equal(Expected);
        await using var ctx = _db.Context();
        // In the database, as the lists' sorts run it.
#pragma warning disable CA1304, CA1311
        (await ctx.Actions.Where(a => a.Number == 4).Select(a => a.Title.ToLower()).SingleAsync()).Should().Be("égout",
            "lower() lowers every letter, as SQL Server's LOWER() does, not only A to Z");
#pragma warning restore CA1304, CA1311
        (await ctx.Database.SqlQueryRaw<string?>("SELECT lower(NULL) AS \"Value\"").SingleAsync()).Should().BeNull("an empty value stays empty");
    }
}
