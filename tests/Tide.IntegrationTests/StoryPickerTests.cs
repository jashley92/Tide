using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Intake;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>The story picker's search: a few stories at a time however many there are, closed ones only when asked for.</summary>
public class StorySearchTests : IDisposable
{
    private TestDb _db = new();

    public StorySearchTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task WithNothingTyped_TheOpenStoriesWithTheNewestArticlesCome_AFewAtATime(string provider)
    {
        _db = _db.On(provider);
        var stories = _db.Stories();
        var ids = new List<Guid>();
        for (var i = 0; i < StoryService.PickLimit + 4; i++) ids.Add(await stories.CreateAsync($"Campaign {i:00}", null));
        var closed = await stories.CreateAsync("Campaign closed", null);
        await stories.UpdateAsync(closed, "Campaign closed", null, StoryStatus.Closed);
        var article = await _db.Articles().CreateAsync("Latest report");
        await stories.LinkAsync(article, ids[3]);

        var found = await stories.FindAsync(null);

        found.Should().HaveCount(StoryService.PickLimit);
        found[0].Id.Should().Be(ids[3], "it has the newest article");
        found.Should().NotContain(r => r.Id == closed, "a closed story waits until it's searched for");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Typing_FindsAnyStoryByName_ClosedOnesToo_AndTakesWildcardsAsText(string provider)
    {
        _db = _db.On(provider);
        var stories = _db.Stories();
        var open = await stories.CreateAsync("Ivanti Connect Secure exploitation", null);
        var closed = await stories.CreateAsync("Ivanti EPMM zero-day", null);
        await stories.UpdateAsync(closed, "Ivanti EPMM zero-day", null, StoryStatus.Closed);
        await stories.CreateAsync("100% Scattered Spider", null);

        (await stories.FindAsync("ivanti")).Select(r => r.Id).Should().Equal(open, closed);
        (await stories.FindAsync("%")).Select(r => r.Name).Should().Equal("100% Scattered Spider");
        (await stories.RowAsync(closed))!.Status.Should().Be(StoryStatus.Closed);
        (await stories.RowAsync(Guid.NewGuid())).Should().BeNull();
    }
}

/// <summary>The picker as an analyst sees it in the editor.</summary>
public class StoryPickerRenderTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task ThePicker_SearchesAsYouType_OffersToStartAStory_AndShowsTheStoryOnceIn()
    {
        Guid articleId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            articleId = (await db.Articles.FirstAsync(a => a.StoryId == null)).Id;
        }
        await using var r = await ComponentHarness.RenderAsync<StoryPicker>(app, $"/articles/{articleId}", new() { ["ArticleId"] = articleId });
        (await r.Html()).Should().Contain("id=\"story-pick\"").And.Contain("Find a story, or name a new one");

        r.Component._text = "Picker test story";
        await r.Click(c => c.Find());
        (await r.Html()).Should().Contain("Start a story called “Picker test story”");

        await r.Click(c => c.StartStory());
        using var check = app.Services.CreateScope();
        var story = await check.ServiceProvider.GetRequiredService<AppDbContext>().Stories.SingleAsync(s => s.Name == "Picker test story");
        (await check.ServiceProvider.GetRequiredService<AppDbContext>().Articles.SingleAsync(a => a.Id == articleId)).StoryId.Should().Be(story.Id);
    }
}
