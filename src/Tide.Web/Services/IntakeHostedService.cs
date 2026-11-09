using Tide.Application.Abstractions;
using Microsoft.Extensions.Options;
using Tide.Application.Intake;
using Tide.Application.Operations;

namespace Tide.Web.Services;

/// <summary>
/// Runs intake in the background: polls the sources that are due (when <c>Intake:Enabled</c>), refreshes the CVE data
/// when it's due (when <c>Cve:Enabled</c>), and prunes old feed items once a day. Each setting is the one saved on
/// Sources, else the server's, read on every pass. Each part's failure is logged and
/// tried again at the next pass; it never stops the app. Each part's runs are reported to <see cref="JobMonitor"/> for
/// Diagnostics. A process that doesn't hold the job lease skips its passes (another process runs them).
/// </summary>
public sealed class IntakeHostedService(IServiceScopeFactory scopes, ChangeFeed changes, JobMonitor jobs, IJobLease lease, IOptionsMonitor<IntakeOptions> intake,
    IOptionsMonitor<CveOptions> cve, TimeProvider time, ILogger<IntakeHostedService> log) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // A setting the server's file has that won't bind (Intake:Enabled "yes", hand-edited while TIDE runs) throws
            // as the pass reads it; it's logged and read again next pass, rather than stopping the app.
            try { await PassAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogError(ex, "An intake pass failed before it ran (check the Intake and Cve settings); it's tried again in a minute."); }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass. Whether polling and enrichment are on is read afresh each time (saved on Sources, else the
    /// server's), so turning either on or off takes effect at the next pass.</summary>
    internal async Task PassAsync(CancellationToken stoppingToken)
    {
        if (!await lease.HoldAsync(stoppingToken)) return;
        var now = time.GetUtcNow();
        var polling = intake.CurrentValue.Enabled;
        var enriching = cve.CurrentValue.Enabled;
        // Each part's on/off is known before any of them runs, so a slow first pass doesn't show the later ones as off.
        jobs.Scheduled(JobMonitor.FeedPoll, polling, polling ? now + PollInterval : null);
        jobs.Scheduled(JobMonitor.CveRefresh, enriching, enriching ? jobs.Get(JobMonitor.CveRefresh).NextDueUtc ?? now : null, CveInterval(cve.CurrentValue));
        jobs.Scheduled(JobMonitor.Prune, true, _lastPrune == DateTimeOffset.MinValue ? now : _lastPrune + TimeSpan.FromDays(1));
        await Step("Polling the sources", JobMonitor.FeedPoll, async sp =>
        {
            var taken = await sp.GetRequiredService<IntakeService>().PollDueAsync(stoppingToken);
            if (taken > 0) changes.Publish(ChangeFeed.Triage);
            return $"{taken} new item{(taken == 1 ? "" : "s")}";
        }, stoppingToken);
        await CveStep(stoppingToken);
        // A prune that failed is tried again at the next pass, as the others are: it waited a day (Diagnostics said so).
        if (now - _lastPrune > TimeSpan.FromDays(1)
            && await Step("Pruning old feed items", JobMonitor.Prune, async sp =>
            {
                var items = await sp.GetRequiredService<IntakeService>().PruneAsync(stoppingToken);
                var texts = await sp.GetRequiredService<ReadingService>().PruneAsync(stoppingToken);
                return $"{items} removed, {texts} kept text{(texts == 1 ? "" : "s")} expired";
            }, stoppingToken))
            _lastPrune = now;
        jobs.Scheduled(JobMonitor.Prune, true, now - _lastPrune > TimeSpan.FromDays(1) ? now + PollInterval : _lastPrune + TimeSpan.FromDays(1));
    }

    // The CVE refresh is asked every pass but runs only when it's due, so only a pass that ran is recorded.
    private async Task CveStep(CancellationToken ct)
    {
        var o = cve.CurrentValue;
        var started = time.GetUtcNow();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var run = await scope.ServiceProvider.GetRequiredService<CveService>().EnrichDueAsync(ct);
            if (run is { Error: { } error }) jobs.Failed(JobMonitor.CveRefresh, error);
            else if (run is not null) jobs.Succeeded(JobMonitor.CveRefresh, started, $"KEV {run.Kev}, EPSS {run.Epss}, NVD {run.Nvd}");
            var last = jobs.Get(JobMonitor.CveRefresh).LastFinishedUtc;
            jobs.Scheduled(JobMonitor.CveRefresh, o.Enabled, o.Enabled ? (last ?? started) + CveInterval(o) : null, CveInterval(o));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutting down */ }
        catch (Exception ex)
        {
            // Logged by the monitor (a Warning, with the job's event id); it's tried again at the next pass.
            jobs.Failed(JobMonitor.CveRefresh, ex.GetType().Name + ": " + ex.Message, ex);
        }
    }

    private static TimeSpan CveInterval(CveOptions o) => TimeSpan.FromHours(Math.Max(1, o.RefreshHours));

    // Whether the step ran to the end.
    private async Task<bool> Step(string what, JobDefinition job, Func<IServiceProvider, Task<string?>> work, CancellationToken ct)
    {
        try
        {
            await jobs.RunAsync(job, async () =>
            {
                await using var scope = scopes.CreateAsyncScope();
                return await work(scope.ServiceProvider);
            });
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutting down */ }
        catch (Exception ex)
        {
            // Recorded and logged (a Warning, with the job's event id) by the monitor.
            log.LogDebug(ex, "{What} failed; it's tried again at the next pass.", what);
        }
        return false;
    }
}
