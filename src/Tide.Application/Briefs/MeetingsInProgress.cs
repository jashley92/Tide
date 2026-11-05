namespace Tide.Application.Briefs;

/// <summary>
/// Which briefs' meetings are running in present mode now. Present mode keeps them in memory in the web app (ADR 0001:
/// one process); this is how the brief's own rules see them, so Back to ready is refused mid-meeting.
/// </summary>
public interface IMeetingsInProgress
{
    /// <summary>Whether the meeting of the brief <paramref name="briefId"/> is running in present mode.</summary>
    bool IsLive(Guid briefId);
}

/// <summary>No meeting is ever running: where present mode isn't (a test, a tool).</summary>
public sealed class NoMeetingsInProgress : IMeetingsInProgress
{
    public static readonly NoMeetingsInProgress Instance = new();

    public bool IsLive(Guid briefId) => false;
}
