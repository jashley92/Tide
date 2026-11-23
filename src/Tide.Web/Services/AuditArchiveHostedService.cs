using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Files;
using Tide.Application.History;
using Tide.Application.Operations;

namespace Tide.Web.Services;

/// <summary>
/// Runs the audit log's archive-then-prune (<see cref="AuditArchive"/>) once a day while this process holds the job lease,
/// only when <c>Audit:RetentionDays</c> is set (off by default). Due by its last run (kept in the job history), not in the
/// first hour after start-up. Each month archived is the run's detail on Diagnostics and an Information line in the log.
/// </summary>
public sealed class AuditArchiveHostedService(IServiceScopeFactory scopes, IOptionsMonitor<AuditOptions> audit, IOptionsMonitor<FileStoreOptions> files,
    IHostEnvironment env, JobMonitor jobs, IJobLease lease, TimeProvider time, ILogger<AuditArchiveHostedService> log) : BackgroundService
{
    public static readonly TimeSpan FirstAfter = TimeSpan.FromHours(1);
    public static readonly TimeSpan Poll = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            var last = jobs.Get(JobMonitor.AuditArchive).LastStartedUtc;
            if (now - started >= FirstAfter && (last is null || now - last >= JobMonitor.AuditArchive.Interval))
                await jobs.PassAsync(JobMonitor.AuditArchive, lease, JobMonitor.AuditArchive.Interval,
                    () => Task.FromResult(AuditArchive.Days(audit.CurrentValue) is not null), () => RunAsync(stoppingToken), log,
                    "Archiving the audit log failed; nothing was removed that wasn't archived, and it's tried again shortly.", stoppingToken);
            try { await Task.Delay(Poll, time, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task<string?> RunAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync(ct);
        var options = audit.CurrentValue;
        var folder = AuditArchive.Folder(options, Path.GetFullPath(files.CurrentValue.RootPath, env.ContentRootPath));
        var months = await AuditArchive.RunAsync(db, options, folder, time.GetUtcNow(), ct);
        foreach (var m in months) log.LogInformation("Archived {Rows} audit log rows from {Month:yyyy-MM} to {File} and removed them from the database.", m.Rows, m.FromUtc, m.File);
        return months.Count == 0 ? "nothing older than the retention" : $"{months.Sum(m => m.Rows):N0} rows archived ({string.Join(", ", months.Select(m => m.FromUtc.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)))})";
    }
}
