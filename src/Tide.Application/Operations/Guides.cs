using Tide.Domain.Enums;

namespace Tide.Application.Operations;

/// <summary>
/// A walkthrough of one page (<paramref name="Page"/>, as a link from the app's root has it: "" for Today,
/// "actions/{n}" for any action's page, "articles/{id}" for any article's), started only when asked: from Help's Guides,
/// or the page's own Show me around. It needs what its page needs (<paramref name="Needs"/>), and each step what its
/// control needs. Its steps are the tour's (<see cref="TourStep"/>), all on that page. "Learn to present" is the one
/// guide that isn't a page's: it runs in present mode's rehearsal, and each of its steps' <c>Route</c> names the slide
/// it's on ("title", "metrics", "open", "article", "article:indicators" and the like, or "wrap"), and it alone has a <paramref name="Summary"/> for Help.
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
            new("people", "title", Permission.PresentBriefs, ".tide-present-here || .tide-rehearsal",
                "Who's here",
                "Once the meeting is live, the top bar shows here who's following and any hand that's up. In a rehearsal nobody joins."),
            new("rail", "title", Permission.PresentBriefs, "#present-rail || #present-rail-toggle",
                "The presenter's rail",
                "Your controls for the slide on screen are in the presenter's rail: beside the slide on a wide screen, over it on a narrow one. ] or Rail shows or hides it. Nobody following ever sees it."),
            new("numbers", "metrics", Permission.PresentBriefs, ".tide-slide-body[data-slide-key^=\"metrics:\"] h2",
                "The numbers",
                "On the team edition, the day's numbers follow: a slide for each group, against the day before."),
            new("open", "open", Permission.PresentBriefs, ".tide-slide-body[data-slide-key=\"open\"] h2 || .tide-agenda-first",
                "Actions that need review",
                "Open actions from any brief that are overdue, escalated or quiet too long get a slide before the articles, with the one thing the room can do for each."),
            new("article", "article", Permission.PresentBriefs, ".tide-slide-body[data-slide-key^=\"article:\"] h2",
                "An article's slide",
                "Each article has a slide: its bottom line, its bullets and why it matters, as the edition on screen carries them."),
            new("indicators", "article:indicators", Permission.PresentBriefs, ".tide-slide-iocsec",
                "Indicators",
                "On the team edition, the article's indicators. Select one to show them all over the slide, for the room to read."),
            new("raised", "article:raised", Permission.PresentBriefs, ".tide-reco li.is-raised .tide-reco-done",
                "The action it became",
                "A recommended line that became an action shows its number, its status and who holds it."),
            new("raise", "article", Permission.WriteArticles, ".tide-present-rail-raise .btn-primary || #rail-actions-h || #present-rail-toggle",
                "Raising an action",
                "An article's recommended lines wait in the rail. Choose a kind and a team, then Raise, and the line becomes an action the room sees on the slide."),
            new("quick", "article", Permission.WriteArticles, "#present-rail .tide-quick-add || #present-rail-toggle",
                "Quick add",
                "Work the room asks for that the article didn't recommend: a title, then Add."),
            new("note", "article", Permission.PresentBriefs, "#rail-note-mine-h || #present-rail-toggle",
                "Your note",
                "A note on the article, kept with the meeting's notes."),
            new("discussed", "article", Permission.PresentBriefs, "#present-rail .tide-present-rail-discussed || #present-rail-toggle",
                "Mark discussed",
                "Mark discussed (D) ticks the article off. The wrap-up lists those that weren't."),
            new("card", "article:card", Permission.ReadActions, ".tide-slide [data-card-opener^=\"action:\"] || .tide-slide-actions",
                "An action's card",
                "An action's number opens its card over the slide, or beside it if your organization has turned that on. Everyone following sees the card you open."),
            new("wrap", "wrap", Permission.PresentBriefs, ".tide-wrap-list h3 || .tide-wrap",
                "The wrap-up",
                "The last slide has three lists: recommended lines with no action yet, who is doing what, and the articles not marked discussed."),
            new("attendance", "wrap", Permission.PresentBriefs, ".tide-attendance || #rail-end-h || #present-rail-toggle",
                "Who attended",
                "Under Before you end, check who attended: it's filled in from who followed the meeting."),
            new("end", "wrap", Permission.PresentBriefs, "#present-end || #present-rail-toggle",
                "Ending the meeting",
                "Raise what's left, and tick an article not discussed to carry it to the next brief. End the meeting then marks the brief presented."),
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
