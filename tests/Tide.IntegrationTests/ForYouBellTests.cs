using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Preferences;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Layout;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The For you bell in the top bar: its count is the person's new notices and part of its name; its panel lists them
/// with what they're about; a click or Mark all seen marks them seen; one that arrives moves the count live and shows a
/// toast, unless the person turned toasts off; and Readers have no bell. Each test signs in as a person of its own, so
/// the notices it makes are its alone.
/// </summary>
public class ForYouBellTests(TideApp app) : IClassFixture<TideApp>
{
    private static SignIn Person(string id) => new($"dev:bell-{id}", $"Bell {id}", "Analyst");

    // A notice for `who`, about a sample article, saved through the app's own context so the change feed hears it.
    private async Task<Guid> Notice(SignIn who, string? detail = null, DateTimeOffset? at = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var n = new Notice
        {
            UserId = who.UserId, Kind = NoticeKind.ArticleHandedToYou, SubjectId = (await FirstArticle()).Id, ActorId = "dev:sam", ActorName = "Sam Rivera",
            Detail = detail, CreatedAtUtc = at ?? DateTimeOffset.UtcNow,
        };
        db.Notices.Add(n);
        await db.SaveChangesAsync();
        return n.Id;
    }

    // The sample article every notice here is about.
    private async Task<Article> FirstArticle()
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Articles.AsNoTracking().OrderBy(a => a.Id).FirstAsync();
    }

    private async Task<string> ArticleTitle() => (await FirstArticle()).Title;

    [Fact]
    public async Task TheCount_IsThePersonsNewNotices_AndPartOfTheBellsName_AndThePanelListsWhatEachIsAbout()
    {
        var me = Person("list");
        await Notice(me, "Can you finish the indicators? I'm out tomorrow.");
        await Notice(me);
        await Notice(Person("someone-else"));

        await using var bell = await RenderAsync<ForYouBell>(app, "/articles", @as: me);
        var html = await bell.Html();
        html.Should().Contain("aria-label=\"For you, 2 new\"", "the count is in the bell's name, for someone who doesn't see the badge")
            .And.Contain("<span class=\"tide-bell-count\" aria-hidden=\"true\" data-count=\"2\"></span>")
            .And.Contain("aria-expanded=\"false\"").And.NotContain(ForYouBell.PanelId + "-title", "the panel is closed until the bell is pressed");

        await bell.Click(b => b.Toggle());
        html = await bell.HtmlOnce("tide-bell-list");
        html.Should().Contain("role=\"dialog\"").And.Contain("aria-expanded=\"true\"").And.Contain($"aria-controls=\"{ForYouBell.PanelId}\"");
        html.Should().Contain("Sam Rivera handed you an article").And.Contain(System.Net.WebUtility.HtmlEncode(await ArticleTitle()))
            .And.Contain("Can you finish the indicators?", "the hand-off's note comes with it")
            .And.Contain("Mark all seen");
        CountOf(html, "tide-bell-item is-new").Should().Be(2, "someone else's notice isn't listed, and opening the panel marks nothing seen");
    }

    [Fact]
    public async Task ClickingOne_MarksItSeen_AndMarkAllSeen_DoesTheRest()
    {
        var me = Person("seen");
        await Notice(me);
        await Notice(me);
        await using var bell = await RenderAsync<ForYouBell>(app, "/articles", @as: me);
        await bell.Click(b => b.Toggle());
        await bell.HtmlOnce("tide-bell-list");

        await bell.Click(b => b.Opened(b.Listed![0]));
        (await bell.Until(b => !b.IsOpen)).Should().BeTrue("the panel closes as the notice's page opens");
        (await bell.HtmlOnce("For you, 1 new")).Should().Contain("aria-label=\"For you, 1 new\"");

        await bell.Click(b => b.Toggle());
        await bell.HtmlOnce("tide-bell-list");
        await bell.Click(b => b.AllSeen());
        var html = await bell.HtmlOnce("aria-label=\"For you\"");
        html.Should().NotContain("tide-bell-count").And.NotContain("tide-bell-item is-new").And.NotContain("Mark all seen");
        CountOf(html, "class=\"tide-bell-item \"").Should().Be(2, "seen ones stay listed");
    }

    [Fact]
    public async Task OneThatArrives_MovesTheCountLive_AndShowsAToastWithALinkToIt()
    {
        var me = Person("live");
        await using var bell = await RenderAsync<ForYouBell>(app, "/articles", @as: me);
        (await bell.Html()).Should().Contain("aria-label=\"For you\"").And.NotContain("tide-bell-count");

        await Notice(me);
        (await bell.HtmlOnce("For you, 1 new")).Should().Contain("aria-label=\"For you, 1 new\"");
        var toasts = bell.Scope.ServiceProvider.GetRequiredService<ToastService>();
        (await bell.Until(_ => toasts.Toasts.Count > 0)).Should().BeTrue();
        var toast = toasts.Toasts.Should().ContainSingle().Subject;
        toast.Message.Should().Be($"Sam Rivera handed you an article: {await ArticleTitle()}");
        toast.Link!.Href.Should().StartWith("/articles/");
        toast.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task WithToastsTurnedOff_OneThatArrives_IsCountedQuietly()
    {
        var me = Person("quiet");
        await using (var scope = SignedIn(app, me))
            await scope.ServiceProvider.GetRequiredService<UserPreferenceService>().SetAsync(UserPreferences.NoticeToasts, UserPreferences.Off);
        await using var bell = await RenderAsync<ForYouBell>(app, "/articles", @as: me);

        await Notice(me);
        (await bell.HtmlOnce("For you, 1 new")).Should().Contain("aria-label=\"For you, 1 new\"");
        bell.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().BeEmpty("the person turned the toast off; the bell still counts");
    }

    [Fact]
    public async Task WithNothingYet_ThePanelSaysWhatWillShowThere()
    {
        await using var bell = await RenderAsync<ForYouBell>(app, "/articles", @as: Person("empty"));
        await bell.Click(b => b.Toggle());
        (await bell.HtmlOnce("Nothing for you yet")).Should().Contain("Nothing for you yet").And.NotContain("Mark all seen");
    }

    [Fact]
    public void TheBadge_StaysOneSmallMark()
    {
        ForYouBell.Badge(1).Should().Be("1");
        ForYouBell.Badge(9).Should().Be("9");
        ForYouBell.Badge(10).Should().Be("9+");
    }

    [Fact]
    public async Task TheBell_IsInTheTopBar_ForAnAnalyst()
    {
        var html = await app.CreateClient().GetStringAsync("/articles");
        var top = html[html.IndexOf("class=\"top-row\"", StringComparison.Ordinal)..html.IndexOf("id=\"main-content\"", StringComparison.Ordinal)];
        top.Should().Contain("class=\"tide-bell\"");
    }

    private static int CountOf(string html, string what) => html.Split(what).Length - 1;
}

public class ForYouBellReaderTests(ReaderApp app) : IClassFixture<ReaderApp>
{
    [Fact]
    public async Task AReader_HasNoBell()
    {
        var html = await app.CreateClient().GetStringAsync("/published");
        html.Should().Contain("class=\"top-row\"").And.NotContain("tide-bell");
    }
}
