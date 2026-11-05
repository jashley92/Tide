using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.History;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>The History view on an article and a brief: the audit log read back, newest first, for analysts only.</summary>
public class HistoryServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public HistoryServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    private HistoryService History() => new(_db, _db.User);

    [Fact]
    public async Task AnArticle_ListsWhoChangedWhat_NewestFirst_WithItsTagsAndTechniques()
    {
        var svc = _db.Articles();
        var id = await svc.CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);
        var other = await svc.CreateAsync("Someone else's story");
        await _db.Scorecard().SeedStarterSetAsync();
        var tag = (await _db.Scorecard().ListAsync())[0].Tags[0];
        var form = ArticleForm.From((await svc.GetAsync(id))!);
        form.Title = "Edge VPN zero-day: patch now";
        form.Bluf = "Exploited.";
        form.TagIds = [tag.Id];
        form.TechniqueIds = ["T1190"];
        await svc.SaveAsync(form);

        var page = await History().ArticleAsync(id);

        page.HasMore.Should().BeFalse();
        page.Entries[^1].What.Should().Be("Created", "newest first, so the create is last");
        page.Entries[^1].Changes.Should().Contain(new HistoryChange("Title", null, "Edge VPN zero-day"));
        var changed = page.Entries.Single(e => e.What == "Changed");
        changed.UserName.Should().Be("Test Analyst");
        changed.Changes.Should().Contain(new HistoryChange("Title", "Edge VPN zero-day", "Edge VPN zero-day: patch now"))
            .And.Contain(new HistoryChange("Bottom line", null, "Exploited."))
            .And.NotContain(c => c.Field.Contains("version", StringComparison.OrdinalIgnoreCase), "the edit version is noise");
        page.Entries.Should().Contain(e => e.What == "Added a scorecard tag" && e.Changes.Single().After == tag.Name)
            .And.Contain(e => e.What == "Added a technique" && e.Changes.Single().After == "T1190");
        page.Entries.SelectMany(e => e.Changes).Should().NotContain(c => c.After == "Someone else's story", "only this article's rows");
        (await History().ArticleAsync(other)).Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task AnArticle_PagesFiftyAtATime()
    {
        var svc = _db.Articles();
        var id = await svc.CreateAsync("Draft");
        for (var i = 0; i < HistoryService.PageSize + 5; i++)
        {
            var form = ArticleForm.From((await svc.GetAsync(id))!);
            form.Bluf = $"Version {i}.";
            await svc.SaveAsync(form);
        }

        var first = await History().ArticleAsync(id);
        first.Entries.Should().HaveCount(HistoryService.PageSize);
        first.HasMore.Should().BeTrue();
        first.Entries[0].Changes.Should().Contain(new HistoryChange("Bottom line", $"Version {HistoryService.PageSize + 3}.", $"Version {HistoryService.PageSize + 4}."));

        var second = await History().ArticleAsync(id, first.Next);
        second.HasMore.Should().BeFalse();
        second.Entries.Should().HaveCount(6, "the five saves left and the create");
        second.Entries[^1].What.Should().Be("Created");
    }

    [Fact]
    public async Task ABrief_ListsItsOwnChanges()
    {
        var brief = await _db.Briefs().GetOrStartAsync(new DateOnly(2026, 10, 26));
        await _db.Briefs().SetDetailsAsync(brief.Id, _db.User.UserId, "Test Analyst", "Partner briefing at 2pm.", TlpLevel.Amber);

        var page = await History().BriefAsync(brief.Id);

        page.Entries[0].What.Should().Be("Changed");
        page.Entries[0].Changes.Should().Contain(new HistoryChange("Title slide line", null, "Partner briefing at 2pm."))
            .And.Contain(new HistoryChange("Title slide line marking", "TLP:CLEAR", "TLP:AMBER"));
        page.Entries[^1].What.Should().Be("Created");
    }

    [Theory]
    [InlineData(AppRole.ActionOwner)]
    [InlineData(AppRole.Reader)]
    public async Task OnlyAnalysts_SeeTheHistory(AppRole role)
    {
        var id = await _db.Articles().CreateAsync("Draft");
        _db.User.Roles = new HashSet<AppRole> { role };

        var article = () => History().ArticleAsync(id);
        await article.Should().ThrowAsync<ForbiddenException>("the log holds every field at every marking");
        var brief = () => History().BriefAsync(Guid.NewGuid());
        await brief.Should().ThrowAsync<ForbiddenException>();
    }
}

/// <summary>The History disclosure: drawn for analysts, never for anyone else, and loaded when it's opened.</summary>
public class HistoryPanelTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:history-owner", "Owner", "ActionOwner");

    private async Task<Guid> NewArticle(string title)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var svc = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var id = await svc.CreateAsync(title);
        var form = ArticleForm.From((await svc.GetAsync(id))!);
        form.WhyItMatters = new string('w', HistoryChange.ShortLength + 40);
        await svc.SaveAsync(form);
        return id;
    }

    [Fact]
    public async Task AnAnalyst_OpensIt_AndSeesTheChanges_WithLongTextCut()
    {
        var id = await NewArticle("History on the page");
        await using var r = await RenderAsync<HistoryPanel>(app, "/articles/" + id, new() { ["Id"] = id });
        var closed = await r.Html();
        closed.Should().Contain("aria-expanded=\"false\"").And.Contain(">who changed what, and when<").And.NotContain("Created", "it loads when it's opened");

        await r.Click(p => p.Toggle());

        var html = await r.Html();
        html.Should().Contain("aria-expanded=\"true\"").And.Contain("Created").And.Contain("Changed")
            .And.Contain("Why it matters:").And.Contain("Show all of Why it matters").And.Contain("Title:");
        html.Should().NotContain(new string('w', HistoryChange.ShortLength + 40), "long text is cut until it's asked for");
    }

    [Fact]
    public async Task AnActionOwner_GetsNothing()
    {
        var id = await NewArticle("Hidden history");
        await using var r = await RenderAsync<HistoryPanel>(app, "/articles/" + id, new() { ["Id"] = id }, Owner);
        (await r.Html()).Should().NotContain("History");
    }
}
