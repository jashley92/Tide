using Tide.Application.Abstractions;
using Tide.Application.Files;
using Tide.Application.Operations;

namespace Tide.Web.Services;

/// <summary>
/// Runs the weekly check of stored files (<see cref="FileSweep"/>) while this process holds the job lease. It looks every
/// <see cref="Poll"/> and runs when the last run (put back from the job history at start-up) is a week old, but not in the
/// first <see cref="FirstAfter"/> after start-up; so the week carries across restarts and lease handovers. It only
/// reports: the run's detail on Diagnostics, and a Warning (the job's event id, 7112) when anything is missing, has no
/// row or doesn't match its hash. Nothing is ever deleted or changed.
/// </summary>
public sealed class FileSweepHostedService(IServiceScopeFactory scopes, IFileStore store, JobMonitor jobs, IJobLease lease,
    TimeProvider time, ILogger<FileSweepHostedService> log) : BackgroundService
{
    /// <summary>How long after start-up the first check may run: out of the way of a restart's busy minutes.</summary>
    public static readonly TimeSpan FirstAfter = TimeSpan.FromHours(1);

    /// <summary>How often it looks whether a check is due.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            var last = jobs.Get(JobMonitor.FileSweep).LastStartedUtc;
            if (now - started >= FirstAfter && (last is null || now - last >= JobMonitor.FileSweep.Interval))
                await jobs.PassAsync(JobMonitor.FileSweep, lease, JobMonitor.FileSweep.Interval, isOn: null, () => RunAsync(stoppingToken), log,
                    "Checking the stored files failed; it's tried again shortly.", stoppingToken);
            try { await Task.Delay(Poll, time, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task<string?> RunAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync(ct);
        var result = await FileSweep.RunAsync(db, store, time.GetUtcNow(), ct: ct);
        var summary = result.Summary();
        if (!result.Clean)
            log.LogWarning(new EventId(JobMonitor.FileSweep.EventId, "FilesNeedALook"), "The weekly check of stored files found problems: {Summary}. Nothing was changed.", summary);
        return summary;
    }
}
