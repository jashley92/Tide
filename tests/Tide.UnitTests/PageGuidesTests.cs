using AwesomeAssertions;
using Tide.Application.Operations;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>Each page's guide: for whoever can use its page, a full walk through a page work is done on, three to five
/// steps on the others, none on a control they can't use.</summary>
public class PageGuidesTests
{
    private static Func<Permission, bool> Can(params AppRole[] roles) => RoleDefinitions.PermissionsFor(roles).Contains;

    private static string[] Keys(params AppRole[] roles) =>
        Guides.For(Can(roles)).Where(g => !g.Rehearsal && !g.Travels).Select(g => g.Key).ToArray();

    private static readonly string[] Everyones =
        ["today", "actions", "action", "metrics", "trends", "stories", "adversaries", "library", "stack", "search", "workload", "rotation"];

    [Fact]
    public void AnAnalyst_GetsAGuideForEveryPage()
    {
        Keys(AppRole.Analyst).Should().BeEquivalentTo([.. Everyones, "triage", "article", "coverage"]);
        Keys(AppRole.Admin).Should().BeEquivalentTo(Keys(AppRole.Analyst));
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.ActionOwner)]
    public void AViewerOrActionOwner_GetsOnlyThePagesTheyCanUse(AppRole role)
    {
        Keys(role).Should().BeEquivalentTo(Everyones, "Triage, the article editor and Coverage need WriteArticles");
    }

    // The pages work is done on get a full walkthrough; the reference pages a short one.
    private static readonly string[] Walkthroughs = ["today", "triage", "article", "actions", "action"];

    [Fact]
    public void EachWorkPagesGuide_WalksThePage_AndEachOtherIsThreeToFiveSteps_ForAnAnalyst()
    {
        foreach (var g in Guides.For(Can(AppRole.Analyst)).Where(g => !g.Rehearsal))
        {
            if (Walkthroughs.Contains(g.Key) || g.Travels) g.Steps.Count.Should().BeGreaterThanOrEqualTo(9, g.Key);
            else g.Steps.Count.Should().BeInRange(3, 5, g.Key);
            g.Steps.Should().OnlyContain(s => s.Target != null, g.Key);
            g.Steps.Select(s => s.Key).Should().OnlyHaveUniqueItems(g.Key);
        }
        Guides.For(Can(AppRole.Analyst)).Select(g => g.Key).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.ActionOwner)]
    public void EveryGuide_HasAtLeastTwoSteps_ForSomeoneWhoCantWrite(AppRole role)
    {
        foreach (var g in Guides.For(Can(role))) g.Steps.Count.Should().BeGreaterThanOrEqualTo(2, g.Key);
    }

    [Fact]
    public void AViewersSteps_NeverNameWhatOnlyWritersDo()
    {
        string[] writers = ["Take it", "Add a ", "Swap or get cover", "Promote", "Raise", "Start an outreach"];
        foreach (var g in Guides.For(Can(AppRole.Viewer)))
            g.Steps.Should().OnlyContain(s => !writers.Any(w => s.Text.Contains(w, StringComparison.Ordinal)), g.Key);
    }

    [Theory]
    [InlineData("", "today")]
    [InlineData("triage", "triage")]
    [InlineData("articles/3f2504e0-4f89-11d3-9a0c-0305e82c3301", "article")]
    [InlineData("actions", "actions")]
    [InlineData("actions/12", "action")]
    [InlineData("metrics", "metrics")]
    [InlineData("search", "search")]
    [InlineData("rotation", "rotation")]
    public void APage_HasItsGuide(string page, string key) => Guides.ForPage(page, Can(AppRole.Analyst))!.Key.Should().Be(key);

    [Fact]
    public void EachStep_RingsItsOwnControl_NeverAnotherStepsByFallingBack()
    {
        // A step's fallbacks may be its own control drawn another way, or the context rail (or its button where it's
        // shut), never what another step is about: that step's text would be ringing the wrong control.
        string[] rail = ["#tide-rail", "#tide-rail-toggle", "#present-rail", "#present-rail-toggle"];
        foreach (var g in Guides.For(Can(AppRole.Admin)))
        {
            var primaries = g.Steps.Select(s => Choices(s.Target)[0]).ToList();
            foreach (var s in g.Steps)
            {
                var own = Choices(s.Target)[0];
                Choices(s.Target).Skip(1).Where(c => !rail.Contains(c))
                    .Should().NotIntersectWith(primaries.Where(p => p != own), $"{g.Key}/{s.Key} falls back to another step's control");
            }
        }
    }

    private static string[] Choices(string? target) =>
        (target ?? "").Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void OurStacksGuide_NamesTriageOnlyToThoseWhoSeeIt()
    {
        Guides.Find("stack", Can(AppRole.Viewer))!.Steps.Should().OnlyContain(s => !s.Text.Contains("triage", StringComparison.OrdinalIgnoreCase),
            "a Viewer's Right now has no triage counts");
        Guides.Find("stack", Can(AppRole.Analyst))!.Steps.Should().Contain(s => s.Text.Contains("triage", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.ActionOwner)]
    public void AGuidesDescription_IsMadeOfTheStepsThePersonGets(AppRole role)
    {
        var today = Guides.Find("today", Can(role))!;
        today.Description.Should().Contain("Today's brief").And.NotContain("running order").And.NotContain("Edition check");
        Guides.Find("today", Can(AppRole.Analyst))!.Description.Should().Contain("running order");
    }

    [Fact]
    public void SearchsGuide_ShowsTheExports() =>
        Guides.Find("search", Can(AppRole.Viewer))!.Steps.Should().Contain(s => s.Text.Contains("STIX 2.1", StringComparison.Ordinal));

    [Fact]
    public void TheEditorsGuide_WalksTheWholeForm_InThePagesOrder_FromItsTitle()
    {
        var steps = Guides.Find("article", Can(AppRole.Analyst))!.Steps;
        steps.Select(s => s.Key).Should().Equal("title", "review", "pills", "class", "prio", "tlp", "source", "addlink", "linkform", "draft",
            "fetch", "drafting", "bluf", "bullets", "shots", "why", "whycheck", "werun", "rec", "indicators", "find", "tags", "browse", "attack",
            "atkfilter", "atktactic", "atkcell", "atkapply", "adversary", "preview", "edition", "raise", "details", "saved", "save", "ready", "brief");
        steps.Single(s => s.Key == "tlp").Target.Should().Be("#f-tlp", "the article's TLP, never the priority beside it");
        steps.Single(s => s.Key == "save").Target.Should().Be("[data-guide=\"save\"]");
        steps.Single(s => s.Key == "ready").Target.Should().Be("[data-guide=\"mark-ready\"]", "Save and mark ready is its own step");
        steps.Single(s => s.Key == "whycheck").Text.Should().Contain("Set to AMBER", "it rings that button, so names only it");
        steps.Single(s => s.Key == "bullets").Text.Should().Contain("grows");
        steps.Single(s => s.Key == "raise").Opens.Should().Be("#reader-panel-tab-actions", "its control is under the rail's Actions tab");
    }

    [Fact]
    public void AStepInAPicker_OpensIt_AndAPickerItOpensHasItsOwnClose()
    {
        // The ATT&CK picker's steps open it and close it by its own Close, which keeps nothing ticked; a section, menu or
        // tab the guide opens closes by its own button, which says whether it's open (aria-expanded).
        var attack = Guides.Find("article", Can(AppRole.Analyst))!.Steps.Where(s => s.Key.StartsWith("atk", StringComparison.Ordinal)).ToList();
        attack.Should().HaveCount(4).And.OnlyContain(s => s.Opens == "#f-techniques" && s.Closes == ".tide-atkm-panel .btn-close");
        Guides.Find("article", Can(AppRole.Analyst))!.Steps.Single(s => s.Key == "linkform").Closes.Should().NotBeNull("Add a report link has no open state");
        Guides.Find("triage", Can(AppRole.Analyst))!.Steps.Single(s => s.Key == "promote").Closes.Should().NotBeNull("Promote opens a form with its own Cancel");
    }

    [Theory]
    [InlineData("today", new[] { "head", "chip", "next", "steps", "feeds", "order", "fix", "move", "add", "note", "mine", "recent", "numbers",
        "brief", "check", "queue", "reviews", "meeting", "coming" })]
    [InlineData("triage", new[] { "tabs", "hand", "filter", "pills", "sort", "missed", "groups", "list", "group", "covered", "read", "decide",
        "promote", "cves", "railcovered", "same", "named", "sources" })]
    [InlineData("actions", new[] { "new", "views", "saveview", "layout", "search", "pills", "filter", "rows", "lanes", "move", "closed", "rail",
        "take", "moveto", "reopen", "open" })]
    [InlineData("action", new[] { "title", "badges", "status", "do", "more", "next", "checklist", "hunt", "outreach", "feed", "comment", "facts",
        "article", "files" })]
    public void AWorkPagesGuide_WalksItInThePagesOrder(string key, string[] keys) =>
        Guides.Find(key, Can(AppRole.Admin))!.Steps.Select(s => s.Key).Should().Equal(keys);

    [Fact]
    public void AViewersWorkPageGuides_HaveOnlyStepsForWhatTheyCanUse()
    {
        Guides.Find("today", Can(AppRole.Viewer))!.Steps.Select(s => s.Key).Should().Equal("head", "brief", "meeting", "coming");
        Guides.Find("actions", Can(AppRole.Viewer))!.Steps.Select(s => s.Key).Should().NotContain(["new", "take", "move", "moveto", "reopen"]);
        Guides.Find("action", Can(AppRole.Viewer))!.Steps.Select(s => s.Key).Should().NotContain(["status", "do", "more", "comment"]);
    }

    [Fact]
    public void AnAnalystsDay_WalksTheWorkflowAcrossPages_WithEachPagesGuideInline()
    {
        var day = Guides.WholeDay(Can(AppRole.Analyst))!;
        day.Key.Should().Be(Guides.Day);
        day.Travels.Should().BeTrue();
        // Today's own steps frame the pages' guides: Triage's, the editor's, presenting, Actions'.
        string Frame(TourStep s) => s.Section is { } section ? "[" + section + "]" : s.Key;
        day.Steps.Select(Frame).Distinct().Should().Equal("strip", "due", "[triage]", "[article]", "save", "review", "ready", "brief", "order",
            "fix", "check", "briefready", "[present]", "send", "[actions]", "work", "search");
        foreach (var page in new[] { "triage", "actions" })
            day.Steps.Where(s => s.Section == page).Select(s => s.Key)
                .Should().Equal(Guides.Find(page, Can(AppRole.Analyst))!.Steps.Select(s => page + "." + s.Key), $"{page}'s whole guide, inline");
        day.Steps.Where(s => s.Section == "article").Should().HaveCount(Guides.Find("article", Can(AppRole.Analyst))!.Steps.Count - 5,
            "the editor's, but for saving, review, ready and the brief, which follow it as steps of their own in the order they're done");
        day.Steps.Select(s => s.Key).Should().OnlyHaveUniqueItems();
        Guides.ForPage("", Can(AppRole.Analyst))!.Key.Should().Be("today", "Today's Show me around is Today's own guide");
    }

    [Fact]
    public void NoStepsTitle_IsNumbered_AndEachSavingStepRingsItsOwnButton()
    {
        foreach (var g in Guides.For(Can(AppRole.Admin)))
            g.Steps.Should().OnlyContain(s => !System.Text.RegularExpressions.Regex.IsMatch(s.Title, @"^\d+\."), g.Key);
        var day = Guides.Find(Guides.Day, Can(AppRole.Admin))!.Steps;
        day.Single(s => s.Key == "briefready").Target.Should().StartWith("[data-guide=\"brief-mark-ready\"]", "Mark ready in the page head");
        day.Single(s => s.Key == Guides.Presenting).Target.Should().Be("[data-guide=\"brief-present\"]", "Present in the page head");
        day.Single(s => s.Key == "send").Target.Should().StartWith("[data-guide=\"brief-send\"]");
        day.Single(s => s.Key == "fix").Target.Should().Contain("tide-need-fix", "the row's Set to AMBER, a step before the rail's edition check");
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.ActionOwner)]
    public void SomeoneWhoFollowsTheBrief_GetsFollowingTheBrief_NotTheAnalystsDay(AppRole role)
    {
        var day = Guides.WholeDay(Can(role))!;
        day.Key.Should().Be(Guides.Following);
        day.Steps.Select(s => s.Key).Should().Equal("today", "brief", "meeting", "actions", "search");
        Guides.Find(Guides.Day, Can(role)).Should().BeNull();
        Guides.Find(Guides.Following, Can(AppRole.Analyst)).Should().BeNull("an analyst has the whole day instead");
    }

    [Fact]
    public void TriagesGuide_SaysNothingIsFetchedOrPulledUnasked()
    {
        var steps = Guides.Find("triage", Can(AppRole.Analyst))!.Steps;
        steps.Single(s => s.Key == "read").Text.Should().Contain("nothing is fetched by itself");
        steps.Single(s => s.Key == "sources").Text.Should().Contain("nothing is pulled unless you ask");
    }
}
