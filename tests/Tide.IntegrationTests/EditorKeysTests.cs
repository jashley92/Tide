using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Shared;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>
/// The article editor's keys (editor.js, ArticleEditorPanel.OnEditorKey): G then a letter to a section's first field, N
/// to the next readiness gap in the order the page runs, and Ctrl+Shift+Enter to save, mark ready and go on to the next
/// article on today's brief; and their place in the "?" list.
/// </summary>
public partial class EditorKeysTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<Guid> NewArticle(string title, Action<Article>? fill = null)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var a = new Article { Title = title, AuthorId = "x", AuthorName = "X", Classification = Classification.Informational };
        fill?.Invoke(a);
        db.Articles.Add(a);
        await db.SaveChangesAsync();
        return a.Id;
    }

    // Nothing blocking: it can be marked ready.
    private static void Readyable(Article a)
    {
        a.Bluf = "Exploited in the wild.";
        a.Bullets.Add(new ArticleBullet { Text = "Patch is out." });
    }

    private async Task<IReadOnlyList<BriefStop>> OnTodaysBrief(params Guid[] ids)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var brief = await briefs.GetOrStartAsync(briefs.CurrentDate);
        foreach (var id in ids) await briefs.AddArticleAsync(brief.Id, id);
        return await briefs.RunningOrderAsync(briefs.CurrentDate);
    }

    private async Task<ArticleStatus> StatusOf(Guid id)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<ArticleService>().GetAsync(id))!.Status;
    }

    private Task<ComponentHarness.Rendered<ArticleEditorPanel>> Editor(Guid id, Func<Guid, string>? stepHref = null)
    {
        var values = new Dictionary<string, object?> { ["Id"] = id };
        if (stepHref is not null) values["StepHref"] = stepHref;
        return ComponentHarness.RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", values);
    }

    private static string[] Told(ComponentHarness.Rendered<ArticleEditorPanel> r) =>
        [.. r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Select(t => t.Message)];

    // ---- N: the next gap ----

    [Fact]
    public void NextGap_GoesOnInTheOrderThePageRuns_RoundToTheFirst()
    {
        // Screenshots runs after Recommended on the page, not after Bullets as the enum has it.
        ArticlePart[] gaps = [ArticlePart.Tags, ArticlePart.Screenshots, ArticlePart.BottomLine, ArticlePart.Recommended];

        EditorKeys.NextGap(gaps, null).Should().Be(ArticlePart.BottomLine, "from no section: the first from the top");
        EditorKeys.NextGap(gaps, ArticlePart.Assessment).Should().Be(ArticlePart.BottomLine);
        EditorKeys.NextGap(gaps, ArticlePart.BottomLine).Should().Be(ArticlePart.Recommended, "the one after the section focus is in");
        EditorKeys.NextGap(gaps, ArticlePart.WhyItMatters).Should().Be(ArticlePart.Recommended);
        EditorKeys.NextGap(gaps, ArticlePart.Recommended).Should().Be(ArticlePart.Screenshots);
        EditorKeys.NextGap(gaps, ArticlePart.Screenshots).Should().Be(ArticlePart.Tags);
        EditorKeys.NextGap(gaps, ArticlePart.Tags).Should().Be(ArticlePart.BottomLine, "past the last: round to the first");
        EditorKeys.NextGap([ArticlePart.Tags], ArticlePart.Tags).Should().Be(ArticlePart.Tags, "the only gap, from inside it");
        EditorKeys.NextGap([], ArticlePart.Bullets).Should().BeNull();
        EditorKeys.NextGap([ArticlePart.Assessment, ArticlePart.Assessment], null).Should().Be(ArticlePart.Assessment, "two gaps in one section: one stop");
    }

    [Fact]
    public void TheSectionFocusIsIn_IsReadFromItsHeadingsId()
    {
        EditorKeys.SectionOf("sec-bluf").Should().Be(ArticlePart.BottomLine);
        EditorKeys.SectionOf("sec-shots").Should().Be(ArticlePart.Screenshots);
        EditorKeys.SectionOf("sec-tags").Should().Be(ArticlePart.Tags);
        EditorKeys.SectionOf(null).Should().BeNull();
        EditorKeys.SectionOf("sec-history").Should().BeNull();
        EditorKeys.PageOrder.Should().HaveCount(Enum.GetValues<ArticlePart>().Length, "every section is somewhere on the page");
    }

    [Fact]
    public async Task N_GoesToTheNextGapsField_AfterTheSectionFocusIsIn()
    {
        // Gaps: bottom line, a bullet (both blocking), a scorecard tag, the source.
        var id = await NewArticle("N gaps");
        await using var r = await Editor(id);

        await r.Click(p => p.OnEditorKey("gap", null));
        (await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.JumpingTo)).Should().Be((ArticlePart.Assessment, "src"), "the source, first on the page");

        await r.Click(p => p.OnEditorKey("gap", "sec-assessment"));
        (await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.JumpingTo)).Should().Be((ArticlePart.BottomLine, "f-bluf"));

        await r.Click(p => p.OnEditorKey("gap", "sec-why"));
        (await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.JumpingTo)).Should().Be((ArticlePart.Tags, (string?)null), "the tags' heading, as a gap's link goes");

        await r.Click(p => p.OnEditorKey("gap", "sec-tags"));
        (await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.JumpingTo)).Should().Be((ArticlePart.Assessment, "src"), "round to the first");
        Told(r).Should().BeEmpty();
    }

    [Fact]
    public async Task N_WithNoGaps_SaysSo_AndStaysPut()
    {
        var tag = await AnyTag();
        var id = await NewArticle("N none", a =>
        {
            Readyable(a);
            a.SourceName = "Vendor blog";
            a.Tags.Add(new ArticleTag { TagId = tag });
        });
        await using var r = await Editor(id);

        await r.Click(p => p.OnEditorKey("gap", "sec-bluf"));

        Told(r).Should().Equal("No gaps: nothing is missing.");
        (await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.JumpingTo)).Should().BeNull();
    }

    private async Task<Guid> AnyTag()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = "keys-" + Guid.NewGuid().ToString("N")[..8];
        var tag = new ScorecardTag { Category = new ScorecardCategory { Key = key, Name = "Editor keys " + key }, Key = key, Name = "Editor keys" };
        db.ScorecardTags.Add(tag);
        await db.SaveChangesAsync();
        return tag.Id;
    }

    // ---- G then a letter ----

    [Theory]
    [InlineData('b', ArticlePart.BottomLine, "f-bluf")]
    [InlineData('U', ArticlePart.Bullets, "f-bullet-0")]
    [InlineData('w', ArticlePart.WhyItMatters, "f-why")]
    [InlineData('r', ArticlePart.Recommended, "f-rec")]
    [InlineData('i', ArticlePart.Indicators, "f-indicator")]
    [InlineData('t', ArticlePart.Tags, "tags-search")]
    public async Task G_ThenALetter_GoesToThatSectionsFirstField_WhichTheEditorDraws(char letter, ArticlePart part, string field)
    {
        EditorKeys.Section(letter).Should().Be(part);
        var id = await NewArticle("G " + letter, Readyable);
        await using var r = await Editor(id);

        await r.Click(p => p.OnEditorKey("go-" + letter, null));

        (await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.JumpingTo)).Should().Be((part, field));
        (await r.Html()).Should().Contain($"id=\"{field}\"", "the field the key goes to is on the page");
        r.Component.Dirty.Should().BeFalse("going somewhere changes nothing");
    }

    [Fact]
    public async Task G_U_WithNoBullets_GoesToTheHeading_AddingNone()
    {
        var id = await NewArticle("G U none");
        await using var r = await Editor(id);

        await r.Click(p => p.OnEditorKey("go-u", null));

        (await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.JumpingTo)).Should().Be((ArticlePart.Bullets, (string?)null));
        r.Component.Dirty.Should().BeFalse();
    }

    [Fact]
    public void G_ThenAnotherLetter_IsNothing()
    {
        EditorKeys.Section('a').Should().BeNull("Assessment has no letter");
        EditorKeys.Section('s').Should().BeNull("nor Screenshots");
        EditorKeys.Section('g').Should().BeNull();
    }

    // ---- Ctrl+Shift+Enter ----

    [Fact]
    public async Task SaveReadyNext_MarksItReady_AndGoesOnToTheNextArticleOnTheBrief_KeepingWhatThePageShows()
    {
        Guid a = await NewArticle("Ready next one", Readyable), b = await NewArticle("Ready next two");
        var order = (await OnTodaysBrief(a, b)).Select(s => s.ArticleId).ToList();
        order.IndexOf(b).Should().Be(order.IndexOf(a) + 1);
        await using var r = await Editor(a, to => BriefStepper.Href(to, "company"));

        var went = await FluentActions.Invoking(() => r.Click(p => p.OnEditorKey("ready-next", null))).Should().ThrowAsync<NavigationException>();

        went.Which.Location.Should().EndWith($"articles/{b}?edition=company");
        (await StatusOf(a)).Should().Be(ArticleStatus.Ready);
        Told(r).Should().Equal("Marked ready for a brief.");
    }

    [Fact]
    public async Task SaveReadyNext_SavesWhatWasTyped_First()
    {
        Guid a = await NewArticle("Ready next typed", Readyable), b = await NewArticle("Ready next after typed");
        await OnTodaysBrief(a, b);
        await using var r = await Editor(a);
        await r.Click(p => p.OnEditorKey("go-u", null));
        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.MentionAsync("We run it."));

        await FluentActions.Invoking(() => r.Click(p => p.SaveReadyNextAsync())).Should().ThrowAsync<NavigationException>();

        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var saved = (await scope.ServiceProvider.GetRequiredService<ArticleService>().GetAsync(a))!;
        saved.WhyItMatters.Should().Be("We run it.");
        saved.Status.Should().Be(ArticleStatus.Ready);
    }

    [Fact]
    public async Task SaveReadyNext_OnTheLastArticle_MarksItReady_AndStays_SayingSo()
    {
        var last = await NewArticle("Ready next last", Readyable);
        var order = await OnTodaysBrief(last);
        order[^1].ArticleId.Should().Be(last);
        await using var r = await Editor(last);

        await r.Click(p => p.SaveReadyNextAsync());

        (await StatusOf(last)).Should().Be(ArticleStatus.Ready);
        Told(r).Should().Equal("Marked ready for a brief. It's the last article on today's brief.");
    }

    [Fact]
    public async Task SaveReadyNext_OffTheBrief_MarksItReady_AndStays_SayingSo()
    {
        var id = await NewArticle("Ready next off the brief", Readyable);
        await using var r = await Editor(id);

        await r.Click(p => p.SaveReadyNextAsync());

        (await StatusOf(id)).Should().Be(ArticleStatus.Ready);
        Told(r).Should().Equal("Marked ready for a brief. It isn't on today's brief, so there's no next article.");
    }

    [Fact]
    public async Task SaveReadyNext_WithABlockingGap_IsRefusedAsTheButtonIs_AndStays()
    {
        Guid a = await NewArticle("Ready next blocked", x => x.Bluf = "Exploited."), b = await NewArticle("Ready next after blocked");
        await OnTodaysBrief(a, b);
        await using var r = await Editor(a);

        await r.Click(p => p.SaveReadyNextAsync());   // no navigation: it would throw

        (await StatusOf(a)).Should().Be(ArticleStatus.Draft);
        Told(r).Should().Equal("It isn't ready yet: Add at least one bullet.");
    }

    [Fact]
    public async Task SaveReadyNext_OnAReadyArticle_GoesOn_AsItIs()
    {
        Guid a = await NewArticle("Ready next already", x => { Readyable(x); x.Status = ArticleStatus.Ready; }), b = await NewArticle("Ready next after already");
        await OnTodaysBrief(a, b);
        await using var r = await Editor(a);

        var went = await FluentActions.Invoking(() => r.Click(p => p.SaveReadyNextAsync())).Should().ThrowAsync<NavigationException>();

        went.Which.Location.Should().EndWith($"articles/{b}");
        Told(r).Should().BeEmpty("nothing to save or mark");
    }

    [Fact]
    public async Task TheKeys_DoNothing_ForSomeoneWhoCantEdit()
    {
        Guid a = await NewArticle("Keys reader", Readyable), b = await NewArticle("Keys reader next");
        await OnTodaysBrief(a, b);
        await using var r = await ComponentHarness.RenderAsync<ArticleEditorPanel>(app, $"/articles/{a}", new() { ["Id"] = a },
            new ComponentHarness.SignIn("dev:owner", "Owner", "ActionOwner"));

        await r.Click(p => p.OnEditorKey("ready-next", null));
        await r.Click(p => p.OnEditorKey("go-b", null));

        (await StatusOf(a)).Should().Be(ArticleStatus.Draft);
        (await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.JumpingTo)).Should().BeNull();
    }

    // ---- The "?" list ----

    [Fact]
    public void TheShortcutsList_HasTheEditorsKeys_ForSomeoneWhoCanEdit_LettersMarked()
    {
        var path = "articles/" + Guid.NewGuid();
        var editing = Shortcuts.For(path).Single(g => g.Title == "Editing the article").Keys;

        editing.SelectMany(k => k.Keys).Should().Equal(
            "G then B", "G then U", "G then W", "G then R", "G then I", "G then T", "N", "Ctrl Shift Enter", "⌘ Shift Enter");
        editing.Where(k => !k.Keys[0].StartsWith("Ctrl", StringComparison.Ordinal)).Should().OnlyContain(k => k.Letter, "single characters: off with the switch");
        editing.Single(k => k.Keys.Contains("Ctrl Shift Enter")).Letter.Should().BeFalse("a modifier key isn't turned off");

        Shortcuts.For(path, canEdit: false).Select(g => g.Title).Should().NotContain("Editing the article")
            .And.Contain("Article", "the preview's and the stepper's keys stay");
        Shortcuts.For("articles").Select(g => g.Title).Should().NotContain("Editing the article");
    }

    [Fact]
    public void TheListedLetters_AreTheOnesEditorJsHandles()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Tide.sln"))) dir = Path.GetDirectoryName(dir)!;
        var js = File.ReadAllText(Path.Combine(dir, "src", "Tide.Web", "wwwroot", "js", "editor.js"));
        var handled = Letters().Match(js).Groups[1].Value.Split(',').Select(l => l.Trim().Trim('\'').ToUpperInvariant()).ToList();

        var listed = Shortcuts.For("articles/" + Guid.NewGuid()).Single(g => g.Title == "Editing the article").Keys
            .SelectMany(k => k.Keys).Where(k => k.StartsWith("G then ", StringComparison.Ordinal)).Select(k => k[^1..]).ToList();
        listed.Should().Equal(handled);
        handled.Should().OnlyContain(l => EditorKeys.Section(l[0]) != null, "each letter names a section in .NET too");
    }

    [GeneratedRegex(@"const sections = \[([^\]]*)\]")]
    private static partial Regex Letters();
}
