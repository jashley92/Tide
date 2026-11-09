using System.Net;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Operations;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages.Admin;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>A lease this process always holds, for a test of a hosted service's own work.</summary>
public sealed class HeldLease : IJobLease
{
    public Task<bool> HoldAsync(CancellationToken ct = default) => Task.FromResult(true);
    public LeaseStatus Status { get; } = new(LeaseState.Held, "TEST/1/held", null, null, null, null);
}

/// <summary>A lease another process holds.</summary>
public sealed class StandbyLease : IJobLease
{
    public int Asked { get; private set; }

    public Task<bool> HoldAsync(CancellationToken ct = default)
    {
        Asked++;
        return Task.FromResult(false);
    }

    public LeaseStatus Status { get; } = new(LeaseState.StandingBy, "OTHER/2/held", null, null, null, null);
}

/// <summary>The test database, made to fail on demand (the database down).</summary>
public sealed class FlakyDbs(IAppDbContextFactory inner) : IAppDbContextFactory
{
    public bool Down { get; set; }

    public Task<IAppDbContext> CreateAsync(CancellationToken ct = default) =>
        Down ? throw new InvalidOperationException("A network-related error occurred; Password=hunter2") : inner.CreateAsync(ct);
}

public class JobLeaseTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private static (BackgroundJobLease Lease, ServiceProvider Provider) Lease(IAppDbContextFactory dbs, TimeProvider clock, string holder)
    {
        var provider = new ServiceCollection().AddSingleton(dbs).BuildServiceProvider();
        return (new BackgroundJobLease(provider.GetRequiredService<IServiceScopeFactory>(), clock, NullLogger<BackgroundJobLease>.Instance, holder), provider);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheFirstHolderRunsTheJobs_ASecondStandsBy_AndTakesOverOnceItLapses(string provider)
    {
        using var db = new TestDb(provider: provider);
        var clock = new TestClock(Start);
        var (a, pa) = Lease(db, clock, "WEB01/100/aaaa");
        var (b, pb) = Lease(db, clock, "WEB02/200/bbbb");
        await using var _ = pa;
        await using var __ = pb;

        (await a.HoldAsync()).Should().BeTrue("nobody held it: the row is made");
        (await b.HoldAsync()).Should().BeFalse("another live holder has it");
        b.Status.Should().Match<LeaseStatus>(s => s.State == LeaseState.StandingBy && s.HolderName == "WEB01, process 100");
        a.Status.Should().Match<LeaseStatus>(s => s.State == LeaseState.Held && s.HeldSinceUtc == Start);

        // The holder renews it each pass; the other keeps standing by.
        clock.Now = Start.AddMinutes(2);
        (await a.HoldAsync()).Should().BeTrue();
        clock.Now = Start.AddMinutes(4);
        (await b.HoldAsync()).Should().BeFalse("renewed at 2 minutes, it runs to 5");
        await using (var ctx = db.Context())
            (await ctx.JobLeases.SingleAsync()).Should().Match<Domain.Entities.JobLease>(l =>
                l.Holder == "WEB01/100/aaaa" && l.AcquiredAtUtc == Start && l.ExpiresAtUtc == Start.AddMinutes(2) + BackgroundJobLease.Expiry);

        // The holder stops renewing (it died): once the lease lapses the other takes it, and the first stands by.
        clock.Now = Start.AddMinutes(6);
        (await b.HoldAsync()).Should().BeTrue("the lease lapsed at 5 minutes");
        b.Status.HeldSinceUtc.Should().Be(clock.Now);
        (await a.HoldAsync()).Should().BeFalse();
        a.Status.Should().Match<LeaseStatus>(s => s.State == LeaseState.StandingBy && s.Holder == "WEB02/200/bbbb");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ALeaseLetGoAtShutdown_IsTakenAtOnce(string provider)
    {
        using var db = new TestDb(provider: provider);
        var clock = new TestClock(Start);
        var (a, pa) = Lease(db, clock, "WEB01/100/aaaa");
        var (b, pb) = Lease(db, clock, "WEB01/101/bbbb");
        await using var _ = pa;
        await using var __ = pb;
        (await a.HoldAsync()).Should().BeTrue();
        (await b.HoldAsync()).Should().BeFalse();

        await a.ReleaseAsync();
        clock.Now = Start.AddSeconds(10);
        (await b.HoldAsync()).Should().BeFalse("a standby looks again only after RenewAfter");
        clock.Now = Start + BackgroundJobLease.RenewAfter;
        (await b.HoldAsync()).Should().BeTrue("the overlapping process takes over without waiting for the lease to lapse");
    }

    [Fact]
    public async Task WhenTheDatabaseDoesntAnswer_AHolderKeepsGoingUntilNearItsExpiry_ThenStops()
    {
        using var db = new TestDb();
        var dbs = new FlakyDbs(db);
        var clock = new TestClock(Start);
        var (a, pa) = Lease(dbs, clock, "WEB01/100/aaaa");
        await using var _ = pa;
        (await a.HoldAsync()).Should().BeTrue();

        dbs.Down = true;
        clock.Now = Start.AddMinutes(1);
        (await a.HoldAsync()).Should().BeTrue("nobody can take it before the expiry it wrote");
        a.Status.Problem.Should().Contain("network-related").And.NotContain("hunter2");
        clock.Now = Start + BackgroundJobLease.Expiry - BackgroundJobLease.Margin;
        (await a.HoldAsync()).Should().BeFalse("too near the expiry: another may take it");
        a.Status.State.Should().Be(LeaseState.Unreachable);

        // A process that never held it doesn't start running jobs on a database it can't reach.
        var (b, pb) = Lease(dbs, clock, "WEB02/200/bbbb");
        await using var __ = pb;
        (await b.HoldAsync()).Should().BeFalse();
        b.Status.State.Should().Be(LeaseState.Unreachable);
    }

    [Fact]
    public async Task AHostedServiceStandingBy_SkipsItsPass()
    {
        // Nothing is registered: a pass that ran would fail to find the reminder service.
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var jobs = new JobMonitor(TimeProvider.System);
        var lease = new StandbyLease();
        var hosted = new ReminderHostedService(provider.GetRequiredService<IServiceScopeFactory>(), jobs, lease, TimeProvider.System,
            NullLogger<ReminderHostedService>.Instance);

        await hosted.PassAsync(CancellationToken.None);

        lease.Asked.Should().Be(1);
        jobs.Get(JobMonitor.Reminders).Should().Match<JobState>(s => s.LastStartedUtc == null && s.LastSucceeded == null);
        jobs.TakeFinished().Should().BeEmpty();
    }
}

public class JobRunLogTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Runs_AreWritten_AndPutBackAfterARestart(string provider)
    {
        using var db = new TestDb(provider: provider);
        var clock = new TestClock(Start);
        var jobs = new JobMonitor(clock);
        var log = new JobRunLog(jobs, clock, NullLogger<JobRunLog>.Instance);
        await jobs.RunAsync(JobMonitor.Reminders, () => Task.FromResult<string?>("2 emails"));
        clock.Now = Start.AddMinutes(5);
        jobs.Failed(JobMonitor.Reminders, "Overdue: relay down; Password=hunter2");
        clock.Now = Start.AddMinutes(10);
        jobs.Failed(JobMonitor.Reminders, "Overdue: relay down");
        await jobs.RunAsync(JobMonitor.Webhooks, () => Task.FromResult<string?>(null));

        (await log.FlushAsync(db)).Should().Be(4);
        (await log.FlushAsync(db)).Should().Be(0, "each run is written once");
        await using (var ctx = db.Context())
        {
            var rows = await ctx.JobRuns.AsNoTracking().ToListAsync();
            rows.Should().HaveCount(4).And.OnlyContain(r => r.Machine == Environment.MachineName);
            rows.Should().NotContain(r => r.Error != null && r.Error.Contains("hunter2"));
        }

        // A restart: a new monitor knows each job's latest run, when it last went well, and the failures since.
        var restarted = new JobMonitor(clock);
        await new JobRunLog(restarted, clock, NullLogger<JobRunLog>.Instance).RestoreAsync(db);
        restarted.Get(JobMonitor.Reminders).Should().Match<JobState>(s =>
            s.LastSucceeded == false && s.LastStartedUtc == Start.AddMinutes(10) && s.LastError == "Overdue: relay down"
            && s.LastSuccessUtc == Start && s.FailuresInARow == 2);
        restarted.Get(JobMonitor.Webhooks).LastSucceeded.Should().BeTrue();
        restarted.Get(JobMonitor.FeedPoll).LastStartedUtc.Should().BeNull("it never ran");

        var history = await JobRunLog.HistoryAsync(db);
        history.For(JobMonitor.Reminders).Should().Be(new JobHistory("reminders", 3, 2));
        history.RecentFailures.Select(f => f.FinishedUtc).Should().Equal(Start.AddMinutes(10), Start.AddMinutes(5));
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Runs_OlderThanThirtyDays_ArePruned(string provider)
    {
        using var db = new TestDb(provider: provider);
        var clock = new TestClock(Start.AddDays(-31));
        var jobs = new JobMonitor(clock);
        var log = new JobRunLog(jobs, clock, NullLogger<JobRunLog>.Instance);
        jobs.Succeeded(JobMonitor.Prune, clock.Now, "old");
        clock.Now = Start.AddDays(-29);
        jobs.Succeeded(JobMonitor.Prune, clock.Now, "kept");
        await log.FlushAsync(db);

        clock.Now = Start;
        (await log.PruneAsync(db)).Should().Be(1);
        await using var ctx = db.Context();
        (await ctx.JobRuns.Select(r => r.Detail).ToListAsync()).Should().Equal("kept");
    }

    [Fact]
    public async Task RunsThatCantBeWritten_AreKept_AndWrittenOnceTheDatabaseAnswers()
    {
        using var db = new TestDb();
        var dbs = new FlakyDbs(db) { Down = true };
        var jobs = new JobMonitor(new TestClock(Start));
        var log = new JobRunLog(jobs, TimeProvider.System, NullLogger<JobRunLog>.Instance);
        jobs.Succeeded(JobMonitor.Webhooks, Start, "1 sent");

        (await log.FlushAsync(dbs)).Should().Be(0);
        dbs.Down = false;
        (await log.FlushAsync(dbs)).Should().Be(1);
    }
}

public class JobHealthEndpointTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task HealthJobs_AnswersAStatusWordOnly_WithoutSignIn()
    {
        var res = await app.CreateClient().GetAsync("/health/jobs");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().BeOneOf("Healthy", "Degraded");
    }

    [Fact]
    public async Task Diagnostics_NamesTheLeaseHolder_AndReadsTheJobHistory()
    {
        // The app's own upkeep takes the lease within its first pass.
        var lease = app.Services.GetRequiredService<BackgroundJobLease>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (lease.Status.State != LeaseState.Held && DateTime.UtcNow < deadline) await Task.Delay(50);
        lease.Status.State.Should().Be(LeaseState.Held);

        var jobs = app.Services.GetRequiredService<JobMonitor>();
        jobs.Failed(JobMonitor.UpdateCheck, "the release feed did not answer");
        using var scope = app.Services.CreateScope();
        await app.Services.GetRequiredService<JobRunLog>().FlushAsync(scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>());
        Audit.ServiceScopes.SignIn(scope, Permission.Administer);
        var report = await scope.ServiceProvider.GetRequiredService<SystemDiagnostics>().RunAsync();

        report.Lease.State.Should().Be(LeaseState.Held);
        report.Checks.Should().Contain(c => c.Label == "Job lease" && c.Level == CheckLevel.Ok && c.Detail.Contains(Environment.MachineName));
        report.JobHistory.RecentFailures.Should().Contain(f => f.Job == "updates" && f.Error == "the release feed did not answer");

        await using var page = await ComponentHarness.RenderAsync<DiagnosticsAdmin>(app, "/admin/system");
        var html = await page.HtmlOnce("Recent failures");
        html.Should().Contain("runs them").And.Contain("the release feed did not answer");
    }
}

public class JobChecksTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private static List<HealthItem> Checks(JobMonitor jobs, LeaseStatus lease) =>
        [.. SystemDiagnostics.JobChecks(jobs.States, lease, JobHealth.Evaluate(jobs.States, lease, Now))];

    [Fact]
    public void StandingBy_SaysWhoRunsThem_AndJudgesNone()
    {
        var jobs = new JobMonitor(new TestClock(Now.AddDays(-2)));
        jobs.Scheduled(JobMonitor.Reminders, true);
        var checks = Checks(jobs, new LeaseStatus(LeaseState.StandingBy, "WEB02/7/x", Now.AddMinutes(2), null, Now, null));
        checks.Select(c => (c.Label, c.Level)).Should().Equal(("Job lease", CheckLevel.Info), ("Background jobs", CheckLevel.Info));
        checks[0].Detail.Should().Contain("Standing by: WEB02, process 7 runs the background jobs");
    }

    [Fact]
    public void AnOverdueOrHungJob_IsAProblem_AndOneThatKeepsFailing_NeedsAttention()
    {
        var held = new LeaseStatus(LeaseState.Held, "WEB01/1/x", Now.AddMinutes(3), Now.AddDays(-5), Now, null);
        var clock = new TestClock(Now.AddDays(-2));
        var jobs = new JobMonitor(clock);
        jobs.Scheduled(JobMonitor.Reminders, true);
        var checks = Checks(jobs, held);
        checks[0].Should().Match<HealthItem>(c => c.Label == "Job lease" && c.Level == CheckLevel.Ok);
        checks[1].Should().Match<HealthItem>(c => c.Level == CheckLevel.Problem && c.Detail.Contains("Reminder and digest emails: hasn't run well"));

        clock.Now = Now;
        jobs.Succeeded(JobMonitor.Reminders, Now, null);
        for (var i = 0; i < 3; i++) jobs.Failed(JobMonitor.Reminders, "relay down");
        Checks(jobs, held)[1].Should().Match<HealthItem>(c => c.Level == CheckLevel.Attention && c.Detail.Contains("the last 3 runs failed"));

        jobs.Succeeded(JobMonitor.Reminders, Now, null);
        Checks(jobs, held)[1].Should().Match<HealthItem>(c => c.Level == CheckLevel.Ok);
        Checks(jobs, new LeaseStatus(LeaseState.Unreachable, null, null, null, Now, "timeout"))[0].Level.Should().Be(CheckLevel.Problem);
    }
}

/// <summary>The app on a process standing by: another holds the job lease.</summary>
public sealed class StandbyApp : TideApp
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder, services =>
        {
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<IJobLease>(services);
            services.AddSingleton<IJobLease, StandbyLease>();
        });
    }
}

// A job run by hand on a standby ran beside the holder's scheduled run: the reminder scans' lock is the process's own,
// so the same emails could go from both. Each Run now asks for the lease, as the scheduled passes do.
public class RunNowOnAStandbyTests(StandbyApp app) : IClassFixture<StandbyApp>
{
    private const string Refusal = "This server is standing by: OTHER, process 2 runs the background jobs. Run it there.";

    [Fact]
    public async Task EveryRunNow_IsRefused_NamingTheHolder()
    {
        using var scope = app.Services.CreateScope();
        Audit.ServiceScopes.SignIn(scope, Permission.Administer, Permission.WriteArticles);
        var sp = scope.ServiceProvider;

        foreach (var kind in new[] { ReminderKind.Overdue, ReminderKind.Escalation })
            (await FluentActions.Awaiting(() => sp.GetRequiredService<Tide.Application.Actions.ReminderService>().RunNowAsync(kind))
                .Should().ThrowAsync<StandingByException>()).WithMessage(Refusal);
        (await FluentActions.Awaiting(() => sp.GetRequiredService<Tide.Application.Intake.IntakeService>().PollNowAsync(Guid.NewGuid()))
            .Should().ThrowAsync<StandingByException>()).WithMessage(Refusal);
        (await FluentActions.Awaiting(() => sp.GetRequiredService<Tide.Application.Intake.IntakeService>().PullNowAsync(Guid.NewGuid()))
            .Should().ThrowAsync<StandingByException>()).WithMessage(Refusal);
        (await FluentActions.Awaiting(() => sp.GetRequiredService<Tide.Application.Intake.CveService>().EnrichNowAsync())
            .Should().ThrowAsync<StandingByException>()).WithMessage(Refusal);

        var updates = app.Services.GetRequiredService<UpdateCheckService>();
        updates.Enabled = true;
        try
        {
            (await FluentActions.Awaiting(() => updates.CheckAsync(manual: true)).Should().ThrowAsync<StandingByException>()).WithMessage(Refusal);
        }
        finally { updates.Enabled = false; }
    }

    [Fact]
    public void ThePagesToast_ShowsTheRefusalAsWritten()
    {
        UserFacingError.For(new StandingByException("OTHER, process 2")).Should().Be(Refusal);
        UserFacingError.For(new StandingByException(null)).Should().StartWith("This server is standing by: another server runs");
    }

    [Fact]
    public async Task Diagnostics_ShowsTheJobsAsStandingBy_NotAsNotRunYet()
    {
        await using var page = await ComponentHarness.RenderAsync<DiagnosticsAdmin>(app, "/admin/system");
        var html = await page.HtmlOnce("Standing by");
        html.Should().Contain("Standing by").And.NotContain("Not run yet");
    }
}

public class RunNowOnTheHolderTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task RunNow_StillRuns_WhereTheLeaseIsHeld()
    {
        using var scope = app.Services.CreateScope();
        Audit.ServiceScopes.SignIn(scope, Permission.Administer);
        var sent = await scope.ServiceProvider.GetRequiredService<Tide.Application.Actions.ReminderService>().RunNowAsync(ReminderKind.Overdue);
        sent.Should().BeGreaterThanOrEqualTo(0);
        app.Services.GetRequiredService<BackgroundJobLease>().Status.State.Should().Be(LeaseState.Held);
    }
}
