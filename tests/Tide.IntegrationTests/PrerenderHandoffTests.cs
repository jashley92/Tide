using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Services;
namespace Tide.IntegrationTests;

/// <summary>A prerendered page hands what it read to its interactive render, so a reload draws the page once instead
/// of the page, its loading state, then the page again.</summary>
public class PrerenderHandoffTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void WhatsKept_IsTakenOnce_ByTheSameUser_WhileItsFresh()
    {
        var clock = new Clock();
        var handoff = new PrerenderStore(clock);
        var state = new object();

        var key = handoff.Keep("S-1-5-21-A", state);
        handoff.Take<object>(key, "S-1-5-21-A").Should().BeSameAs(state);
        handoff.Take<object>(key, "S-1-5-21-A").Should().BeNull("it's taken once");

        // Someone else holding the key gets nothing, and it's gone for its owner too.
        var other = handoff.Keep("S-1-5-21-A", state);
        handoff.Take<object>(other, "S-1-5-21-B").Should().BeNull();
        handoff.Take<object>(other, "S-1-5-21-A").Should().BeNull();

        // A circuit that comes too late, or not at all, finds nothing; a later prerender sweeps what was left.
        var late = handoff.Keep("S-1-5-21-A", state);
        clock.Now += PrerenderStore.Lifetime;
        handoff.Take<object>(late, "S-1-5-21-A").Should().BeNull();
        handoff.Keep("S-1-5-21-A", state);
        clock.Now += PrerenderStore.Lifetime;
        handoff.Keep("S-1-5-21-A", state);
        handoff.Count.Should().Be(1, "the expired one was swept");

        handoff.Take<object>(null, "S-1-5-21-A").Should().BeNull();
        handoff.Take<object>("not a key", "S-1-5-21-A").Should().BeNull();
        handoff.Take<string>(handoff.Keep("S-1-5-21-A", state), "S-1-5-21-A").Should().BeNull("it's another kind of state");
    }

    [Fact]
    public void TheStore_KeepsAtMostItsCapacity_DroppingTheOldest_AndNeverHandsOnAnExpiredOne()
    {
        var clock = new Clock();
        var store = new PrerenderStore(clock);
        var first = store.Keep("S-1-5-21-A", "first");
        for (var i = 1; i < PrerenderStore.Capacity; i++) store.Keep("S-1-5-21-A", "more");
        store.Count.Should().Be(PrerenderStore.Capacity);
        var last = store.Keep("S-1-5-21-A", "last");
        store.Count.Should().Be(PrerenderStore.Capacity, "the oldest made room");
        store.Take<string>(first, "S-1-5-21-A").Should().BeNull();

        // Expired between sweeps: still not handed on.
        var late = store.Keep("S-1-5-21-A", "late");
        clock.Now += PrerenderStore.Lifetime;
        store.Take<string>(late, "S-1-5-21-A").Should().BeNull();
        store.Take<string>(last, "S-1-5-21-A").Should().BeNull();
    }

    [Fact]
    public void TheHandoff_KeepsOncePerPrerender_AndTakesOnlyOnceInteractive()
    {
        var store = new PrerenderStore(new Clock());
        var handoff = new PrerenderHandoff(store, new FakeUser(AppRole.Analyst) { UserId = "S-1-5-21-A" }, new ChangeFeed());
        RendererInfo prerendering = new("Static", isInteractive: false), interactive = new("Server", isInteractive: true);
        var state = new object();

        var key = handoff.Keep(prerendering, state, null);
        handoff.Keep(prerendering, state, key).Should().Be(key, "kept once, however often its parameters are set");
        store.Count.Should().Be(1);
        handoff.Take<object>(prerendering, key).Should().BeNull("nothing is taken while prerendering");
        handoff.Keep(interactive, state, key).Should().BeNull("there's nothing to hand on once interactive");
        handoff.Take<object>(interactive, key).Should().BeSameAs(state);
        store.Count.Should().Be(0);
    }

    [Fact]
    public void TheHandoff_SaysWhetherAnythingWasSaved_SinceThePrerenderBeganReading()
    {
        var store = new PrerenderStore(new Clock());
        var feed = new ChangeFeed();
        var user = new FakeUser(AppRole.Analyst) { UserId = "S-1-5-21-A" };
        RendererInfo prerendering = new("Static", isInteractive: false), interactive = new("Server", isInteractive: true);

        // Nothing saved between the prerender and its circuit: what it read is current, so the circuit needn't read again.
        var quiet = new PrerenderHandoff(store, user, feed);
        var key = quiet.Keep(prerendering, "read", null);
        new PrerenderHandoff(store, user, feed).Take<string>(interactive, key, out var current).Should().Be("read");
        current.Should().BeTrue();

        // A save while the prerender was still reading (after its scope began): read again, though the state is still handed on.
        var busy = new PrerenderHandoff(store, user, feed);
        feed.Publish(ChangeFeed.Articles);
        key = busy.Keep(prerendering, "read", null);
        new PrerenderHandoff(store, user, feed).Take<string>(interactive, key, out current).Should().Be("read");
        current.Should().BeFalse("something changed after the prerender began reading");

        // A save after the prerender, before its circuit came: read again too.
        key = new PrerenderHandoff(store, user, feed).Keep(prerendering, "read", null);
        feed.Publish(ChangeFeed.Triage);
        new PrerenderHandoff(store, user, feed).Take<string>(interactive, key, out current).Should().Be("read");
        current.Should().BeFalse();

        // Nothing to take: not current, whatever the feed says.
        new PrerenderHandoff(store, user, feed).Take<string>(interactive, key, out current).Should().BeNull();
        current.Should().BeFalse();
    }

    [Fact]
    public async Task EveryWrite_MovesTheFeedsCount_EvenOneNoPageFollows_AndAReadDoesNot()
    {
        using var app = new TideApp();
        var feed = app.Services.GetRequiredService<ChangeFeed>();
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = "w-" + Guid.NewGuid().ToString("N")[..8];

        var before = feed.Version;
        _ = await db.Teams.AsNoTracking().ToListAsync();
        feed.Version.Should().Be(before, "a read writes nothing");

        // A team: no page follows it, so the feed tells no one, but a prerender that read it is out of date.
        db.Teams.Add(new Team { Key = key, Name = "Write count " + key });
        await db.SaveChangesAsync();
        feed.Version.Should().BeGreaterThan(before);

        // A bulk update skips SaveChanges altogether.
        var saved = feed.Version;
        await db.Teams.Where(t => t.Key == key).ExecuteUpdateAsync(u => u.SetProperty(t => t.Description, "changed"));
        feed.Version.Should().BeGreaterThan(saved);
    }

    [Fact]
    public async Task OpeningToday_WritesNothing_SoItsCircuitNeedNotReadAgain()
    {
        using var app = new TideApp();
        var feed = app.Services.GetRequiredService<ChangeFeed>();
        var client = app.CreateClient();
        await client.GetStringAsync("/");   // the first visit records who's signed in

        var before = feed.Version;
        await client.GetStringAsync("/");

        feed.Version.Should().Be(before, "a page that writes as it opens would always read twice");
    }

    [Fact]
    public async Task TodaysPrerender_KeepsWhatItRead_ForItsInteractiveRender()
    {
        using var app = new TideApp();
        var handoff = app.Services.GetRequiredService<PrerenderStore>();
        var before = handoff.Count;

        var html = await app.CreateClient().GetStringAsync("/");

        html.Should().Contain("id=\"running-order-h\"");
        handoff.Count.Should().Be(before + 1, "the prerender kept its brief for the circuit to start from");
    }

    [Fact]
    public async Task TriagesPrerender_KeepsTheInboxItRead_ForItsInteractiveRender()
    {
        using var app = new TideApp();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            var source = new Source { Name = "Handoff source", Url = "https://handoff.example/feed" };
            var at = DateTimeOffset.UtcNow;
            db.AddRange(source, new FeedItem
            {
                Source = source, Title = "handoff: vendor fixes an exploited VPN bug", Url = "https://handoff.example/1", UrlHash = "handoff-triage-1",
                PublishedAtUtc = at, FetchedAtUtc = at, Status = FeedItemStatus.New,
            });
            await db.SaveChangesAsync();
        }
        var handoff = app.Services.GetRequiredService<PrerenderStore>();
        var before = handoff.Count;

        var html = await app.CreateClient().GetStringAsync("/triage");

        html.Should().Contain("handoff: vendor fixes an exploited VPN bug");
        handoff.Count.Should().Be(before + 1, "the prerender kept its inbox for the circuit to start from");
    }

    [Theory]
    [InlineData("/actions?view=open&layout=list", "Sweep VPN and firewall logs")]
    [InlineData("/actions?view=open&layout=board", "Sweep VPN and firewall logs")]
    public async Task TheActionsPrerender_KeepsWhatItRead_ForItsInteractiveRender(string path, string shown)
    {
        using var app = new TideApp();
        var handoff = app.Services.GetRequiredService<PrerenderStore>();
        var before = handoff.Count;

        var html = await app.CreateClient().GetStringAsync(path);

        html.Should().Contain(shown);
        handoff.Count.Should().Be(before + 1, "the prerender kept its list or board for the circuit to start from");
    }

    [Fact]
    public async Task AnActionsPrerender_KeepsItsPage_ItsChecklist_AndItsHuntPanel()
    {
        using var app = new TideApp();
        var handoff = app.Services.GetRequiredService<PrerenderStore>();
        var before = handoff.Count;

        // A sweep with a checklist, which its worker can work: the checklist and the hunt panel read their own, and keep
        // it as the page does.
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
            await actions.AddChecklistItemAsync((await actions.GetAsync(1))!.Action.Id, "Pull 30 days of VPN logs");
        }
        var html = await app.CreateClient().GetStringAsync("/actions/1");

        html.Should().Contain("Sweep VPN and firewall logs").And.Contain("Hunt queries").And.Contain("Pull 30 days of VPN logs");
        handoff.Count.Should().Be(before + 3);
    }

    [Fact]
    public async Task ANewActionsPrerender_KeepsItsFormAndItsEditor()
    {
        using var app = new TideApp();
        var handoff = app.Services.GetRequiredService<PrerenderStore>();
        var before = handoff.Count;

        var html = await app.CreateClient().GetStringAsync("/actions/new");

        html.Should().Contain("id=\"a-title\"").And.Contain("From the SLA target.");
        handoff.Count.Should().Be(before + 2, "the page keeps its form, and the editor what it read for it");
    }

    [Fact]
    public async Task PresentsPrerender_KeepsTheSlidesItRead_ForItsInteractiveRender()
    {
        using var app = new TideApp();
        var handoff = app.Services.GetRequiredService<PrerenderStore>();
        var day = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        var before = handoff.Count;

        var html = await app.CreateClient().GetStringAsync($"/present/{day:yyyy-MM-dd}");

        html.Should().Contain("tide-cover-meta").And.NotContain("Loading the brief");
        handoff.Count.Should().Be(before + 1, "the prerender kept its slides for the circuit to start from");
    }

    private static async Task<Guid> NewArticle(TideApp app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var article = new Domain.Entities.Article { Title = "Handoff check", Bluf = "What happened.", AuthorId = "x", AuthorName = "X" };
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        return article.Id;
    }

    [Fact]
    public async Task TheEditorsPrerender_KeepsWhatEachPartOfItRead_ForItsInteractiveRender()
    {
        using var app = new TideApp();
        var handoff = app.Services.GetRequiredService<PrerenderStore>();
        var id = await NewArticle(app);
        var before = handoff.Count;

        var html = await app.CreateClient().GetStringAsync($"/articles/{id}");

        html.Should().Contain("Handoff check").And.Contain("What readers get");
        // The editor, the reader preview, the review card, the story picker, We run, Threat actors and Draft from the sources.
        handoff.Count.Should().Be(before + 7, "each part that reads for itself kept it for the circuit to start from");
    }

    [Fact]
    public async Task TheReviewsPrerender_KeepsWhatItRead_ForItsInteractiveRender()
    {
        using var app = new TideApp();
        var handoff = app.Services.GetRequiredService<PrerenderStore>();
        var id = await NewArticle(app);
        var before = handoff.Count;

        var html = await app.CreateClient().GetStringAsync($"/articles/{id}/review");

        html.Should().Contain("Review: Handoff check");
        handoff.Count.Should().Be(before + 1);
    }

    [Fact]
    public async Task AMissingArticle_KeepsNothing()
    {
        using var app = new TideApp();
        var handoff = app.Services.GetRequiredService<PrerenderStore>();
        var before = handoff.Count;

        (await app.CreateClient().GetStringAsync($"/articles/{Guid.NewGuid()}")).Should().Contain("That article doesn't exist");
        (await app.CreateClient().GetStringAsync($"/articles/{Guid.NewGuid()}/review")).Should().Contain("That article doesn't exist");

        handoff.Count.Should().Be(before);
    }
}
