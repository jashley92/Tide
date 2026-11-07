using Tide.Domain.Entities;

namespace Tide.Application.Actions;

/// <summary>One entry of an action's "What's happened": a comment, or a line of its history.</summary>
public sealed record FeedEntry(DateTimeOffset At, ActionComment? Comment = null, ActionEvent? Event = null)
{
    public bool IsComment => Comment is not null;
}

/// <summary>The part of the feed shown, and how many earlier entries are folded away above it.</summary>
public sealed record FeedWindow(IReadOnlyList<FeedEntry> Shown, int Earlier);

/// <summary>
/// An action's comments and history as one feed, oldest first, so the newest sits by the comment box. A recorded reply is
/// a comment already, and hand-offs and escalations are history lines, so those two lists are everything. Kept pure, so
/// it's tested on its own.
/// </summary>
public static class ActionFeed
{
    /// <summary>How many entries show before "Show N earlier".</summary>
    public const int Latest = 10;

    /// <summary>
    /// The two lists merged by time, oldest first. At the same instant the history line comes first: a recorded update
    /// saves its line ("… Their reply is kept as a comment.") and the comment together, and reads in that order.
    /// </summary>
    public static IReadOnlyList<FeedEntry> Of(IEnumerable<ActionComment> comments, IEnumerable<ActionEvent> events) =>
        events.Select(e => new FeedEntry(e.AtUtc, Event: e))
            .Concat(comments.Select(c => new FeedEntry(c.AtUtc, Comment: c)))
            .OrderBy(e => e.At).ThenBy(e => e.IsComment)
            .ToList();

    /// <summary>
    /// The latest <paramref name="latest"/> entries, or all of them with <paramref name="all"/>, and how many are left
    /// above. The comment <paramref name="keep"/> (one being edited) is never folded away, nor anything after it, so a
    /// colleague's new comment can't take an open edit off the page.
    /// </summary>
    public static FeedWindow Window(IReadOnlyList<FeedEntry> entries, bool all, Guid? keep = null, int latest = Latest)
    {
        var from = all ? 0 : Math.Max(0, entries.Count - latest);
        if (keep is { } id)
        {
            var at = entries.ToList().FindIndex(e => e.Comment?.Id == id);
            if (at >= 0) from = Math.Min(from, at);
        }
        return new FeedWindow(entries.Skip(from).ToList(), from);
    }
}
