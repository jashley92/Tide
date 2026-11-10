using System.Net;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Intake;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Bug round, lists and their controls: the filter bar, paging, board caps, triage grouping and the starter sets.
/// </summary>
public class ListsBugRoundTests(TideApp app) : IClassFixture<TideApp>
{
    // An address a person typed, or a link an email client cut short: a value that isn't one is ignored, as an unknown
    // ?status= or ?page= already is, and the list opens. Read as a Guid, number or flag, it failed the whole page.
    [Theory]
    [InlineData("/triage?named=1", "<h1>Triage</h1>")]
    [InlineData("/triage?source=not-a-source&since=yes&older=on", "<h1>Triage</h1>")]
    [InlineData("/triage?at=12345&row=first&promote=1", "<h1>Triage</h1>")]
    [InlineData("/actions?team=network", "<h1 tabindex=\"-1\">Actions</h1>")]
    [InlineData("/actions?rule=x&parent=99999999999", "<h1 tabindex=\"-1\">Actions</h1>")]
    [InlineData("/articles?new=yes", "<h1>Articles</h1>")]
    [InlineData("/reports/teams?days=thirty&parts=y&external=n", "Team commitments")]
    [InlineData("/actions/new?article=12ab&parent=nope", "New action")]
    [InlineData("/trends?subs=1&n=all", "<h1>Trends</h1>")]
    public async Task AValueThatIsntOne_IsIgnored_AndTheListOpens(string path, string heading)
    {
        var html = await app.CreateClient().GetStringAsync(path);
        html.Should().NotContain("Something went wrong on this page", path).And.Contain(heading, path);
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // 15 KQL queries and 10 FQL ones named for the test, and the page filtered to them, as Find does.
    private async Task<Rendered<SavedQueriesPage>> QueriesPage(string tag)
    {
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var svc = scope.ServiceProvider.GetRequiredService<SavedQueryService>();
            for (var i = 0; i < 25; i++)
                await svc.SaveAsync(new SavedQueryForm { Name = $"Edited {tag} {i:00}", Dialect = i < 15 ? "kql" : "fql", Text = "DeviceEvents | take 1" });
        }
        var r = await RenderAsync<SavedQueriesPage>(app, "/library/queries");
        await r.Click(c =>
        {
            typeof(SavedQueriesPage).GetField("_words", Private)!.SetValue(c, tag);
            typeof(SavedQueriesPage).GetMethod("Refilter", Private)!.Invoke(c, null);
            return Task.CompletedTask;
        });
        return r;
    }

    private async Task AsColleague(Func<SavedQueryService, Task> change)
    {
        await using var scope = SignedIn(app, new SignIn("dev:round-colleague", "Casey Colleague", "Analyst"));
        await change(scope.ServiceProvider.GetRequiredService<SavedQueryService>());
    }

    // A colleague's new query pushed the entry being edited (the one shown) onto the next page: the page goes with it, so
    // the form stays where the entry is shown, rather than standing above a page it isn't on (LibraryPage.EditingAway,
    // for one that's gone).
    [Fact]
    public async Task ALibraryCardPushedToTheNextPageWhileEdited_IsFollowed()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        await using var r = await QueriesPage(tag);
        var last = r.Component.OnPage[^1].Query;   // the 20th: "… 19", FQL
        await r.Click(c => { c.Pick(last.Id); c.Edit(last); return Task.CompletedTask; });

        await AsColleague(svc => svc.SaveAsync(new SavedQueryForm { Name = $"Edited {tag} 00a", Dialect = "kql", Text = "DeviceEvents | take 2" }));
        await r.Click(c => c.OnChangedElsewhere([]));

        r.Component.Page.Should().Be(2, "the card being edited is on page 2 now");
        r.Component.Selected!.Query.Id.Should().Be(last.Id, "and it's still the one shown");
        WebUtility.HtmlDecode(await r.Html()).Should().Contain($"Edit {last.Name}</h3>");
    }

    // A source of the test's own with that many dismissed items, newest first.
    private async Task<Guid> DismissedItems(int count)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feed = new Source { Name = "Round feed " + Guid.NewGuid().ToString("N")[..6], Url = $"https://round.example/{Guid.NewGuid():N}/feed" };
        db.Add(feed);
        var now = DateTimeOffset.UtcNow;
        db.AddRange(Enumerable.Range(0, count).Select(n => new FeedItem
        {
            Source = feed, Status = FeedItemStatus.Dismissed, Title = $"Round dismissed {n} {Guid.NewGuid():N}", Url = $"https://round.example/{feed.Id}/{n}",
            UrlHash = $"round-{feed.Id}-{n}", PublishedAtUtc = now.AddMinutes(-n), FetchedAtUtc = now.AddMinutes(-n),
        }));
        await db.SaveChangesAsync();
        return feed.Id;
    }

    // Triage stays open when a link changes only its query. It read only the page from a new address, so the sidebar's
    // Triage (or Back to an earlier tab) changed the address and left the list on the tab and filters it had.
    [Fact]
    public async Task Triage_ALinkToAnotherTabOrFilter_ShowsWhatTheAddressSays()
    {
        var source = await DismissedItems(3);
        await using var r = await RenderAsync<Triage>(app, $"/triage?tab=dismissed&source={source}");
        (await r.HtmlOnce("3 items")).Should().Contain("3 items");
        var status = typeof(Triage).GetField("_status", Private)!;
        var sourceId = typeof(Triage).GetField("_sourceId", Private)!;

        // The sidebar's Triage: the same page, its address now plain "triage".
        await r.Click(async t =>
        {
            t.TabParam = null;
            t.SourceParam = null;
            await (Task)typeof(Triage).GetMethod("OnParametersSetAsync", Private)!.Invoke(t, null)!;
        });

        status.GetValue(r.Component).Should().Be(FeedItemStatus.New, "the address names no tab: New");
        sourceId.GetValue(r.Component).Should().BeNull("nor a source");
        ((IReadOnlyList<InboxRow>)typeof(Triage).GetField("_rows", Private)!.GetValue(r.Component)!)
            .Should().NotContain(row => row.Item.Status == FeedItemStatus.Dismissed, "the list is New's, not the dismissed items it showed");
    }

    // A page past the end (an old link, or the list shrank) shows the last page; the address said the old page all the
    // same, so a reload or a copied link carried it, and the page's own record of its address disagreed with what it showed.
    [Fact]
    public async Task Triage_APagePastTheEnd_ShowsTheLast_AndTheAddressSaysSo()
    {
        var source = await DismissedItems(60);
        await using var r = await RenderAsync<Triage>(app, $"/triage?tab=dismissed&source={source}&page=9");
        WebUtility.HtmlDecode(await r.HtmlOnce("of 60 items")).Should().Contain("51–60 of 60 items");
        ((int)typeof(Triage).GetField("_addressPage", Private)!.GetValue(r.Component)!).Should().Be(2, "the address is written with the page shown");
    }

    [Fact]
    public async Task Actions_APagePastTheEnd_ShowsTheLast_AndTheAddressSaysSo()
    {
        var tag = "Pastend" + Guid.NewGuid().ToString("N")[..6];
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var team = await db.Teams.Select(t => t.Id).FirstAsync();
            var n = await db.Actions.MaxAsync(a => (int?)a.Number) ?? 0;
            for (var i = 0; i < 60; i++) db.Actions.Add(new ActionItem { Number = ++n, Title = $"{tag} {i}", TeamId = team });
            await db.SaveChangesAsync();
        }
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={tag}&page=9");
        WebUtility.HtmlDecode(await r.HtmlOnce("of 60 matching")).Should().Contain("51–60 of 60 matching actions");
        var applied = typeof(ActionsBoard).GetField("_applied", Private)!.GetValue(r.Component)!;
        applied.GetType().GetProperty("Page")!.GetValue(applied).Should().Be("2", "the address is written with the page shown");
    }
}
