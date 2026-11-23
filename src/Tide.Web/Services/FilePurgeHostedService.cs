using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Files;
using Tide.Application.Operations;

namespace Tide.Web.Services;

/// <summary>
/// Runs the removed-file byte purge (<see cref="RemovedFilePurge"/>) once a day while this process holds the job lease,
/// only when <c>Files:PurgeRemovedAfterDays</c> is set (off by default). Due by its last run (kept in the job history),
/// not in the first hour after start-up; Diagnostics shows whether it's on and when it next runs.
/// </summary>
public sealed class FilePurgeHostedService(IServiceScopeFactory scopes, IFileStore store, IOptionsMonitor<FileStoreOptions> files, JobMonitor jobs,
    IJobLease lease, TimeProvider time, ILogger<FilePurgeHostedService> log) : BackgroundService
{
    public static readonly TimeSpan FirstAfter = TimeSpan.FromHours(1);
    public static readonly TimeSpan Poll = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            var last = jobs.Get(JobMonitor.FilePurge).LastStartedUtc;
            var on = RemovedFilePurge.Days(files.CurrentValue.PurgeRemovedAfterDays) is not null;
            if (now - started >= FirstAfter && (last is null || now - last >= JobMonitor.FilePurge.Interval))
                await jobs.PassAsync(JobMonitor.FilePurge, lease, JobMonitor.FilePurge.Interval, () => Task.FromResult(on), () => RunAsync(stoppingToken), log,
                    "Purging removed files' bytes failed; it's tried again at its next daily run.", stoppingToken);
            else if (await lease.HoldAsync(stoppingToken))
                jobs.Scheduled(JobMonitor.FilePurge, on, on ? (last is { } l ? l + JobMonitor.FilePurge.Interval : started + FirstAfter) : null);
            try { await Task.Delay(Poll, time, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task<string?> RunAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync(ct);
        var purged = await RemovedFilePurge.RunAsync(db, store, files.CurrentValue.PurgeRemovedAfterDays, time.GetUtcNow(), ct: ct);
        if (purged > 0) log.LogInformation("Purged the bytes of {Count} removed file(s); their records and hashes stay.", purged);
        return purged == 0 ? "nothing removed long enough" : $"{purged:N0} removed file(s) purged";
    }
}
