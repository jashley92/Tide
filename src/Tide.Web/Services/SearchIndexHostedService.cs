using Tide.Application.Abstractions;
using Tide.Application.Operations;
using Tide.Application.Search;

namespace Tide.Web.Services;

/// <summary>
/// Keeps the articles' search text current (<see cref="ArticleSearchIndex"/>): every <see cref="Poll"/> it rebuilds the
/// rows saves have marked, a batch at a time, so a search goes back to the fast match within seconds of an edit (it finds
/// the edit at once either way). After an upgrade every article's row is marked, so it builds them all, a pass at a time;
/// searches get faster as it goes. Only the process holding the job lease runs it. Its work is recorded as one run every
/// <see cref="Quiet"/> (the job's interval on Diagnostics), with how many it rebuilt since, so an analyst's autosaves don't
/// fill the job history.
/// </summary>
public sealed class SearchIndexHostedService(IServiceScopeFactory scopes, JobMonitor jobs, IJobLease lease, TimeProvider time,
    ILogger<SearchIndexHostedService> log) : BackgroundService
{
    /// <summary>How often it looks for marked rows.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);

    /// <summary>How often its work is recorded as a run.</summary>
    public static readonly TimeSpan Quiet = JobMonitor.SearchIndex.Interval;

    /// <summary>Batches rebuilt in one pass.</summary>
    public const int BatchesPerPass = 10;

    // Rows built by an older builder are marked once per process (a new build is a new process); rebuilt since the last run
    // recorded; when that run's period began.
    private bool _olderMarked;
    private int _built;
    private DateTimeOffset? _since;

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

    /// <summary>One look: rebuilds what's marked, and records a run once a <see cref="Quiet"/> period has passed. Never
    /// throws but to cancel: a failure is recorded on Diagnostics and logged under the job's event id.</summary>
    internal async Task PassAsync(CancellationToken ct)
    {
        if (!await lease.HoldAsync(ct)) return;
        var started = time.GetUtcNow();
        _since ??= started;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync(ct);
            if (!_olderMarked)
            {
                await ArticleSearchIndex.MarkOlderBuildsAsync(db, ct);
                _olderMarked = true;
            }
            var more = true;
            for (var i = 0; i < BatchesPerPass && more; i++)
            {
                var taken = await ArticleSearchIndex.RebuildAsync(db, ct: ct);
                _built += taken;
                more = taken == ArticleSearchIndex.Batch;
            }
            if (started - _since < Quiet && !more) return;
            jobs.Succeeded(JobMonitor.SearchIndex, _since.Value,
                more ? $"{_built:N0} rebuilt, {await ArticleSearchIndex.LeftAsync(db, ct):N0} left" : _built == 0 ? "up to date" : $"{_built:N0} rebuilt");
            (_built, _since) = (0, started);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "The search index pass failed; it's tried again at the next poll.");
            jobs.Failed(JobMonitor.SearchIndex, ex);
        }
        finally
        {
            jobs.Scheduled(JobMonitor.SearchIndex, true, time.GetUtcNow() + Poll, Quiet);
        }
    }
}
