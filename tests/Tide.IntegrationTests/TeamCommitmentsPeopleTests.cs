using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Application.Trends;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Team commitments by person: each action due in the window counts for whoever held it at the end of its due date, read
/// back from the audit trail, and each reader sees only the lines they may: their own, or a manager's everyone's.
/// </summary>
public class TeamCommitmentsPeopleTests : IDisposable
{
    // Today: Tuesday 15 September 2026, as the scorecard's own tests have it.
    private static readonly OrgClock Clock = new(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(2026, 9, 15, 16, 0, 0, TimeSpan.Zero)));
    private const string Robin = "dev:robin", Sam = "dev:sam";
    private static int _number = 9000;
    private TestDb _db = new();
    private Team _team = null!;

    public void Dispose() => _db.Dispose();

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static ActionItem Act(Team team, DateOnly due, string? holder, ActionStatus status = ActionStatus.Open, DateOnly? closed = null) => new()
    {
        Number = Interlocked.Increment(ref _number), Title = "Person " + _number, Kind = ActionKind.Other, Team = team, DueDate = due,
        Status = status, ClosedDate = closed, AssigneeId = holder, AssigneeName = Name(holder),
    };

    private static string? Name(string? id) => id switch { Robin => "Robin", Sam => "Sam", _ => null };

    // A change of assignee as the audit trail records it, at a local time on a day.
    private static AuditLogEntry Reassigned(ActionItem a, DateOnly day, string? from, string? to) => new()
    {
        AtUtc = OrgClock.At(Clock.Zone, day, new TimeOnly(9, 0)), UserId = "dev:lead", UserName = "Lead", Action = "Modified",
        EntityType = nameof(ActionItem), EntityKey = a.Id.ToString("D"),
        ChangesJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?[]>
        {
            [nameof(ActionItem.AssigneeId)] = [from, to],
            [nameof(ActionItem.AssigneeName)] = [Name(from), Name(to)],
        }),
    };

    private async Task Seed()
    {
        _team = new Team { Key = "cd-people", Name = "Cyber Defense", SortOrder = 1 };
        var onTime = Act(_team, D(9, 10), Robin, ActionStatus.Done, D(9, 8));
        var handedAfter = Act(_team, D(9, 1), Sam, ActionStatus.Done, D(9, 5));     // Robin's on its due date: 4 days late
        var handedBefore = Act(_team, D(9, 5), Sam, ActionStatus.InProgress);       // Sam's from Aug 30: 10 days late, overdue
        var nobodys = Act(_team, D(9, 2), null, ActionStatus.Done, D(9, 1));          // nobody's, on time
        await using var c = _db.Context();
        c.AddRange(_team, onTime, handedAfter, handedBefore, nobodys);
        await c.SaveChangesAsync();
        c.AuditLog.AddRange(Reassigned(handedAfter, D(9, 3), Robin, Sam), Reassigned(handedBefore, D(8, 30), Robin, Sam));
        await c.SaveChangesAsync();
    }

    private Task<TeamPeople> People() => _db.TeamScorecard(Clock).PeopleAsync(_team.Id, 30);

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task EachActionCounts_ForWhoeverHeldItOnItsDueDate(string provider)
    {
        _db = _db.On(provider);
        await Seed();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Manager };

        var p = await People();

        p.Everyone.Should().BeTrue();
        var robin = p.People.Single(x => x.PersonId == Robin);
        (robin.Parts, robin.OnTime, robin.Late, robin.AverageDaysLate).Should().Be((2, 1, 1, 4.0));
        (robin.OpenNow, robin.OverdueNow).Should().Be((0, 0));
        var sam = p.People.Single(x => x.PersonId == Sam);
        (sam.Parts, sam.OnTime, sam.Late, sam.OpenNow, sam.OverdueNow).Should().Be((1, 0, 1, 1, 1));
        var nobody = p.People.Single(x => x.PersonId is null);
        (nobody.Parts, nobody.OnTime).Should().Be((1, 1));
        p.People[^1].PersonId.Should().BeNull("the team's own line comes last");
        (p.Team.Parts, p.Team.OnTime, p.Team.Late).Should().Be((4, 2, 2), "the team's line is the scorecard's");
    }

    [Theory]
    [InlineData(AppRole.ActionOwner)]
    [InlineData(AppRole.Analyst)]
    [InlineData(AppRole.Viewer)]
    public async Task AnyoneElseWhoReadsActions_SeesOnlyTheirOwnLine(AppRole role)
    {
        await Seed();
        (_db.User.UserId, _db.User.DisplayName, _db.User.Roles) = (Sam, "Sam", new HashSet<AppRole> { role });

        var p = await People();

        p.Everyone.Should().BeFalse();
        p.People.Should().ContainSingle().Which.PersonId.Should().Be(Sam);
        p.Team.Parts.Should().Be(4, "team rows stay as they are");
    }

    [Theory]
    [InlineData(AppRole.Reader)]
    [InlineData(AppRole.Visitor)]
    public async Task WhoeverDoesntReadActions_IsRefused(AppRole role)
    {
        await Seed();
        _db.User.Roles = new HashSet<AppRole> { role };
        var read = () => People();
        await read.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task AnAdministrator_SeesEveryone()
    {
        await Seed();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        (await People()).People.Select(x => x.PersonId).Should().BeEquivalentTo([Robin, Sam, null]);
    }

    [Fact]
    public async Task ATeamNotOnTheScorecard_IsNotFound()
    {
        await Seed();
        var read = () => _db.TeamScorecard(Clock).PeopleAsync(Guid.NewGuid());
        await read.Should().ThrowAsync<KeyNotFoundException>();
    }
}

/// <summary>The by-person page in the app: a manager's lines, an action owner's own only.</summary>
public class TeamCommitmentsPeoplePageTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<Guid> TeamWithTwoHolders()
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var c = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        var today = app.Services.GetRequiredService<OrgClock>().Today;
        var team = new Team { Key = "people-" + Guid.NewGuid().ToString("N")[..6], Name = "People desk" };
        c.Teams.Add(team);
        foreach (var (who, name) in new[] { ("dev:olive", "Olive"), ("dev:pete", "Pete") })
            c.Actions.Add(new ActionItem
            {
                Number = 50000 + Random.Shared.Next(100000), Title = "Due for " + name, Kind = ActionKind.Other, TeamId = team.Id,
                AssigneeId = who, AssigneeName = name, DueDate = today.AddDays(-3), Status = ActionStatus.InProgress,
            });
        await c.SaveChangesAsync();
        return team.Id;
    }

    [Fact]
    public async Task AManager_SeesEachPersonsLine_AndAnActionOwner_OnlyTheirOwn()
    {
        var team = await TeamWithTwoHolders();

        await using (var page = await RenderAsync<TeamCommitmentsPeople>(app, $"/reports/teams/{team}", new() { ["TeamId"] = team }, new SignIn("dev:lee", "Lee", "Manager")))
        {
            var html = await page.Html();
            html.Should().Contain("People desk by person").And.Contain("Olive").And.Contain("Pete");
        }
        await using (var page = await RenderAsync<TeamCommitmentsPeople>(app, $"/reports/teams/{team}", new() { ["TeamId"] = team }, new SignIn("dev:olive", "Olive", "ActionOwner")))
        {
            var html = await page.Html();
            html.Should().Contain("Olive").And.NotContain("Pete").And.Contain("You see only your own line");
        }
    }

    [Theory]
    [InlineData("Reader")]
    [InlineData("Visitor")]
    public async Task WhoeverDoesntReadActions_IsTurnedAway(string role)
    {
        var team = await TeamWithTwoHolders();
        var page = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync($"/reports/teams/{team}?as=Val&roles={role}");
        page.StatusCode.Should().Be(HttpStatusCode.Redirect);
        page.Headers.Location!.OriginalString.Should().StartWith("/access-denied");
    }

    [Fact]
    public async Task TheScorecard_LinksEachTeamToItsPeople()
    {
        var team = await TeamWithTwoHolders();
        var html = await (await app.CreateClient().GetAsync("/reports/teams")).Content.ReadAsStringAsync();
        html.Should().Contain($"href=\"reports/teams/{team}\"");
    }
}
