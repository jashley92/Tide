using Markdig;
using Tide.Application.Content;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>
/// An article's "Recommended actions" as lines the meeting can raise one at a time: each line becomes an action's title,
/// with a kind guessed from its wording (both stay editable). The field is free Markdown, so this is a heuristic: one
/// line per non-blank line, list markers and formatting dropped.
/// </summary>
public static class RecommendedActions
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    /// <summary>The lines of <paramref name="markdown"/> as action titles: plain text, at most an action title's length.</summary>
    public static IReadOnlyList<string> Lines(string? markdown) => MarkdownService.BulletLines(markdown)
        .Select(l => MarkdownService.OneLine(Markdown.ToPlainText(l, Pipeline)))
        .Where(l => l.Length > 0)
        .Select(l => l.Length > ActionItem.MaxTitle ? l[..ActionItem.MaxTitle].TrimEnd() : l)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Whether an action titled <paramref name="title"/> is the one raised for <paramref name="line"/>.</summary>
    /// <summary>The lines of <paramref name="markdown"/> that none of <paramref name="titles"/> raised (<see cref="Raised"/>):
    /// the meeting slide's Raise rows and the article page's "not raised yet" count, alike.</summary>
    public static IReadOnlyList<string> Unraised(string? markdown, IEnumerable<string> titles)
    {
        var raised = titles.ToList();
        return Lines(markdown).Where(l => !raised.Any(t => Raised(l, t))).ToList();
    }

    public static bool Raised(string line, string title) =>
        string.Equals(MarkdownService.OneLine(line), MarkdownService.OneLine(title), StringComparison.OrdinalIgnoreCase);

    // The words that say what kind of action a line is, in the order a tie is settled. A word matches as the start of a
    // word in the line ("patch" matches "patched", "user" matches "users", "notif" both "notify" and "notification");
    // "search logs" needs both words, in order.
    private static readonly (string[] Words, ActionKind Kind)[] Cues =
    [
        (["patch", "upgrade", "update", "fix", "hotfix", "apply"], ActionKind.PatchRemediate),
        (["hunt", "search log"], ActionKind.ThreatHunt),
        (["sweep", "ioc", "indicator"], ActionKind.IocSweep),
        (["block"], ActionKind.Block),
        (["config", "disable", "enforce"], ActionKind.ConfigChange),
        (["notif", "awareness", "user"], ActionKind.Awareness),
        (["vendor", "ask", "isac", "partner", "reach out"], ActionKind.Outreach),
    ];

    /// <summary>
    /// The kind a recommended line most likely is, from its wording: the cue that comes first in the line wins ("Block
    /// the indicators" is a block, "Sweep for the indicators" a sweep), and a line with none is Other.
    /// </summary>
    public static ActionKind GuessKind(string? line)
    {
        var words = Words(line);
        (int At, ActionKind Kind)? best = null;
        foreach (var (cues, kind) in Cues)
        {
            foreach (var cue in cues)
            {
                var at = Find(words, cue.Split(' '));
                if (at >= 0 && (best is not { } b || at < b.At)) best = (at, kind);
            }
        }
        return best?.Kind ?? ActionKind.Other;
    }

    private static List<string> Words(string? text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var c in (text ?? "").ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) current.Append(c);
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    // Where the cue's words start in the line, each matching the start of consecutive words, or -1.
    private static int Find(List<string> words, string[] cue)
    {
        for (var i = 0; i + cue.Length <= words.Count; i++)
        {
            var all = true;
            for (var j = 0; j < cue.Length && all; j++) all = words[i + j].StartsWith(cue[j], StringComparison.Ordinal);
            if (all) return i;
        }
        return -1;
    }
}
