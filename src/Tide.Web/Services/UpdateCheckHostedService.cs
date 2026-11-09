using Microsoft.Extensions.Options;
using Tide.Application.Operations;

namespace Tide.Web.Services;

/// <summary>
/// Runs the update check a few minutes after start, then every <c>Updates:CheckIntervalHours</c> (saved on the Updates
/// page, else the server's, read each pass) while it's on. Every few minutes it re-reads whether the check is on (the setting saved on the Updates page, else
/// <c>Updates:CheckEnabled</c>), so turning it on takes effect without a restart. A failed check is shown on the
/// Updates page and Diagnostics; it never stops the app. A process that doesn't hold the job lease skips its passes
/// (another process checks).
/// </summary>
public sealed class UpdateCheckHostedService(IServiceScopeFactory scopes, UpdateCheckService updates, IOptionsMonitor<UpdateOptions> options,
    JobMonitor jobs, IJobLease lease, TimeProvider time, ILogger<UpdateCheckHostedService> log) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);

    private DateTimeOffset? _lastRunUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Whether it's on is known at once (Diagnostics and the Updates page say so); the first check waits for start-up.
        try
        {
            await ReadEnabledAsync(stoppingToken);
            jobs.Scheduled(JobMonitor.UpdateCheck, updates.Enabled, updates.Enabled ? time.GetUtcNow() + FirstDelay : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't read whether the update check is on; it's read again before the first check.");
        }
        if (!await DelayAsync(FirstDelay, stoppingToken)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PassAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            if (!await DelayAsync(PollInterval, stoppingToken)) return;
        }
    }

    /// <summary>One pass: re-reads whether the check is on, and checks when it's on and due. A failure is recorded and
    /// logged, never thrown (except for shutting down).</summary>
    internal async Task PassAsync(CancellationToken ct)
    {
        if (!await lease.HoldAsync(ct)) return;
        try
        {
            await ReadEnabledAsync(ct);
            var interval = Interval(options.CurrentValue);
            if (updates.Enabled && (_lastRunUtc is not { } last || time.GetUtcNow() - last >= interval))
            {
                var started = time.GetUtcNow();
                var status = await updates.CheckAsync(manual: false, ct);
                _lastRunUtc = time.GetUtcNow();
                if (status.State == UpdateState.Failed) jobs.Failed(JobMonitor.UpdateCheck, status.Problem ?? "couldn't check");
                else jobs.Succeeded(JobMonitor.UpdateCheck, started, status.State == UpdateState.Available ? $"{status.Latest!.Version} is available" : "up to date");
            }
            jobs.Scheduled(JobMonitor.UpdateCheck, updates.Enabled, updates.Enabled ? (_lastRunUtc ?? time.GetUtcNow()) + interval : null, interval);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Logged by the monitor (a Warning, with the job's event id); it's tried again later.
            jobs.Failed(JobMonitor.UpdateCheck, ex);
            _lastRunUtc = time.GetUtcNow();
        }
    }

    private async Task ReadEnabledAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        updates.Enabled = await scope.ServiceProvider.GetRequiredService<UpdateSettings>().GetEnabledAsync(ct);
    }

    internal static TimeSpan Interval(UpdateOptions o) => TimeSpan.FromHours(Math.Clamp(o.CheckIntervalHours > 0 ? o.CheckIntervalHours : 24, 6, 168));

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }
}
