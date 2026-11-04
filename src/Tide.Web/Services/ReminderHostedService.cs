using Tide.Application.Actions;
using Tide.Application.Operations;

namespace Tide.Web.Services;

/// <summary>
/// Runs the reminder and digest scans (<see cref="ReminderService"/>) every few minutes. Each scan is off until it's
/// turned on (on Teams &amp; SLA, else under <c>Notifications</c> on the server, read afresh each scan), waits for its time of day, and sends a thing once (marks), so a frequent poll costs only
/// a few queries. A failed pass is logged and the next poll tries again; it never stops the app.
/// </summary>
public sealed class ReminderHostedService(IServiceScopeFactory scopes, JobMonitor jobs, TimeProvider time, ILogger<ReminderHostedService> log) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await jobs.RunAsync(JobMonitor.Reminders, async () =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ReminderService>().RunDueAsync(stoppingToken);
                    return null;
                });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogError(ex, "A reminder scan failed; it's tried again at the next poll.");
            }
            jobs.Scheduled(JobMonitor.Reminders, true, time.GetUtcNow() + PollInterval);
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
