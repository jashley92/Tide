using Tide.Application.Abstractions;

namespace Tide.Web.Services;

/// <summary>
/// Keeps the attendance record honest when everyone leaves a meeting without ending it. The sweep drops the meeting
/// (<see cref="LiveMeetings.Dropped"/>) with when it last saw each person, and whoever is still marked there is given
/// that as their left time (<see cref="AttendanceCloser"/>), as the system: no one is signed in to do it. A restart
/// loses the meeting, and when anyone was last seen with it, so those rows stay open until a rejoin or an End. A
/// failure is logged and the record stays as it was: it's a nicety, not something to stop the app for.
/// </summary>
public sealed class MeetingAttendanceHostedService(LiveMeetings meetings, IServiceScopeFactory scopes,
    ILogger<MeetingAttendanceHostedService> log) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        meetings.Dropped += OnDropped;
        return Task.CompletedTask;
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
            await new AttendanceCloser(scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>())
                .CloseAsync(dropped.BriefId, dropped.LastSeenUtc, _stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { /* shutting down */ }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Couldn't record who left the meeting of {Day}, dropped with no one in it.", dropped.Day);
        }
    }
}
