using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Domain.Entities;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Shared;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>
/// Walking today's brief from the article page (BriefStepper), the unsaved-changes guard every move goes through
/// (LeaveDialog: Save and leave, Discard and leave, Stay), and the Edit | Preview switch that narrow screens get.
/// </summary>
public class BriefStepperTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<Guid> NewArticle(string title, Action<Article>? fill = null)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var a = new Article { Title = title, AuthorId = "x", AuthorName = "X", Bluf = "Exploited in the wild." };
        fill?.Invoke(a);
        db.Articles.Add(a);
        await db.SaveChangesAsync();
        return a.Id;
    }

    // Puts the articles at the end of today's brief, and hands back its running order.
    private async Task<IReadOnlyList<BriefStop>> OnTodaysBrief(params Guid[] ids)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var brief = await briefs.GetOrStartAsync(briefs.CurrentDate);
        foreach (var id in ids) await briefs.AddArticleAsync(brief.Id, id);
        return await briefs.RunningOrderAsync(briefs.CurrentDate);
    }

    private async Task<string> Stepper(Guid id, string query = "")
    {
        var html = await app.CreateClient().GetStringAsync($"/articles/{id}{query}");
        var at = html.IndexOf("<nav class=\"tide-stepper\"", StringComparison.Ordinal);
        at.Should().BePositive("the article is on today's brief");
        return html[at..html.IndexOf("</nav>", at, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task OnTodaysBrief_TheArticleSaysWhereItIs_AndLinksBeforeAndAfter_InTheRunningOrder()
    {
        Guid a = await NewArticle("Stepper first"), b = await NewArticle("Stepper second"), c = await NewArticle("Stepper third");
        var order = (await OnTodaysBrief(a, b, c)).Select(s => s.ArticleId).ToList();
        var at = order.IndexOf(b);

        var nav = await Stepper(b);

        nav.Should().Contain("aria-label=\"Today&#x27;s brief\"").And.Contain($"On today&#x27;s brief &#xB7; <strong>{at + 1} of {order.Count}</strong>")
            .And.Contain($"<a href=\"articles/{a}\" data-step=\"prev\" aria-keyshortcuts=\"K\" title=\"Stepper first (K)\">&#x2039; Previous</a>")
            .And.Contain($"<a href=\"articles/{c}\" data-step=\"next\" aria-keyshortcuts=\"J\" title=\"Stepper third (J)\">Next &#x203A;</a>")
            .And.Contain("<a href>Back to Today</a>").And.NotContain("aria-disabled");
    }

    [Fact]
    public async Task AtTheEnds_ThereIsNoStepFurther()
    {
        await OnTodaysBrief(await NewArticle("Stepper end"));
        var order = await OnTodaysBrief();

        var first = await Stepper(order[0].ArticleId);
        first.Should().Contain($"<strong>1 of {order.Count}</strong>")
            .And.Contain("<a role=\"link\" aria-disabled=\"true\" class=\"tide-stepper-end\">&#x2039; Previous</a>")
            .And.NotContain("data-step=\"prev\"");
        var last = await Stepper(order[^1].ArticleId);
        last.Should().Contain($"<strong>{order.Count} of {order.Count}</strong>")
            .And.Contain("<a role=\"link\" aria-disabled=\"true\" class=\"tide-stepper-end\">Next &#x203A;</a>")
            .And.NotContain("data-step=\"next\"");
    }

    [Fact]
    public async Task TheSteps_KeepTheEditionThePreviewShows()
    {
        Guid a = await NewArticle("Edition one"), b = await NewArticle("Edition two");
        await OnTodaysBrief(a, b);

        (await Stepper(a, "?edition=company")).Should().Contain($"href=\"articles/{b}?edition=company\" data-step=\"next\"");
        (await Stepper(b, "?edition=company")).Should().Contain($"href=\"articles/{a}?edition=company\" data-step=\"prev\"");
        BriefStepper.Href(a, null).Should().Be($"articles/{a}");
    }

    [Fact]
    public async Task AnArticleNotOnTodaysBrief_HasItsBreadcrumbs_AndNoStepper()
    {
        var html = await app.CreateClient().GetStringAsync($"/articles/{await NewArticle("Not briefed")}");

        html.Should().NotContain("tide-stepper").And.NotContain("On today&#x27;s brief")
            .And.Contain("<nav class=\"tide-crumbs\" aria-label=\"Breadcrumb\">");
    }

    [Fact]
    public void TheShortcutsList_HasTheSteps_OnAnArticle()
    {
        Shortcuts.For("articles/" + Guid.NewGuid()).SelectMany(g => g.Keys)
            .Should().Contain(k => k.Keys.SequenceEqual(new[] { "J" }) && k.Letter && k.What.Contains("next"))
            .And.Contain(k => k.Keys.SequenceEqual(new[] { "K" }) && k.Letter && k.What.Contains("previous"));
    }

    // ---- The guard ----

    private async Task<ComponentHarness.Rendered<ArticleEditorPanel>> Editor(Guid id) =>
        await ComponentHarness.RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new()
        {
            ["Id"] = id, ["Aside"] = (RenderFragment<ArticleEditorPanel.State>)(_ => b => b.AddContent(0, "The preview")),
        });

    // An edit, as a placeholder's "Add one" makes: a bullet added, so the form has unsaved changes.
    private static Task Edit(ComponentHarness.Rendered<ArticleEditorPanel> r) => r.Click(p => p.FocusFieldAsync(ArticlePart.Bullets));

    // The editor drawn again (a pill pressed), as the next thing on the page would draw it.
    private static async Task<string> Redrawn(ComponentHarness.Rendered<ArticleEditorPanel> r)
    {
        await r.Click(p => p.FocusAsync(ArticlePart.Assessment));
        return await r.Html();
    }

    // Starts leaving, and waits for the question.
    private static async Task<Task<bool>> Leaving(ComponentHarness.Rendered<ArticleEditorPanel> r)
    {
        Task<bool>? leaving = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { leaving = (Task<bool>)r.Pressed(p => p.MayLeave()); });
        var html = await r.HtmlOnce("Leave with unsaved changes?");
        html.Should().Contain("role=\"alertdialog\"").And.Contain(">Stay</button>")
            .And.Contain(">Discard and leave</button>").And.Contain(">Save and leave</button>");
        leaving!.IsCompleted.Should().BeFalse("the move waits for an answer");
        return leaving;
    }

    [Fact]
    public async Task WithNothingUnsaved_LeavingIsntAsked()
    {
        await using var r = await Editor(await NewArticle("Clean"));
        Task<bool>? leaving = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { leaving = (Task<bool>)r.Pressed(p => p.MayLeave()); });

        (await leaving!).Should().BeTrue();
        (await r.Html()).Should().NotContain("Leave with unsaved changes?");
    }

    [Fact]
    public async Task Stay_KeepsTheChanges_AndTheArticle()
    {
        await using var r = await Editor(await NewArticle("Stay"));
        await Edit(r);
        var leaving = await Leaving(r);

        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Leave.Stay());

        (await leaving).Should().BeFalse();
        (await Redrawn(r)).Should().Contain("Unsaved changes");
    }

    [Fact]
    public async Task DiscardAndLeave_GoesWithoutSaving()
    {
        var id = await NewArticle("Discard");
        await using var r = await Editor(id);
        await Edit(r);
        var leaving = await Leaving(r);

        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Leave.Close(LeaveDialog.Choice.Discard));

        (await leaving).Should().BeTrue();
        (await Redrawn(r)).Should().NotContain("Unsaved changes");
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().NotContain(t => t.Message == "Saved.");
    }

    [Fact]
    public async Task SaveAndLeave_SavesFirst()
    {
        var id = await NewArticle("Save", a => a.Bullets.Add(new ArticleBullet { Text = "Kept" }));
        await using var r = await Editor(id);
        await r.Click(p => { p.ShowPane(ArticleEditorPanel.PreviewPane); return Task.CompletedTask; });
        await r.Click(p => p.FocusFieldAsync(ArticlePart.BottomLine));
        (await r.Html()).Should().Contain("data-pane=\"edit\"");
        await Edit(r);   // the one bullet isn't empty: one is added
        var leaving = await Leaving(r);

        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Leave.Close(LeaveDialog.Choice.Save));

        (await leaving).Should().BeTrue();
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.Message == "Saved.");
        (await Redrawn(r)).Should().Contain("All changes saved").And.NotContain("Unsaved changes");
    }

    // ---- Narrow screens ----

    [Fact]
    public async Task TheWorkspace_HasAnEditPreviewSwitch_ThatChoosesThePaneShown()
    {
        var id = await NewArticle("Narrow");
        var html = await app.CreateClient().GetStringAsync($"/articles/{id}");
        html.Should().Contain("<div class=\"tide-tabbar tide-tabbar-sub tide-ws-switch\" role=\"group\" aria-label=\"Show\">")
            .And.Contain("aria-pressed=\"true\"><span>Edit</span>").And.Contain("aria-pressed=\"false\"><span>Preview</span>")
            .And.Contain("<div class=\"tide-editor\" data-pane=\"edit\">");

        await using var r = await Editor(id);
        await r.Click(p => { p.ShowPane(ArticleEditorPanel.PreviewPane); return Task.CompletedTask; });
        var shown = await r.Html();
        shown.Should().Contain("data-pane=\"preview\"").And.Contain("aria-pressed=\"true\"><span>Preview</span>");

        var css = await app.CreateClient().GetStringAsync("/app.css");
        css.Should().Contain(".tide-tabbar.tide-ws-switch { display: none; }")
            .And.Contain(".tide-editor[data-pane=\"edit\"] > .tide-editor-side:has(.tide-reader) { display: none; }")
            .And.Contain(".tide-editor[data-pane=\"preview\"]:has(.tide-reader) > .tide-editor-main > :not(.tide-sticky-save) { display: none; }");
    }

    [Fact]
    public async Task InPreview_TheStickyBar_StaysOnScreen_WithTheSaveState()
    {
        await using var r = await Editor(await NewArticle("Preview bar"));
        await r.Click(p => { p.ShowPane(ArticleEditorPanel.PreviewPane); return Task.CompletedTask; });
        await Edit(r);
        await r.Click(p => { p.ShowPane(ArticleEditorPanel.PreviewPane); return Task.CompletedTask; });
        var html = await r.Html();

        // The bar is still in the edit column (where it sits beside the preview on a wide screen), and saying what's unsaved.
        var editor = html[html.IndexOf("<div class=\"tide-editor\" data-pane=\"preview\">", StringComparison.Ordinal)..];
        var bar = editor[editor.IndexOf("class=\"tide-sticky-save", StringComparison.Ordinal)..];
        editor.IndexOf("class=\"tide-editor-main\"", StringComparison.Ordinal).Should().BeLessThan(editor.IndexOf("class=\"tide-sticky-save", StringComparison.Ordinal));
        bar[..bar.IndexOf("<aside", StringComparison.Ordinal)].Should().Contain("Unsaved changes").And.Contain("> Save</button>");

        // Narrow, in Preview, the edit column gives up its box to the bar alone, which follows the preview and sticks.
        var css = await app.CreateClient().GetStringAsync("/app.css");
        css.Should().Contain(".tide-editor[data-pane=\"preview\"]:has(.tide-reader) { display: flex; flex-direction: column; }")
            .And.Contain(".tide-editor[data-pane=\"preview\"]:has(.tide-reader) > .tide-editor-main { display: contents; }")
            .And.Contain(".tide-editor[data-pane=\"preview\"]:has(.tide-reader) .tide-sticky-save { order: 1; }")
            .And.Contain("html:has(.tide-sticky-save) { scroll-padding-bottom:")
            .And.NotContain("> .tide-editor-main { display: none; }", "the bar would go with the fields");
    }

    // ---- Adding to today's brief ----

    private static string Bar(string html)
    {
        var bar = html[html.IndexOf("class=\"tide-sticky-save", StringComparison.Ordinal)..];
        return bar[..bar.IndexOf("</div>", StringComparison.Ordinal)];
    }

    [Fact]
    public async Task OnTodaysBrief_TheBarSaysSo_InPlaceOfAddingIt()
    {
        var id = await NewArticle("Already briefed");
        await OnTodaysBrief(id);

        var bar = Bar(await app.CreateClient().GetStringAsync($"/articles/{id}"));

        bar.Should().Contain("On today's brief").And.NotContain("Add to today");
    }

    [Fact]
    public async Task AddingToTodaysBrief_SwapsTheButton_ForOnTodaysBrief_AndShowsTheStepper()
    {
        var id = await NewArticle("Briefed now");
        await using var r = await ComponentHarness.RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new()
        {
            ["Id"] = id, ["Aside"] = (RenderFragment<ArticleEditorPanel.State>)(_ => b => b.AddContent(0, "The preview")),
            ["Stepper"] = (RenderFragment)(b => b.AddContent(0, "The stepper")),
        });
        var before = await r.Html();
        Bar(before).Should().Contain("Add to today's brief").And.NotContain("On today's brief");
        before.Should().NotContain("The stepper");

        await r.Click(p => p.AddToTodaysBrief());
        var after = await Redrawn(r);

        Bar(after).Should().Contain("On today's brief").And.NotContain("Add to today").And.NotContain("Delete draft", "it's on a brief");
        after.Should().Contain("The stepper");
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.Message.StartsWith("On the brief for"));
    }

    [Fact]
    public async Task APlaceholder_GoesToItsField_NotOnlyTheHeading()
    {
        var id = await NewArticle("Fields", a =>
        {
            a.Classification = Domain.Enums.Classification.Threat;
            a.Bullets.Add(new ArticleBullet { Text = "Full", SortOrder = 0 });
            a.Bullets.Add(new ArticleBullet { Text = "", SortOrder = 1 });
        });
        await using var r = await Editor(id);
        var p = r.Component;

        p.FieldId(ArticlePart.BottomLine).Should().Be("f-bluf");
        p.FieldId(ArticlePart.WhyItMatters).Should().Be("f-why");
        p.FieldId(ArticlePart.Recommended).Should().Be("f-rec");
        p.FieldId(ArticlePart.Bullets).Should().Be("f-bullet-1", "the first empty bullet");
        p.FieldId(ArticlePart.Assessment).Should().Be("prio", "a threat with no priority");
        p.FieldId(ArticlePart.Screenshots).Should().BeNull("every screenshot (there are none) has a caption");
        p.FieldId(ArticlePart.Tags).Should().BeNull("the disclosure is the place to go");
        var html = await r.Html();
        html.Should().Contain("id=\"f-bluf\"").And.Contain("id=\"f-bullet-1\"").And.Contain("id=\"f-why\"").And.Contain("id=\"f-rec\"");
    }
}
