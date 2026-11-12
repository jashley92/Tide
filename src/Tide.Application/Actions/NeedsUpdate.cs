using Tide.Application.Org;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>How long an open action may go without anyone updating it before the meeting asks about it: Open and In
/// progress ones after <see cref="OpenDays"/> business days, Blocked ones after <see cref="BlockedDays"/>
/// (<c>Org:StaleOpenDays</c>, <c>Org:StaleBlockedDays</c>).</summary>
public sealed record StaleLimits(int OpenDays = StaleLimits.DefaultOpenDays, int BlockedDays = StaleLimits.DefaultBlockedDays)
{
    public const int DefaultOpenDays = 2;
    public const int DefaultBlockedDays = 5;
    public const int MinDays = 1;
    public const int MaxDays = 30;

    public static readonly StaleLimits Default = new();
}

/// <summary>The last thing a person did to an action: who, and the history line it wrote (null for a comment).</summary>
public sealed record LastUpdate(string By, string? EventText = null);

/// <summary>
/// The rule for an action that needs an update: open (Open, In progress or Blocked) and quiet for its status's limit of
/// business days or more, counted on the organization's working days (weekends and holidays don't count). Only what a
/// person did is an update (<see cref="ActionRow.LastActivityUtc"/>): TIDE's own lines (the escalation ladder's, a
/// skipped reminder's) aren't.
/// </summary>
public static class NeedsUpdate
{
    /// <summary>Business days from <paramref name="lastUpdate"/> (the day it was last updated) to <paramref name="asOf"/>:
    /// the working days after the one and up to the other, counted as a due date's are. 0 on the day, or for a day after
    /// <paramref name="asOf"/>.</summary>
    public static int QuietDays(DateOnly lastUpdate, DateOnly asOf, WorkingDays days) =>
        Math.Max(0, -ActionSla.BusinessDaysUntil(asOf, lastUpdate, days));

    /// <summary>The business days an action of <paramref name="status"/> may be quiet for; null for a status that's never
    /// asked about (Tracking, followed elsewhere; or closed).</summary>
    public static int? Limit(ActionStatus status, StaleLimits limits) => status switch
    {
        ActionStatus.Open or ActionStatus.InProgress => limits.OpenDays,
        ActionStatus.Blocked => limits.BlockedDays,
        _ => null,
    };

    /// <summary>Whether an action of <paramref name="status"/>, quiet for <paramref name="quietDays"/> business days, needs an update.</summary>
    public static bool IsStale(ActionStatus status, int quietDays, StaleLimits limits) => Limit(status, limits) is { } n && quietDays >= n;

    /// <summary>The last update in short, as the slide says it: "comment by Sam", "moved to Blocked by Sam". Read from the
    /// history line the action's page wrote for it (<see cref="ActionService"/>'s wording); one it doesn't know is an "edit".</summary>
    public static string Describe(LastUpdate last) => $"{What(last.EventText)} by {last.By}";

    // The statuses' names, longest first, so a name isn't read as a shorter one it starts with.
    private static readonly string[] StatusNames = [.. Enum.GetValues<ActionStatus>().Select(ActionService.StatusName).OrderByDescending(n => n.Length)];

    private static string What(string? line)
    {
        if (line is null) return "comment";
        if (MovedTo(line) is { } status) return "moved to " + status;
        if (line.StartsWith("Created", StringComparison.Ordinal) || line.StartsWith("Raised", StringComparison.Ordinal)) return "raised";
        if (line.StartsWith("Ticked ", StringComparison.Ordinal)) return "checklist tick";
        if (line.StartsWith("Unticked ", StringComparison.Ordinal) || line.Contains("checklist", StringComparison.Ordinal)) return "checklist change";
        if (line.StartsWith("Took it on", StringComparison.Ordinal) || line.Contains("assignee: ", StringComparison.Ordinal)) return "holder change";
        if (line.Contains("due: ", StringComparison.Ordinal)) return "due date change";
        if (line.StartsWith("Handed from ", StringComparison.Ordinal)) return "hand-off";
        if (line.StartsWith("Update from ", StringComparison.Ordinal)) return "team update";
        return "edit";
    }

    // The status a "Status: Open → Blocked" line moved it to, wherever in the line it is (after a team's update, say).
    private static string? MovedTo(string line)
    {
        var at = line.IndexOf("Status: ", StringComparison.Ordinal);
        var arrow = at < 0 ? -1 : line.IndexOf("→ ", at, StringComparison.Ordinal);
        if (arrow < 0) return null;
        var rest = line[(arrow + 2)..];
        return StatusNames.FirstOrDefault(n => rest.StartsWith(n, StringComparison.Ordinal));
    }
}
