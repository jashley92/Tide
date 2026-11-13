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
    /// <summary>A guide across pages (An analyst's day, Following the brief): each step's <c>Route</c> is its page, and it
    /// has no page's Show me around.</summary>
    public bool Travels { get; init; }

    /// <summary>Whom it isn't for: someone who can this has the fuller guide instead (Following the brief is for those
    /// who can't write).</summary>
    public Permission? NotFor { get; init; }

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

    /// <summary>"An analyst's day"'s key, and "Following the brief"'s.</summary>
    public const string Day = "day", Following = "following";

    // A cross-page step: on `route` (Today is "", a real article's "articles/{id}", an action's "actions/{n}", Search's
    // "search", opened on a lookup). A control a page may not draw names a fallback that's always there (its heading).
    private static TourStep On(string route, string key, Permission needs, string target, string title, string text) =>
        new(key, route, needs, target, title, text);

    private const string TodayHead = ".tide-page-head h1";

    // The whole workflow in the order an analyst works it, across pages; each stop rings its real control.
    private static readonly Guide AnalystsDay = new(Day, "An analyst's day", "", Permission.WriteArticles,
        [
            On("", "today", Permission.WriteArticles, "ol.tide-steps || " + TodayHead, "1. Today",
                "The day starts on Today: the brief's steps show where it stands, and the main button does what's next."),
            On("", "due", Permission.WorkActions, "#my-actions-h || " + TodayHead, "What's due",
                "Your actions with their due dates; in the rail, your teams' queue and the reviews asked of you."),
            On("triage", "pull", Permission.WriteArticles, ".tide-tabbar[aria-label=\"Inbox\"] .tide-tabbar-apart || .tide-tabbar[aria-label=\"Inbox\"]",
                "2. Triage: the feeds", "New reports come in from the feeds. Sources, or Pull all on Today, pulls them now; nothing is pulled unless you ask."),
            On("triage", "read", Permission.WriteArticles, ".tide-reading-tools button || li.tide-triage-row.selected || ol.tide-triage", "Reading",
                "J and K move through the items. Read it here (V) reads one in the pane, only when you ask."),
            On("triage", "decide", Permission.WriteArticles, "section.tide-triage-decide || .tide-triage-layout", "Deciding",
                "Promote makes an item an article, Attach adds it to the one that covers it, and Dismiss puts it away with a reason."),
            On("articles/{id}", "write", Permission.WriteArticles, ".tide-page-head-main input[aria-label=\"Title\"] || .tide-page-head-main",
                "3. Writing the article", "The title and assessment, then the bottom line, bullets and why it matters. The editor's own guide walks every part."),
            On("articles/{id}", "review", Permission.WriteArticles, "#review-who || nav.tide-section-pills", "Asking for a review",
                "Choose a colleague and what to look at, then Ask for review."),
            On("articles/{id}", "ready", Permission.WriteArticles, ".tide-sticky-save .btn-primary || .tide-sticky-save", "Marking it ready",
                "Save and mark ready says it can go on a brief; Add to today's brief puts it in the running order."),
            On("", "order", Permission.WriteArticles, "#running-order-h || " + TodayHead, "4. Back on Today: the order",
                "The brief's articles in the order they're read, each with what it still needs. Drag a row, or use its arrows."),
            On("", "check", Permission.WriteArticles, "#edition-check-h || #tide-rail-toggle", "The edition check",
                "In the rail: what each edition will carry. A row's Set to AMBER or Keep it, in the running order, fixes what reaches everyone."),
            On("", "briefready", Permission.WriteArticles, "ol.tide-steps .tide-step-btn[title^=\"Mark the brief ready\"] || .tide-brief-head .btn-primary || " + TodayHead,
                "The brief, ready", "Order marks the brief ready once its articles are; so does the main button."),
            On("", "present", Permission.PresentBriefs, "ol.tide-steps a[href^=\"present/\"] || .tide-brief-head a[href^=\"present/\"] || " + TodayHead,
                "5. Presenting", "Present opens present mode for the meeting. Help's Learn to present rehearses it first, with nothing shared or saved."),
            On("", "send", Permission.PresentBriefs, "ol.tide-steps button.tide-step-btn[title^=\"Send a test copy\"] || " + TodayHead,
                "6. Sending the editions", "After the meeting, Send emails each edition, with a test copy to you first."),
            On("actions", "actions", Permission.ReadActions, ".tide-actions-views", "7. Following up",
                "What the meeting raised: Mine and My teams list it, each with its due date."),
            On("actions/{n}", "work", Permission.WorkActions, ".tide-action-do [data-primary] || .tide-action-do .btn-primary || .tide-action-head h1",
                "Working an action", "On an action's page, the main button does what's next, such as Take it, Start work or Close."),
            On("search", "search", Permission.ReadTeamEdition, "#search-q", "8. When something comes up",
                "Search: paste an indicator or a CVE to see every article and action that names it."),
        ],
        Summary: "The whole workflow across pages: Today, triage, writing, the running order, presenting, sending, actions and Search.")
    { Travels = true };

    // For whoever follows the brief rather than writing it.
    private static readonly Guide FollowingTheBrief = new(Following, "Following the brief", "", Permission.ReadTeamEdition,
        [
            On("", "today", Permission.ReadTeamEdition, TodayHead, "Today", "Today shows the day's brief and where it stands."),
            On("", "brief", Permission.ReadTeamEdition, "#todays-brief-h || .tide-page-head", "The brief",
                "Its articles, in order. Read it opens the edition you get."),
            On("", "meeting", Permission.ReadTeamEdition, "#meeting-h || #tide-rail-toggle", "The meeting",
                "When it meets and who presents. While it's live, Join in the top bar follows it."),
            On("actions", "actions", Permission.ReadActions, ".tide-actions-views", "Actions",
                "Mine lists the actions you hold, and My teams your teams'."),
            On("search", "search", Permission.ReadTeamEdition, "#search-q", "Search",
                "Paste an indicator or a CVE to see every article and action that names it."),
        ],
        Summary: "Today, the meeting, your actions and Search, across pages.")
    { Travels = true, NotFor = Permission.WriteArticles };

    /// <summary>The whole-day guide for someone who <paramref name="can"/> what they can: An analyst's day, else Following
    /// the brief.</summary>
    public static Guide? WholeDay(Func<Permission, bool> can) => Find(Day, can) ?? Find(Following, can);

    // The guides across pages, each page's guide (Guides.Pages.cs), then Learn to present.
    private static IEnumerable<Guide> All => new[] { AnalystsDay, FollowingTheBrief }.Concat(PageGuides).Append(LearnToPresent);

    /// <summary>The guides for someone who <paramref name="can"/> what they can: those for pages they can use, without any
    /// step whose control they can't, in Help's order.</summary>
    public static IReadOnlyList<Guide> For(Func<Permission, bool> can) => All.Select(g => Theirs(g, can)).OfType<Guide>().ToList();

    /// <summary>The guide <paramref name="key"/>, if it's one of theirs.</summary>
    public static Guide? Find(string key, Func<Permission, bool> can) =>
        All.FirstOrDefault(g => g.Key == key) is { } guide ? Theirs(guide, can) : null;

    /// <summary>The guide for the page open at <paramref name="page"/> (as a link from the app's root has it, without its
    /// query), if there's one of theirs. Never Learn to present: that one starts from Help.</summary>
    public static Guide? ForPage(string page, Func<Permission, bool> can) =>
        All.FirstOrDefault(g => !g.Rehearsal && !g.Travels && Matches(g.Page, page)) is { } guide ? Theirs(guide, can) : null;

    // The guide as someone who can what they can sees it: none for a page they can't use, and without any step whose
    // control they can't; none if that leaves no step.
    private static Guide? Theirs(Guide guide, Func<Permission, bool> can)
    {
        if (!can(guide.Needs) || guide.NotFor is { } not && can(not)) return null;
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
