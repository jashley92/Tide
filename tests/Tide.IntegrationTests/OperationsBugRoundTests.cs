using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tide.Application.Actions;
using Tide.Application.Operations;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Infrastructure;
using Tide.TestSupport;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>Bug round, running and administering TIDE: the background jobs, admin settings and Diagnostics.</summary>
public class OperationsBugRoundTests : IDisposable
{
    private readonly TestDb _db = new();

    public OperationsBugRoundTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };

    public void Dispose() => _db.Dispose();

    // 09:45 local on a Monday: the reminders' time has come.
    private static OrgClock Monday() =>
        new(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(2026, 10, 26, 13, 45, 0, TimeSpan.Zero)));

    // A reminder scan that failed (the relay down) left the job's run a success on Diagnostics, "No failures since the
    // app started": the scans catch their own failures so the others still run, and the poller never heard of it.
    [Fact]
    public async Task AReminderScanThatFails_MakesTheJobsRunAFailure_OnDiagnostics()
    {
        var clock = Monday();
        var team = await _db.Teams().AddAsync("Cyber Defense", null, "defense@example.org");
        await _db.Actions().CreateAsync(new ActionForm { Title = "Late", TeamId = team, DueDate = clock.Today.AddDays(-5) });
        var options = new ReminderOptions { Overdue = { Enabled = true } };
        _db.Email.Fault = _ => new InvalidOperationException("The relay refused the connection.");
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.Reminders(options, clock));
        await using var provider = services.BuildServiceProvider();
        var jobs = new JobMonitor(TimeProvider.System);
        var hosted = new ReminderHostedService(provider.GetRequiredService<IServiceScopeFactory>(), jobs, TimeProvider.System,
            NullLogger<ReminderHostedService>.Instance);

        await hosted.PassAsync(CancellationToken.None);

        var state = jobs.Get(JobMonitor.Reminders);
        state.LastSucceeded.Should().BeFalse();
        state.LastError.Should().Contain("Overdue").And.Contain("relay refused");
        state.NextDueUtc.Should().NotBeNull("it's tried again at the next poll");

        _db.Email.Fault = null;
        await hosted.PassAsync(CancellationToken.None);
        jobs.Get(JobMonitor.Reminders).Should().Match<JobState>(s => s.LastSucceeded == true && s.LastDetail == "1 email");
    }

    // A mistyped provider ("SQL Server", "MSSQL") was taken for SQLite: start-up then failed reading the SQL Server
    // connection string as a SQLite one ("Connection string keyword 'server' is not supported"), which names neither the
    // setting nor the fix; with no connection string it ran on a new, empty SQLite file instead of the install's database.
    [Theory]
    [InlineData("SQL Server")]
    [InlineData("MSSQL")]
    [InlineData(" SqlServer")]
    public void AMistypedDatabaseProvider_StopsStartUp_SayingWhichSettingAndWhatItTakes(string typed)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = typed,
            ["ConnectionStrings:Default"] = "Server=sql01;Database=TIDE;Trusted_Connection=True",
        }).Build();

        var start = () => new ServiceCollection().AddInfrastructure(config);

        start.Should().Throw<InvalidOperationException>().WithMessage($"*Database:Provider*'{typed}'*SqlServer*Sqlite*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sqlite")]
    [InlineData("sqlserver")]
    public void ADatabaseProviderInAnyCase_OrNone_IsTaken(string? typed)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Provider"] = typed }).Build();

        var start = () => new ServiceCollection().AddInfrastructure(config);

        start.Should().NotThrow();
    }

    // A team retired while it still had open actions (they stay on it) dropped off Workload with them: its overdue
    // actions were on no team's card, though Team commitments still listed it. Once its actions are closed, it goes.
    [Fact]
    public async Task ARetiredTeam_StaysOnWorkload_WhileItHasOpenActions()
    {
        var teams = _db.Teams();
        var retiring = await teams.AddAsync("Old SOC", null, null);
        var idle = await teams.AddAsync("Idle", null, null);
        await teams.AddAsync("Busy", null, null);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Late", TeamId = retiring, DueDate = _db.Clock.Today.AddDays(-20) });
        // Idle owned an action once (so it's retired, not deleted), now closed.
        var closed = await _db.Actions().CreateAsync(new ActionForm { Title = "Done", TeamId = idle });
        await using (var ctx = _db.Context())
        {
            var a = ctx.Actions.Single(x => x.Number == closed);
            (a.Status, a.ClosedDate) = (ActionStatus.Done, _db.Clock.Today.AddDays(-30));
            await ctx.SaveChangesAsync();
        }
        await teams.SetRetiredAsync(retiring, true);
        await teams.SetRetiredAsync(idle, true);

        var load = await _db.Actions().WorkloadAsync();

        load.Select(t => t.Team.Name).Should().BeEquivalentTo(["Old SOC", "Busy"]);
        load.Single(t => t.Team.Id == retiring).Should().Match<TeamLoad>(t => t.Team.IsRetired && t.Open == 1 && t.Overdue == 1);
    }
}
