using Tide.Application.Abstractions;
using Tide.Application.Intake;
using Tide.Application.Operations;

namespace Tide.Web.Services;

/// <summary>
/// Gives the articles and links saved before their address's hash was kept that hash (<see cref="UrlHashFill"/>), once,
/// after the app is listening: a large database is filled in the background rather than holding up the start (which IIS
/// would cut short and recycle). Each pass fills <see cref="BatchesPerPass"/> batches as one run on Diagnostics, with how
/// many are left, then pauses briefly so the database isn't kept busy. A process that doesn't hold the job lease waits.
/// Once every address has its hash it stops for good, as it does at once on a database that never needed it.
/// </summary>
public sealed class UrlHashFillHostedService(IServiceScopeFactory scopes, JobMonitor jobs, IJobLease lease, IHostApplicationLifetime life,
    TimeProvider time, ILogger<UrlHashFillHostedService> log) : BackgroundService
{
    /// <summary>Batches filled in one run.</summary>
    public const int BatchesPerPass = 20;

    /// <summary>The pause between runs while there's more to fill.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

    /// <summary>The wait before trying again: the lease is another process's, or a run failed.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromMinutes(1);

    private readonly UrlHashProgress _progress = new();

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
    /// One run: true when every address has its hash (nothing more to do), false when there's more, null when it didn't
    /// run (the lease is another process's, or it failed: recorded on Diagnostics and logged, never thrown but to cancel).
    /// </summary>
    internal async Task<bool?> PassAsync(CancellationToken ct)
    {
        if (!await lease.HoldAsync(ct)) return null;
        var ran = false;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync(ct);
            if (await UrlHashFill.DoneAsync(db, ct))
            {
                jobs.Scheduled(JobMonitor.UrlHashes, false);
                return true;
            }
            jobs.Scheduled(JobMonitor.UrlHashes, true, time.GetUtcNow());
            var done = false;
            ran = true;
            await jobs.RunAsync(JobMonitor.UrlHashes, async () =>
            {
                var before = _progress.Filled;
                done = await UrlHashFill.FillAsync(db, _progress, BatchesPerPass, ct: ct);
                return done ? $"done: {_progress.Filled:N0} filled"
                    : $"{_progress.Filled - before:N0} filled, {await UrlHashFill.LeftAsync(db, _progress, ct):N0} left";
            });
            jobs.Scheduled(JobMonitor.UrlHashes, !done, done ? null : time.GetUtcNow() + Pause);
            if (done) log.LogInformation("Every stored article and link address has its hash ({Filled} filled).", _progress.Filled);
            return done;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A run's failure is recorded and logged by the monitor (a Warning, with the job's event id); one before it (the
            // database not answering) is logged here. Either way it's tried again shortly.
            if (!ran) log.LogWarning(ex, "Couldn't read whether every stored address has its hash; it's tried again shortly.");
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
