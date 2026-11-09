using Microsoft.Extensions.Options;
using Tide.Application.Metrics;
using Tide.Application.Operations;
using Tide.Infrastructure.Storage;

namespace Tide.Web.Services;

/// <summary>
/// Reads the metrics drop folder every <c>Metrics:Import:PollMinutes</c> when <c>Metrics:Import:DropFolderEnabled</c> is
/// set (ADR 0014): each saved on Metric imports, else the server's. Both are read on each pass, so a change takes effect
/// without a restart. A failure is logged and tried again at the next pass; it never stops the app. A process that
/// doesn't hold the job lease skips its passes (another process reads the folder).
/// </summary>
public sealed class MetricImportHostedService(IServiceScopeFactory scopes, MetricDropFolder folder, IOptionsMonitor<MetricImportOptions> options,
    JobMonitor jobs, IJobLease lease, TimeProvider time, ILogger<MetricImportHostedService> log) : BackgroundService
{
    public static readonly TimeSpan OffInterval = TimeSpan.FromMinutes(1);

    // A setting read afresh, or its fallback when the server's file has a value that won't bind (PollMinutes "5m",
    // hand-edited while TIDE runs): logged, and read again next pass, rather than stopping the app.
    private T Read<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (InvalidOperationException ex)
        {
            log.LogError(ex, "A Metrics:Import setting can't be read (check appsettings); the drop folder waits until it can.");
            return fallback;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var on = Read(() => folder.Enabled, false);
            if (on)
            {
                try
                {
                    if (await lease.HoldAsync(stoppingToken))
                        await jobs.RunAsync(JobMonitor.MetricDropFolder, async () =>
                        {
                            await using var scope = scopes.CreateAsyncScope();
                            var read = await folder.ProcessAsync(scope.ServiceProvider.GetRequiredService<MetricImportService>(), stoppingToken);
                            if (read > 0) log.LogInformation("Imported {Count} metrics file(s) from the drop folder.", read);
                            return $"{read} file{(read == 1 ? "" : "s")}";
                        });
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // Recorded and logged (a Warning, with the job's event id) by the monitor.
                    log.LogDebug(ex, "Reading the metrics drop folder failed; it's tried again at the next pass.");
                }
            }
            // While it's off, whether it's on is asked every minute, so turning it on takes effect soon.
            on = Read(() => folder.Enabled, false);
            var wait = on ? Read(() => TimeSpan.FromMinutes(Math.Clamp(options.CurrentValue.PollMinutes, 1, 60)), OffInterval) : OffInterval;
            jobs.Scheduled(JobMonitor.MetricDropFolder, on, on ? time.GetUtcNow() + wait : null, on ? wait : null);
            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
