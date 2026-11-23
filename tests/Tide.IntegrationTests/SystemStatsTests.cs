using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Files;
using Tide.Application.Operations;
using Tide.Domain.Entities;
using Tide.Infrastructure.Persistence;
using Tide.TestSupport;

namespace Tide.IntegrationTests;

/// <summary>
/// The daily snapshot of how big TIDE is (<see cref="SystemStats"/>), the Data &amp; retention panel's report, and the disk
/// and backup checks <c>/health/jobs</c> makes: a stale backup only when <c>Backup:StaleAfterHours</c> is set.
/// </summary>
public class SystemStatsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tide-stats-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private SystemStats Stats(TestDb db, int? staleAfterHours = null, TimeProvider? time = null) =>
        new(db, new DataUsage(db), db.Store, new StaticOptionsMonitor<FileStoreOptions>(new FileStoreOptions { RootPath = _root }),
            new StaticOptionsMonitor<BackupOptions>(new BackupOptions { StaleAfterHours = staleAfterHours }), db.Clock, time ?? TimeProvider.System);

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task A_snapshot_is_taken_once_a_day_with_each_tables_rows_and_the_files_by_folder(string provider)
    {
        using var db = new TestDb(provider: provider);
        Directory.CreateDirectory(Path.Combine(_root, "screenshots"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "screenshots", "a.png"), new byte[3000]);
        foreach (var fan in new[] { "3a", "f0" })
        {
            Directory.CreateDirectory(Path.Combine(_root, fan));
            await File.WriteAllBytesAsync(Path.Combine(_root, fan, Guid.NewGuid().ToString("N") + ".bin"), new byte[500]);
        }
        await File.WriteAllBytesAsync(Path.Combine(_root, "loose.txt"), new byte[10]);
        await using (var ctx = db.Context())
        {
            ctx.Articles.AddRange(new Article { Title = "One", AuthorId = "x", AuthorName = "X" }, new Article { Title = "Two", AuthorId = "x", AuthorName = "X" });
            await ctx.SaveChangesAsync();
        }
        var stats = Stats(db);

        (await stats.TakeAsync()).Should().BeTrue();
        (await stats.TakeAsync()).Should().BeFalse("one a day");

        var report = await stats.ReportAsync([("Feed items", "90 days")]);
        report.Latest!.Day.Should().Be(db.Clock.Today);
        report.Latest.DatabaseBytes.Should().BePositive();
        report.Tables.Single(t => t.Name == "Articles").Rows.Should().Be(2);
        report.Tables.Should().NotContain(t => t.Name == "__EFMigrationsHistory");
        if (provider == TestDb.SqlServer) report.Tables.Single(t => t.Name == "Articles").Bytes.Should().BePositive("sa can read the partition stats");
        report.Folders.Should().Equal(new FolderSize("screenshots", 3000), new FolderSize(SystemStats.StoredFiles, 1000), new FolderSize("(top level)", 10));
        report.Latest.FilesBytes.Should().Be(4010);
        (report.Month, report.Quarter).Should().Be((null, null), "nothing older yet");
    }

    [Fact]
    public async Task Growth_is_read_from_the_snapshots_30_and_90_days_before()
    {
        using var db = new TestDb();
        var today = db.Clock.Today;
        await using (var ctx = db.Context())
        {
            foreach (var (days, bytes) in new[] { (100, 1000L), (40, 2000L), (10, 3000L), (0, 5000L) })
                ctx.SystemStats.Add(new SystemStat { Day = today.AddDays(-days), TakenAtUtc = DateTimeOffset.UtcNow, DatabaseBytes = bytes });
            await ctx.SaveChangesAsync();
        }
        var report = await Stats(db).ReportAsync([]);
        (report.Latest!.DatabaseBytes, report.Month!.DatabaseBytes, report.Quarter!.DatabaseBytes).Should().Be((5000L, 2000L, 1000L));
    }

    [Fact]
    public async Task A_stale_backup_is_a_problem_only_when_a_limit_is_set_and_the_newest_success_counts()
    {
        using var db = new TestDb();
        var clock = new TestClock(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
        await using (var ctx = db.Context())
        {
            ctx.BackupRuns.AddRange(
                new BackupRun { Kind = "full", Succeeded = true, FinishedAtUtc = clock.Now.AddHours(-30), Machine = "WEB1" },
                new BackupRun { Kind = "full", Succeeded = false, FinishedAtUtc = clock.Now.AddHours(-2), Machine = "WEB1", Note = "disk full" },
                new BackupRun { Kind = "log", Succeeded = true, FinishedAtUtc = clock.Now.AddMinutes(-10), Machine = "WEB1" });
            await ctx.SaveChangesAsync();
        }
        var last = await Stats(db).LastFullBackupAsync();
        last.Should().Be(clock.Now.AddHours(-30), "a failed set and a log backup aren't a full backup");

        Stats(db, time: clock).BackupProblem(last).Should().BeNull("off unless Backup:StaleAfterHours is set");
        Stats(db, 0, clock).BackupProblem(last).Should().BeNull("0 is off too");
        Stats(db, 48, clock).BackupProblem(last).Should().BeNull();
        Stats(db, 26, clock).BackupProblem(last).Should().Contain("30 hours ago").And.Contain("26");
        Stats(db, 26, clock).BackupProblem(null).Should().Contain("No full backup has been recorded");

        var report = await Stats(db).ReportAsync([]);
        (report.LastFull!.FinishedAtUtc, report.LastLog!.FinishedAtUtc).Should().Be((clock.Now.AddHours(-30), clock.Now.AddMinutes(-10)));
    }

    [Fact]
    public void The_disk_and_the_folders_read_nothing_when_the_root_is_missing()
    {
        SystemStats.Folders(Path.Combine(_root, "never-made")).Should().BeNull();
        var (free, total) = SystemStats.Disk(Path.GetTempPath());
        (free, total).Should().Match<(long?, long?)>(d => d.Item1 > 0 && d.Item2 >= d.Item1);
    }

    [Fact]
    public async Task The_job_takes_the_days_snapshot_reads_the_newest_backup_and_health_says_when_its_stale()
    {
        using var db = new TestDb();
        Directory.CreateDirectory(_root);
        await using (var ctx = db.Context())
        {
            ctx.BackupRuns.Add(new BackupRun { Kind = "full", Succeeded = true, FinishedAtUtc = DateTimeOffset.UtcNow.AddHours(-50), Machine = "WEB1" });
            await ctx.SaveChangesAsync();
        }
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped(services, _ => Stats(db, staleAfterHours: 26));
        await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var (jobs, alerts, log) = (new JobMonitor(TimeProvider.System), new StorageAlerts(), new ListLogger<Tide.Web.Services.SystemStatsHostedService>());
        var job = new Tide.Web.Services.SystemStatsHostedService(
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(provider),
            jobs, new HeldLease(), alerts, TimeProvider.System, log);

        alerts.Problems.Should().BeEmpty("nothing until the job has looked");
        await job.PassAsync(CancellationToken.None);

        alerts.Problems.Should().ContainSingle().Which.Should().Contain("50 hours ago");
        log.Warnings.Should().ContainSingle().Which.Should().Contain("50 hours ago");
        jobs.Get(JobMonitor.Statistics).LastDetail.Should().Be("today's snapshot taken");
        await using (var ctx = db.Context()) (await ctx.SystemStats.CountAsync()).Should().Be(1);

        await job.PassAsync(CancellationToken.None);
        log.Warnings.Should().ContainSingle("a lasting problem is logged once an hour");

        var health = await new Tide.Web.Services.JobsHealthCheck(jobs, new HeldLease(), TimeProvider.System, alerts)
            .CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        health.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded);
        health.Description.Should().Contain("Backup:StaleAfterHours");
        alerts.Set([]);
        (await new Tide.Web.Services.JobsHealthCheck(jobs, new HeldLease(), TimeProvider.System, alerts)
            .CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext())).Status
            .Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, "nothing found wrong");
    }
}
