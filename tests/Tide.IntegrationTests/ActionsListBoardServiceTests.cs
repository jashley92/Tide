using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// The Actions list and board in the services: each view tab's count, No owner's and the board's closed strip's from one
/// grouped query that counts what each shows and nothing more, Take it offered where taking is allowed (and refused when
/// someone took it meanwhile), and the rows and cards with their article, its KEV flag, how long each has been in its
/// column, how its hunts went and when something last happened to it, in a fixed number of queries.
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
    public async Task OnTheBoard_TheTabsCountOpenActions_AsTheListDoes_AndTheClosedStripCountsEachWayOfClosing_InOneQuery()
    {
        var (mine, other) = await Seed();
        var now = DateTimeOffset.UtcNow;
        await Add(mine, a => (a.Status, a.ClosedAtUtc) = (ActionStatus.Cancelled, now.AddDays(-3)));
        await Add(other, a => (a.Status, a.ClosedAtUtc) = (ActionStatus.NotApplicable, now.AddDays(-2)));
        await Add(mine, a => (a.AssigneeId, a.AssigneeName, a.Status, a.ClosedAtUtc) = (Me, "Test Analyst", ActionStatus.Done, now.AddDays(-1)));
        await Add(mine, a => (a.Status, a.ClosedAtUtc) = (ActionStatus.Done, now.AddDays(-20)));   // past the week, within the month
        var svc = _db.Actions();

        _db.Commands.Reset();
        var counts = await svc.ViewCountsAsync(new ActionFilter(ActionView.Open), closedDays: ActionService.BoardClosedDays);
        _db.Commands.Count.Should().Be(1, "the tabs and the closed strip are counted together");
        var list = await svc.ViewCountsAsync(new ActionFilter(ActionView.Open));
        (counts.Mine, counts.MyTeams, counts.Open).Should().Be((list.Mine, list.MyTeams, list.Open), "\"All open\" means the same on the board as in the list");
        foreach (var view in new[] { ActionView.Mine, ActionView.MyTeams, ActionView.Open })
            counts.For(view).Should().Be((await svc.BoardAsync(new ActionFilter(view))).Cards.Count, $"the board's {view} tab counts the cards it draws");
        (counts.ClosedAs(ActionStatus.Done), counts.ClosedAs(ActionStatus.Cancelled), counts.ClosedAs(ActionStatus.NotApplicable))
            .Should().Be((2, 1, 1), "closed in the last week, each way");
        counts.ClosedAs(ActionStatus.Open).Should().Be(0, "an open status is no way of closing");
        list.ClosedAs(ActionStatus.Done).Should().Be(0, "the list has no strip");

        var month = await svc.ViewCountsAsync(new ActionFilter(ActionView.Open), closedDays: ActionService.BoardOlderDays);
        month.ClosedAs(ActionStatus.Done).Should().Be(3, "the last 30 days reach further back");

        // The strip follows the board's view and filters, as the cards do.
        var mineOnly = await svc.ViewCountsAsync(new ActionFilter(ActionView.Mine), closedDays: ActionService.BoardClosedDays);
        (mineOnly.ClosedAs(ActionStatus.Done), mineOnly.ClosedAs(ActionStatus.Cancelled)).Should().Be((1, 0));
        var teams = await svc.ViewCountsAsync(new ActionFilter(ActionView.MyTeams), closedDays: ActionService.BoardClosedDays);
        (teams.ClosedAs(ActionStatus.Done), teams.ClosedAs(ActionStatus.Cancelled), teams.ClosedAs(ActionStatus.NotApplicable)).Should().Be((2, 1, 0), "not another team's");
        var nobodys = await svc.ViewCountsAsync(new ActionFilter(ActionView.Open, AssigneeId: ActionFilter.Unassigned), closedDays: ActionService.BoardClosedDays);
        nobodys.ClosedAs(ActionStatus.Done).Should().Be(1, "held by nobody: not the one I closed");
    }

    [Fact]
    public async Task TheBoard_DrawsOpenActionsOnly()
    {
        var (mine, _) = await Seed();
        await Add(mine, a => (a.Status, a.ClosedAtUtc) = (ActionStatus.Cancelled, DateTimeOffset.UtcNow.AddHours(-1)));
        var cards = (await _db.Actions().BoardAsync(new ActionFilter(ActionView.Open))).Cards;
        cards.Should().HaveCount(4).And.OnlyContain(c => c.IsOpen, "closed actions are counted in the strip under the lanes, not drawn");
    }

    [Fact]
    public async Task NoOwner_IsCountedForEachView_InTheSameOneQuery()
    {
        var (mine, _) = await Seed();
        await Add(mine, a => a.DueDate = _db.Clock.Today.AddDays(-2));   // my team's, nobody's, overdue
        var svc = _db.Actions();
        foreach (var view in Enum.GetValues<ActionView>())
        {
            _db.Commands.Reset();
            var counts = await svc.ViewCountsAsync(new ActionFilter(view));
            _db.Commands.Count.Should().Be(1, "No owner is counted with the tabs");
            // No owner chosen on Mine shows the person's teams' work nobody has.
            var shows = view == ActionView.Mine ? ActionView.MyTeams : view;
            counts.NoOwner(view).Should().Be((await svc.PageAsync(new ActionFilter(shows, AssigneeId: ActionFilter.Unassigned))).Total, $"No owner on {view}");
        }
        (await svc.ViewCountsAsync(new ActionFilter(ActionView.Open))).NoOwner(ActionView.Open).Should().Be(3, "not the deleted one, nor a held or closed one");
        (await svc.ViewCountsAsync(new ActionFilter(ActionView.Open, Kind: ActionKind.Block))).NoOwner(ActionView.Open).Should().Be(0, "it follows the other filters");
        (await svc.ViewCountsAsync(new ActionFilter(ActionView.Open, AssigneeId: Colleague))).NoOwner(ActionView.Open).Should().Be(3, "it's what choosing it shows: it takes Held by's place");
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
    public async Task EachDueGroup_SaysHowManyNobodyHas_ReadWithTheGroupsTotals()
    {
        var (mine, other) = await Seed();
        var today = _db.Clock.Today;
        await Add(other, a => a.DueDate = today);
        await Add(other, a => (a.DueDate, a.AssigneeId, a.AssigneeName) = (today, Colleague, "Colleague"));
        var svc = _db.Actions();

        _db.Commands.Reset();
        await svc.PageAsync(new ActionFilter(ActionView.Open), group: ActionGrouping.Kind);
        var byKind = _db.Commands.Count;
        _db.Commands.Reset();
        var page = await svc.PageAsync(new ActionFilter(ActionView.Open), group: ActionGrouping.Urgency);
        _db.Commands.Count.Should().Be(byKind, "nobody's are counted in the groups' own query");

        var expected = (await svc.ListAsync(new ActionFilter(ActionView.Open)))
            .GroupBy(r => ActionSla.UrgencyName(r.Urgency(today, svc.Days)))
            .ToDictionary(g => g.Key, g => g.Count(r => r.AssigneeId is null));
        page.GroupsUnowned.Should().Equal(expected);
        page.GroupsUnowned["Due today"].Should().Be(1);
    }

    [Fact]
    public async Task TheListsRows_SayKev_AsTheBoardsCardsDo_OnlyToSomeoneWhoReadsArticles()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var (kev, plain) = await Articles();
        var a = await Add(team, x => x.ArticleId = kev);
        var b = await Add(team, x => x.ArticleId = plain);
        var rows = (await _db.Actions().PageAsync(new ActionFilter(ActionView.Open))).Rows;
        (rows.Single(r => r.Id == a.Id).ArticleKev, rows.Single(r => r.Id == b.Id).ArticleKev).Should().Be((true, false));

        var worker = new OnlyUser("S-1-5-21-WORKER", Permission.WorkActions, Permission.ReadActions);
        var svc = new ActionService(_db, worker, _db.Sla(), _db.Clock, _db.Queries(), _db.Screenshots(), _db.Articles());
        (await svc.PageAsync(new ActionFilter(ActionView.Open))).Rows.Should().OnlyContain(r => !r.ArticleKev, "someone who can't open the article isn't told what it says");
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

        // An analyst works every action, but takes on only their own teams' (as only a member is given one), and never an
        // external team's (nobody in TIDE takes it).
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        page = await _db.Actions().PageAsync(new ActionFilter(ActionView.Open));
        Offered(page).Should().Equal([(mine, (string?)null)], "not another team's, though an analyst works it");
        page.Rows.Where(r => r.TeamId == other).Should().OnlyContain(r => page.Workers.CanWork(r.TeamId));

        // A viewer works none.
        _db.User.Roles = new HashSet<AppRole> { AppRole.Viewer };
        Offered(await _db.Actions().PageAsync(new ActionFilter(ActionView.Open))).Should().BeEmpty();

        static List<(Guid, string?)> Offered(ActionListPage p) => [.. p.Rows.Where(p.Workers.CanTake).Select(r => (r.TeamId, r.AssigneeId))];
    }

    [Fact]
    public async Task TakeIt_FromARow_IsRefused_WhenSomeoneTookItMeanwhile()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        await _db.Teams().AddMemberAsync(team, Me, "Test Analyst");
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

    [Fact]
    public async Task TakeIt_FromARow_IsRefused_WhenItWasClosedMeanwhile_EvenUnassigned()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        // Offered as open and nobody's; a colleague then cancelled it without assigning it.
        var a = await Add(team, x => (x.Status, x.Outcome, x.ClosedAtUtc) = (ActionStatus.Cancelled, ActionOutcome.None, DateTimeOffset.UtcNow));

        var take = () => _db.Actions().TakeAsync(a.Id, onlyIfNobodys: true);
        (await take.Should().ThrowAsync<StaleEditException>()).WithMessage("*Cancelled*");
        // The action page's Take (from someone or nobody) is held to the same rule: a page left open doesn't take it either.
        var fromPage = () => _db.Actions().TakeAsync(a.Id);
        await fromPage.Should().ThrowAsync<StaleEditException>();
        var after = await _db.Context().Actions.SingleAsync(x => x.Id == a.Id);
        (after.AssigneeId, after.Status).Should().Be(((string?)null, ActionStatus.Cancelled), "a closed action isn't taken from a stale row");
        (await _db.Context().ActionEvents.CountAsync(e => e.ActionId == a.Id && e.Text.StartsWith("Took it on"))).Should().Be(0);
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
        var a = await Add(team, x => (x.ArticleId, x.Status, x.StatusChangedAtUtc) = (kev, ActionStatus.InProgress, moved));
        var b = await Add(team, x => x.ArticleId = plain);
        await using (var ctx = _db.Context())
        {
            // History lines don't decide it, a person's words that look like a move least of all.
            ctx.ActionEvents.Add(new ActionEvent { ActionId = a.Id, AtUtc = moved.AddHours(2), ByName = "x", Text = "Deleted the checklist item \"Status: Open → In progress\"." });
            ctx.ActionEvents.Add(new ActionEvent { ActionId = b.Id, AtUtc = moved.AddHours(3), ByName = "x", Text = "Status: Open → Blocked: pasted from a ticket." });
            await ctx.SaveChangesAsync();
        }

        var board = await _db.Actions().BoardAsync(new ActionFilter(ActionView.Open));
        var cardA = board.Cards.Single(c => c.Id == a.Id);
        (cardA.ArticleTitle, cardA.ArticleKev).Should().Be(("Edge VPN zero-day exploited", true));
        cardA.InStatusSinceUtc!.Value.Should().BeCloseTo(moved, TimeSpan.FromSeconds(1), "the time its status last changed, as stored");
        var cardB = board.Cards.Single(c => c.Id == b.Id);
        (cardB.ArticleTitle, cardB.ArticleKev).Should().Be(("Phishing kit update", false));
        cardB.InStatusSinceUtc!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), "never moved: in Open since it was raised, whatever its history says");
    }

    // An action's stored status time, put back ten days, so a test can see whether the next step moves it.
    private async Task<DateTimeOffset> Age(Guid id)
    {
        var old = DateTimeOffset.UtcNow.AddDays(-10);
        await using var ctx = _db.Context();
        (await ctx.Actions.SingleAsync(x => x.Id == id)).StatusChangedAtUtc = old;
        await ctx.SaveChangesAsync();
        return old;
    }

    private async Task<DateTimeOffset?> Stamp(Guid id)
    {
        await using var ctx = _db.Context();
        return (await ctx.Actions.AsNoTracking().SingleAsync(x => x.Id == id)).StatusChangedAtUtc;
    }

    [Fact]
    public async Task EveryWayAnActionsStatusChanges_RecordsWhen_AndNothingElseDoes()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        await _db.Teams().AddMemberAsync(team, Me, "Test Analyst");
        var other = await _db.Teams().AddAsync("Network Engineering", null, null);
        var svc = _db.Actions();
        static void Now(DateTimeOffset? at, string because) => at!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), because);

        var n = await svc.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.Block, TeamId = team });
        var id = (await svc.GetAsync(n))!.Action.Id;
        Now(await Stamp(id), "raised: in Open from now");

        var old = await Age(id);
        var form = ActionForm.From((await svc.GetAsync(n))!.Action);
        form.Priority = Priority.P1;
        await svc.SaveAsync(form);
        (await Stamp(id)).Should().Be(old, "reading it, or editing what isn't its status, isn't a move");

        await svc.MoveAsync(id, ActionStatus.Open, ActionStatus.Blocked);
        Now(await Stamp(id), "a move on the board");

        await Age(id);
        await svc.SetStatusAsync(id, ActionStatus.InProgress, ActionOutcome.None, null);
        Now(await Stamp(id), "a status set on its page");

        old = await Age(id);
        await svc.TakeAsync(id);
        (await Stamp(id)).Should().Be(old, "taking one already in progress doesn't move it");

        await svc.RecordUpdateAsync(new TeamUpdate { ActionId = id, From = ActionStatus.InProgress, Status = ActionStatus.Done, Outcome = ActionOutcome.Blocked, Note = "Blocked at the edge." });
        Now(await Stamp(id), "an update from the team that closes it");

        old = await Age(id);
        await svc.ChangeOutcomeAsync(id, ActionStatus.Done, ActionOutcome.NotAffected, "Not affected after all.");
        (await Stamp(id)).Should().Be(old, "a closed action's outcome changed where it is");

        var fresh = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Second", Kind = ActionKind.Block, TeamId = team })))!.Action.Id;
        await Age(fresh);
        await svc.TakeAsync(fresh);
        Now(await Stamp(fresh), "taking an open one starts it");

        await _db.Handoffs().SplitAsync(fresh, [new SplitPart(other)]);
        await using var ctx = _db.Context();
        var child = await ctx.Actions.AsNoTracking().SingleAsync(x => x.ParentId == fresh);
        Now(child.StatusChangedAtUtc, "a part split off is raised now");
    }

    [Fact]
    public void SettingTheStatus_RecordsWhen_OnlyWhenItChanges()
    {
        var a = new ActionItem();
        a.StatusChangedAtUtc.Should().NotBeNull("a new action is in its first status from now");
        var old = DateTimeOffset.UtcNow.AddDays(-5);
        a.StatusChangedAtUtc = old;
        a.Status = ActionStatus.Open;
        a.StatusChangedAtUtc.Should().Be(old, "the status it already has");
        a.Status = ActionStatus.Blocked;
        a.StatusChangedAtUtc.Should().NotBe(old);
    }

    // A hunt query on an action, with its result.
    private static HuntQuery Hunt(Guid action, HuntResult result) =>
        new() { ActionId = action, Dialect = "xql", Text = "dataset = x", GeneratedText = "dataset = x", CreatedAtUtc = DateTimeOffset.UtcNow, CreatedByName = "x", Result = result };

    [Fact]
    public async Task EachRowAndCard_SaysHowItsHuntsWent_AndWhenSomethingLastHappenedToIt()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var none = await Add(team, x => x.Kind = ActionKind.IocSweep);
        var notRun = await Add(team, x => x.Kind = ActionKind.IocSweep);
        var clean = await Add(team, x => x.Kind = ActionKind.ThreatHunt);
        var hits = await Add(team, x => x.Kind = ActionKind.IocSweep);
        var quiet = await Add(team, x => x.Kind = ActionKind.PatchRemediate);
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = _db.Context())
        {
            ctx.HuntQueries.AddRange(Hunt(notRun.Id, HuntResult.NotRun), Hunt(clean.Id, HuntResult.NoHits), Hunt(clean.Id, HuntResult.NotRun),
                Hunt(hits.Id, HuntResult.NoHits), Hunt(hits.Id, HuntResult.Hits));
            foreach (var a in await ctx.Actions.Where(x => x.TeamId == team).ToListAsync()) a.CreatedAtUtc = now.AddDays(-10);
            ctx.ActionEvents.Add(new ActionEvent { ActionId = quiet.Id, AtUtc = now.AddDays(-4), ByName = "x", Text = "Changed the due date." });
            ctx.ActionComments.Add(new ActionComment { ActionId = quiet.Id, AtUtc = now.AddDays(-2), AuthorId = Me, AuthorName = "x", Text = "Asked the vendor." });
            ctx.ActionEvents.Add(new ActionEvent { ActionId = clean.Id, AtUtc = now.AddDays(-3), ByName = "x", Text = "Recorded the hunt's result." });
            await ctx.SaveChangesAsync();
        }

        var svc = _db.Actions();
        var page = (await svc.PageAsync(new ActionFilter(ActionView.Open))).Rows;
        var board = (await svc.BoardAsync(new ActionFilter(ActionView.Open))).Cards;
        foreach (var rows in new[] { page, board })
        {
            HuntResult? Of(ActionItem a) => rows.Single(r => r.Id == a.Id).Hunted;
            (Of(none), Of(notRun), Of(clean), Of(hits), Of(quiet)).Should().Be(((HuntResult?)null, HuntResult.NotRun, HuntResult.NoHits, HuntResult.Hits, (HuntResult?)null),
                "a hit anywhere is hits, else a clean run is no hits");
            DateTimeOffset? Last(ActionItem a) => rows.Single(r => r.Id == a.Id).LastActivityUtc;
            Last(quiet)!.Value.Should().BeCloseTo(now.AddDays(-2), TimeSpan.FromSeconds(1), "the latest of its history and its comments");
            Last(clean)!.Value.Should().BeCloseTo(now.AddDays(-3), TimeSpan.FromSeconds(1));
            Last(none)!.Value.Should().BeCloseTo(now.AddDays(-10), TimeSpan.FromSeconds(1), "nothing since it was raised");
        }
    }

    [Fact]
    public async Task TheList_ReadsItsRowsInAFixedNumberOfQueries_HoweverManyThereAre()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var svc = _db.Actions();
        var first = await Add(team, x => x.Kind = ActionKind.IocSweep);
        await using (var ctx = _db.Context())
        {
            ctx.HuntQueries.Add(Hunt(first.Id, HuntResult.NoHits));
            await ctx.SaveChangesAsync();
        }
        _db.Commands.Reset();
        await svc.PageAsync(new ActionFilter(ActionView.Open));
        var few = _db.Commands.Count;

        var (kev, _) = await Articles();
        for (var i = 0; i < 15; i++)
        {
            var a = await Add(team, x => (x.Kind, x.ArticleId) = (ActionKind.IocSweep, kev));
            await using var ctx = _db.Context();
            ctx.HuntQueries.Add(Hunt(a.Id, i % 2 == 0 ? HuntResult.Hits : HuntResult.NotRun));
            ctx.ActionEvents.Add(new ActionEvent { ActionId = a.Id, AtUtc = DateTimeOffset.UtcNow, ByName = "x", Text = "Changed it." });
            await ctx.SaveChangesAsync();
        }
        _db.Commands.Reset();
        (await svc.PageAsync(new ActionFilter(ActionView.Open))).Rows.Should().HaveCount(16).And.OnlyContain(r => r.Hunted != null && r.LastActivityUtc != null);
        _db.Commands.Count.Should().Be(few + 1, "the hunt state and last activity are read with the rows, and the KEV flag for them all at once, not row by row");
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

        for (var i = 0; i < 15; i++)
        {
            var a = await Add(team, x => (x.ArticleId, x.Status, x.Kind) = (kev, i % 2 == 0 ? ActionStatus.Blocked : ActionStatus.Open, ActionKind.IocSweep));
            await using var ctx = _db.Context();
            ctx.HuntQueries.Add(Hunt(a.Id, HuntResult.NoHits));
            ctx.ActionEvents.Add(new ActionEvent { ActionId = a.Id, AtUtc = DateTimeOffset.UtcNow, ByName = "x", Text = "Changed it." });
            await ctx.SaveChangesAsync();
        }
        _db.Commands.Reset();
        (await svc.BoardAsync(new ActionFilter(ActionView.Open))).Cards.Should().HaveCount(16).And.Contain(c => c.Hunted == HuntResult.NoHits);
        _db.Commands.Count.Should().Be(few, "the article, KEV flag, time in column, hunt state and last activity are read for all the cards at once, not card by card");
    }

    [Fact]
    public async Task AnArticlesTitle_IsShownOnlyToSomeoneWhoReadsArticles()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var (kev, _) = await Articles();
        await Add(team, x => (x.ArticleId, x.RecommendationLine) = (kev, "Patch the edge VPN"));

        // Someone who works actions but doesn't read the team edition (so can't open the article): no title, no KEV flag,
        // no line copied from it, and no finding actions by its title.
        var worker = new OnlyUser("S-1-5-21-WORKER", Permission.WorkActions, Permission.ReadActions);
        var svc = new ActionService(_db, worker, _db.Sla(), _db.Clock, _db.Queries(), _db.Screenshots(), _db.Articles());
        var card = (await svc.BoardAsync(new ActionFilter(ActionView.Open))).Cards.Single();
        (card.ArticleTitle, card.ArticleKev).Should().Be(((string?)null, false));
        var row = (await svc.PageAsync(new ActionFilter(ActionView.Open))).Rows.Single();
        (row.ArticleTitle, row.RecommendationLine).Should().Be(((string?)null, (string?)null));
        (await svc.PageAsync(new ActionFilter(ActionView.Open) { Search = "zero-day" })).Rows.Should().BeEmpty("the article's title isn't theirs to search");

        // Someone who reads articles finds it by the article's title.
        (await _db.Actions().PageAsync(new ActionFilter(ActionView.Open) { Search = "zero-day" })).Rows.Should().ContainSingle();
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
