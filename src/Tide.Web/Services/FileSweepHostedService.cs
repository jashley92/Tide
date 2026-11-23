using Tide.Application.Abstractions;
using Tide.Application.Files;
using Tide.Application.Operations;

namespace Tide.Web.Services;

/// <summary>
/// Runs the weekly check of stored files (<see cref="FileSweep"/>) while this process holds the job lease, an hour after
/// start-up and then every <see cref="JobMonitor.FileSweep"/> interval. It only reports: the run's detail on Diagnostics,
/// and a Warning (the job's event id, 7112) when anything is missing, has no row or doesn't match its hash. Nothing is
/// ever deleted or changed.
/// </summary>
public sealed class FileSweepHostedService(IServiceScopeFactory scopes, IFileStore store, JobMonitor jobs, IJobLease lease,
    TimeProvider time, ILogger<FileSweepHostedService> log) : BackgroundService
{
    /// <summary>How long after start-up the first check runs: out of the way of a restart's busy minutes.</summary>
    public static readonly TimeSpan FirstAfter = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstAfter, time, stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            await jobs.PassAsync(JobMonitor.FileSweep, lease, JobMonitor.FileSweep.Interval, isOn: null, () => RunAsync(stoppingToken), log,
                "Checking the stored files failed; it's tried again next week.", stoppingToken);
            try { await Task.Delay(JobMonitor.FileSweep.Interval, time, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task<string?> RunAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync(ct);
        var result = await FileSweep.RunAsync(db, store, time.GetUtcNow(), ct: ct);
        if (!result.Clean)
            log.LogWarning(new EventId(JobMonitor.FileSweep.EventId, "FilesNeedALook"), "The weekly check of stored files found problems: {Summary}. Nothing was changed.", result.Summary());
        return result.Summary();
    }
}
