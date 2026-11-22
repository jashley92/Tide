using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>The "What moved" strip at the top of Trends and Reports (P13), and where its lines open.</summary>
public class TrendHighlightsPageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TheStrip_ShowsWhatMoved_OnTrendsAndReports_AndEachLineOpensWhatItCounted()
    {
        // One article briefed in February 2023, five in March: a month long before anything else the app holds.
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        await using (var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync())
        {
            var articles = Enumerable.Range(0, 6).Select(i => new Article { Title = $"Moved {i}", Status = ArticleStatus.Ready, Classification = Classification.Informational }).ToList();
            db.Articles.AddRange(articles);
            db.Briefs.Add(Briefed(new DateOnly(2023, 2, 14), articles[0]));
            db.Briefs.Add(Briefed(new DateOnly(2023, 3, 7), articles[1], articles[2]));
            db.Briefs.Add(Briefed(new DateOnly(2023, 3, 21), articles[3], articles[4], articles[5]));
            await db.SaveChangesAsync();
        }
        const string opens = "trends?p=2023-03&view=tags&line=articles%3Aall&at=2023-03";

        await using (var trends = await ComponentHarness.RenderAsync<Trends>(app, "/trends?p=2023-03"))
        {
            var html = System.Net.WebUtility.HtmlDecode(await trends.Html());
            html.Should().Contain(">What moved</h2>").And.Contain("<strong>Articles</strong>: 5, the highest in 6 months")
                .And.Contain(opens).And.NotContain("Informational articles", "it counted the same five articles as Articles");
        }

        await using (var reports = await ComponentHarness.RenderAsync<Reports>(app, "/reports?p=2023-03"))
        {
            var html = System.Net.WebUtility.HtmlDecode(await reports.Html());
            html.Should().Contain("tide-highlights tide-noprint").And.Contain("<strong>Articles</strong>: 5, the highest in 6 months")
                .And.Contain($"href=\"{opens}\"");
        }

        // April: nothing briefed, so the line opens to March's five.
        await using var april = await ComponentHarness.RenderAsync<Trends>(app, "/trends?p=2023-04");
        System.Net.WebUtility.HtmlDecode(await april.Html()).Should().Contain("<strong>Articles</strong>: none this month, after 5")
            .And.Contain("trends?p=2023-04&view=tags&line=articles%3Aall&at=2023-03");
    }

    private static Brief Briefed(DateOnly day, params Article[] articles)
    {
        var b = new Brief { BriefDate = day };
        foreach (var a in articles) b.Items.Add(new BriefItem { ArticleId = a.Id, SortOrder = b.Items.Count });
        return b;
    }
}
