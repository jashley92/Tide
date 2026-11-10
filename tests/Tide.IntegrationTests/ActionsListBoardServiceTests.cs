using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// The Actions list and board in the services: each view tab's count from one grouped query that counts what that view
/// shows and nothing more, Take it offered where taking is allowed (and refused when someone took it meanwhile), and the
/// board's cards with their article, its KEV flag and how long each has been in its column, in a fixed number of queries.
/// </summary>
public class ActionsListBoardServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public ActionsListBoardServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    private const string Colleague = "S-1-5-21-COLLEAGUE";

    public void Dispose() => _db.Dispose();

    private string Me => _db.User.UserId;

    // Actions written straight in, numbered on from the last.
    private async Task<ActionItem> Add(Guid team, Action<ActionItem>? set = null)
    {
        await using var ctx = _db.Context();
        var n = await ctx.Actions.IgnoreQueryFilters().MaxAsync(a => (int?)a.Number) ?? 0;
        var a = new ActionItem { Number = n + 1, Title = $"Action {n + 1}", TeamId = team };
        set?.Invoke(a);
        ctx.Actions.Add(a);
        await ctx.SaveChangesAsync();
        return a;
    }

    // My team (I'm on it) and another one (I'm not), with one action of each kind a view tells apart.
    private async Task<(Guid Mine, Guid Other)> Seed()
    {
        var mine = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var other = await _db.Teams().AddAsync("Vulnerability Management", null, null);
        await _db.Teams().AddMemberAsync(mine, Me, "Test Analyst");
        var today = _db.Clock.Today;
        await Add(mine, a => (a.AssigneeId, a.AssigneeName, a.DueDate) = (Me, "Test Analyst", today.AddDays(-1)));   // mine, overdue
        await Add(mine);                                                                                                 // my team's, nobody's
        await Add(mine, a => (a.AssigneeId, a.AssigneeName, a.Kind) = (Colleague, "Colleague", ActionKind.Block));     // my team's, a colleague's
        await Add(other);                                                                                                // another team's, nobody's
        await Add(mine, a => (a.Status, a.Outcome, a.ClosedAtUtc, a.ClosedDate) = (ActionStatus.Done, ActionOutcome.Remediated, DateTimeOffset.UtcNow, today));
        // Deleted: nobody sees it in any view, so no tab counts it.
        await Add(mine, a => (a.DeletedAtUtc, a.DeletedByName) = (DateTimeOffset.UtcNow, "Admin"));
        return (mine, other);
    }

    [Fact]
    public async Task TheTabCounts_AreOneQuery_AndEachIsWhatThatViewShows()
    {
        await Seed();
        var svc = _db.Actions();

        _db.Commands.Reset();
        var counts = await svc.ViewCountsAsync(new ActionFilter(ActionView.Open));
        _db.Commands.Count.Should().Be(1, "every tab is counted in one grouped query");

        (counts.Mine, counts.MyTeams, counts.Open, counts.Overdue, counts.Closed).Should().Be((1, 3, 4, 1, 1));
        counts.MyTeamsUnowned.Should().Be(1, "one of my teams' open actions has nobody");
        foreach (var view in Enum.GetValues<ActionView>())
            counts.For(view).Should().Be((await svc.PageAsync(new ActionFilter(view))).Total, $"the {view} tab counts what it shows");
    }

    [Fact]
    public async Task TheTabCounts_FollowTheFilters_AsEachTabWouldShowThem()
    {
        await Seed();
        var svc = _db.Actions();

        var nobodys = await svc.ViewCountsAsync(new ActionFilter(ActionView.Open, AssigneeId: ActionFilter.Unassigned));
        nobodys.Open.Should().Be(2);
        nobodys.MyTeams.Should().Be(1);
        nobodys.Mine.Should().Be(1, "Mine is the person's own: choosing it lets go of Held by, so it counts theirs");
        foreach (var view in new[] { ActionView.MyTeams, ActionView.Open, ActionView.Overdue, ActionView.Closed })
            nobodys.For(view).Should().Be((await svc.PageAsync(new ActionFilter(view, AssigneeId: ActionFilter.Unassigned))).Total, view.ToString());

        var blocks = await svc.ViewCountsAsync(new ActionFilter(ActionView.Open, Kind: ActionKind.Block));
        (blocks.Mine, blocks.MyTeams, blocks.Open).Should().Be((0, 1, 1));
    }

    [Fact]
    public async Task TheTabCounts_LeaveOutWhatThePersonDoesntSeeInAView()
    {
        var (_, other) = await Seed();
        // A colleague's action on a team I'm not on: in All open, never in Mine or My teams.
        await Add(other, a => (a.AssigneeId, a.AssigneeName) = (Colleague, "Colleague"));
        var counts = await _db.Actions().ViewCountsAsync(new ActionFilter(ActionView.Open));
        (counts.Mine, counts.MyTeams, counts.Open).Should().Be((1, 3, 5));
    }

    [Fact]
    public async Task ARuleChosenAsAFilter_IsCountedInTheSameOneQuery()
    {
        await Seed();
        var rule = new ActionCardRule { Name = "Nobody's", Unassigned = true };
        _db.Commands.Reset();
        var counts = await _db.Actions().ViewCountsAsync(new ActionFilter(ActionView.Open), rule);
        _db.Commands.Count.Should().Be(1);
        (counts.Open, counts.MyTeams).Should().Be((2, 1));
    }

    [Fact]
    public async Task TakeIt_IsOfferedOnlyWhereTakingIsAllowed()
    {
        var (mine, other) = await Seed();
        var external = await _db.Teams().AddExternalAsync("Managed SOC", "noc@provider.example", confirmOutside: true);
        await Add(external);

        // An action owner on my team: their team's open actions that nobody has.
        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        var page = await _db.Actions().PageAsync(new ActionFilter(ActionView.Open));
        Offered(page).Should().Equal([(mine, (string?)null)], "only my team's, nobody's; not another team's, a held one or a closed one");

        // An analyst works every action: any open one nobody has, but not an external team's (nobody in TIDE takes it).
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        page = await _db.Actions().PageAsync(new ActionFilter(ActionView.Open));
        Offered(page).Should().BeEquivalentTo([(mine, (string?)null), (other, (string?)null)]);

        // A viewer works none.
        _db.User.Roles = new HashSet<AppRole> { AppRole.Viewer };
        Offered(await _db.Actions().PageAsync(new ActionFilter(ActionView.Open))).Should().BeEmpty();

        static List<(Guid, string?)> Offered(ActionListPage p) => [.. p.Rows.Where(p.Workers.CanTake).Select(r => (r.TeamId, r.AssigneeId))];
    }

    [Fact]
    public async Task TakeIt_FromARow_IsRefused_WhenSomeoneTookItMeanwhile()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var a = await Add(team);
        var svc = _db.Actions();
        await using (var ctx = _db.Context())
        {
            var held = await ctx.Actions.SingleAsync(x => x.Id == a.Id);
            (held.AssigneeId, held.AssigneeName) = (Colleague, "Colleague");
            await ctx.SaveChangesAsync();
        }

        var take = () => svc.TakeAsync(a.Id, onlyIfNobodys: true);
        (await take.Should().ThrowAsync<StaleEditException>()).WithMessage("*Colleague*");
        (await _db.Context().Actions.SingleAsync(x => x.Id == a.Id)).AssigneeId.Should().Be(Colleague, "it stays with whoever took it");

        var free = await Add(team);
        await svc.TakeAsync(free.Id, onlyIfNobodys: true);
        var taken = (await svc.GetAsync(free.Number))!.Action;
        (taken.AssigneeId, taken.Status).Should().Be((Me, ActionStatus.InProgress));
        taken.Events.Should().Contain(e => e.Text.StartsWith("Took it on", StringComparison.Ordinal), "taking is recorded as it always is");
    }

    // An article on the board's cards: one about a CVE on CISA's KEV list, and one not.
    private async Task<(Guid Kev, Guid Plain)> Articles()
    {
        await using var ctx = _db.Context();
        var kev = new Article { Title = "Edge VPN zero-day exploited", Tlp = TlpLevel.Amber, AuthorId = Me, AuthorName = "Test Analyst" };
        var plain = new Article { Title = "Phishing kit update", AuthorId = Me, AuthorName = "Test Analyst" };
        ctx.Articles.AddRange(kev, plain);
        ctx.ArticleCves.Add(new ArticleCve { ArticleId = kev.Id, CveId = "CVE-2026-1111" });
        ctx.ArticleCves.Add(new ArticleCve { ArticleId = plain.Id, CveId = "CVE-2026-2222" });
        ctx.CveRecords.Add(new CveRecord { CveId = "CVE-2026-1111", IsKev = true });
        ctx.CveRecords.Add(new CveRecord { CveId = "CVE-2026-2222" });
        await ctx.SaveChangesAsync();
        return (kev.Id, plain.Id);
    }

    [Fact]
    public async Task TheBoardsCards_SayTheirArticle_ItsKevFlag_AndSinceWhenTheyreInTheirColumn()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var (kev, plain) = await Articles();
        var moved = DateTimeOffset.UtcNow.AddDays(-3);
        var a = await Add(team, x => (x.ArticleId, x.Status) = (kev, ActionStatus.InProgress));
        var b = await Add(team, x => x.ArticleId = plain);
        await using (var ctx = _db.Context())
        {
            ctx.ActionEvents.Add(new ActionEvent { ActionId = a.Id, AtUtc = moved.AddDays(-1), ByName = "x", Text = "Raised from the brief." });
            ctx.ActionEvents.Add(new ActionEvent { ActionId = a.Id, AtUtc = moved, ByName = "x", Text = "Status: Open → In progress." });
            ctx.ActionEvents.Add(new ActionEvent { ActionId = a.Id, AtUtc = moved.AddHours(2), ByName = "x", Text = "Changed priority: P3 → P2." });
            await ctx.SaveChangesAsync();
        }

        var board = await _db.Actions().BoardAsync(new ActionFilter(ActionView.Open));
        var cardA = board.Cards.Single(c => c.Id == a.Id);
        (cardA.ArticleTitle, cardA.ArticleKev).Should().Be(("Edge VPN zero-day exploited", true));
        cardA.InStatusSinceUtc!.Value.Should().BeCloseTo(moved, TimeSpan.FromSeconds(1), "the last move into its column, not a later edit");
        var cardB = board.Cards.Single(c => c.Id == b.Id);
        (cardB.ArticleTitle, cardB.ArticleKev, cardB.InStatusSinceUtc).Should().Be(("Phishing kit update", false, (DateTimeOffset?)null),
            "never moved: it has been in Open since it was raised");
    }

    [Fact]
    public async Task TheBoard_ReadsItsCardsInAFixedNumberOfQueries_HoweverManyThereAre()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var (kev, _) = await Articles();
        await Add(team, x => x.ArticleId = kev);
        var svc = _db.Actions();
        _db.Commands.Reset();
        await svc.BoardAsync(new ActionFilter(ActionView.Open));
        var few = _db.Commands.Count;

        for (var i = 0; i < 15; i++) await Add(team, x => (x.ArticleId, x.Status) = (kev, i % 2 == 0 ? ActionStatus.Blocked : ActionStatus.Open));
        _db.Commands.Reset();
        (await svc.BoardAsync(new ActionFilter(ActionView.Open))).Cards.Should().HaveCount(16);
        _db.Commands.Count.Should().Be(few, "the article, KEV flag and time in column are read for all the cards at once, not card by card");
    }

    [Fact]
    public async Task AnArticlesTitle_IsShownOnlyToSomeoneWhoReadsArticles()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var (kev, _) = await Articles();
        await Add(team, x => x.ArticleId = kev);

        // Someone who works actions but doesn't read the team edition (so can't open the article): no title, no KEV flag.
        var worker = new OnlyUser("S-1-5-21-WORKER", Permission.WorkActions, Permission.ReadActions);
        var svc = new ActionService(_db, worker, _db.Sla(), _db.Clock, _db.Queries(), _db.Screenshots(), _db.Articles());
        var card = (await svc.BoardAsync(new ActionFilter(ActionView.Open))).Cards.Single();
        (card.ArticleTitle, card.ArticleKev).Should().Be(((string?)null, false));
        (await svc.PageAsync(new ActionFilter(ActionView.Open))).Rows.Single().ArticleTitle.Should().BeNull();
    }

    /// <summary>A signed-in person holding exactly these permissions.</summary>
    private sealed class OnlyUser(string id, params Permission[] permissions) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public string UserId => id;
        public string DisplayName => "Worker";
        public string? UserPrincipalName => null;
        public string? Email => null;
        public IReadOnlySet<AppRole> Roles { get; } = new HashSet<AppRole>();
        public IReadOnlySet<Permission> Permissions { get; } = permissions.ToHashSet();
        public bool Has(Permission permission) => Permissions.Contains(permission);
    }
}
