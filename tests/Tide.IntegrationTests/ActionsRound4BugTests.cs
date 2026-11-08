using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Files;
using Tide.Application.Metrics;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Infrastructure.Storage;
using Tide.TestSupport;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Bug round 4, actions: a meeting's new due date leaves whoever holds the action now; removing a query can't
/// take one a colleague just recorded; and a metrics file copied with an old time stamp isn't pruned the pass it's read.</summary>
public class ActionsRound4BugTests : IDisposable
{
    private readonly TestDb _db = new();

    public ActionsRound4BugTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AMeetingsNewDueDate_LeavesWhoeverHoldsTheActionNow()
    {
        var teams = _db.Teams();
        var team = await teams.AddAsync("Cyber Defense", null, null);
        await teams.AddMemberAsync(team, "S-1-5-21-SAM", "Sam");
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Sweep the proxy logs", Kind = ActionKind.IocSweep, TeamId = team });
        var id = (await svc.GetAsync(n))!.Action.Id;
        await svc.AssignAsync(id, "S-1-5-21-SAM", null);

        var due = svc.Today.AddDays(9);
        await svc.SetDueAsync(id, due);

        var saved = (await svc.GetAsync(n))!.Action;
        saved.AssigneeId.Should().Be("S-1-5-21-SAM", "only the date was changed");
        saved.AssigneeName.Should().Be("Sam");
        saved.DueDate.Should().Be(due);
        saved.Events.First().Text.Should().Contain("due:").And.NotContain("assignee");
    }

    [Fact]
    public async Task RemovingAQuery_AColleagueRecordedAtTheSameMoment_IsRefused_AndTheRecordingKept()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Hunt", Kind = ActionKind.ThreatHunt, TeamId = team });
        var action = (await svc.GetAsync(n))!.Action;
        var hunt = await svc.AddHandWrittenHuntAsync(action.Id, "kql", "DeviceProcessEvents | take 1");

        // A colleague records it (moving the action's version, as a recording does) between this removal's check and its save.
        _db.BeforeSave = _ =>
        {
            _db.BeforeSave = null;
            using var ctx = _db.PlainContext();
            ctx.HuntQueries.Where(h => h.Id == hunt.Id).ExecuteUpdate(u => u.SetProperty(h => h.Result, HuntResult.Hits).SetProperty(h => h.ResultCount, 3));
            ctx.Actions.Where(a => a.Id == action.Id).ExecuteUpdate(u => u.SetProperty(a => a.EditVersion, Guid.NewGuid()));
        };
        await FluentActions.Invoking(() => svc.RemoveHuntAsync(hunt.Id)).Should().ThrowAsync<StaleEditException>();

        var saved = (await svc.GetAsync(n))!.Action;
        saved.Hunts.Should().ContainSingle().Which.Result.Should().Be(HuntResult.Hits, "the recorded query stays, as one with a result always does");

        // With nothing racing it, a query not run is still removed.
        var other = await svc.AddHandWrittenHuntAsync(action.Id, "kql", "DeviceFileEvents | take 1");
        await svc.RemoveHuntAsync(other.Id);
        (await svc.GetAsync(n))!.Action.Hunts.Should().ContainSingle(h => h.Id == hunt.Id);
    }

    [Fact]
    public async Task AMetricsFileCopiedWithAnOldTimeStamp_IsKeptInProcessed_NotPrunedTheSamePass()
    {
        var group = await _db.Metrics().AddGroupAsync("SOC");
        await _db.Metrics().SaveMetricAsync(new MetricForm { GroupId = group, Name = "Open" });
        var root = Path.Combine(Path.GetTempPath(), "tide-r4-drop-" + Guid.NewGuid().ToString("N"));
        var folder = new MetricDropFolder(Microsoft.Extensions.Options.Options.Create(new FileStoreOptions { RootPath = root }), new TestHostEnvironment(),
            new StaticOptionsMonitor<MetricImportOptions>(new MetricImportOptions { DropFolderEnabled = true }), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MetricDropFolder>.Instance);
        try
        {
            Directory.CreateDirectory(folder.Inbox);
            var path = Path.Combine(folder.Inbox, "queue.csv");
            await File.WriteAllTextAsync(path, $"date,metric_key,value\n{_db.Clock.Today.AddDays(-1):yyyy-MM-dd},open,42\n");
            // Copied by a job that keeps the source's time (robocopy, an unzip): written long before the KeepFor.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-200));
            _db.User.IsAuthenticated = false;

            (await folder.ProcessAsync(_db.MetricImports())).Should().Be(1);

            var processed = Directory.GetFiles(Path.Combine(folder.Inbox, "processed"));
            processed.Should().ContainSingle(f => f.EndsWith("-queue.csv"), "it's kept for KeepFor from when it was read");
            File.GetLastWriteTimeUtc(processed[0]).Should().BeAfter(DateTime.UtcNow.AddMinutes(-5));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

/// <summary>Bug round 4, actions, as the pages do it: the meeting's assignee and due-date boxes change only what they
/// say, and an approved swap whose taken days are still to come can be undone.</summary>
public class ActionsRound4RenderTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TheMeetingsAssignee_KeepsTheDueDate_AndItsDueDate_KeepsTheHolder_AsTheyAreNow()
    {
        int number;
        Guid id, team;
        var today = app.Services.GetRequiredService<Tide.Application.Org.OrgClock>().Today;
        var (seenDue, theirDue, newDue) = (today.AddDays(3), today.AddDays(6), today.AddDays(12));
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var teams = scope.ServiceProvider.GetRequiredService<TeamService>();
            team = await teams.AddAsync("Meeting Assign Team " + Guid.NewGuid().ToString("N")[..6], null, null);
            await teams.AddMemberAsync(team, "u:r4-sam", "Sam");
            await teams.AddMemberAsync(team, "u:r4-robin", "Robin");
            var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
            number = await actions.CreateAsync(new ActionForm { Title = "Patch the VPN", Kind = ActionKind.PatchRemediate, TeamId = team, DueDate = seenDue });
            id = (await actions.GetAsync(number))!.Action.Id;
        }

        var day = Tide.Application.Org.WorkingDays.Weekdays.Previous(today.AddDays(-60));
        await using var r = await RenderAsync<Present>(app, $"/present/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" });
        // What the meeting's slide read: unassigned, due seenDue. Then a colleague, elsewhere, gives it to Sam and moves its date.
        var seen = new EditionAction(number, "Patch the VPN", "Team", seenDue, ActionStatus.Open, id, team);
        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<ActionService>().AssignAsync(id, "u:r4-sam", theirDue);

        await r.Click(p => p.Assign(seen, "u:r4-robin"));
        var after = await Saved();
        after.AssigneeId.Should().Be("u:r4-robin");
        after.DueDate.Should().Be(theirDue, "the slide's older date isn't put back");

        // The slide still shows Sam as the holder; a new date typed there leaves Robin with it.
        var stale = seen with { AssigneeId = "u:r4-sam", AssigneeName = "Sam", DueDate = theirDue };
        var typed = (Dictionary<Guid, string?>)typeof(Present).GetField("_dueTyped", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(r.Component)!;
        await r.Click(p =>
        {
            typed[id] = Tide.Web.Components.Shared.Ui.IsoDay(newDue);
            return p.SetDue(stale);
        });
        after = await Saved();
        after.AssigneeId.Should().Be("u:r4-robin", "only the date was changed, not who holds it");
        after.DueDate.Should().Be(newDue);

        async Task<ActionItem> Saved()
        {
            await using var scope = SignedIn(app, SignIn.Analyst);
            return (await scope.ServiceProvider.GetRequiredService<ActionService>().GetAsync(number))!.Action;
        }
    }

    [Fact]
    public async Task AnApprovedSwap_WhoseTakenDaysAreStillToCome_CanBeUndone()
    {
        var today = app.Services.GetRequiredService<Tide.Application.Org.OrgClock>().Today;
        var name = "Undo Swapper " + Guid.NewGuid().ToString("N")[..6];
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.RotationChanges.Add(new RotationChange
            {
                Kind = RotationChangeKind.Swap, RequesterId = "u:r4-a", RequesterName = name, PartnerId = "u:r4-b", PartnerName = "Partner",
                GiveFrom = today.AddDays(-9), GiveTo = today.AddDays(-8), TakeFrom = today.AddDays(20), TakeTo = today.AddDays(21),
                Status = RotationChangeStatus.Approved, CreatedById = "u:r4-a", CreatedByName = name, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(1),
                DecidedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using var r = await RenderAsync<RotationPage>(app, "/rotation");
        var html = System.Net.WebUtility.HtmlDecode(await r.Html());
        var row = html[html.IndexOf(name + " and Partner swap", StringComparison.Ordinal)..];
        row[..row.IndexOf("</div>", StringComparison.Ordinal)].Should().Contain("Undo</button>", "its second half hasn't happened yet");
    }
}
