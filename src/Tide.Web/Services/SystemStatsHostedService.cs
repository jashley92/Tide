using Tide.Application.Operations;

namespace Tide.Web.Services;

/// <summary>
/// The statistics job: once a day it takes the snapshot of how big TIDE is (<see cref="SystemStats.TakeAsync"/>), which
/// Diagnostics' Data &amp; retention panel shows, and every <see cref="Poll"/> it reads when the newest full backup finished
/// (for <c>/health/jobs</c>) and warns, once an hour while it lasts, of a disk under 10% free (event id 7203) or a stale
/// backup when <c>Backup:StaleAfterHours</c> is set (7205). Only the process holding the job lease takes the snapshot.
/// </summary>
public sealed class SystemStatsHostedService(IServiceScopeFactory scopes, JobMonitor jobs, IJobLease lease, BackupWatch backups,
    TimeProvider time, ILogger<SystemStatsHostedService> log) : BackgroundService
{
    /// <summary>How often it looks.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromMinutes(5);

    /// <summary>How often a lasting problem is logged again.</summary>
    public static readonly TimeSpan WarnEvery = TimeSpan.FromHours(1);

    private DateTimeOffset _diskWarned = DateTimeOffset.MinValue, _backupWarned = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PassAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            try { await Task.Delay(Poll, time, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One look; never throws but to cancel.</summary>
    internal async Task PassAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var stats = scope.ServiceProvider.GetRequiredService<SystemStats>();
        var now = time.GetUtcNow();
        try
        {
            backups.Set(await stats.LastFullBackupAsync(ct));
            if (stats.DiskProblem() is { } disk && now - _diskWarned >= WarnEvery)
            {
                _diskWarned = now;
                log.LogWarning(new EventId(SystemStats.DiskLowEventId, "DiskLow"), "{Problem} Free some space or move the data root.", disk);
            }
            if (stats.BackupProblem(backups.LastFull) is { } stale && now - _backupWarned >= WarnEvery)
            {
                _backupWarned = now;
                log.LogWarning(new EventId(SystemStats.BackupStaleEventId, "BackupStale"), "{Problem} Check the backup task.", stale);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { log.LogDebug(ex, "Couldn't read the newest backup; it's tried again shortly."); }

        await jobs.PassAsync(JobMonitor.Statistics, lease, Poll, isOn: null,
            async () => await stats.TakeAsync(ct) ? "today's snapshot taken" : null, log, "Taking the daily statistics failed; it's tried again shortly.", ct);
    }
}
