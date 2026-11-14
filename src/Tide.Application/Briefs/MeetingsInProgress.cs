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

    /// <summary>Who has presented the brief <paramref name="briefId"/>'s live meeting so far, by name, in the order they
    /// took it; none when it isn't live.</summary>
    IReadOnlyList<string> PresentersOf(Guid briefId) => [];
}

/// <summary>A live meeting someone is seated in: its day and brief, and the action (its number) or the card the
/// presenter has open over the slide for the room, if any; and whether the one seated is presenting it.</summary>
public sealed record SeatedMeeting(DateOnly Day, Guid BriefId, int? Action, MeetingCard? Card, bool Presenting = false);

/// <summary>What a card opened over a slide in present mode shows.</summary>
public enum MeetingCardKind
{
    /// <summary>An Our stack product's card, by the product's id.</summary>
    Product,

    /// <summary>An article's indicators, by the article's id.</summary>
    Indicators,

    /// <summary>An article's CVEs, all of them, by the article's id.</summary>
    Cves,
}

/// <summary>A card opened over a slide in present mode: what kind it is, and whose (a product's id, an article's).</summary>
public sealed record MeetingCard(MeetingCardKind Kind, Guid Id)
{
    /// <summary>The Our stack product <paramref name="productId"/>'s card.</summary>
    public static MeetingCard ForProduct(Guid productId) => new(MeetingCardKind.Product, productId);

    /// <summary>The article <paramref name="articleId"/>'s indicators.</summary>
    public static MeetingCard ForIndicators(Guid articleId) => new(MeetingCardKind.Indicators, articleId);

    /// <summary>The article <paramref name="articleId"/>'s CVEs.</summary>
    public static MeetingCard ForCves(Guid articleId) => new(MeetingCardKind.Cves, articleId);
}

/// <summary>
/// When a brief's meeting may be held: on the brief's day or later, in the organization's zone. Starting it in present
/// mode and marking it presented (<see cref="BriefService.MarkPresentedAsync"/>) keep to this one rule, so a meeting that
/// could start can always be ended.
/// </summary>
public static class MeetingDay
{
    /// <summary>Why a meeting before its brief's day is refused.</summary>
    public const string TooEarly = "The meeting can't be held before the brief's day.";

    /// <summary>Whether <paramref name="atUtc"/> falls before <paramref name="day"/> in the organization's zone.</summary>
    public static bool Before(Org.OrgClock clock, DateOnly day, DateTimeOffset atUtc) => clock.DayOf(atUtc) < day;
}

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

    /// <summary><paramref name="user"/>'s seat in the live meeting of brief <paramref name="briefId"/>, whatever their
    /// role, or null. Callers decide what a seat lets them do.</summary>
    public static SeatedMeeting? SeatIn(ICurrentUser user, IMeetingsInProgress? meetings, Guid briefId) =>
        meetings is null || !user.IsAuthenticated ? null : meetings.SeatedIn(user.UserId).FirstOrDefault(s => s.BriefId == briefId);

    /// <summary>Whether a meeting-only user is seated in the live meeting of brief <paramref name="briefId"/>.</summary>
    public static bool SeatedFor(ICurrentUser user, IMeetingsInProgress? meetings, Guid briefId) =>
        Seats(user, meetings).Any(s => s.BriefId == briefId);

    /// <summary>Whether a meeting-only user is seated in the live meeting of <paramref name="day"/>'s brief.</summary>
    public static bool SeatedOn(ICurrentUser user, IMeetingsInProgress? meetings, DateOnly day) =>
        Seats(user, meetings).Any(s => s.Day == day);

    /// <summary>Whether a meeting-only user is seated in any live meeting.</summary>
    public static bool SeatedAnywhere(ICurrentUser user, IMeetingsInProgress? meetings) => Seats(user, meetings).Count > 0;
}
