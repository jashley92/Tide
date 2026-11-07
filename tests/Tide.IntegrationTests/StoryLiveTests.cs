using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Intake;
using Tide.Application.Security;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>The owner's decision: a story's page follows its own articles (linked, taken off, or changed), not others.</summary>
public class StoryTopicTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly List<Change> _heard = [];

    public StoryTopicTests()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        _db.Feed.Changed += c => { lock (_heard) _heard.Add(c); };
    }

    public void Dispose() => _db.Dispose();

    private List<string> Heard()
    {
        lock (_heard)
        {
            var topics = _heard.Select(c => c.Topic).ToList();
            _heard.Clear();
            return topics;
        }
    }

    [Fact]
    public async Task LinkingMovingAndTakingOffAnArticle_IsHeardOnEachStoryInvolved_AndItsEditsOnItsStory()
    {
        var first = await _db.Stories().CreateAsync("Edge VPN campaign", null);
        var second = await _db.Stories().CreateAsync("Ransomware wave", null);
        var article = await _db.Articles().CreateAsync("Edge VPN exploited", "Vendor", null, TlpLevel.Clear);
        Heard();

        await _db.Stories().LinkAsync(article, first);
        Heard().Should().Contain(ChangeFeed.Story(first)).And.NotContain(ChangeFeed.Story(second));

        await _db.Stories().LinkAsync(article, second);
        Heard().Should().Contain([ChangeFeed.Story(first), ChangeFeed.Story(second)], "the story it left and the one it joined");

        await _db.Stories().UpdateAsync(second, "Ransomware wave, autumn", null, StoryStatus.Active);
        Heard().Should().Contain(ChangeFeed.Story(second)).And.Contain(ChangeFeed.Stories);

        await _db.Stories().LinkAsync(article, null);
        Heard().Should().Contain(ChangeFeed.Story(second)).And.NotContain(ChangeFeed.Story(first));
    }
}

/// <summary>The story page: re-read when a colleague links, takes off or changes one of its articles; silent for others.</summary>
public class StoryLivePageTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Colleague = new("dev:story-robin", "Robin", "Analyst");

    private static IReadOnlyList<Change> ByRobin(string topic) => [new Change(topic, Colleague.UserId, Colleague.Name, Guid.NewGuid())];

    private static List<string> Toasts<T>(Rendered<T> page) =>
        page.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Select(t => t.Message).ToList();

    // Robin's story service, saving through the page's own origin, which its live feed skips: the page hears Robin's change
    // only when the test hands it over (OnChangedElsewhere), never between the test's steps.
    private static StoryService Unheard(AsyncServiceScope robin, Rendered<Stories> page) =>
        ActivatorUtilities.CreateInstance<StoryService>(robin.ServiceProvider, page.Scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>());

    private async Task<(Guid Story, Guid Article)> StoryAndArticle(string name)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var story = await scope.ServiceProvider.GetRequiredService<StoryService>().CreateAsync(name + " " + Guid.NewGuid().ToString("N")[..6], null);
        var article = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(name + " article", "Vendor", null, TlpLevel.Clear);
        return (story, article);
    }

    private Task<Rendered<Stories>> Open(Guid story) =>
        RenderAsync<Stories>(app, $"/stories/{story}", new Dictionary<string, object?> { [nameof(Stories.Id)] = (Guid?)story });

    [Fact]
    public async Task AnArticleAColleagueLinks_ShowsOnTheStorysPage_ByItself()
    {
        var (story, article) = await StoryAndArticle("Live link");
        await using var page = await Open(story);
        page.Component.Detail!.Articles.Should().BeEmpty();

        await using (var robin = SignedIn(app, Colleague))
            await robin.ServiceProvider.GetRequiredService<StoryService>().LinkAsync(article, story);

        (await page.Until(p => p.Detail?.Articles.Count == 1)).Should().BeTrue("the link is heard on the story's own topic");
    }

    [Fact]
    public async Task LinkingAndTakingOffAnArticle_ReReadsTheStory_KeepingWhatsTypedInEdit()
    {
        var (story, article) = await StoryAndArticle("Live typed");
        await using var page = await Open(story);
        await page.Click(p => { p.StartEdit(); p.TypedName = "Typed, not saved"; return Task.CompletedTask; });

        await using (var robin = SignedIn(app, Colleague))
            await Unheard(robin, page).LinkAsync(article, story);
        page.Component.Detail!.Articles.Should().BeEmpty("the page hasn't heard it yet");
        page.Component.Wanted(ByRobin(ChangeFeed.Story(story))[0]).Should().BeTrue();
        await page.Click(p => p.OnChangedElsewhere(ByRobin(ChangeFeed.Story(story))));

        page.Component.Detail!.Articles.Select(a => a.Id).Should().Equal(article);
        page.Component.TypedName.Should().Be("Typed, not saved", "what's typed stays");
        Toasts(page).Should().Contain("Robin updated this story.");

        await using (var robin = SignedIn(app, Colleague))
            await Unheard(robin, page).LinkAsync(article, null);
        await page.Click(p => p.OnChangedElsewhere(ByRobin(ChangeFeed.Story(story))));

        page.Component.Detail!.Articles.Should().BeEmpty();
        page.Component.TypedName.Should().Be("Typed, not saved");
    }

    [Fact]
    public async Task TheStorysPage_FollowsItsOwnArticles_AndNotOthers()
    {
        var (story, mine) = await StoryAndArticle("Live follow");
        var (other, theirs) = await StoryAndArticle("Live other");
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            await scope.ServiceProvider.GetRequiredService<StoryService>().LinkAsync(mine, story);
            await scope.ServiceProvider.GetRequiredService<StoryService>().LinkAsync(theirs, other);
        }
        await using var page = await Open(story);
        bool Wants(string topic) => page.Renderer.Dispatcher.InvokeAsync(() => page.Component.Wanted(ByRobin(topic)[0])).GetAwaiter().GetResult();

        Wants(ChangeFeed.Story(story)).Should().BeTrue("the story, or an article linked to it or taken off it");
        Wants(ChangeFeed.Article(mine)).Should().BeTrue("one of its articles changed (its CVEs and techniques too)");
        Wants(ChangeFeed.ArticleTags(mine)).Should().BeTrue("one of its articles' adversary tags, its Actors");
        Wants(ChangeFeed.Article(theirs)).Should().BeFalse("another story's article");
        Wants(ChangeFeed.Articles).Should().BeFalse("any article: only the list's counts");
        Wants(ChangeFeed.Story(other)).Should().BeFalse("another story");
        Wants(ChangeFeed.Stories).Should().BeFalse("any story: the list");

        // A change to its article is shown, and told.
        await using (var robin = SignedIn(app, Colleague))
        {
            var articles = ActivatorUtilities.CreateInstance<ArticleService>(robin.ServiceProvider, page.Scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>());
            var form = ArticleForm.From((await articles.GetAsync(mine))!);
            form.Title = "Live follow, retitled";
            await articles.SaveAsync(form);
        }
        await page.Click(p => p.OnChangedElsewhere(ByRobin(ChangeFeed.Article(mine))));
        page.Component.Detail!.Articles.Single().Title.Should().Be("Live follow, retitled");
    }
}
