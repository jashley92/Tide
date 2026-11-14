using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;

namespace Tide.IntegrationTests;

/// <summary>The demo with <c>Demo:SlideFiller</c> set, as tools/screenshots/layout.mjs starts it for the slide checks.</summary>
public class DemoSlideFillerApp : DemoTideApp
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Demo:SlideFiller", "true");
    }
}

public class DemoSlideFillerTests(DemoSlideFillerApp app) : IClassFixture<DemoSlideFillerApp>
{
    private AppDbContext Db()
    {
        app.CreateClient().Dispose();   // starts the app, which seeds
        return app.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
    }

    [Fact]
    public async Task Filler_gives_the_last_article_of_the_latest_brief_a_dozen_indicators_and_a_dozen_CVEs()
    {
        await using var db = Db();
        var brief = await db.Briefs.Include(b => b.Items).OrderByDescending(b => b.BriefDate).FirstAsync();
        var last = brief.Items.OrderBy(i => i.SortOrder).Last().ArticleId;

        var indicators = await db.ArticleIndicators.Where(i => i.ArticleId == last).Select(i => new { i.Type, i.Value }).ToListAsync();
        indicators.Count.Should().BeGreaterThanOrEqualTo(12, "the slide shows ten and says how many more");
        indicators.Should().Contain(i => i.Type == IndicatorType.FileHash && i.Value.Length >= 64, "a long hash tests wrapping");
        indicators.Should().Contain(i => i.Type == IndicatorType.Url && i.Value.Length > 80, "a long address tests wrapping");

        var cves = await db.ArticleCves.Where(c => c.ArticleId == last).Select(c => c.CveId).ToListAsync();
        cves.Should().Contain(Enumerable.Range(11, 12).Select(n => $"CVE-2026-00{n}"));
    }

    [Fact]
    public async Task Filler_carries_an_opening_note_over_not_yet_looked_at()
    {
        await using var db = Db();
        var brief = await db.Briefs.AsNoTracking().OrderByDescending(b => b.BriefDate).FirstAsync();
        brief.TeamNote.Should().NotBeNullOrWhiteSpace();
        Tide.Application.Briefs.BriefService.TeamNoteCarriedOver(brief).Should().BeTrue("the presenter's Still current and Edit show on its slide");
    }
}
