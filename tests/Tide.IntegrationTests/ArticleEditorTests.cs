using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Infrastructure.Persistence;

namespace Tide.IntegrationTests;

/// <summary>
/// The article editor (/articles/{id}), as drawn for the dev user, and the fixes from the analyst walkthrough
/// (docs/analyst-walkthrough-2026-10.md) that live in it, so moving its markup about can't quietly undo them.
/// </summary>
public partial class ArticleEditorTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<Guid> NewArticle(Action<Domain.Entities.Article>? fill = null)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var article = new Domain.Entities.Article { Title = "Editor check", AuthorId = "x", AuthorName = "X" };
        fill?.Invoke(article);
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        return article.Id;
    }

    // Walkthrough bug 3: the sticky save bar covered the indicator "Add" button, so clicks landed on the bar.
    [Fact]
    public async Task TheStickySaveBar_LeavesRoomToScrollTheLastControlClearOfIt()
    {
        var html = await app.CreateClient().GetStringAsync($"/articles/{await NewArticle()}");
        var css = await app.CreateClient().GetStringAsync("/app.css");

        html.Should().Contain("class=\"tide-sticky-save");
        css.Should().Contain("html:has(.tide-sticky-save) { scroll-padding-bottom:", "focused or scrolled-to controls stop short of the bar");
    }

    // Walkthrough bug 4: fields bound on change enabled Save only once they lost focus, and the click that blurred one
    // was lost. Every text box the editor binds must update on input. Enter in the new-indicator box adds it (bug 3).
    [Fact]
    public void TheEditorsTextBoxes_BindOnInput_SoSaveEnablesWhileTyping()
    {
        var bound = EditorSources().SelectMany(s => TextBox().Matches(s).Select(m => m.Value))
            .Where(tag => tag.Contains("@bind=", StringComparison.Ordinal)).ToList();

        bound.Should().HaveCountGreaterThan(10, "the title, source, link, bottom line, bullets, captions, why it matters, recommended actions, notes and context are all bound");
        bound.Should().OnlyContain(tag => tag.Contains("@bind:event=\"oninput\"", StringComparison.Ordinal));
        bound.Should().ContainSingle(tag => tag.Contains("aria-label=\"New indicator\"", StringComparison.Ordinal))
            .Which.Should().Contain("@onkeydown=\"NewIndicatorKey\"");
    }

    [Fact]
    public async Task AThreatPromotedWithWhyAtTheAmberDefault_SaysWhy_AndAWrittenOneDoesnt()
    {
        var fresh = await NewArticle(a => { a.Classification = Domain.Enums.Classification.Threat; a.WhyItMattersTlp = Domain.Enums.TlpLevel.Amber; });
        var written = await NewArticle(a =>
        {
            a.Classification = Domain.Enums.Classification.Threat; a.WhyItMattersTlp = Domain.Enums.TlpLevel.Amber; a.WhyItMatters = "We run it.";
        });
        const string Hint = "Internal context starts at AMBER; lower it if it's safe to share.";

        var html = await app.CreateClient().GetStringAsync($"/articles/{fresh}");
        html.Should().Contain(Hint).And.Contain(">who changed what, and when<", "an analyst gets the History disclosure");
        (await app.CreateClient().GetStringAsync($"/articles/{written}")).Should().NotContain(Hint);
    }

    [Fact]
    public async Task TheSectionPills_SayNeeded_WhereTheReadinessChecksFindAGap()
    {
        var id = await NewArticle(a => { a.Bluf = "Exploited in the wild."; a.Classification = Domain.Enums.Classification.Threat; });

        var html = await app.CreateClient().GetStringAsync($"/articles/{id}");

        var nav = html[html.IndexOf("aria-label=\"Article sections\"", StringComparison.Ordinal)..];
        nav = nav[..nav.IndexOf("</nav>", StringComparison.Ordinal)];
        Pills().Matches(nav).Select(m => (Label: m.Groups["label"].Value.Trim(), Needed: m.Groups["needed"].Success)).Should().Equal(
            ("Assessment", true),            // a threat with no priority, and no source
            ("Bottom line", false),
            ("Bullets", true),
            ("Why it matters", false),
            ("Recommended", true),           // a threat should say what to do
            ("Indicators &amp; CVEs", false),
            ("Tags &amp; ATT&amp;CK", true));
        html.Should().Contain("What readers get", "the reader preview stays beside the editor").And.Contain("A threat needs a priority.");
    }

    // Adding an indicator left Save disabled: the box tells the editor through a plain callback, which redraws only the
    // box, so the bar went on saying "All changes saved" until something else redrew it.
    [Fact]
    public async Task AddingAnIndicator_EnablesSave()
    {
        var id = await NewArticle();
        await using var r = await ComponentHarness.RenderAsync<Web.Components.Shared.ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id });
        SaveButton(await r.Html()).Should().Contain(" disabled", "nothing has changed yet");

        await r.Click(p =>
        {
            p.IndicatorBox!.FindIn("Seen calling back to 203.0.113.45.");
            return p.IndicatorBox.AddFound();
        });

        var html = await r.Html();
        SaveButton(html).Should().NotContain(" disabled", "the indicator is an unsaved change");
        html.Should().Contain("Unsaved changes").And.Contain("203.0.113[.]45");
    }

    private static string SaveButton(string html)
    {
        var bar = html[html.IndexOf("class=\"tide-sticky-save", StringComparison.Ordinal)..];
        var save = bar.IndexOf("> Save</button>", StringComparison.Ordinal);
        return bar[bar.LastIndexOf("<button", save, StringComparison.Ordinal)..save];
    }

    [Fact]
    public async Task TheStickyBar_SaysAllChangesSaved_UntilSomethingChanges()
    {
        var html = await app.CreateClient().GetStringAsync($"/articles/{await NewArticle()}");

        var bar = html[html.IndexOf("class=\"tide-sticky-save", StringComparison.Ordinal)..];
        bar = bar[..bar.IndexOf("</div>", StringComparison.Ordinal)];
        bar.Should().Contain("All changes saved").And.Contain("Save and mark ready").And.Contain("Add to today's brief")
            .And.NotContain("Unsaved changes").And.NotContain("Discard", "there's nothing to discard");
    }

    // CON-3: Sam saves while Robin's editor is open and clean, and Robin clicks Back to draft before Sam's notice
    // arrives. Robin's editor mustn't take Sam's version with Robin's old text, or its next save overwrites Sam's.
    [Fact]
    public async Task BackToDraft_AfterAColleaguesSave_ShowsTheirText_SoTheNextSaveKeepsIt()
    {
        var id = await NewArticle(a => { a.Bluf = "Robin's first bottom line."; a.Status = Domain.Enums.ArticleStatus.Ready; });
        Application.Articles.ArticleForm? robinsForm = null;
        await using var robin = await ComponentHarness.RenderAsync<Web.Components.Shared.ArticleEditorPanel>(app, $"/articles/{id}", new()
        {
            ["Id"] = id, ["OnFormChanged"] = Microsoft.AspNetCore.Components.EventCallback.Factory.Create<Application.Articles.ArticleForm>(this, f => robinsForm = f),
        });
        robinsForm!.Bluf.Should().Be("Robin's first bottom line.");

        await using (var sam = ComponentHarness.SignedIn(app, new ComponentHarness.SignIn("dev:sam", "Sam", "Analyst")))
        {
            var articles = sam.ServiceProvider.GetRequiredService<Application.Articles.ArticleService>();
            var form = Application.Articles.ArticleForm.From((await articles.GetAsync(id))!);
            form.Bluf = "Sam's corrected bottom line.";
            await articles.SaveAsync(form);
        }
        await robin.Click(c => c.BackToDraft());
        robinsForm.Bluf.Should().Be("Sam's corrected bottom line.", "the editor was clean, so it reads Sam's save rather than keeping its own text under Sam's version");

        var ok = false;
        await robin.Click(async c => ok = await c.Save());
        ok.Should().BeTrue();
        await using var check = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var saved = (await check.ServiceProvider.GetRequiredService<Application.Articles.ArticleService>().GetAsync(id))!;
        (saved.Bluf, saved.Status).Should().Be(("Sam's corrected bottom line.", Domain.Enums.ArticleStatus.Draft));
    }

    // UI4-1: Sam tags the article Robin is editing with unsaved changes. A tag doesn't make Robin's save stale, so the
    // editor doesn't say saving would be refused (the Related tab's card still shows the tag). And once the editor has
    // said so for a change that left the version alone, a save that goes through takes the warning down.
    [Fact]
    public async Task AColleaguesTag_DoesntWarnTheEditor_AndASaveThatWorksClearsAWarning()
    {
        var id = await NewArticle();
        await using var robin = await ComponentHarness.RenderAsync<Web.Components.Shared.ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id });
        await using var card = await ComponentHarness.RenderAsync<Web.Components.Shared.ArticleAdversaryCard>(app, $"/articles/{id}",
            new() { ["ArticleId"] = id, ["CanWrite"] = true });
        await robin.Click(p => p.FocusFieldAsync(Application.Articles.ArticlePart.Bullets));   // an unsaved edit

        await using (var sam = ComponentHarness.SignedIn(app, new ComponentHarness.SignIn("dev:sam", "Sam", "Analyst")))
        {
            var adversaries = sam.ServiceProvider.GetRequiredService<Application.Adversaries.AdversaryService>();
            await adversaries.TagArticleAsync(id, await adversaries.SaveAsync(new Application.Adversaries.AdversaryForm { Name = "editor-test Spider" }));
        }
        (await card.HtmlOnce("Untag editor-test Spider")).Should().Contain("Untag editor-test Spider");
        await Task.Delay(Web.Services.LiveChanges.Pause);   // the editor would have heard by now, had it been told
        (await robin.Html()).Should().NotContain("while you were editing");

        app.Services.GetRequiredService<Application.Abstractions.ChangeFeed>().Publish(
            new Application.Abstractions.Change(Application.Abstractions.ChangeFeed.Article(id), "dev:sam", "Sam", Guid.NewGuid()));
        (await robin.HtmlOnce("Sam saved this article while you were editing")).Should().Contain("Saving now would be refused");
        var ok = false;
        await robin.Click(async p => ok = await p.Save());
        ok.Should().BeTrue("nothing moved the article's version");
        await robin.Click(p => p.FocusAsync(Application.Articles.ArticlePart.Assessment));   // drawn again, as the button's click would
        (await robin.Html()).Should().NotContain("while you were editing");
    }

    // An item of the section index: its label, then "Needed" when that section has a gap (or what it has, when it hasn't).
    [GeneratedRegex("<button type=\"button\"[^>]*>(?<label>[^<]*)(?:<span class=\"visually-hidden\">,</span>)?(?:<span class=\"tide-pill-state[^\"]*\">(?:(?<needed>Needed)|[^<]*)</span>)?\\s*</button>")]
    private static partial Regex Pills();

    // The editor's markup: the routed page, and the panel it hands the fields to once that exists.
    private static IEnumerable<string> EditorSources()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Tide.sln"))) root = root.Parent;
        root.Should().NotBeNull("the tests run from inside the repository");
        var components = Path.Combine(root!.FullName, "src", "Tide.Web", "Components");
        return new[] { Path.Combine(components, "Pages", "ArticleEdit.razor"), Path.Combine(components, "Shared", "ArticleEditorPanel.razor"),
                Path.Combine(components, "Shared", "IndicatorAdd.razor") }
            .Where(File.Exists).Select(File.ReadAllText);
    }

    // An <input> or <textarea> start tag, quoted attribute values and all (a lambda's => sits inside quotes).
    [GeneratedRegex("<(?:input|textarea)\\b(?:[^>\"]|\"[^\"]*\")*>")]
    private static partial Regex TextBox();
}
