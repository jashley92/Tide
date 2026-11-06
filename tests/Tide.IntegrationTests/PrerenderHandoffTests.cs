using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
        var handoff = new PrerenderHandoff(clock);
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
        clock.Now += PrerenderHandoff.Lifetime;
        handoff.Take<object>(late, "S-1-5-21-A").Should().BeNull();
        handoff.Keep("S-1-5-21-A", state);
        clock.Now += PrerenderHandoff.Lifetime;
        handoff.Keep("S-1-5-21-A", state);
        handoff.Count.Should().Be(1, "the expired one was swept");

        handoff.Take<object>(null, "S-1-5-21-A").Should().BeNull();
        handoff.Take<object>("not a key", "S-1-5-21-A").Should().BeNull();
        handoff.Take<string>(handoff.Keep("S-1-5-21-A", state), "S-1-5-21-A").Should().BeNull("it's another kind of state");
    }

    [Fact]
    public async Task TodaysPrerender_KeepsWhatItRead_ForItsInteractiveRender()
    {
        using var app = new TideApp();
        var handoff = app.Services.GetRequiredService<PrerenderHandoff>();
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
        var handoff = app.Services.GetRequiredService<PrerenderHandoff>();
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
        var handoff = app.Services.GetRequiredService<PrerenderHandoff>();
        var before = handoff.Count;

        var html = await app.CreateClient().GetStringAsync(path);

        html.Should().Contain(shown);
        handoff.Count.Should().Be(before + 1, "the prerender kept its list or board for the circuit to start from");
    }

    [Fact]
    public async Task AnActionsPrerender_KeepsItsPage_ItsChecklist_AndItsHuntPanel()
    {
        using var app = new TideApp();
        var handoff = app.Services.GetRequiredService<PrerenderHandoff>();
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
        var handoff = app.Services.GetRequiredService<PrerenderHandoff>();
        var before = handoff.Count;

        var html = await app.CreateClient().GetStringAsync("/actions/new");

        html.Should().Contain("id=\"a-title\"").And.Contain("From the SLA target.");
        handoff.Count.Should().Be(before + 2, "the page keeps its form, and the editor what it read for it");
    }

    [Fact]
    public async Task PresentsPrerender_KeepsTheSlidesItRead_ForItsInteractiveRender()
    {
        using var app = new TideApp();
        var handoff = app.Services.GetRequiredService<PrerenderHandoff>();
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
        var handoff = app.Services.GetRequiredService<PrerenderHandoff>();
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
        var handoff = app.Services.GetRequiredService<PrerenderHandoff>();
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
        var handoff = app.Services.GetRequiredService<PrerenderHandoff>();
        var before = handoff.Count;

        (await app.CreateClient().GetStringAsync($"/articles/{Guid.NewGuid()}")).Should().Contain("That article doesn't exist");
        (await app.CreateClient().GetStringAsync($"/articles/{Guid.NewGuid()}/review")).Should().Contain("That article doesn't exist");

        handoff.Count.Should().Be(before);
    }
}
