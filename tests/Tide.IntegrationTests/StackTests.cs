using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Intake;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Tide.IntegrationTests;

public class StackServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public StackServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AnalystsKeepTheList_NamesAreUnique_AndOwnersCanOnlyRead()
    {
        var stack = _db.Stack();
        var id = await stack.SaveAsync(new StackForm { Name = " GitLab ", OtherNames = "GitLab CE, gitlab; GitLab Runner", Versions = "17.11.3", Notes = "Two hosts.\nThursdays." });

        var p = (await stack.GetAsync(id))!.Product;
        (p.Name, p.OtherNames, p.Versions, p.Notes).Should().Be(("GitLab", "GitLab CE; GitLab Runner", "17.11.3", "Two hosts.\nThursdays."), "the name itself isn't repeated as another name");
        await FluentActions.Invoking(() => stack.SaveAsync(new StackForm { Name = "gitlab" })).Should().ThrowAsync<ArgumentException>().WithMessage("*already on the list*");
        await FluentActions.Invoking(() => stack.SaveAsync(new StackForm { Name = "  " })).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => stack.SaveAsync(new StackForm { Name = "X", OtherNames = "--" })).Should().ThrowAsync<ArgumentException>().WithMessage("*letter or digit*");
        (await stack.AddByNameAsync("GitLab")).Should().Be(id, "adding by name finds what's there");

        await stack.SetRetiredAsync(id, true);
        (await stack.ListAsync()).Should().BeEmpty();
        (await stack.ListAsync(includeRetired: true)).Should().ContainSingle();
        (await stack.AddByNameAsync("GitLab")).Should().Be(id);
        (await stack.ListAsync()).Should().ContainSingle("adding a retired product brings it back");

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        (await stack.ListAsync()).Should().ContainSingle();
        await FluentActions.Invoking(() => stack.SaveAsync(new StackForm { Name = "Pillow" })).Should().ThrowAsync<ForbiddenException>();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Invoking(() => stack.ListAsync()).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public void AProduct_MatchesByAnyOfItsNames_AsWholeWords()
    {
        var entries = new[] { StackService.Entry(new StackProduct { Name = "Ivanti Connect Secure", OtherNames = "Pulse Secure" }), StackService.Entry(new StackProduct { Name = "Go" }) };

        StackService.Match(entries, "Attackers chain three Ivanti Connect-Secure flaws", null)!.Name.Should().Be("Ivanti Connect Secure");
        StackService.Match(entries, "Old VPN bug", "Pulse secure appliances are targeted again")!.Name.Should().Be("Ivanti Connect Secure");
        StackService.Match(entries, "Google patches Chrome", null).Should().BeNull("'Go' isn't the start of 'Google'");
        StackService.Match(entries, "Unrelated", new string('x', StackService.SummaryStart) + " Pulse Secure").Should().BeNull("only the start of a summary is read");
        var match = StackService.Matcher(entries);
        match("Attackers chain three Ivanti Connect-Secure flaws", null)!.Name.Should().Be("Ivanti Connect Secure", "the matcher for many items reads alike");
        match("Old VPN bug", "Pulse secure appliances are targeted again")!.Name.Should().Be("Ivanti Connect Secure");
        match("Google patches Chrome", null).Should().BeNull();
        match("Go 1.26 fixes a flaw", null)!.Name.Should().Be("Go");
        match("Unrelated", new string('x', StackService.SummaryStart) + " Pulse Secure").Should().BeNull();
        StackService.Matcher([])("Go", null).Should().BeNull();
        StackService.SplitNames("a; b,\nB ;; c").Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task AProductsHistory_HasItsTaggedArticles_ThoseNamingIt_AndTheirActions()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };   // the team needs an administrator
        var stack = _db.Stack();
        var gitlab = await stack.SaveAsync(new StackForm { Name = "GitLab" });
        var articles = _db.Articles();
        var named = await articles.CreateAsync("GitLab patches a max-severity RCE", "Vendor", null, TlpLevel.Clear);
        var tagged = await articles.CreateAsync("Runner token leak in CI", "Vendor", null, TlpLevel.Clear);
        var other = await articles.CreateAsync("Pillow heap overflow", "Vendor", null, TlpLevel.Clear);
        await stack.TagArticleAsync(tagged, gitlab);
        var team = await _db.Teams().AddAsync("VM", null, null);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch the hosts", TeamId = team, ArticleId = named });
        await _db.Actions().CreateAsync(new ActionForm { Title = "Rotate GitLab runner tokens", TeamId = team });
        await _db.Actions().CreateAsync(new ActionForm { Title = "Upgrade Pillow", TeamId = team, ArticleId = other });

        var d = (await stack.GetAsync(gitlab))!;

        d.Articles.Select(a => (a.Title, a.Tagged)).Should().BeEquivalentTo([("GitLab patches a max-severity RCE", false), ("Runner token leak in CI", true)]);
        d.Actions.Select(a => a.Title).Should().BeEquivalentTo("Patch the hosts", "Rotate GitLab runner tokens");
        var row = (await stack.ListAsync()).Single();
        (row.Articles, row.OpenActions).Should().Be((2, 1), "one open action raised from its articles; the other names it");
    }

    [Fact]
    public async Task AnArticle_IsTaggedWithAnyNumberOfProducts_AndOffersTheOnesItNames()
    {
        var stack = _db.Stack();
        var gitlab = await stack.SaveAsync(new StackForm { Name = "GitLab" });
        var fortigate = await stack.SaveAsync(new StackForm { Name = "FortiGate", OtherNames = "FortiOS" });
        var article = await _db.Articles().CreateAsync("FortiOS and GitLab flaws exploited", "Vendor", null, TlpLevel.Clear);

        var before = await stack.ForArticleAsync(article);
        before.Tagged.Should().BeEmpty("tagging is optional; naming a product only suggests it");
        before.Suggested.Select(p => p.Name).Should().Equal("FortiGate", "GitLab");

        await stack.TagArticleAsync(article, gitlab);
        var after = await stack.ForArticleAsync(article);
        after.Tagged.Select(p => p.Name).Should().Equal("GitLab");
        after.Suggested.Select(p => p.Name).Should().Equal("FortiGate");

        await stack.TagArticleAsync(article, fortigate);
        (await stack.ForArticleAsync(article)).Tagged.Should().HaveCount(2);
        await stack.UntagArticleAsync(article, gitlab);
        await stack.UntagArticleAsync(article, fortigate);
        (await stack.ForArticleAsync(article)).Tagged.Should().BeEmpty();
        await stack.UntagArticleAsync(article, fortigate);
        await FluentActions.Invoking(() => stack.TagArticleAsync(article, Guid.NewGuid())).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TwoPeopleTaggingAnArticle_KeepBothTags_AndTaggingTheSameOneTwiceIsFine()
    {
        var stack = _db.Stack();
        var gitlab = await stack.SaveAsync(new StackForm { Name = "GitLab" });
        var okta = await stack.SaveAsync(new StackForm { Name = "Okta" });
        var article = await _db.Articles().CreateAsync("Two tags", "Vendor", null, TlpLevel.Clear);
        // Robin's card and Sam's card both opened on an untagged article.
        (await stack.ForArticleAsync(article)).Tagged.Should().BeEmpty();

        await Task.WhenAll(_db.Stack().TagArticleAsync(article, gitlab), _db.Stack().TagArticleAsync(article, okta));
        await Task.WhenAll(_db.Stack().TagArticleAsync(article, okta), _db.Stack().TagArticleAsync(article, okta));
        await stack.UntagArticleAsync(article, Guid.NewGuid());

        (await stack.ForArticleAsync(article)).Tagged.Select(p => p.Name).Should().BeEquivalentTo(["GitLab", "Okta"], "each tag is its own change, not the list a card loaded");
    }

    [Fact]
    public async Task Triage_LiftsItemsNamingWhatWeRun_SaysWhich_AndNeverAgesThem()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        await _db.Stack().SaveAsync(new StackForm { Name = "GitLab" });
        var source = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "News", Url = "https://news.example.com/feed.xml", PollMinutes = 30 });
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = _db.Context())
        {
            FeedItem Item(string t, int daysAgo) => new()
            {
                SourceId = source, Title = t, Url = "https://x.example/" + t.Replace(' ', '-'), UrlHash = t, PublishedAtUtc = now.AddDays(-daysAgo), FetchedAtUtc = now.AddDays(-daysAgo),
            };
            ctx.FeedItems.AddRange(Item("fresh story", 1), Item("stale story", 10), Item("stale GitLab runner fix", 10), Item("new GitLab advisory", 2));
            await ctx.SaveChangesAsync();
        }
        var intake = _db.Intake(new IntakeOptions { Enabled = true, MinPollMinutes = 5, AgeDays = 7 });

        var inNew = await intake.InboxAsync(new InboxFilter());
        inNew.Select(r => r.Item.Title).Should().BeEquivalentTo("fresh story", "stale GitLab runner fix", "new GitLab advisory");
        inNew.Take(2).Select(r => r.WeRun?.Name).Should().Equal("GitLab", "GitLab");
        inNew[^1].WeRun.Should().BeNull();
        (await intake.OlderCountAsync()).Should().Be(1, "only the stale story aged; the GitLab one never does");
    }
}

public class StackPageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task OurStack_ListsTheProducts_AndAProductsPageEditsAndShowsItsHistory()
    {
        Guid id;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            id = await scope.ServiceProvider.GetRequiredService<StackService>().SaveAsync(new StackForm { Name = "Page Test Product", Versions = "1.2.3", Notes = "Owned by **ops**." });

        await using (var list = await ComponentHarness.RenderAsync<StackPage>(app, "/stack"))
            (await list.Html()).Should().Contain(">Our stack</h1>").And.Contain($"href=\"stack/{id}\">Page Test Product</a>").And.Contain("1.2.3").And.Contain("Add a product");

        await using var page = await ComponentHarness.RenderAsync<StackPage>(app, $"/stack/{id}", new() { ["Id"] = id });
        var html = await page.Html();
        html.Should().Contain("id=\"f-versions\"").And.Contain("id=\"f-notes\"").And.Contain(">Articles <span class=\"tide-count\">(0)</span>").And.Contain(">Retire</button>");
    }

    // An article's open "we run" card shows a colleague's tag and untag: they're heard on the article's tags topic.
    [Fact]
    public async Task AnOpenCard_ShowsAColleaguesTagAndUntag()
    {
        Guid article, product;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            product = await scope.ServiceProvider.GetRequiredService<StackService>().SaveAsync(new StackForm { Name = "Card Test Gateway" });
            article = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Stack card tagging", "Vendor", null, TlpLevel.Clear);
        }
        await using var robin = await ComponentHarness.RenderAsync<Tide.Web.Components.Shared.ArticleStackCard>(app, $"/articles/{article}",
            new() { ["ArticleId"] = article, ["CanWrite"] = true });
        (await robin.Html()).Should().NotContain("Untag Card Test Gateway");

        await using (var sam = ComponentHarness.SignedIn(app, new ComponentHarness.SignIn("dev:sam", "Sam", "Analyst")))
            await sam.ServiceProvider.GetRequiredService<StackService>().TagArticleAsync(article, product);
        (await robin.HtmlOnce("Untag Card Test Gateway")).Should().Contain("Untag Card Test Gateway");

        await using (var sam = ComponentHarness.SignedIn(app, new ComponentHarness.SignIn("dev:sam", "Sam", "Analyst")))
            await sam.ServiceProvider.GetRequiredService<StackService>().UntagArticleAsync(article, product);
        for (var i = 0; i < 50 && (await robin.Html()).Contains("Untag Card Test Gateway", StringComparison.Ordinal); i++) await Task.Delay(100);
        (await robin.Html()).Should().NotContain("Untag Card Test Gateway");
    }
}
