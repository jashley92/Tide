namespace Tide.Web.Components.Layout;

/// <summary>
/// One keyboard shortcut: the keys (any of them does it), what it does, and whether it's a single character that the
/// letter-shortcut switch turns off (WCAG 2.1.4).
/// </summary>
public sealed record Shortcut(IReadOnlyList<string> Keys, string What, bool Letter = false);

/// <summary>The shortcuts of one page or place, under a heading.</summary>
public sealed record ShortcutGroup(string Title, IReadOnlyList<Shortcut> Keys);

/// <summary>
/// The keyboard shortcuts the "?" overlay lists (ShortcutsOverlay): those of the page you're on, then those that work
/// everywhere. They describe the handlers in wwwroot/js (palette.js, theme.js, rows.js (the actions list's too), triage.js, library.js, present.js, chart.js, editor.js, submitkeys.js) and the dialogs'
/// own keys, so a change to one of those changes this too.
/// </summary>
public static class Shortcuts
{
    public static readonly ShortcutGroup Everywhere = new("Everywhere",
    [
        new(["Ctrl K", "⌘ K"], "Go to a page, article or action"),
        new(["?"], "This list of shortcuts", Letter: true),
        new(["Ctrl Enter", "⌘ Enter"], "Post or save the text box you're in"),
        new(["Esc"], "Close a dialog or menu"),
        new(["Alt Z", "⌥ Z"], "Undo, while a toast offers it (the newest)"),
    ]);

    public static readonly ShortcutGroup Palette = new("In the Go to box",
    [
        new(["↓", "↑"], "Next and previous result"),
        new(["Enter"], "Open the result"),
        new(["Esc"], "Close, back where you were"),
    ]);

    /// <summary>"]" (theme.js): listed among Everywhere's on a page that has a context rail, where it does something.</summary>
    public static readonly Shortcut RailKey = new(["]"], "Show or hide the context rail", Letter: true);

    /// <summary>The groups for the page at <paramref name="relativePath"/> (relative to the app's base), then the global ones,
    /// with "]" among them when the page has a context <paramref name="rail"/>. The Actions list's T is listed only for
    /// someone who <paramref name="canTake"/> actions on.</summary>
    public static IReadOnlyList<ShortcutGroup> For(string relativePath, bool rail = false, bool canTake = true)
    {
        var path = relativePath.Split('?', '#')[0].Trim('/');
        var segments = path.Split('/');
        var groups = new List<ShortcutGroup>();
        switch (segments[0].ToLowerInvariant())
        {
            case "triage":
                groups.Add(Triage);
                break;
            case "present":
                groups.Add(Present);
                break;
            // A tab that lists entries (the first, when the address names none).
            case LibraryTabs.Root when LibraryTabs.All.FirstOrDefault(t => segments.Length == 1 || t.Key.Equals(segments[1], StringComparison.OrdinalIgnoreCase)) is { Topic: not null }:
                groups.Add(Library);
                break;
            // The list or the board, as the address says (it always names its layout once the page has drawn).
            case "actions" when segments.Length == 1:
                var group = !relativePath.Contains("layout=list", StringComparison.OrdinalIgnoreCase) ? Board : ActionsList;
                groups.Add(canTake ? group : group with { Keys = [.. group.Keys.Where(k => k != TakeKey)] });
                break;
            case "actions" when int.TryParse(segments[1], out _):
                groups.Add(Screenshots);
                break;
            case "articles" when segments.Length > 1:
                groups.Add(ArticlePage);
                groups.Add(Screenshots);
                break;
            case "trends" or "metrics":
                groups.Add(Charts);
                break;
        }
        // After "?", as the two single keys that work on every page that has what they act on.
        groups.Add(rail ? Everywhere with { Keys = [.. Everywhere.Keys.Take(2), RailKey, .. Everywhere.Keys.Skip(2)] } : Everywhere);
        groups.Add(Palette);
        return groups;
    }

    private static readonly ShortcutGroup Triage = new("Triage",
    [
        new(["↓", "↑"], "Next and previous item"),
        new(["J"], "Next item, on to the next page at the end", Letter: true),
        new(["K"], "Previous item, back a page at the start", Letter: true),
        new(["S"], "Shortlist", Letter: true),
        new(["X"], "Dismiss", Letter: true),
        new(["D"], "Mark as a duplicate of the similar article", Letter: true),
        new(["P"], "Promote to an article", Letter: true),
        new(["Esc"], "Close the promote form or the reading pane"),
        new(["O"], "Open the source in a new tab", Letter: true),
        new(["V"], "Read it here (fetched from its site only when nothing is kept); pressed again, close the reading pane", Letter: true),
        new(["R"], "Put back in New", Letter: true),
        new(["Enter", "→"], "On a group: open it"),
        new(["←"], "Close the group"),
    ]);

    private static readonly ShortcutGroup Library = new("Library",
    [
        new(["J"], "Next entry in the list", Letter: true),
        new(["K"], "Previous entry", Letter: true),
        new(["↓", "↑"], "With the list focused: next and previous entry"),
    ]);

    private static readonly ShortcutGroup Present = new("Present mode",
    [
        new(["→", "Enter"], "Next slide"),
        new(["←", "Backspace"], "Previous slide"),
        new(["Space", "Page Down"], "Scroll a long slide down; at its end, the next slide"),
        new(["Shift Space", "Page Up"], "Scroll a long slide up; at its top, the previous slide"),
        new(["Home", "End"], "First and last slide"),
        new(["D"], "Mark the article discussed, or not", Letter: true),
        new(["F"], "Full screen, or leave it", Letter: true),
        new(["Esc"], "Leave full screen, then close a zoomed screenshot"),
    ]);

    private static readonly Shortcut TakeKey = new(["T"], "Take the selected action on, when it's yours to take", Letter: true);

    private static readonly ShortcutGroup ActionsList = new("Actions list",
    [
        new(["↓", "↑"], "Next and previous action"),
        new(["J"], "Next action, on to the next page at the end", Letter: true),
        new(["K"], "Previous action, back a page at the start", Letter: true),
        TakeKey,
        new(["Enter"], "Open the selected action"),
        new(["Tab", "Enter"], "A row's status opens the statuses it can move to"),
    ]);

    private static readonly ShortcutGroup Board = new("Actions board",
    [
        new(["↓", "↑"], "Next and previous card, lane after lane"),
        new(["J"], "Next card, on to the next lane at the end of one", Letter: true),
        new(["K"], "Previous card", Letter: true),
        new(["←", "→"], "On a card: the card beside it, in the next lane with cards"),
        TakeKey,
        new(["Enter"], "Open the selected card"),
        new(["Tab", "Enter"], "A card's Move button opens the columns it can go to; Enter on one moves it"),
        new(["Esc"], "Close the Move menu"),
    ]);

    private static readonly ShortcutGroup ArticlePage = new("Article",
    [
        new(["1"], "Preview the Team edition", Letter: true),
        new(["2"], "Preview the Company-wide edition", Letter: true),
        new(["J"], "On today's brief: the next article", Letter: true),
        new(["K"], "On today's brief: the previous article", Letter: true),
        new(["Enter"], "In the new-indicator box: add the indicator"),
        new(["Esc"], "Close the ATT&CK picker; Tab stays inside it while it's open"),
    ]);

    private static readonly ShortcutGroup Screenshots = new("A screenshot shown full size",
    [
        new(["←", "→"], "Previous and next screenshot"),
        new(["Esc"], "Close it"),
    ]);

    private static readonly ShortcutGroup Charts = new("Charts",
    [
        new(["←", "↓"], "With a chart focused: the previous point"),
        new(["→", "↑"], "The next point"),
        new(["Home", "End"], "The first and last point"),
        new(["Esc"], "Hide the reading"),
    ]);
}
