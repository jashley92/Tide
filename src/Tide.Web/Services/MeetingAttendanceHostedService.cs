using Tide.Application.Briefs;

namespace Tide.Web.Services;

/// <summary>
/// Keeps the attendance record honest when nobody ends a meeting. A meeting everyone left is dropped by the sweep
/// (<see cref="LiveMeetings.Dropped"/>), and a restart drops every meeting with it; either way, whoever was still marked
/// there is given a left time (<see cref="AttendanceCloser"/>), as the system: no one is signed in to do it. At start-up,
/// before the app takes requests, meetings the last run left open are closed; then each dropped meeting as it goes. A
/// failure is logged and the record stays as it was: it's a nicety, not something to stop the app for.
/// </summary>
public sealed class MeetingAttendanceHostedService(LiveMeetings meetings, IServiceScopeFactory scopes, TimeProvider time,
    ILogger<MeetingAttendanceHostedService> log) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        meetings.Dropped += OnDropped;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var closed = await scope.ServiceProvider.GetRequiredService<AttendanceCloser>()
                .CloseAllAsync(time.GetUtcNow(), meetings.IsLive, cancellationToken);
            if (closed > 0) log.LogInformation("Closed the attendance of {Count} people a restart left marked as in a meeting.", closed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Couldn't close the attendance meetings left open by the last run; it's tried again at the next start.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        meetings.Dropped -= OnDropped;
        await _stopping.CancelAsync();
    }

    public void Dispose() => _stopping.Dispose();

    private void OnDropped(DroppedMeeting dropped) => _ = CloseAsync(dropped);

    internal async Task CloseAsync(DroppedMeeting dropped)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AttendanceCloser>()
                .CloseAsync(dropped.BriefId, dropped.LastSeenUtc, dropped.AtUtc, _stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { /* shutting down */ }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Couldn't record who left the meeting of {Day}, dropped with no one in it.", dropped.Day);
        }
    }
}
