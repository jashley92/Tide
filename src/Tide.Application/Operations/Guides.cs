using Tide.Domain.Enums;

namespace Tide.Application.Operations;

/// <summary>
/// A short walkthrough of one page (<paramref name="Page"/>, as a link from the app's root has it: "" for Today,
/// "actions/{n}" for any action's page, "articles/{id}" for any article's), started only when asked: from Help's Guides,
/// or the page's own Show me around. It needs what its page needs (<paramref name="Needs"/>), and each step what its
/// control needs. Its steps are the tour's (<see cref="TourStep"/>), all on that page. "Learn to present" is the one
/// guide that isn't a page's: it runs in present mode's rehearsal, and each of its steps' <c>Route</c> names the slide
/// it's on ("title", "article", "open" or "wrap"), and it alone has a <paramref name="Summary"/> for Help.
/// </summary>
public sealed record Guide(string Key, string Title, string Page, Permission Needs, IReadOnlyList<TourStep> Steps, string? Summary = null)
{
    /// <summary>Whether it's "Learn to present", which runs in present mode's rehearsal.</summary>
    public bool Rehearsal => Key == Guides.Present;

    /// <summary>What Help says it shows: Learn to present's summary; a page's guide, the titles of the steps the person
    /// gets, so it never promises one they don't.</summary>
    public string Description => Summary ?? string.Join(" · ", Steps.Select(s => s.Title)) + ".";
}

/// <summary>Help's guides: one per page, for whoever can use that page, and "Learn to present" for whoever can present.</summary>
public static partial class Guides
{
    /// <summary>"Learn to present"'s key.</summary>
    public const string Present = "present";

    private static readonly Guide LearnToPresent = new(Present, "Learn to present",
        "present", Permission.PresentBriefs,
        [
            new("title", "title", Permission.PresentBriefs, ".tide-present-bottom .tide-progress",
                "The title slide",
                "The meeting opens on the title slide: the day, who presents and the articles in order. ← and → move between slides, as does the bar along the bottom."),
            new("rail", "title", Permission.PresentBriefs, "#present-rail || #present-rail-toggle",
                "The presenter's rail",
                "Your controls for the slide on screen are in the presenter's rail: beside the slide on a wide screen, over it on a narrow one. ] or Rail shows or hides it. Nobody following ever sees it."),
            new("raise", "article", Permission.WriteArticles, ".tide-present-rail-raise .btn-primary || #rail-actions-h || #present-rail-toggle",
                "Raising an action",
                "An article's recommended lines wait in the rail. Choose a kind and a team, then Raise, and the line becomes an action the room sees on the slide."),
            new("card", "article", Permission.ReadActions, ".tide-slide [data-card-opener^=\"action:\"] || .tide-slide-actions",
                "An action's card",
                "An action's number opens its card over the slide, or beside it if your organization has turned that on. Everyone following sees the card you open."),
            new("open", "open", Permission.PresentBriefs, ".tide-slide-body h2 || .tide-agenda-first",
                "Actions that need review",
                "Open actions from any brief that are overdue, escalated or quiet too long get a slide before the articles, with the one thing the room can do for each."),
            new("wrap", "wrap", Permission.PresentBriefs, ".tide-wrap",
                "The wrap-up",
                "The last slide has three lists: recommended lines with no action yet, who is doing what, and the articles not marked discussed."),
            new("end", "wrap", Permission.PresentBriefs, "#present-end || #rail-end-h || #present-rail-toggle",
                "Ending the meeting",
                "Under Before you end, raise what's left, tick an article not discussed to carry it to the next brief, and check who attended. End the meeting then marks the brief presented."),
        ],
        Summary: "Present mode in rehearsal, on today's brief: the slides, your rail, and ending the meeting. No meeting starts, and nothing is shared or saved.");

    // Each page's guide (Guides.Pages.cs), then Learn to present.
    private static IEnumerable<Guide> All => PageGuides.Append(LearnToPresent);

    /// <summary>The guides for someone who <paramref name="can"/> what they can: those for pages they can use, without any
    /// step whose control they can't, in Help's order.</summary>
    public static IReadOnlyList<Guide> For(Func<Permission, bool> can) => All.Select(g => Theirs(g, can)).OfType<Guide>().ToList();

    /// <summary>The guide <paramref name="key"/>, if it's one of theirs.</summary>
    public static Guide? Find(string key, Func<Permission, bool> can) =>
        All.FirstOrDefault(g => g.Key == key) is { } guide ? Theirs(guide, can) : null;

    /// <summary>The guide for the page open at <paramref name="page"/> (as a link from the app's root has it, without its
    /// query), if there's one of theirs. Never Learn to present: that one starts from Help.</summary>
    public static Guide? ForPage(string page, Func<Permission, bool> can) =>
        All.FirstOrDefault(g => !g.Rehearsal && Matches(g.Page, page)) is { } guide ? Theirs(guide, can) : null;

    // The guide as someone who can what they can sees it: none for a page they can't use, and without any step whose
    // control they can't; none if that leaves no step.
    private static Guide? Theirs(Guide guide, Func<Permission, bool> can)
    {
        if (!can(guide.Needs)) return null;
        var steps = guide.Steps.Where(s => can(s.Needs)).ToList();
        return steps.Count > 0 ? guide with { Steps = steps } : null;
    }

    /// <summary>Whether <paramref name="open"/> is the page <paramref name="page"/> names: the same path, ignoring case,
    /// where "{n}" stands for a number and "{id}" for a GUID.</summary>
    public static bool Matches(string page, string open)
    {
        var want = page.Split('/');
        var got = open.Split('/');
        if (want.Length != got.Length) return false;
        for (var i = 0; i < want.Length; i++)
        {
            var ok = want[i] switch
            {
                "{n}" => int.TryParse(got[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _),
                "{id}" => Guid.TryParse(got[i], out _),
                _ => string.Equals(want[i], got[i], StringComparison.OrdinalIgnoreCase),
            };
            if (!ok) return false;
        }
        return true;
    }
}
