using Tide.Application.Abstractions;
using Tide.Application.Operations;

namespace Tide.Web.Services;

/// <summary>
/// A background job that fills a stored column the rows saved before it was kept don't have (a hash, a lookup key),
/// once, after the app is listening: a large database is filled in the background rather than holding up the start (which
/// IIS would cut short and recycle). Each pass fills some batches as one run on Diagnostics, with how many are left, then
/// pauses briefly so the database isn't kept busy. A process that doesn't hold the job lease waits. Once every row is
/// filled it stops for good, as it does at once on a database that never needed it.
/// </summary>
public abstract class OneTimeFillService(IServiceScopeFactory scopes, JobMonitor jobs, IJobLease lease, IHostApplicationLifetime life,
    TimeProvider time, ILogger log) : BackgroundService
{
    /// <summary>The pause between runs while there's more to fill.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

    /// <summary>The wait before trying again: the lease is another process's, or a run failed.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromMinutes(1);

    /// <summary>The job it runs as.</summary>
    protected abstract JobDefinition Job { get; }

    /// <summary>What's filled, for the log ("every stored address has its hash").</summary>
    protected abstract string Filled { get; }

    /// <summary>Whether nothing is left to fill.</summary>
    protected abstract Task<bool> DoneAsync(IAppDbContext db, CancellationToken ct);

    /// <summary>One run's filling: whether it's done, and what the run says on Diagnostics.</summary>
    protected abstract Task<(bool Done, string Result)> FillAsync(IAppDbContext db, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await StartedAsync(stoppingToken)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            bool? done;
            try { done = await PassAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            if (done == true) return;
            try { await Task.Delay(done == false ? Pause : Retry, time, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One run: true when everything is filled (nothing more to do), false when there's more, null when it didn't run
    /// (the lease is another process's, or it failed: recorded on Diagnostics and logged, never thrown but to cancel).
    /// </summary>
    internal async Task<bool?> PassAsync(CancellationToken ct)
    {
        if (!await lease.HoldAsync(ct)) return null;
        var ran = false;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync(ct);
            if (await DoneAsync(db, ct))
            {
                jobs.Scheduled(Job, false);
                return true;
            }
            jobs.Scheduled(Job, true, time.GetUtcNow());
            var done = false;
            string? result = null;
            ran = true;
            await jobs.RunAsync(Job, async () =>
            {
                (done, result) = await FillAsync(db, ct);
                return result;
            });
            jobs.Scheduled(Job, !done, done ? null : time.GetUtcNow() + Pause);
            if (done) log.LogInformation("{Filled} ({Result}).", Filled, result);
            return done;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A run's failure is recorded and logged by the monitor (a Warning, with the job's event id); one before it (the
            // database not answering) is logged here. Either way it's tried again shortly.
            if (!ran) log.LogWarning(ex, "Couldn't read whether {Job} has anything left to fill; it's tried again shortly.", Job.Name);
            return null;
        }
    }

    // Waits until the app is listening; false when it stops first.
    private async Task<bool> StartedAsync(CancellationToken ct)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = life.ApplicationStarted.Register(() => started.TrySetResult());
        using var __ = ct.Register(() => started.TrySetCanceled(ct));
        try
        {
            await started.Task;
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }
}
