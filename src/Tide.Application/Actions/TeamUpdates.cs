using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>
/// What a team said about its action outside TIDE (a reply by email), recorded in one step by whoever works it: the
/// reply as they wrote it, the status it's at now (an outcome to close it), a new due date and why, and a short note.
/// <see cref="From"/> is the status the page showed when the dialog opened, so a move made meanwhile isn't overwritten.
/// </summary>
public sealed class TeamUpdate
{
    public Guid ActionId { get; set; }
    public ActionStatus From { get; set; }
    public ActionStatus Status { get; set; }
    public ActionOutcome Outcome { get; set; }

    /// <summary>The reply, pasted; kept on the action as a comment quoting it.</summary>
    public string? Reply { get; set; }

    /// <summary>A new due date, or null to leave it; moving it needs <see cref="DueReason"/>.</summary>
    public DateOnly? DueDate { get; set; }
    public string? DueReason { get; set; }

    /// <summary>The analyst's short note: the history line's text, and the outcome's note when it closes.</summary>
    public string? Note { get; set; }

    /// <summary>Why it goes ahead over the team's active limit (a team that warns asks for one).</summary>
    public string? OverLimitReason { get; set; }
}

/// <summary>How a recorded update reads, kept pure so it's tested on its own.</summary>
public static class TeamUpdates
{
    /// <summary>The longest reply kept: it goes in a comment, with a line saying whose it is.</summary>
    public const int MaxReply = 7000;

    /// <summary>The longest note and due-date reason.</summary>
    public const int MaxNote = 500;

    /// <summary>
    /// The comment that keeps a reply: whose it is, then the reply quoted line by line, so it reads as theirs. A line the
    /// next one continues ends in a Markdown hard break, so an email's lines stay lines rather than running together.
    /// </summary>
    public static string ReplyComment(string team, string reply)
    {
        var lines = reply.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        var quoted = lines.Select((l, i) => l.Length == 0 ? ">" : "> " + l + (i + 1 < lines.Count && lines[i + 1].Length > 0 ? "  " : ""));
        return $"**Reply from {team}**, recorded by email:\n\n" + string.Join("\n", quoted);
    }

    /// <summary>
    /// The start of the update's history line: "Update from Network Engineering: patched the edge. Due: Fri Nov 20 → Fri
    /// Dec 4 (change window moved)." A status change follows it on the same line. <paramref name="note"/> is left out when
    /// the update closes the action, since the status change carries it as the outcome's note.
    /// </summary>
    public static string Line(string team, string? note, (DateOnly? Was, DateOnly Now, string Why)? due, bool replied)
    {
        var text = $"Update from {team}" + (note is null ? "" : $": {note.TrimEnd('.')}") + ".";
        if (due is var (was, now, why)) text += $" Due: {(was is { } w ? ActionService.Day(w) : "none")} → {ActionService.Day(now)} ({why}).";
        if (replied) text += " Their reply is kept as a comment.";
        return text;
    }
}
