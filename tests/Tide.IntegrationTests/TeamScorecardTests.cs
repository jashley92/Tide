using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tide.Application.Actions;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Application.Trends;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The team commitments scorecard (EP-88): each team against the due dates on its actions, over a window ending today,
/// counted in the database on either provider.
/// </summary>
public class TeamScorecardTests
{
    // Today, for every test here: Tuesday 15 September 2026.
    private static readonly DateOnly Today = new(2026, 9, 15);
    private static readonly OrgClock Clock = new(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(2026, 9, 15, 16, 0, 0, TimeSpan.Zero)));

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static int _number;

    private static ActionItem Act(Team team, DateOnly? due, ActionStatus status = ActionStatus.Open, DateOnly? closed = null, ActionItem? parent = null) => new()
    {
        Number = Interlocked.Increment(ref _number), Title = "Scorecard " + _number, Kind = ActionKind.Other, Team = team, DueDate = due,
        Status = status, ClosedDate = closed, ParentId = parent?.Id,
    };

    private static TestDb Db(string provider = TestDb.Sqlite)
    {
        var db = new TestDb(provider: provider);
        db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        return db;
    }

    private static async Task Add(TestDb db, params object[] rows)
    {
        await using var c = db.Context();
        c.AddRange(rows);
        await c.SaveChangesAsync();
    }

    private sealed record Seeded(Team Internal, Team External, Team RetiredBusy);

    // An internal team with each kind of action, an external one with a part of a split, a retired team with an action
    // still open, and a retired team with nothing (which isn't listed).
    private static async Task<Seeded> Seed(TestDb db)
    {
        var cd = new Team { Key = "cyber-defense", Name = "Cyber Defense", SortOrder = 1 };
        var soc = new Team { Key = "managed-soc", Name = "Managed SOC", SortOrder = 2, IsExternal = true };
        var old = new Team { Key = "old-desk", Name = "Old desk", SortOrder = 3, IsRetired = true };
        var gone = new Team { Key = "gone", Name = "Gone", SortOrder = 4, IsRetired = true };
        var parent = Act(cd, null);   // open, no due date: open now, but no commitment
        await Add(db, cd, soc, old, gone,
            Act(cd, D(9, 10), ActionStatus.Done, D(9, 8)),            // on time
            Act(cd, D(9, 1), ActionStatus.Done, D(9, 1)),             // on time, on the day
            Act(cd, D(9, 1), ActionStatus.Done, D(9, 5)),             // 4 days late
            Act(cd, D(9, 5), ActionStatus.InProgress),                // 10 days late so far, overdue now
            Act(cd, D(9, 15)),                                        // due today: counted, neither yet
            Act(cd, D(9, 2), ActionStatus.Cancelled, D(9, 9)),        // counted, neither
            Act(cd, D(10, 1), ActionStatus.Tracking),                 // due after the window: open now only
            Act(cd, D(6, 1), ActionStatus.Done, D(6, 20)),            // due before the window
            parent,
            Act(soc, D(9, 3), ActionStatus.Done, D(9, 4), parent),    // a part, 1 day late
            Act(soc, D(8, 1), ActionStatus.Done, D(7, 30)),           // on time
            Act(old, D(8, 1), ActionStatus.Blocked));                 // 45 days late, overdue now
        return new Seeded(cd, soc, old);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task EachTeam_CountsOnTimeLateOpenAndOverdue_ByDueDateInTheWindow(string provider)
    {
        using var db = Db(provider);
        var s = await Seed(db);

        var card = await db.TeamScorecard(Clock).BuildAsync();

        card.Days.Should().Be(90);
        (card.From, card.To).Should().Be((D(6, 18), Today));
        card.Teams.Select(t => t.Team.Name).Should().Equal(["Cyber Defense", "Managed SOC", "Old desk"], "a retired team with nothing to show isn't listed");
        card.Teams[0].Should().BeEquivalentTo(new { Parts = 6, OnTime = 2, Late = 2, AverageDaysLate = 7.0, OnTimePercent = 50.0, OpenNow = 4, OverdueNow = 1 });
        card.Teams[1].Should().BeEquivalentTo(new { Parts = 2, OnTime = 1, Late = 1, AverageDaysLate = 1.0, OnTimePercent = 50.0, OpenNow = 0, OverdueNow = 0 });
        card.Teams[1].Team.IsExternal.Should().BeTrue();
        card.Teams[2].Should().BeEquivalentTo(new { Parts = 1, OnTime = 0, Late = 1, AverageDaysLate = 45.0, OnTimePercent = 0.0, OpenNow = 1, OverdueNow = 1 });
        card.Teams[2].Team.Id.Should().Be(s.RetiredBusy.Id);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task PartsOnly_CountsTheSplitsParts_AndExternalOnly_ListsTheExternalTeams(string provider)
    {
        using var db = Db(provider);
        await Seed(db);
        var scorecard = db.TeamScorecard(Clock);

        var parts = await scorecard.BuildAsync(partsOnly: true);
        parts.PartsOnly.Should().BeTrue();
        parts.Teams[0].Should().BeEquivalentTo(new { Parts = 0, OnTime = 0, Late = 0, OpenNow = 0, OverdueNow = 0 }, "no part of a split is Cyber Defense's");
        parts.Teams[0].OnTimePercent.Should().BeNull();
        parts.Teams[0].AverageDaysLate.Should().BeNull();
        parts.Teams[1].Should().BeEquivalentTo(new { Parts = 1, OnTime = 0, Late = 1, AverageDaysLate = 1.0, OnTimePercent = 0.0 });
        parts.Teams.Select(t => t.Team.Name).Should().NotContain("Old desk", "retired, with no part to show");

        var external = await scorecard.BuildAsync(externalOnly: true);
        external.Teams.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Parts = 2, OnTime = 1, Late = 1 });

        (await scorecard.BuildAsync(partsOnly: true, externalOnly: true)).Teams.Should().ContainSingle().Which.Parts.Should().Be(1);
    }

    [Fact]
    public async Task TheWindow_RunsFromItsFirstDayToToday_Inclusive_AndOnlyTheOffersAreTaken()
    {
        using var db = Db();
        var team = new Team { Key = "window", Name = "Window team" };
        await Add(db, team,
            Act(team, D(8, 16), ActionStatus.Done, D(8, 16)),   // the day before the 30 days
            Act(team, D(8, 17), ActionStatus.Done, D(8, 17)),   // the first of them
            Act(team, D(9, 15), ActionStatus.Done, D(9, 15)),   // today
            Act(team, D(9, 16), ActionStatus.Done, D(9, 15)));  // tomorrow
        var scorecard = db.TeamScorecard(Clock);

        var month = await scorecard.BuildAsync(30);
        (month.From, month.To, month.Days).Should().Be((D(8, 17), Today, 30));
        month.Teams.Single().Should().BeEquivalentTo(new { Parts = 2, OnTime = 2, Late = 0, OnTimePercent = 100.0 });
        (await scorecard.BuildAsync(365)).From.Should().Be(D(9, 16).AddYears(-1));
        (await scorecard.BuildAsync(45)).Days.Should().Be(90, "45 isn't offered");
        TeamScorecardService.WindowOrDefault(null).Should().Be(TeamScorecardService.DefaultDays);
        TeamScorecardService.Windows.Should().Equal([30, 90, 180, 365]);
    }

    [Theory]
    [InlineData(AppRole.Reader)]
    [InlineData(AppRole.Visitor)]
    public async Task OnlyThoseWhoReadActions_ReadIt(AppRole role)
    {
        using var db = Db();
        await db.TeamScorecard(Clock).BuildAsync();   // an action owner
        db.User.Roles = new HashSet<AppRole> { AppRole.Viewer };
        await db.TeamScorecard(Clock).BuildAsync();

        db.User.Roles = new HashSet<AppRole> { role };
        await db.TeamScorecard(Clock).Invoking(s => s.BuildAsync()).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task TheCsv_HasARowPerTeam_ItsWindow_AndNoFormula()
    {
        using var db = Db();
        await Seed(db);
        await Add(db, new Team { Key = "formula", Name = "=HYPERLINK(\"x\")", SortOrder = 9 });

        var csv = TeamScorecardService.ToCsv(await db.TeamScorecard(Clock).BuildAsync());

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be("team,external,parts,on_time,late,on_time_percent,average_days_late,open_now,overdue_now,from,to");
        lines.Should().Contain("Cyber Defense,no,6,2,2,50,7,4,1,2026-06-18,2026-09-15")
            .And.Contain("Managed SOC,yes,2,1,1,50,1,0,0,2026-06-18,2026-09-15")
            .And.Contain("\"'=HYPERLINK(\"\"x\"\")\",no,0,0,0,,,0,0,2026-06-18,2026-09-15", "no on-time share or days late with nothing measured");
    }
}

/// <summary>The scorecard's page, its links and its CSV endpoint, in the running app.</summary>
public class TeamScorecardPageTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<Guid> Team(string name, bool external)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var teams = scope.ServiceProvider.GetRequiredService<TeamService>();
        return external ? await teams.AddExternalAsync(name, null) : await teams.AddAsync(name, null, null);
    }

    [Fact]
    public async Task ThePage_ListsEachTeam_LinkingToItsActions_WithTheWindowAndFilters()
    {
        var inside = await Team("EP88 scorecard team", external: false);
        var outside = await Team("EP88 scorecard vendor", external: true);

        await using (var r = await RenderAsync<TeamCommitments>(app, "/reports/teams"))
        {
            var html = WebUtility.HtmlDecode(await r.Html());
            html.Should().Contain("<h1>Team commitments</h1>").And.Contain("EP88 scorecard team").And.Contain("EP88 scorecard vendor")
                .And.Contain($"href=\"actions?view=open&team={inside}&layout=list\"").And.Contain($"href=\"actions?view=overdue&team={outside}&layout=list\"")
                .And.Contain(">External<").And.Contain("href=\"reports/teams/export.csv\"").And.Contain(">90 days</button>")
                .And.Contain("Parts of splits only").And.Contain("External teams only");
        }

        await using var filtered = await RenderAsync<TeamCommitments>(app, "/reports/teams?days=30&parts=true&external=true");
        var only = WebUtility.HtmlDecode(await filtered.Html());
        only.Should().Contain("EP88 scorecard vendor").And.NotContain("EP88 scorecard team").And.Contain(">Parts due<")
            .And.Contain("href=\"reports/teams/export.csv?days=30&parts=true&external=true\"");
        TeamCommitments.Href(90, false, false).Should().Be("reports/teams");
        TeamCommitments.Href(180, true, false).Should().Be("reports/teams?days=180&parts=true");
    }

    [Fact]
    public async Task ReportsAndWorkload_LinkToIt()
    {
        await using (var reports = await RenderAsync<Reports>(app, "/reports?p=2026-10"))
            (await reports.Html()).Should().Contain("href=\"reports/teams\"");
        await using var workload = await RenderAsync<Workload>(app, "/workload");
        (await workload.Html()).Should().Contain("href=\"reports/teams\"");
    }

    [Fact]
    public async Task TheCsv_DownloadsWithItsWindowAndFiltersInItsName()
    {
        await Team("EP88 csv team", external: false);
        var res = await app.CreateClient().GetAsync("/reports/teams/export.csv?days=180&parts=true");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        res.Content.Headers.ContentDisposition!.FileName.Should().MatchRegex(@"^tide-team-commitments-\d{4}-\d{2}-\d{2}-180d-parts\.csv$");
        res.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await res.Content.ReadAsStringAsync()).Should().StartWith("team,external,parts,").And.Contain("EP88 csv team,no,0,");
    }
}
