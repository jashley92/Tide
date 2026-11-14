using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Application.Abstractions;
using Tide.Application.Operations;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>A dead holder on this machine is taken over at once, rather than when its lease lapses.</summary>
public class DeadHolderTakeoverTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Here = Environment.MachineName;

    private static (BackgroundJobLease Lease, ServiceProvider Provider) Lease(IAppDbContextFactory dbs, TimeProvider clock, string holder) =>
        JobLeaseTests.Lease(dbs, clock, holder);

    /// <summary>The id of a process that has run and exited, and that no running process has taken since.</summary>
    internal static int DeadPid()
    {
        for (var tries = 0; tries < 5; tries++)
        {
            int pid;
            using (var p = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false })!)
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                pid = p.Id;
            }
            try { using var _ = Process.GetProcessById(pid); }
            catch (ArgumentException) { return pid; }
        }
        throw new InvalidOperationException("Couldn't find a process id that isn't running.");
    }

    /// <summary>Writes the lease as <paramref name="holder"/>'s, live for its whole expiry.</summary>
    private static async Task HeldBy(TestDb db, string holder, DateTimeOffset at)
    {
        await using var ctx = db.Context();
        ctx.JobLeases.Add(new Domain.Entities.JobLease { Name = BackgroundJobLease.BackgroundJobs, Holder = holder, AcquiredAtUtc = at, ExpiresAtUtc = at + BackgroundJobLease.Expiry });
        await ctx.SaveChangesAsync();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AHolderWhoseProcessIsGone_OnThisMachine_IsTakenOverAtOnce(string provider)
    {
        using var db = new TestDb(provider: provider);
        var clock = new TestClock(Start);
        var dead = $"{Here}/{DeadPid()}/aaaa";
        await HeldBy(db, dead, Start);
        var (b, pb) = Lease(db, clock, $"{Here}/{Environment.ProcessId}/bbbb");
        await using var _ = pb;

        clock.Now = Start.AddSeconds(5);
        (await b.HoldAsync()).Should().BeTrue("the holder's process is gone, so its lease is let go without waiting out the expiry");
        b.Status.HeldSinceUtc.Should().Be(clock.Now);
        await using var ctx = db.Context();
        (await ctx.JobLeases.SingleAsync()).Should().Match<Domain.Entities.JobLease>(l =>
            l.Holder == b.Holder && l.AcquiredAtUtc == clock.Now && l.ExpiresAtUtc == clock.Now + BackgroundJobLease.Expiry);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AHolderWhoseProcessIsAlive_OrOnAnotherMachine_IsWaitedOut(string provider)
    {
        // A live pid may be the holder or an unrelated process given its id: either way it isn't taken over early.
        using (var db = new TestDb(provider: provider))
        {
            var clock = new TestClock(Start);
            await HeldBy(db, $"{Here}/{Environment.ProcessId}/aaaa", Start);
            var (b, pb) = Lease(db, clock, $"{Here}/{Environment.ProcessId}/bbbb");
            await using var _ = pb;
            (await b.HoldAsync()).Should().BeFalse("its process is running");
            clock.Now = Start + BackgroundJobLease.Expiry;
            (await b.HoldAsync()).Should().BeTrue("it lapsed");
        }

        // Another machine's process can't be looked at from here.
        using (var db = new TestDb(provider: provider))
        {
            var clock = new TestClock(Start);
            await HeldBy(db, $"NOT-{Here}/{DeadPid()}/aaaa", Start);
            var (b, pb) = Lease(db, clock, $"{Here}/{Environment.ProcessId}/bbbb");
            await using var _ = pb;
            (await b.HoldAsync()).Should().BeFalse("its machine isn't this one");
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("odd", false)]
    [InlineData("WEB01/notapid/x", false)]
    [InlineData("WEB01/-4/x", false)]
    public void OnlyAWellFormedHolderOnThisMachine_IsLookedAt(string? holder, bool gone) =>
        BackgroundJobLease.IsGoneFromThisMachine(holder).Should().Be(gone);

    [Fact]
    public void AProcessIdThatIsntRunning_IsGone_AndThisOneIsnt()
    {
        BackgroundJobLease.IsGoneFromThisMachine($"{Here}/{DeadPid()}/x").Should().BeTrue();
        BackgroundJobLease.IsGoneFromThisMachine($"{Here.ToLowerInvariant()}/{DeadPid()}/x").Should().BeTrue("machine names are compared ignoring case");
        BackgroundJobLease.IsGoneFromThisMachine($"{Here}/{Environment.ProcessId}/x").Should().BeFalse();
        BackgroundJobLease.IsGoneFromThisMachine($"NOT-{Here}/{DeadPid()}/x").Should().BeFalse();
    }
}

/// <summary>Each process's heartbeat: every process, the holder too, sees whether another is running on the database.</summary>
public class SecondProcessTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private static (BackgroundJobLease Lease, ServiceProvider Provider) Lease(IAppDbContextFactory dbs, TimeProvider clock, string holder) =>
        JobLeaseTests.Lease(dbs, clock, holder);

    private static async Task<List<string>> Heartbeats(TestDb db)
    {
        await using var ctx = db.Context();
        return await ctx.JobProcesses.OrderBy(p => p.Holder).Select(p => p.Holder).ToListAsync();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheHolderAndTheStandbySeeEachOther_UntilOneStops(string provider)
    {
        using var db = new TestDb(provider: provider);
        var clock = new TestClock(Start);
        var (a, pa) = Lease(db, clock, "WEB01/100/aaaa");
        var (b, pb) = Lease(db, clock, "WEB02/200/bbbb");
        await using var _ = pa;
        await using var __ = pb;
        var changes = 0;
        a.AlarmChanged += () => changes++;

        (await a.HoldAsync()).Should().BeTrue();
        a.Status.Should().Match<LeaseStatus>(s => s.Self == "WEB01/100/aaaa" && s.Others.Count == 0 && s.Alarm == null);
        (await b.HoldAsync()).Should().BeFalse();
        b.Status.Others.Should().Equal("WEB01/100/aaaa");
        b.Status.Alarm.Should().Contain("WEB01, process 100").And.Contain("WEB02, process 200");

        // The holder sees the standby at its next check.
        clock.Now = Start + BackgroundJobLease.RenewAfter;
        (await a.HoldAsync()).Should().BeTrue();
        a.Status.Others.Should().Equal("WEB02/200/bbbb");
        a.Status.Alarm.Should().Contain("WEB01, process 100").And.Contain("WEB02, process 200");
        changes.Should().Be(1);
        (await Heartbeats(db)).Should().Equal("WEB01/100/aaaa", "WEB02/200/bbbb");

        // The standby stops (recycled, or killed): its heartbeat lapses as a lease does, and its row is cleared.
        clock.Now = Start + BackgroundJobLease.Expiry;
        (await a.HoldAsync()).Should().BeTrue();
        a.Status.Others.Should().Equal(["WEB02/200/bbbb"], "it last beat at the start, an expiry ago");
        clock.Now = Start + BackgroundJobLease.Expiry + BackgroundJobLease.RenewAfter;
        (await a.HoldAsync()).Should().BeTrue();
        a.Status.Alarm.Should().BeNull();
        changes.Should().Be(2);
        (await Heartbeats(db)).Should().Equal("WEB01/100/aaaa");

        // Let go at shutdown, its own heartbeat goes too: the next process isn't alarmed by it.
        await a.ReleaseAsync();
        (await Heartbeats(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task AHeartbeatThatFails_NeverStopsTheLease_KeepsTheOthersLastSeen_AndWarnsOnceUntilItRecovers()
    {
        using var db = new TestDb();
        var clock = new TestClock(Start);
        await using var provider = new ServiceCollection().AddSingleton<IAppDbContextFactory>(db).BuildServiceProvider();
        var log = new ListLogger<BackgroundJobLease>();
        var a = new BackgroundJobLease(provider.GetRequiredService<IServiceScopeFactory>(), clock, log, "WEB01/100/aaaa");
        var (b, pb) = Lease(db, clock, "WEB02/200/bbbb");
        await using var _ = pb;
        (await a.HoldAsync()).Should().BeTrue();
        (await b.HoldAsync()).Should().BeFalse();
        clock.Now = Start + BackgroundJobLease.RenewAfter;
        (await a.HoldAsync()).Should().BeTrue();
        a.Status.Others.Should().Equal("WEB02/200/bbbb");

        async Task Sql(string sql)
        {
            await using var ctx = db.Context();
            await ctx.Database.ExecuteSqlRawAsync(sql);
        }

        // The heartbeat's table is gone (a deadlock or a missing table would do the same): the lease is renewed all the same.
        await Sql("ALTER TABLE JobProcesses RENAME TO JobProcessesAside");
        for (var pass = 2; pass <= 4; pass++)
        {
            clock.Now = Start + pass * BackgroundJobLease.RenewAfter;
            (await a.HoldAsync()).Should().BeTrue("a heartbeat that fails never stops the background jobs");
            a.Status.Should().Match<LeaseStatus>(s => s.State == LeaseState.Held && s.ExpiresAtUtc == clock.Now + BackgroundJobLease.Expiry);
            a.Status.Others.Should().Equal(["WEB02/200/bbbb"], "the others last seen are kept");
        }
        log.Warnings.Should().ContainSingle("once until it recovers, not every pass").Which.Should().Contain("heartbeat");

        // Back, and failing again later: warned again.
        await Sql("ALTER TABLE JobProcessesAside RENAME TO JobProcesses");
        clock.Now = Start + 5 * BackgroundJobLease.RenewAfter;
        (await a.HoldAsync()).Should().BeTrue();
        await Sql("ALTER TABLE JobProcesses RENAME TO JobProcessesAside");
        clock.Now = Start + 6 * BackgroundJobLease.RenewAfter;
        (await a.HoldAsync()).Should().BeTrue();
        log.Warnings.Should().HaveCount(2);
        await Sql("ALTER TABLE JobProcessesAside RENAME TO JobProcesses");
    }

    [Fact]
    public async Task AHeartbeatFromADeadProcessOnThisMachine_IsntAnAlarm()
    {
        using var db = new TestDb();
        var clock = new TestClock(Start);
        await using (var ctx = db.Context())
        {
            ctx.JobProcesses.Add(new Domain.Entities.JobProcess { Holder = $"{Environment.MachineName}/{DeadHolderTakeoverTests.DeadPid()}/dead", StartedAtUtc = Start, LastSeenAtUtc = Start });
            await ctx.SaveChangesAsync();
        }
        var (a, pa) = Lease(db, clock, $"{Environment.MachineName}/{Environment.ProcessId}/aaaa");
        await using var _ = pa;
        (await a.HoldAsync()).Should().BeTrue();
        a.Status.Alarm.Should().BeNull("that process is gone");
    }

    [Fact]
    public async Task AStandbyWithNoAlarm_StandsByQuietly_AndAReleaseByAStandbyClearsItsHeartbeat()
    {
        using var db = new TestDb();
        var clock = new TestClock(Start);
        var (a, pa) = Lease(db, clock, "WEB01/100/aaaa");
        var (b, pb) = Lease(db, clock, "WEB02/200/bbbb");
        await using var _ = pa;
        await using var __ = pb;
        (await a.HoldAsync()).Should().BeTrue();
        (await b.HoldAsync()).Should().BeFalse();
        await b.ReleaseAsync();
        (await Heartbeats(db)).Should().Equal("WEB01/100/aaaa");
    }
}

/// <summary>A lease that sees a second process on the database.</summary>
public sealed class AlarmLease : IJobLease
{
    public Task<bool> HoldAsync(CancellationToken ct = default) => Task.FromResult(true);
    public LeaseStatus Status { get; } = new(LeaseState.Held, "WEB01/100/aaaa", null, null, null, null) { Self = "WEB01/100/aaaa", Others = ["WEB02/200/bbbb"] };
    public event Action? AlarmChanged { add { } remove { } }
}

/// <summary>The app on a process that sees another on its database.</summary>
public sealed class SecondProcessApp : TideApp
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder, services =>
        {
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<IJobLease>(services);
            services.AddSingleton<IJobLease, AlarmLease>();
        });
    }
}

public class SecondProcessAlarmTests(SecondProcessApp app) : IClassFixture<SecondProcessApp>
{
    [Fact]
    public async Task HealthJobs_IsDegraded()
    {
        var res = await app.CreateClient().GetAsync("/health/jobs");
        (await res.Content.ReadAsStringAsync()).Should().Be("Degraded");
        var check = new JobsHealthCheck(new JobMonitor(TimeProvider.System), new AlarmLease(), TimeProvider.System);
        var result = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        result.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded);
        result.Description.Should().Contain("WEB01, process 100").And.Contain("WEB02, process 200");
    }

    [Fact]
    public async Task AnAdministrator_SeesTheBanner_NamingBothProcesses()
    {
        await using var page = await ComponentHarness.RenderAsync<Tide.Web.Components.Shared.SecondProcessBanner>(app, "/");
        var html = await page.Html();
        html.Should().Contain("WEB01, process 100").And.Contain("WEB02, process 200").And.Contain("admin/system");
    }

    [Fact]
    public async Task SomeoneWhoIsntAnAdministrator_DoesNot()
    {
        await using var page = await ComponentHarness.RenderAsync<Tide.Web.Components.Shared.SecondProcessBanner>(app, "/",
            @as: new ComponentHarness.SignIn("dev:sam", "Sam", "Analyst"));
        (await page.Html()).Should().NotContain("process 200");
    }

    [Fact]
    public async Task Diagnostics_HasALine_NamingBothProcesses()
    {
        await using var page = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.Admin.DiagnosticsAdmin>(app, "/admin/system");
        var html = await page.HtmlOnce("Second process");
        html.Should().Contain("Second process").And.Contain("WEB02, process 200");
    }
}

public class SecondProcessCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void JobChecks_AddAProblemLine_ForASecondProcess_OnTheHolderOrAStandby()
    {
        var jobs = new JobMonitor(new TestClock(Now));
        foreach (var lease in new[]
                 {
                     new LeaseStatus(LeaseState.Held, "WEB01/100/aaaa", Now.AddMinutes(3), Now, Now, null) { Self = "WEB01/100/aaaa", Others = ["WEB02/200/bbbb"] },
                     new LeaseStatus(LeaseState.StandingBy, "WEB02/200/bbbb", Now.AddMinutes(3), null, Now, null) { Self = "WEB01/100/aaaa", Others = ["WEB02/200/bbbb"] },
                 })
        {
            var checks = SystemDiagnostics.JobChecks(jobs.States, lease, JobHealth.Evaluate(jobs.States, lease, Now)).ToList();
            checks.Should().ContainSingle(c => c.Label == "Second process").Which.Should().Match<HealthItem>(c =>
                c.Level == CheckLevel.Problem && c.Detail.Contains("WEB01, process 100") && c.Detail.Contains("WEB02, process 200"));
        }
        var alone = new LeaseStatus(LeaseState.Held, "WEB01/100/aaaa", Now.AddMinutes(3), Now, Now, null) { Self = "WEB01/100/aaaa" };
        SystemDiagnostics.JobChecks(jobs.States, alone, JobHealth.Evaluate(jobs.States, alone, Now)).Should().NotContain(c => c.Label == "Second process");
    }
}

/// <summary>A logger torn down with its host: every line throws.</summary>
public sealed class GoneLogger<T>(Exception thrown) : Microsoft.Extensions.Logging.ILogger<T>
{
    public bool Throwing { get; set; } = true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (Throwing) throw thrown;
    }
}

/// <summary>A host torn down around the lease's release at shutdown (as WebApplicationFactory's tests do): the release
/// never throws, and the lease is left to lapse.</summary>
public class ReleaseAtShutdownTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Thrown => ["disposed", "aggregate"];

    private static Exception Make(string kind) => kind == "disposed"
        ? new ObjectDisposedException("EventLogInternal")
        : new AggregateException(new ObjectDisposedException("ConsoleLoggerProvider"));

    [Theory]
    [MemberData(nameof(Thrown))]
    public async Task ReleasingAfterTheContainerAndLoggersAreDisposed_DoesntThrow(string kind)
    {
        using var db = new TestDb();
        var logger = new GoneLogger<BackgroundJobLease>(Make(kind)) { Throwing = false };
        var services = new ServiceCollection().AddSingleton<IAppDbContextFactory>(db);
        services.AddSingleton(sp => new BackgroundJobLease(sp.GetRequiredService<IServiceScopeFactory>(), new TestClock(Start), logger, "WEB01/100/aaaa"));
        var provider = services.BuildServiceProvider();
        var lease = provider.GetRequiredService<BackgroundJobLease>();
        (await lease.HoldAsync()).Should().BeTrue();

        // The container goes first, with the lease in it, and the loggers with it.
        await provider.DisposeAsync();
        logger.Throwing = true;

        await FluentActions.Awaiting(() => lease.ReleaseAsync()).Should().NotThrowAsync();
        await FluentActions.Awaiting(() => lease.ReleaseAsync()).Should().NotThrowAsync("a second release is as safe");
    }

    [Fact]
    public async Task ACancelledRelease_LeavesTheGateFree()
    {
        using var db = new TestDb();
        var provider = new ServiceCollection().AddSingleton<IAppDbContextFactory>(db).BuildServiceProvider();
        await using var _ = provider;
        var lease = new BackgroundJobLease(provider.GetRequiredService<IServiceScopeFactory>(), new TestClock(Start), NullLogger<BackgroundJobLease>.Instance, "WEB01/100/aaaa");
        (await lease.HoldAsync()).Should().BeTrue();

        await FluentActions.Awaiting(() => lease.ReleaseAsync(new CancellationToken(canceled: true))).Should().ThrowAsync<OperationCanceledException>();
        (await lease.HoldAsync()).Should().BeTrue("a cancelled wait took nothing, so it released nothing");
        await lease.ReleaseAsync();
        await using var ctx = db.Context();
        (await ctx.JobLeases.SingleAsync()).ExpiresAtUtc.Should().Be(Start, "let go at shutdown");
    }
}
