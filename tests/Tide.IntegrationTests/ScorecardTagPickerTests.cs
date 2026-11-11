using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Domain.Entities;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The scorecard tag picker: the chosen tags first as chips that remove them, an "Add a tag" search over tag and
/// category names, and the full list folded under "Browse all tags"; read-only, the chips alone. In the article editor
/// it marks the article unsaved and saves through the editor's own save.
/// </summary>
public class ScorecardTagPickerTests(TideApp app) : IClassFixture<TideApp>
{
    private static ScorecardCategory Cat(string name, params string[] tags) =>
        new() { Key = name.ToLowerInvariant().Replace(' ', '-'), Name = name, Tags = tags.Select(t => new ScorecardTag { Key = t.ToLowerInvariant().Replace(' ', '-'), Name = t }).ToList() };

    private static readonly ScorecardCategory Delivery = Cat("Delivery", "Phishing", "Spear phishing", "Drive-by download");
    private static readonly ScorecardCategory Cloud = Cat("Cloud", "Misconfiguration", "Token theft");
    private static readonly ScorecardCategory Impact = Cat("Impact", "Ransomware", "Public cloud outage");
    private static readonly IReadOnlyList<ScorecardCategory> All = [Delivery, Cloud, Impact];

    private Task<Rendered<ScorecardTagPicker>> Picker(HashSet<Guid> chosen, bool readOnly = false, Func<Guid, string?>? retired = null) =>
        RenderAsync<ScorecardTagPicker>(app, "/articles", new()
        {
            ["Id"] = "pick", ["Categories"] = All, ["Chosen"] = (IReadOnlySet<Guid>)chosen, ["ReadOnly"] = readOnly, ["RetiredName"] = retired,
            ["OnToggle"] = EventCallback.Factory.Create<Guid>(this, id => { if (!chosen.Remove(id)) chosen.Add(id); }),
        });

    private static string Between(string html, string from, string to)
    {
        var at = html.IndexOf(from, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"the markup has {from}");
        var rest = html[at..];
        var end = rest.IndexOf(to, StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }

    [Fact]
    public async Task TheChosenTags_ComeFirst_AsChipsThatRemoveThem_GroupedOnlyWhenTheySpanCategories()
    {
        var retired = Guid.NewGuid();
        var chosen = new HashSet<Guid> { Delivery.Tags[0].Id, Delivery.Tags[1].Id };
        await using var one = await Picker(chosen);
        var html = await one.Html();
        var top = html[..html.IndexOf("Add a tag", StringComparison.Ordinal)];
        top.Should().Contain("aria-label=\"Remove Phishing\"").And.Contain("aria-label=\"Remove Spear phishing\"").And.Contain("aria-label=\"Chosen tags\"")
            .And.NotContain("tide-tagpick-cat", "one category needs no heading").And.NotContain("Drive-by download");

        chosen.Add(Impact.Tags[0].Id);
        chosen.Add(retired);
        await using var two = await Picker(chosen, retired: id => id == retired ? "Old campaign" : null);
        top = (await two.Html())[..(await two.Html()).IndexOf("Add a tag", StringComparison.Ordinal)];
        top.Should().Contain("<div class=\"tide-tagpick-cat\">Delivery</div>").And.Contain("<div class=\"tide-tagpick-cat\">Impact</div>")
            .And.Contain("aria-label=\"Chosen: Impact\"").And.Contain("Old campaign (retired)").And.NotContain("aria-label=\"Remove Old campaign\"", "a retired tag shows as it did");

        // A chip removes its tag.
        await two.Click(c => c.Toggle(Impact.Tags[0].Id));
        chosen.Should().NotContain(Impact.Tags[0].Id);
        (await two.Html()).Should().NotContain("aria-label=\"Remove Ransomware\"");
    }

    [Fact]
    public async Task TheSearch_MatchesTagAndCategoryNames_GroupedByCategory_LeavingOutTheChosen()
    {
        var chosen = new HashSet<Guid> { Delivery.Tags[0].Id };
        await using var r = await Picker(chosen);
        var html = await r.Html();
        html.Should().Contain("<label class=\"tide-tagpick-label\" for=\"pick-search\">Add a tag</label>").And.Contain("role=\"combobox\"")
            .And.Contain("aria-controls=\"pick-search-list\"").And.Contain("aria-expanded=\"false\"");

        // Typed in: the list drops down under the box, which has focus.
        await r.Click(c => { c._combo!._focused = true; c.Search("cloud"); return Task.CompletedTask; });
        r.Component._found.Select(h => h.Tag.Name).Should().Equal("Misconfiguration", "Token theft", "Public cloud outage");
        html = await r.Html();
        var list = Between(html, "id=\"pick-search-list\"", "</ul>");
        list.Should().Contain("<span class=\"tide-tagpick-group\" aria-hidden=\"true\">Cloud</span>", "a category matched by name lists all its tags")
            .And.Contain("<span class=\"tide-tagpick-group\" aria-hidden=\"true\">Impact</span>").And.Contain("Token theft<span class=\"visually-hidden\">, Cloud</span>");
        list.IndexOf(">Cloud<", StringComparison.Ordinal).Should().BeLessThan(list.IndexOf(">Impact<", StringComparison.Ordinal), "a name starting with it comes first");
        html.Should().Contain("3 tags found").And.Contain("aria-expanded=\"true\"").And.Contain("aria-activedescendant=\"pick-search-opt-0\"");

        await r.Click(c => { c.Search("PHISH"); return Task.CompletedTask; });
        r.Component._found.Select(h => h.Tag.Name).Should().Equal(["Spear phishing"], "the chosen one is left out");

        // Enter adds it: the box clears, for the next.
        await r.Click(c => c.Add(c._found[0]));
        chosen.Should().Contain(Delivery.Tags[1].Id);
        r.Component._text.Should().BeNull();
        (await r.Html()).Should().Contain("aria-label=\"Remove Spear phishing\"");

        await r.Click(c => { c.Search("no such tag"); return Task.CompletedTask; });
        (await r.Html()).Should().Contain("No tags match “no such tag”.").And.Contain("0 tags found");
    }

    [Fact]
    public async Task RetiredTags_AreNotOffered()
    {
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cat = Cat("Pickcheck retired", "Pickcheck live", "Pickcheck gone");
            cat.Tags[1].IsRetired = true;
            db.ScorecardCategories.Add(cat);
            await db.SaveChangesAsync();
        }
        await using var scope2 = SignedIn(app, SignIn.Analyst);
        var categories = await scope2.ServiceProvider.GetRequiredService<Tide.Application.Scorecard.ScorecardService>().ListAsync();

        Tide.Application.Scorecard.ScorecardTagSearch.Find(categories, "pickcheck").Select(h => h.Tag.Name).Should().Equal("Pickcheck live");
        var withRetired = categories.Append(Cat("Pickcheck all")).ToList();
        withRetired[^1].Tags.Add(new ScorecardTag { Name = "Pickcheck old", IsRetired = true });
        Tide.Application.Scorecard.ScorecardTagSearch.Find(withRetired, "pickcheck").Select(h => h.Tag.Name).Should().Equal("Pickcheck live");
    }

    [Fact]
    public async Task BrowseAllTags_IsFolded_UntilOpened_ItsChipsToggling()
    {
        var chosen = new HashSet<Guid> { Cloud.Tags[1].Id };
        await using var r = await Picker(chosen);
        var html = await r.Html();
        html.Should().Contain("aria-expanded=\"false\" aria-controls=\"pick-browse\"").And.Contain("7 tags in 3 categories")
            .And.Contain("<div id=\"pick-browse\" class=\"tide-disclosure-body tide-tagpick-all\" hidden>");

        await r.Click(c => { c.Browse(true); return Task.CompletedTask; });
        html = await r.Html();
        html.Should().Contain("aria-expanded=\"true\" aria-controls=\"pick-browse\"").And.Contain("<div id=\"pick-browse\" class=\"tide-disclosure-body tide-tagpick-all\">");
        var browse = html[html.IndexOf("id=\"pick-browse\"", StringComparison.Ordinal)..];
        browse.Should().Contain("aria-label=\"Delivery\"").And.Contain("aria-label=\"Cloud\"").And.Contain("aria-label=\"Impact\"")
            .And.Contain("aria-pressed=\"true\"", "the chosen one is pressed");

        await r.Click(c => c.Toggle(Impact.Tags[0].Id));
        chosen.Should().Contain(Impact.Tags[0].Id);
        (await r.Html()).Should().Contain("aria-label=\"Remove Ransomware\"");
    }

    [Fact]
    public async Task ReadOnly_ShowsTheChosenChipsAlone()
    {
        await using var r = await Picker([Delivery.Tags[0].Id], readOnly: true);
        var html = await r.Html();
        html.Should().Contain("<span class=\"tide-chip tide-chip-chosen\">Phishing</span>")
            .And.NotContain("Remove Phishing").And.NotContain("Add a tag").And.NotContain("combobox").And.NotContain("Browse all tags").And.NotContain("Spear phishing");

        await using var none = await Picker([], readOnly: true);
        (await none.Html()).Should().Contain("None chosen.").And.NotContain("Add a tag");
    }

    [Fact]
    public async Task InTheEditor_AddingAndRemovingATag_MarksTheArticleUnsaved_AndSaves()
    {
        Guid id, tagId;
        const string Tag = "Pickeditor cloud token";
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cat = Cat("Pickeditor category", Tag, "Pickeditor other");
            db.ScorecardCategories.Add(cat);
            var article = new Article { Title = "Tag picker check", AuthorId = "x", AuthorName = "X" };
            db.Articles.Add(article);
            await db.SaveChangesAsync();
            (id, tagId) = (article.Id, cat.Tags[0].Id);
        }
        await using var r = await RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id });
        var html = await r.Html();
        html.Should().Contain("Scorecard tags <span class=\"tide-tagpick-count\">0 chosen</span>").And.Contain("None chosen.")
            .And.Contain("for=\"tags-search\">Add a tag</label>").And.Contain("Browse all tags").And.NotContain("Unsaved changes");

        await r.Click(c =>
        {
            var p = c.TagPicker!;
            p.Search("pickeditor cloud");
            return p.Add(p._found.Single());
        });
        html = await r.Html();
        html.Should().Contain("Unsaved changes").And.Contain("1 chosen").And.Contain("aria-label=\"Remove Pickeditor cloud token\"").And.Contain(">1 &#xB7; 0</span>", "the section index counts it");
        var ok = false;
        await r.Click(async c => ok = await c.Save());
        ok.Should().BeTrue();
        (await Saved(id)).Should().Equal(tagId);

        await r.Click(c => c.TagPicker!.Toggle(tagId));
        (await r.Html()).Should().Contain("Unsaved changes").And.Contain("0 chosen");
        await r.Click(async c => ok = await c.Save());
        ok.Should().BeTrue();
        (await Saved(id)).Should().BeEmpty();
    }

    private async Task<IReadOnlyList<Guid>> Saved(Guid id)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<ArticleService>().GetAsync(id))!.Tags.Select(t => t.TagId).ToList();
    }

    [Fact]
    public async Task InTheEditor_SomeoneWhoCantWrite_SeesTheChosenTagsAlone()
    {
        Guid id;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tag = await db.ScorecardTags.Include(t => t.Category).FirstAsync(t => !t.IsRetired && !t.Category!.IsRetired);
            var article = new Article { Title = "Tag picker read-only", AuthorId = "x", AuthorName = "X" };
            article.Tags.Add(new ArticleTag { TagId = tag.Id });
            db.Articles.Add(article);
            await db.SaveChangesAsync();
            id = article.Id;
        }
        await using var r = await RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id }, new SignIn("dev:pick-viewer", "Val Viewer", "Viewer"));
        var html = await r.Html();
        html.Should().Contain("tide-chip tide-chip-chosen").And.Contain("1 chosen").And.NotContain("tags-search").And.NotContain("Browse all tags");
    }
}
