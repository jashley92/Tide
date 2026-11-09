using Tide.Application.Actions;
using Tide.Application.Operations;

namespace Tide.Web.Services;

/// <summary>
/// Runs the reminder and digest scans (<see cref="ReminderService"/>) every few minutes. Each scan is off until it's
/// turned on (on Teams &amp; SLA, else under <c>Notifications</c> on the server, read afresh each scan), waits for its time of day, and sends a thing once (marks), so a frequent poll costs only
/// a few queries. A failed pass is logged and the next poll tries again; it never stops the app. A process that doesn't
/// hold the job lease skips its passes (another process sends them).
/// </summary>
public sealed class ReminderHostedService(IServiceScopeFactory scopes, JobMonitor jobs, IJobLease lease, TimeProvider time, ILogger<ReminderHostedService> log) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PassAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One poll: the scans that are due, recorded as one run of the job. A scan that failed (the relay down)
    /// makes the run a failure on Diagnostics, naming it, though the other scans still ran: it was shown as a success.
    /// A failure is logged, never thrown (except for shutting down).</summary>
    internal async Task PassAsync(CancellationToken stoppingToken)
    {
        if (!await lease.HoldAsync(stoppingToken)) return;
        try
        {
            await jobs.RunAsync(JobMonitor.Reminders, async () =>
            {
                await using var scope = scopes.CreateAsyncScope();
                var pass = await scope.ServiceProvider.GetRequiredService<ReminderService>().RunDueAsync(stoppingToken);
                // An email the relay may not have taken isn't sent again by itself: Diagnostics says to check it arrived.
                var problems = pass.Failed.Select(f => $"{f.Kind}: {f.Error}").Concat(pass.Unsure.Select(u => $"{u.Kind}: {u.Note}")).ToList();
                if (problems.Count > 0) throw new JobProblemException(string.Join("; ", problems));
                return pass.Sent > 0 ? $"{pass.Sent} email{(pass.Sent == 1 ? "" : "s")}" : null;
            });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Recorded and logged (a Warning, with the job's event id) by the monitor.
            log.LogDebug(ex, "A reminder scan failed; it's tried again at the next poll.");
        }
        jobs.Scheduled(JobMonitor.Reminders, true, time.GetUtcNow() + PollInterval);
    }
}
