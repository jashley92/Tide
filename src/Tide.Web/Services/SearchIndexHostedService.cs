using Tide.Application.Abstractions;
using Tide.Application.Operations;
using Tide.Application.Search;

namespace Tide.Web.Services;

/// <summary>
/// Keeps the articles' search text current (<see cref="ArticleSearchIndex"/>): every <see cref="Poll"/> it rebuilds the
/// rows saves have marked, a batch at a time, so a search goes back to the fast match within seconds of an edit (it finds
/// the edit at once either way). After an upgrade, or with a new <see cref="ArticleSearchIndex.Builder"/>, it first builds
/// every article's row, a run at a time; searches get faster as it goes. Only the process holding the job lease runs it.
/// A pass with nothing to do is recorded only every <see cref="Quiet"/>, so the job history isn't filled with them.
/// </summary>
public sealed class SearchIndexHostedService(IServiceScopeFactory scopes, JobMonitor jobs, IJobLease lease, TimeProvider time,
    ILogger<SearchIndexHostedService> log) : BackgroundService
{
    /// <summary>How often it looks for marked rows.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);

    /// <summary>How often a pass with nothing to rebuild is recorded as a run (the job's interval on Diagnostics).</summary>
    public static readonly TimeSpan Quiet = JobMonitor.SearchIndex.Interval;

    /// <summary>Batches rebuilt in one run.</summary>
    public const int BatchesPerPass = 10;

    // Whether every article has had a row of the current builder since this process started: then only marked rows are
    // looked for (a seek), not missing or older ones (a scan). A row is added with every new article, and the builder
    // can't change while the process runs.
    private bool _complete;
    private DateTimeOffset? _quietSince;

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

    /// <summary>One look: rebuilds what's marked (or, until it's all built, missing), recorded as a run when there was
    /// any; never throws but to cancel (a failure is recorded and logged by the monitor).</summary>
    internal async Task PassAsync(CancellationToken ct)
    {
        if (!await lease.HoldAsync(ct))
        {
            _complete = false;   // another process may have run an older build meanwhile
            return;
        }
        var started = time.GetUtcNow();
        var inRun = false;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var dbs = scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>();
            var (built, more) = await RebuildAsync(dbs, started, batches: 1, ct);
            if (built == 0 && !more)
            {
                _complete = true;
                if (_quietSince is { } since && started - since < Quiet) return;
                _quietSince = started;
                jobs.Succeeded(JobMonitor.SearchIndex, started, "up to date");
            }
            else
            {
                _quietSince = null;
                inRun = true;
                await jobs.RunAsync(JobMonitor.SearchIndex, async () =>
                {
                    var (rest, left) = await RebuildAsync(dbs, started, BatchesPerPass - 1, ct);
                    built += rest;
                    if (!left) _complete = true;
                    await using var db = await dbs.CreateAsync(ct);
                    return left ? $"{built:N0} rebuilt, {await ArticleSearchIndex.LeftAsync(db, ct):N0} left" : $"{built:N0} rebuilt";
                });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A run's failure is recorded and logged by the monitor; one before it (the database not answering) is recorded
            // here. Either way it's tried again at the next poll.
            if (inRun) log.LogDebug(ex, "The search index run failed; it's tried again shortly.");
            else jobs.Failed(JobMonitor.SearchIndex, ex);
        }
        finally
        {
            jobs.Scheduled(JobMonitor.SearchIndex, true, time.GetUtcNow() + Poll, Quiet);
        }
    }

    // Up to the given batches, each in its own context; whether any are left.
    private async Task<(int Built, bool More)> RebuildAsync(IAppDbContextFactory dbs, DateTimeOffset now, int batches, CancellationToken ct)
    {
        var built = 0;
        for (var i = 0; i < batches; i++)
        {
            await using var db = await dbs.CreateAsync(ct);
            var (n, more) = await ArticleSearchIndex.RebuildAsync(db, now, markedOnly: _complete, ct: ct);
            built += n;
            if (!more) return (built, false);
        }
        return (built, true);
    }
}
