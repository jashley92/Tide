using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Intake;
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
        // 55 new items of a feed of their own (two pages), none with kept text. Every other one names a CVE that TIDE has a
        // record of and an article in a story covers, so the context rail has something to show for it.
        var key = Guid.NewGuid().ToString("N");
        var prefix = $"https://fetch.example/{key}/";
        var cve = $"CVE-2026-{Random.Shared.Next(90000, 99999)}";
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
                PublishedAtUtc = now.AddMinutes(-n), FetchedAtUtc = now.AddMinutes(-n), Cves = n % 2 == 1 ? cve : null,
            }));
            db.CveRecords.Add(new CveRecord { CveId = cve, IsKev = true, CvssScore = 9.8m });
            var story = new Story { Name = "Fetch story " + key[..6] };
            var article = new Article { Title = "Fetch coverage " + key[..6], AuthorName = "Robin", StoryId = story.Id };
            article.Cves.Add(new ArticleCve { CveId = cve });
            db.AddRange(story, article);
            await db.SaveChangesAsync();
            source = feed.Id;
        }
        int Fetches() { lock (app.Http.Requests) return app.Http.Requests.Count(r => r.Url.StartsWith(prefix, StringComparison.Ordinal)); }
        // Anything at all asked of the outside: the rail reads only what TIDE keeps.
        int Outbound() { lock (app.Http.Requests) return app.Http.Requests.Count; }
        var before = Outbound();

        // Someone of their own: the pane is open by itself (no choice kept yet).
        var who = new ComponentHarness.SignIn("fetch:" + key, "Fetch tester", "Analyst");
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}&row=47", @as: who);
        (await r.HtmlOnce("Fetch item 47")).Should().Contain("tide-triage-layout reading reading-auto");
        Fetches().Should().Be(0, "the pane that opened by itself shows what's kept, and offers to read the rest");
        (await r.Html()).Should().Contain("Not read yet.");

        // The context rail is drawn beside them, about the selected item: its CVE, the article covering it and its story.
        string Rail(string html) => html[html.IndexOf("<aside id=\"tide-rail\"", StringComparison.Ordinal)..];
        Rail(await r.Html()).Should().Contain(cve).And.Contain("Fetch coverage " + key[..6]).And.Contain("Fetch story " + key[..6]);

        // Down across the page boundary and back up across it, the rail following each row.
        for (var i = 0; i < 6; i++)
        {
            await r.Click(t => t.OnTriageKey("next"));
            Rail(await r.Html()).Contains("rail-covered-h", StringComparison.Ordinal).Should().Be((47 + i + 1) % 2 == 1, "the rail follows the selection");
        }
        (await r.Html()).Should().Contain("<h2 class=\"tide-reading-title\">Fetch item 53", "the pane followed onto the second page");
        for (var i = 0; i < 6; i++) await r.Click(t => t.OnTriageKey("prev"));
        Fetches().Should().Be(0, "showing, opening by itself and following the selection read only what's kept");
        Outbound().Should().Be(before, "the context rail, following the selection, asks nothing of any site");

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

    private static string Page(string words) => "<html><body><article>" + string.Concat(Enumerable.Range(0, 6).Select(i =>
        $"<p>{words} Attackers chained two gateway flaws on appliance {i}, and the vendor shipped fixes for every branch.</p>")) + "</article></body></html>";

    // Three new items of a feed of their own, newest first (A, B, C), none read yet; A's page is slow until the hold is
    // released, A's and B's answer with their text.
    private async Task<(Guid Source, Guid A, string Prefix, TaskCompletionSource Slow)> SlowFeed(string name, Guid? cId = null)
    {
        var key = Guid.NewGuid().ToString("N");
        var prefix = $"https://{name}.example/{key}/";
        Guid source, a;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var feed = new Source { Name = $"{name} feed {key[..6]}", Url = prefix + "feed" };
            var now = DateTimeOffset.UtcNow;
            var items = Enumerable.Range(0, 3).Select(n => new FeedItem
            {
                Source = feed, Title = $"{name} item {"ABC"[n]} {key[..6]}", Url = $"{prefix}{"abc"[n]}", UrlHash = $"{name}-{key}-{n}",
                PublishedAtUtc = now.AddMinutes(-n), FetchedAtUtc = now.AddMinutes(-n),
            }).ToList();
            if (cId is { } fixedC) items[2].Id = fixedC;
            db.Add(feed);
            db.AddRange(items);
            await db.SaveChangesAsync();
            (source, a) = (feed.Id, items[0].Id);
        }
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Http.Holds[prefix + "a"] = slow.Task;
        app.Http.Set(prefix + "a", Page("Slow page text about the hospitals."));
        app.Http.Set(prefix + "b", Page("Fast page text about the browser."));
        return (source, a, prefix, slow);
    }

    private int Fetches(string prefix)
    {
        lock (app.Http.Requests) return app.Http.Requests.Count(r => r.Url.StartsWith(prefix, StringComparison.Ordinal));
    }

    private async Task<bool> Kept(Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().FeedItemTexts.AnyAsync(t => t.FeedItemId == id);
    }

    // The head's "Read it here" button, up to its label.
    private static string ReadButton(string html)
    {
        var at = html.IndexOf("Read it here <span class=\"tide-kbd\">V</span>", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, "the pane offers to read the item");
        return html[html.LastIndexOf("<button", at, StringComparison.Ordinal)..at];
    }

    [Fact]
    public async Task ASlowFetch_DoesntHoldUpReadingAnotherRow_AndItsLateTextStaysWithItsOwnItem()
    {
        var (source, a, prefix, slow) = await SlowFeed("slowread");
        var who = new ComponentHarness.SignIn("slowread:" + prefix, "Slow tester", "Analyst");
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: who);
        await r.HtmlOnce("slowread item C");

        // V on A: its page is asked for and doesn't answer yet.
        await r.Press(t => t.OnTriageKey("read"));
        for (var i = 0; i < 200 && Fetches(prefix) == 0; i++) await Task.Delay(50);
        Fetches(prefix).Should().Be(1);
        await r.Drawn();
        (await r.Html()).Should().Contain("aria-busy=\"true\"");

        // On to B, C and back to B while A loads: B isn't busy and can be read, and moving fetched nothing.
        await r.Click(t => t.OnTriageKey("next"));
        await r.Click(t => t.OnTriageKey("next"));
        await r.Click(t => t.OnTriageKey("prev"));
        var html = await r.Html();
        html.Should().Contain("<h2 class=\"tide-reading-title\">slowread item B").And.Contain("aria-busy=\"false\"").And.Contain("Not read yet.");
        ReadButton(html).Should().NotContain("disabled", "B isn't the item being fetched");
        Fetches(prefix).Should().Be(1, "moving between rows fetches nothing, even while a fetch is under way");

        await r.Click(t => t.OnTriageKey("read"));
        (await r.Html()).Should().Contain("Fast page text about the browser.", "B is read while A is still loading");
        Fetches(prefix).Should().Be(2);

        // A's page answers late: its text is kept for A, and B's pane still shows B's.
        slow.SetResult();
        for (var i = 0; i < 200 && !await Kept(a); i++) await Task.Delay(50);
        (await Kept(a)).Should().BeTrue("A's fetch went on, for A");
        await Task.Delay(200);
        await r.Drawn();
        (await r.Html()).Should().Contain("Fast page text about the browser.").And.NotContain("Slow page text", "a late text never shows in another item's pane");

        // Back on A, its kept text shows, with no fetch.
        await r.Click(t => t.OnTriageKey("prev"));
        (await r.Html()).Should().Contain("<h2 class=\"tide-reading-title\">slowread item A").And.Contain("Slow page text about the hospitals.");
        Fetches(prefix).Should().Be(2, "each item was fetched once, when the analyst asked");
    }

    [Fact]
    public async Task BackOnAnItemStillBeingRead_ThePaneIsBusy_AndShowsItsTextWhenItLands()
    {
        var (source, _, prefix, slow) = await SlowFeed("backread");
        var who = new ComponentHarness.SignIn("backread:" + prefix, "Back tester", "Analyst");
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: who);
        await r.HtmlOnce("backread item C");

        await r.Press(t => t.OnTriageKey("read"));
        for (var i = 0; i < 200 && Fetches(prefix) == 0; i++) await Task.Delay(50);
        await r.Click(t => t.OnTriageKey("next"));
        (await r.Html()).Should().Contain("aria-busy=\"false\"", "B isn't being read");
        await r.Click(t => t.OnTriageKey("read"));
        (await r.Html()).Should().Contain("Fast page text about the browser.", "another row reads while A is busy");
        await r.Click(t => t.OnTriageKey("prev"));
        (await r.Html()).Should().Contain("<h2 class=\"tide-reading-title\">backread item A").And.Contain("aria-busy=\"true\"", "A still is");

        await r.Click(t => t.OnTriageKey("read"));
        Fetches(prefix).Should().Be(2, "V on an item being read doesn't fetch it twice");

        slow.SetResult();
        (await r.HtmlOnce("Slow page text about the hospitals.")).Should().Contain("Slow page text about the hospitals.").And.Contain("aria-busy=\"false\"");
    }

    [Fact]
    public async Task AFetchThatFailsAfterTheAnalystMovedOn_SaysWhyWhenTheyComeBack_WithoutAskingAgain()
        => await LateFailure(null);

    // A row id that happens to hold the failure's status digits (random ids sometimes do) says nothing about B's pane.
    [Fact]
    public async Task AFetchThatFailsAfterTheAnalystMovedOn_BsPaneCheck_IgnoresIdsThatHappenToHoldTheStatus()
        => await LateFailure(Guid.Parse("d39fea1a-1f4b-4045-ba65-38912b1270cd"));

    private async Task LateFailure(Guid? cId)
    {
        var (source, _, prefix, slow) = await SlowFeed("lateread", cId);
        app.Http.Responses[prefix + "a"] = new HttpFetch(FetchStatus.Failed, Error: "The server answered 404 Not Found.");
        var who = new ComponentHarness.SignIn("lateread:" + prefix, "Late tester", "Analyst");
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: who);
        await r.HtmlOnce("lateread item C");

        await r.Press(t => t.OnTriageKey("read"));
        for (var i = 0; i < 200 && Fetches(prefix) == 0; i++) await Task.Delay(50);
        await r.Click(t => t.OnTriageKey("next"));

        // A's site answers 404 while the analyst is on B: B's pane doesn't change.
        slow.SetResult();
        await Task.Delay(300);
        await r.Drawn();
        (await r.Html()).Should().Contain("<h2 class=\"tide-reading-title\">lateread item B").And.Contain("Not read yet.").And.NotContain("404");

        // Back on A, why it failed shows, as it would have at the time, and the site isn't asked again.
        await r.Click(t => t.OnTriageKey("prev"));
        (await r.Html()).Should().Contain("<h2 class=\"tide-reading-title\">lateread item A")
            .And.Contain("The server answered 404 Not Found. Open the site instead.").And.NotContain("Not read yet.");
        Fetches(prefix).Should().Be(1, "coming back to an item never fetches it");
    }
}
