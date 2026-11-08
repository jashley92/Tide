using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The formatting toolbar over the Markdown text boxes (MarkdownToolbar): drawn over the article's long fields and the
/// brief's opening note for those who can edit them, with a named toolbar and named buttons, and not drawn for a reader
/// of the same page. The page loads its script by a relative address, so it works under a sub-path.
/// </summary>
public partial class MarkdownToolbarTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:mdtb-owner", "Olive Owner", "ActionOwner");

    private static readonly string[] Names = ["Bold", "Italic", "Bulleted list", "Numbered list", "Outdent list item", "Indent list item", "Link"];

    private async Task<Guid> NewArticle()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var article = new Domain.Entities.Article { Title = "Toolbar check", AuthorId = "x", AuthorName = "X", WhyItMatters = "- We run it\n  - On the edge" };
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        return article.Id;
    }

    // The toolbar for the box with this id, or null.
    private static string? Toolbar(string html, string id)
    {
        var m = Regex.Match(html, $"<div class=\"tide-md-toolbar[^\"]*\" role=\"toolbar\"[^>]*data-md-for=\"{id}\"[\\s\\S]*?</div>");
        return m.Success ? m.Value : null;
    }

    private static void HasTheButtons(string bar, string label, string id)
    {
        bar.Should().Contain($"aria-label=\"Formatting for {label}\"").And.Contain($"aria-controls=\"{id}\"");
        foreach (var name in Names) bar.Should().Contain($"aria-label=\"{name}\"");
        bar.Should().Contain("title=\"Bold (Ctrl&#x2B;B)\"").And.Contain("title=\"Italic (Ctrl&#x2B;I)\"")
            .And.Contain("title=\"Indent list item (Tab)\"").And.Contain("title=\"Outdent list item (Shift&#x2B;Tab)\"");
        bar.Should().Contain("class=\"bi bi-type-bold\"").And.Contain("class=\"bi bi-list-ul\"");
        Regex.Matches(bar, "tabindex=\"0\"").Should().HaveCount(1, "the toolbar is one Tab stop; the arrow keys move inside it");
        Regex.Matches(bar, "<button type=\"button\"").Should().HaveCount(Names.Length);
    }

    [Fact]
    public async Task TheArticleEditor_HasTheToolbar_OverWhyItMattersAndRecommendedActions_ForAnAnalyst()
    {
        var id = await NewArticle();
        await using var r = await RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id });
        var html = await r.Html();

        HasTheButtons(Toolbar(html, "f-why")!, "Why it matters to us", "f-why");
        HasTheButtons(Toolbar(html, "f-rec")!, "Recommended actions", "f-rec");
        html.IndexOf("data-md-for=\"f-why\"", StringComparison.Ordinal).Should()
            .BeLessThan(html.IndexOf("id=\"f-why\"", StringComparison.Ordinal), "it sits above its box");
        Toolbar(html, "f-why")!.Should().NotContain("data-md-change", "the box binds on input");
    }

    [Fact]
    public async Task TheArticleEditor_HasNoToolbar_WhereTheBoxesAreReadOnly()
    {
        var id = await NewArticle();
        await using var r = await RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id }, Owner);
        var html = await r.Html();

        html.Should().Contain("id=\"f-why\"").And.Contain("readonly");
        html.Should().NotContain("tide-md-toolbar");
    }

    [Fact]
    public async Task TheBriefsOpeningNote_HasTheToolbar_ForAnAnalyst_AndNotForAReader()
    {
        var today = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        var day = today;
        for (var i = 0; i < 90; i++) day = WorkingDays.Weekdays.Previous(day);
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var brief = await briefs.GetOrStartAsync(day);
            await briefs.AddArticleAsync(brief.Id, await scope.ServiceProvider.GetRequiredService<ArticleService>()
                .CreateAsync("Toolbar note article", "Vendor", null, TlpLevel.Clear));
            await briefs.SetTeamNoteAsync(brief.Id, "- Invoice lures\n  - Payroll spoof");
        }
        var path = $"/briefs/{day:yyyy-MM-dd}";

        await using (var analyst = await RenderAsync<BriefPage>(app, path, new() { ["Day"] = $"{day:yyyy-MM-dd}" }))
            HasTheButtons(Toolbar(await analyst.Html(), "team-note")!, "Opening note", "team-note");

        await using var owner = await RenderAsync<BriefPage>(app, path, new() { ["Day"] = $"{day:yyyy-MM-dd}" }, Owner);
        var theirs = await owner.Html();
        theirs.Should().Contain("Payroll spoof").And.NotContain("tide-md-toolbar");
    }

    [Fact]
    public async Task TheComponent_DrawsNothingWhenDisabled_AndAsksForChangeEventsForABoxBoundOnChange()
    {
        await using (var off = await RenderAsync<MarkdownToolbar>(app, "/", new() { ["For"] = "x", ["Label"] = "X", ["Disabled"] = true }))
            (await off.Html()).Should().BeEmpty();

        await using var on = await RenderAsync<MarkdownToolbar>(app, "/", new() { ["For"] = "a-desc", ["Label"] = "Details", ["BindsOnChange"] = true });
        var html = await on.Html();
        HasTheButtons(Toolbar(html, "a-desc")!, "Details", "a-desc");
        html.Should().Contain("data-md-change=\"\"");
    }

    [Fact]
    public async Task ThePage_LoadsTheToolbarsScript_ByARelativeAddress()
    {
        var html = await app.CreateClient().GetStringAsync($"/articles/{await NewArticle()}");
        html.Should().Contain("<script src=\"js/markdown-toolbar.js?v=").And.NotContain("src=\"/js/markdown-toolbar.js");
        var js = await app.CreateClient().GetStringAsync("/js/markdown-toolbar.js");
        js.Should().Contain("execCommand('insertText'").And.Contain("tide-md-toolbar");
    }

    // Every Markdown text box the app's analysts write in has one (by the box's id), and each toolbar names a box that's
    // drawn right after it.
    [Fact]
    public void EachToolbar_SitsRightAboveTheBoxItNames()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Tide.sln"))) repo = repo.Parent;
        var root = Path.Combine(repo!.FullName, "src", "Tide.Web", "Components");
        var found = new List<string>();
        foreach (var file in Directory.GetFiles(root, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in ToolbarTag().Matches(text))
            {
                var next = text[(m.Index + m.Length)..].TrimStart();
                next.Should().StartWith($"<textarea id=\"{m.Groups[1].Value}\"", $"{Path.GetFileName(file)}'s toolbar for {m.Groups[1].Value} is right above its box");
                found.Add(m.Groups[1].Value);
            }
        }
        found.Should().Contain(["f-why", "f-rec", "team-note", "a-desc", "comment-new", "comment-edit", "st-sum", "etpl-body", "f-notes"]);
    }

    [GeneratedRegex("<MarkdownToolbar For=\"([^\"]+)\"[^>]*/>")]
    private static partial Regex ToolbarTag();
}
