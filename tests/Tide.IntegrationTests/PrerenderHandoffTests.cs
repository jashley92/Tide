using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
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
}
