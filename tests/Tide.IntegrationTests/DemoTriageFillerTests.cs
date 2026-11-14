using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Infrastructure.Persistence;

namespace Tide.IntegrationTests;

/// <summary>The demo with <c>Demo:TriageFiller</c> set, as tools/screenshots/layout.mjs starts it for the Triage checks.</summary>
public class DemoTriageFillerApp : DemoTideApp
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Demo:TriageFiller", "55");
    }
}

public class DemoTriageFillerTests(DemoTriageFillerApp app) : IClassFixture<DemoTriageFillerApp>
{
    private AppDbContext Db()
    {
        app.CreateClient().Dispose();   // starts the app, which seeds
        return app.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
    }

    [Fact]
    public async Task Filler_gives_Triage_a_second_page_from_one_feed()
    {
        await using var db = Db();
        var filler = await db.FeedItems.Where(i => i.Title.StartsWith("Filler k")).Select(i => new { i.Title, Source = i.Source!.Name }).ToListAsync();

        filler.Should().HaveCount(55, "the layout checks need more new items than one page of 50 holds");
        filler.Select(i => i.Source).Distinct().Should().ContainSingle("filler from one feed never groups");
        filler.Select(i => i.Title).Should().Contain(["Filler k001", "Filler k055"]);
    }

    [Fact]
    public async Task Filler_adds_one_event_from_two_feeds_that_the_Edge_VPN_article_covers()
    {
        await using var db = Db();
        var quillon = await db.FeedItems.Where(i => i.Title.StartsWith("Quillon Bank")).Select(i => i.Source!.Name).ToListAsync();
        quillon.Should().HaveCount(2).And.OnlyHaveUniqueItems("the same event, told by two feeds");

        var vpn = await db.Articles.SingleAsync(a => a.Title.StartsWith("Edge VPN"));
        (await db.ArticleCves.AnyAsync(c => c.ArticleId == vpn.Id && c.CveId == "CVE-2026-0098"))
            .Should().BeTrue("the article covers the event through a CVE it names");
    }
}
