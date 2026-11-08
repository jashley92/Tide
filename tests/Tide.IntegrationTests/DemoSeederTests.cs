using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>The app with the screenshot tool's demo data (Demo:Seed), as tools/screenshots/capture.mjs starts it.</summary>
public class DemoTideApp : TideApp
{
    public string InfoFile { get; } = Path.Combine(Path.GetTempPath(), "tide-demo-" + Guid.NewGuid().ToString("N"), "demo.json");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Demo:Seed", "true");
        builder.UseSetting("Demo:InfoFile", InfoFile);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Path.GetDirectoryName(InfoFile)!, recursive: true); } catch (IOException) { /* best effort */ }
    }
}

public class DemoSeederTests(DemoTideApp app) : IClassFixture<DemoTideApp>
{
    private DemoSeeder.Seeded Info()
    {
        app.CreateClient().Dispose();   // starts the app, which seeds
        return JsonSerializer.Deserialize<DemoSeeder.Seeded>(File.ReadAllText(app.InfoFile))!;
    }

    private AppDbContext Db() => app.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    [Fact]
    public async Task Seeds_yesterdays_brief_sent_in_both_editions_and_names_it()
    {
        var info = Info();
        var sent = DateOnly.Parse(info.Sent, System.Globalization.CultureInfo.InvariantCulture);
        DateOnly.Parse(info.Today, System.Globalization.CultureInfo.InvariantCulture).Should().BeAfter(sent);

        await using var db = Db();
        var brief = await db.Briefs.SingleAsync(b => b.BriefDate == sent);
        brief.Status.Should().Be(BriefStatus.Sent);
        var sends = await db.BriefSends.Where(s => s.BriefId == brief.Id && !s.IsTest).Select(s => s.Edition).ToListAsync();
        sends.Should().BeEquivalentTo([Edition.Team, Edition.Public]);
        (await db.ArticleScreenshots.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Seeds_history_metrics_actions_and_the_rest_the_pages_show()
    {
        var info = Info();
        await using var db = Db();
        (await db.Briefs.CountAsync(b => b.Status == BriefStatus.Sent)).Should().BeGreaterThan(50, "Trends needs months of briefs");
        (await db.MetricValues.CountAsync()).Should().BeGreaterThan(1000, "a year of six metrics");
        (await db.HuntQueries.SingleAsync()).Result.Should().Be(HuntResult.NoHits);
        (await db.Actions.SingleAsync(a => a.Number == info.DetailAction)).Status.Should().Be(ActionStatus.InProgress);
        (await db.Adversaries.CountAsync()).Should().Be(3);
        (await db.ApiTokens.CountAsync()).Should().Be(3);
        (await db.FeedItemTexts.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Runs_once_per_database()
    {
        var info = Info();
        await DemoSeeder.SeedAsync(app.Services, app.Services.GetRequiredService<IConfiguration>(), NullLogger.Instance);
        await using var db = Db();
        (await db.BriefSends.CountAsync(s => !s.IsTest)).Should().Be(2);
        (await db.Adversaries.CountAsync()).Should().Be(3);
        info.HuntAction.Should().Be(1);
    }

    [Theory]
    [InlineData("/briefs/{sent}?tab=preview")]
    [InlineData("/published/{sent}")]
    [InlineData("/actions/{detail}")]
    [InlineData("/trends")]
    public async Task The_captured_pages_render(string route)
    {
        var info = Info();
        using var client = app.CreateClient();
        var response = await client.GetAsync(route.Replace("{sent}", info.Sent).Replace("{detail}", info.DetailAction.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
