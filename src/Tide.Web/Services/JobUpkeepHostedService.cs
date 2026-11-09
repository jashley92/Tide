using Tide.Application.Abstractions;
using Tide.Application.Operations;

namespace Tide.Web.Services;

/// <summary>
/// Keeps the background jobs' own bookkeeping: renews the job lease every <see cref="Interval"/> whatever the jobs are
/// doing (a long CVE refresh doesn't let it lapse, and a standing-by process takes over within a few minutes of the
/// holder stopping), writes the finished runs to the job history, and prunes the history daily while it holds the
/// lease. At shutdown it writes what's left and lets the lease go, so the next process takes over at once. Registered
/// before the jobs, so it stops after them.
/// </summary>
public sealed class JobUpkeepHostedService(IServiceScopeFactory scopes, BackgroundJobLease lease, JobRunLog runs, TimeProvider time,
    ILogger<JobUpkeepHostedService> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>How long the shutdown's last write and the lease's release may take, together.</summary>
    public static readonly TimeSpan ShutdownLimit = TimeSpan.FromSeconds(5);

    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PassAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogWarning(ex, "Writing or pruning the job history failed; it's tried again shortly."); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass: the lease, the runs to write, and the daily prune.</summary>
    internal async Task PassAsync(CancellationToken ct)
    {
        var held = await lease.HoldAsync(ct);
        await using var scope = scopes.CreateAsyncScope();
        var dbs = scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>();
        await runs.FlushAsync(dbs, ct);
        var now = time.GetUtcNow();
        if (held && now - _lastPrune > TimeSpan.FromDays(1))
        {
            var removed = await runs.PruneAsync(dbs, ct);
            _lastPrune = now;
            if (removed > 0) log.LogInformation("Pruned {Count} job run(s) older than {Days} days from the job history.", removed, JobRunLog.Keep.TotalDays);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // Best effort, a few seconds at most, and never thrown: a database that doesn't answer, or a host torn down
        // around it (its services or its loggers already disposed), leaves the last runs unwritten and the lease to lapse.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ShutdownLimit);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await runs.FlushAsync(scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>(), limit.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { log.LogWarning(ex, "Couldn't write the last job runs at shutdown."); }
            catch (Exception) { /* the logging is gone too */ }
        }
        catch (OperationCanceledException) { /* out of time */ }
        try { await lease.ReleaseAsync(limit.Token); }
        catch (Exception) { /* out of time, or torn down: it lapses instead */ }
    }
}
