using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Mitre;
using Tide.Application.Org;
using Tide.Application.Trends;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>The Trends chart, rendered: any line of the view can go on it, from any tactic, as many as you like.</summary>
public class TrendsChartPageTests(TideApp app) : IClassFixture<TideApp>
{
    // Nine techniques over a dozen tactics, written this month.
    private static readonly string[] Techniques = ["T1566", "T1059", "T1486", "T1078", "T1190", "T1053", "T1003", "T1021", "T1071"];

    private async Task<string> Page(string query)
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            if (!db.Articles.Any(a => a.Title == "chart-seed-T1566"))
            {
                foreach (var t in Techniques)
                {
                    var a = new Article { Title = "chart-seed-" + t, Status = ArticleStatus.Ready, Classification = Classification.Informational };
                    a.Techniques = [new ArticleTechnique { ArticleId = a.Id, TechniqueId = t }];
                    db.Add(a);
                }
                await db.SaveChangesAsync();
            }
        }
        var month = app.Services.GetRequiredService<OrgClock>().Today.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        return await app.CreateClient().GetStringAsync($"/trends?p={month}&view=attack&{query}");
    }

    // The chart's key, decoded, and the classes of each line it draws.
    private static string Legend(string html)
    {
        var start = html.IndexOf("<figcaption class=\"tide-chart-legend\">", StringComparison.Ordinal);
        if (start < 0) return "";
        return WebUtility.HtmlDecode(html[start..html.IndexOf("</figcaption>", start, StringComparison.Ordinal)]);
    }

    private static List<string> LineClasses(string html) =>
        [.. html.Split("<polyline class=\"").Skip(1).Select(s => s[..s.IndexOf('"')])];

    [Fact]
    public async Task ATechniqueFromAnyTactic_CanBeCharted_AtTheTopLevel()
    {
        var html = await Page("chart=tactic:TA0001:T1078,tactic:TA0002");

        Legend(html).Should().Contain("Initial Access › T1078 Valid Accounts", "a technique under a tactic the table isn't showing")
            .And.Contain("Execution").And.Contain("class=\"tide-chart-legend-item\"").And.Contain("aria-label=\"Take Execution off the chart\"");
        LineClasses(html).Should().Equal("tide-chart-s1", "tide-chart-s2");
    }

    [Fact]
    public async Task TheChart_IsTheSame_InsideATactic()
    {
        var html = await Page("tactic=tactic:TA0002&chart=tactic:TA0001:T1078,technique:T1486");

        html.Should().Contain("All tactics</button>", "drilled into Execution");
        Legend(html).Should().Contain("Initial Access › T1078 Valid Accounts").And.Contain("T1486 Data Encrypted for Impact");
        html.Should().NotContain("Chart all techniques", "that's for the top level");
    }

    [Fact]
    public async Task MoreThanSixLines_EachLookDifferent_DashedAfterTwelve()
    {
        var techniques = LineClasses(await Page("chart=all-techniques"));
        techniques.Should().HaveCountGreaterThanOrEqualTo(Techniques.Length, "these and any the dev seed has").And.OnlyHaveUniqueItems();
        techniques.Should().Contain("tide-chart-s9");

        var all = await Page("chart=all");
        var lines = LineClasses(all);
        lines.Should().HaveCountGreaterThan(12).And.OnlyHaveUniqueItems().And.Contain("tide-chart-s1 tide-chart-d1");
        all.Should().Contain("tide-chart-swatch tide-chart-s1 tide-chart-d1 me-1", "the table's swatch matches the line");
    }

    [Fact]
    public async Task HelperText_AndTheChartAllButtons()
    {
        var html = await Page("");
        html.Should().Contain("Select rows to chart, from any tactic.")
            .And.Contain(">Chart all shown</button>").And.Contain(">Chart all techniques</button>").And.Contain(">Clear chart</button>")
            .And.NotContain("up to 6");

        var cleared = await Page("chart=none");
        cleared.Should().Contain("No lines selected").And.Contain("disabled>Clear chart</button>");
        LineClasses(cleared).Should().BeEmpty();
    }

    [Fact]
    public async Task ATechniqueChartedFromAnotherTactic_HasItsSwatch_OnTheTablesRow()
    {
        var accounts = AttackCatalog.Find("T1078")!;
        var other = accounts.Tactics.First(t => t != MitreTactic.InitialAccess);

        // The top level lists T1078 once, under its primary tactic; charted from Initial Access, that row is on the chart.
        var top = WebUtility.HtmlDecode(await Page("chart=tactic:TA0001:T1078"));
        top.Should().Contain($"aria-label=\"Take {TrendsService.TechniqueName("T1078", accounts.PrimaryTactic)} off the chart\"");

        // So is the row under any other tactic it's in.
        var drilled = WebUtility.HtmlDecode(await Page($"tactic=tactic:{AttackMatrix.TacticId(other)}&chart=tactic:TA0001:T1078"));
        drilled.Should().Contain($"aria-label=\"Take {TrendsService.TechniqueName("T1078", other)} off the chart\"");
    }

    [Fact]
    public async Task TheTooltip_ListsTheTopTen_HighestFirst_AndCountsTheRest()
    {
        var html = await Page("chart=all");
        var lines = LineClasses(html).Count;
        var stop = html[html.LastIndexOf("<span class=\"tide-chart-stop\"", StringComparison.Ordinal)..];
        stop = WebUtility.HtmlDecode(stop[..stop.IndexOf("</span>", StringComparison.Ordinal)]);

        stop.Should().Contain($"top 10 of {lines}\"").And.Contain($"data-more=\"+ {lines - 10} more");
        var dots = stop.Split("<i class=\"tide-chart-dot").Skip(1).ToList();
        dots.Should().HaveCount(lines);
        dots.Count(d => d.Contains("data-rest", StringComparison.Ordinal)).Should().Be(lines - 10);
        var values = dots.Select(d => int.Parse(d.Split("data-value=\"")[1].Split('"')[0], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        values.Should().BeInDescendingOrder("highest first, so the ten listed are the busiest");
        dots.Take(10).Should().OnlyContain(d => !d.Contains("data-rest", StringComparison.Ordinal));

        var few = await Page("chart=tactic:TA0001,tactic:TA0002");
        few.Should().NotContain("data-more").And.NotContain("data-rest");
    }
}
