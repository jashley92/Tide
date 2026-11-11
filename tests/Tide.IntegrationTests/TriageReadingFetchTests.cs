using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Domain.Entities;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>
/// The reading pane may open by itself, but an article is fetched from its source only when the analyst asks for it: the
/// feeds mustn't be asked for a page each time the selection moves. Opening Triage and moving the selection (on a page
/// and across pages) shows only text already kept; V on an item with none fetches it, once.
/// </summary>
public class TriageReadingFetchTests(PullApp app) : IClassFixture<PullApp>
{
    [Fact]
    public async Task MovingTheSelection_NeverFetches_AndVFetchesOnce()
    {
        // 55 new items of a feed of their own (two pages), none with kept text.
        var key = Guid.NewGuid().ToString("N");
        var prefix = $"https://fetch.example/{key}/";
        Guid source;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var feed = new Source { Name = "Fetch feed " + key[..6], Url = prefix + "feed" };
            var now = DateTimeOffset.UtcNow;
            db.Add(feed);
            db.AddRange(Enumerable.Range(0, 55).Select(n => new FeedItem
            {
                Source = feed, Title = $"Fetch item {n:00} {key[..6]}", Url = $"{prefix}{n}", UrlHash = $"fetch-{key}-{n}",
                PublishedAtUtc = now.AddMinutes(-n), FetchedAtUtc = now.AddMinutes(-n),
            }));
            await db.SaveChangesAsync();
            source = feed.Id;
        }
        int Fetches() { lock (app.Http.Requests) return app.Http.Requests.Count(r => r.Url.StartsWith(prefix, StringComparison.Ordinal)); }

        // Someone of their own: the pane is open by itself (no choice kept yet).
        var who = new ComponentHarness.SignIn("fetch:" + key, "Fetch tester", "Analyst");
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}&row=47", @as: who);
        (await r.HtmlOnce("Fetch item 47")).Should().Contain("tide-triage-layout reading reading-auto");
        Fetches().Should().Be(0, "the pane that opened by itself shows what's kept, and offers to read the rest");
        (await r.Html()).Should().Contain("Not read yet.");

        // Down across the page boundary and back up across it.
        for (var i = 0; i < 6; i++) await r.Click(t => t.OnTriageKey("next"));
        (await r.Html()).Should().Contain("<h2 class=\"tide-reading-title\">Fetch item 53", "the pane followed onto the second page");
        for (var i = 0; i < 6; i++) await r.Click(t => t.OnTriageKey("prev"));
        Fetches().Should().Be(0, "showing, opening by itself and following the selection read only what's kept");

        // V on the selected item, with nothing kept: one fetch. V again closes the pane, and fetches nothing more.
        await r.Click(t => t.OnTriageKey("toggle-reading"));
        Fetches().Should().Be(1);
        await r.Click(t => t.OnTriageKey("toggle-reading"));
        Fetches().Should().Be(1);
        (await r.Html()).Should().NotContain("<aside class=\"tide-card tide-reading\"");
        // V once more opens it on what was tried (here, why it failed), without asking the site again.
        await r.Click(t => t.OnTriageKey("read"));
        Fetches().Should().Be(1, "pressing V again and again never fetches the item more than once");
        (await r.Html()).Should().Contain("<aside class=\"tide-card tide-reading\"");
    }

    [Fact]
    public async Task ThePaneThatOpenedByItself_ReadsNothing_WhileTheWindowIsTooNarrowToShowIt_AndCatchesUpWhenItIs()
    {
        var key = Guid.NewGuid().ToString("N");
        Guid source;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var feed = new Source { Name = "Narrow feed " + key[..6], Url = $"https://narrow.example/{key}/feed" };
            var now = DateTimeOffset.UtcNow;
            db.Add(feed);
            db.AddRange(Enumerable.Range(0, 3).Select(n => new FeedItem
            {
                Source = feed, Title = $"Narrow item {n} {key[..6]}", Url = $"https://narrow.example/{key}/{n}", UrlHash = $"narrow-{key}-{n}",
                PublishedAtUtc = now.AddMinutes(-n), FetchedAtUtc = now.AddMinutes(-n),
            }));
            await db.SaveChangesAsync();
            source = feed.Id;
        }
        var who = new ComponentHarness.SignIn("narrow:" + key, "Narrow tester", "Analyst");
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: who);
        (await r.HtmlOnce("Narrow item 0")).Should().Contain("reading reading-auto").And.Contain($"<h2 class=\"tide-reading-title\">Narrow item 0 {key[..6]}");

        // Below 1400px the pane is hidden: moving the selection reads nothing for it, so it still names the first item.
        await r.Click(t => t.OnReadingRoom(false));
        await r.Click(t => t.OnTriageKey("next"));
        (await r.Html()).Should().Contain($"<h2 class=\"tide-reading-title\">Narrow item 0 {key[..6]}", "a hidden pane doesn't follow");

        // The window widens: the pane shows, with the selected item's kept text.
        await r.Click(t => t.OnReadingRoom(true));
        (await r.Html()).Should().Contain($"<h2 class=\"tide-reading-title\">Narrow item 1 {key[..6]}");
    }
}
