using Tide.Application.Abstractions;
using Tide.Domain.Enums;

namespace Tide.Application.Briefs;

/// <summary>
/// Which briefs' meetings are running in present mode now. Present mode keeps them in memory in the web app (ADR 0001:
/// one process); this is how the brief's own rules see them, so Back to ready is refused mid-meeting, and how a Visitor's
/// reads are let through while they're in one (<see cref="MeetingAccess"/>).
/// </summary>
public interface IMeetingsInProgress
{
    /// <summary>Whether the meeting of the brief <paramref name="briefId"/> is running in present mode.</summary>
    bool IsLive(Guid briefId);

    /// <summary>
    /// The meetings running now that <paramref name="userId"/> has a seat in (a present-mode page of theirs in it, its
    /// connection up): each one's day and brief, and what its presenter has open over the slide.
    /// </summary>
    IReadOnlyList<SeatedMeeting> SeatedIn(string userId) => [];
}

/// <summary>A live meeting someone is seated in: its day and brief, and the action (its number) or Our stack product
/// whose card the presenter has open over the slide for the room, if any.</summary>
public sealed record SeatedMeeting(DateOnly Day, Guid BriefId, int? Action, Guid? Product);

/// <summary>No meeting is ever running: where present mode isn't (a test, a tool).</summary>
public sealed class NoMeetingsInProgress : IMeetingsInProgress
{
    public static readonly NoMeetingsInProgress Instance = new();

    public bool IsLive(Guid briefId) => false;
}

/// <summary>
/// A Visitor's access window. Someone who may join meetings but doesn't read the team edition (a Visitor) reads a
/// brief's team-edition content only while they hold a seat in that brief's live meeting, and only what its slides
/// show: the brief, its numbers, its actions, its screenshots, and the action or product card the presenter has open.
/// The services ask here on every read, so the window closes the moment the meeting ends or they leave it, whatever a
/// page still shows. Anyone on the team edition never needs it, and gets nothing from it.
/// </summary>
public static class MeetingAccess
{
    /// <summary>Whether <paramref name="user"/> sees the team's content only from a seat in a live meeting.</summary>
    public static bool MeetingOnly(ICurrentUser user) => user.Has(Permission.JoinMeetings) && !user.Has(Permission.ReadTeamEdition);

    /// <summary>The live meetings a meeting-only user is seated in now; none for anyone else.</summary>
    public static IReadOnlyList<SeatedMeeting> Seats(ICurrentUser user, IMeetingsInProgress? meetings) =>
        meetings is null || !MeetingOnly(user) || !user.IsAuthenticated ? [] : meetings.SeatedIn(user.UserId);

    /// <summary>Whether a meeting-only user is seated in the live meeting of brief <paramref name="briefId"/>.</summary>
    public static bool SeatedFor(ICurrentUser user, IMeetingsInProgress? meetings, Guid briefId) =>
        Seats(user, meetings).Any(s => s.BriefId == briefId);

    /// <summary>Whether a meeting-only user is seated in the live meeting of <paramref name="day"/>'s brief.</summary>
    public static bool SeatedOn(ICurrentUser user, IMeetingsInProgress? meetings, DateOnly day) =>
        Seats(user, meetings).Any(s => s.Day == day);

    /// <summary>Whether a meeting-only user is seated in any live meeting.</summary>
    public static bool SeatedAnywhere(ICurrentUser user, IMeetingsInProgress? meetings) => Seats(user, meetings).Count > 0;
}
