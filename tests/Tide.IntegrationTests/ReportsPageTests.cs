using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>Reports and the Trends tiles, as the pages render them (EP-22).</summary>
public class ReportsPageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task Reports_ShowsEachSection_WithAssessmentsAndLinksIntoTrends()
    {
        await using var r = await ComponentHarness.RenderAsync<Reports>(app, "/reports?p=2026-10");
        var html = System.Net.WebUtility.HtmlDecode(await r.Html());

        html.Should().Contain("Monthly report · October 2026").And.Contain("Compared with Sep 2026.")
            .And.Contain("1 · Headlines").And.Contain("2 · Outcomes").And.Contain("3 · Opening metrics").And.Contain("4 · Topics and techniques")
            .And.Contain("5 · Actions raised by team").And.Contain("id=\"assess-headlines\"").And.Contain("id=\"assess-teams\"")
            .And.Contain("href=\"trends?p=2026-10&view=tags&line=articles%3Aall&at=2026-10\"").And.Contain("href=\"reports/export.csv?p=2026-10\"");
    }

    [Fact]
    public async Task Reports_ByWeek_AndItsCsv()
    {
        await using (var r = await ComponentHarness.RenderAsync<Reports>(app, "/reports?p=2026-W44"))
            System.Net.WebUtility.HtmlDecode(await r.Html()).Should().Contain("Weekly report · Week of Oct 26").And.Contain("btn-primary\" aria-pressed");

        var csv = await app.CreateClient().GetStringAsync("/reports/export.csv?p=2026-Q4");
        csv.Should().StartWith("section,name,current,previous").And.Contain("Headlines,Articles briefed,");
    }

    [Fact]
    public async Task Reports_SwitchingKind_FromAPeriodToDate_GoesToTheOneHoldingToday_NotOneNotStarted()
    {
        // This month to date, switched to Week, went to the week holding the month's last day: one that hasn't started.
        var clock = app.Services.GetRequiredService<Tide.Application.Org.OrgClock>();
        var month = Tide.Application.Trends.Period.Of(Tide.Application.Trends.PeriodKind.Month, clock.Today);
        await using var r = await ComponentHarness.RenderAsync<Reports>(app, $"/reports?p={month.Key}");

        var week = await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Switched(Tide.Application.Trends.PeriodKind.Week));
        week.Should().Be(Tide.Application.Trends.Period.Of(Tide.Application.Trends.PeriodKind.Week, clock.Today));
        var past = new Tide.Application.Trends.Period(Tide.Application.Trends.PeriodKind.Month, 2026, 9);
        await using var september = await ComponentHarness.RenderAsync<Reports>(app, $"/reports?p={past.Key}");
        (await september.Renderer.Dispatcher.InvokeAsync(() => september.Component.Switched(Tide.Application.Trends.PeriodKind.Week)))
            .Should().Be(Tide.Application.Trends.Period.Of(Tide.Application.Trends.PeriodKind.Week, past.End));
    }

    [Fact]
    public async Task TrendsTiles_OpenWhatTheyCount_AndTheOutcomesRowFollows()
    {
        await using var r = await ComponentHarness.RenderAsync<Trends>(app, "/trends?p=2026-W44");
        var html = System.Net.WebUtility.HtmlDecode(await r.Html());

        html.Should().Contain(">Weekly</button>").And.Contain("Week of Oct 26").And.Contain("weeks</button>")
            .And.Contain("line=actions%3Aon-time").And.Contain(">Outcomes</h2>").And.Contain("Median days to close, P1").And.Contain("Hunts with hits");
    }
}
