using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>The November review's small fixes, as the pages render them.</summary>
public class ReviewFixesRenderTests(TideApp app) : IClassFixture<TideApp>
{
    private static MetricTile Tile(MetricDirection direction, decimal value, decimal previous, MetricTrend trend) =>
        new(new MetricDefinition { Name = "Open", Direction = direction }, value, null,
            new MetricComparison(new DateOnly(2026, 10, 23), previous, value - previous, trend), MetricLevel.Normal, [previous, value]);

    [Theory]
    [InlineData(MetricDirection.LowerIsBetter, 5, 7, MetricTrend.Better, "Better than 7 on Friday, October 23")]
    [InlineData(MetricDirection.LowerIsBetter, 9, 7, MetricTrend.Worse, "Worse than 7 on Friday, October 23")]
    [InlineData(MetricDirection.Neutral, 7, 7, MetricTrend.Unchanged, "The same as 7 on Friday, October 23")]
    [InlineData(MetricDirection.Neutral, 9, 7, MetricTrend.Neutral, "Compared with 7 on Friday, October 23")]
    public async Task TheChangeTooltip_ReadsAsASentence(MetricDirection direction, int value, int previous, MetricTrend trend, string expected)
    {
        await using var r = await ComponentHarness.RenderAsync<MetricChange>(app, "/", new() { ["Tile"] = Tile(direction, value, previous, trend) });
        var html = System.Net.WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain(expected).And.NotContain("Changed than").And.NotContain("Unchanged than");
    }

    [Fact]
    public async Task Workload_LinksEachTeamsOpenAndOverdueCounts_ToThoseActions_FiveTilesARow()
    {
        Guid team;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            team = await scope.ServiceProvider.GetRequiredService<Tide.Application.Actions.TeamService>().AddAsync("Workload Links Team", null, null);

        await using var r = await ComponentHarness.RenderAsync<Workload>(app, "/workload");
        var html = System.Net.WebUtility.HtmlDecode(await r.Html());

        html.Should().Contain($"href=\"actions?view=open&team={team}\" aria-label=\"0 open for Workload Links Team\"")
            .And.Contain($"href=\"actions?view=overdue&team={team}\" aria-label=\"0 overdue for Workload Links Team\"");
        (await app.CreateClient().GetStringAsync("/app.css")).Should().Contain(".tide-load-nums { display: grid; grid-template-columns: repeat(5, 1fr);");
    }

    [Fact]
    public async Task TheRotationHeader_NamesTheBriefDayTodayShows()
    {
        var clock = app.Services.GetRequiredService<OrgClock>();
        await using var r = await ComponentHarness.RenderAsync<RotationPage>(app, "/rotation");
        var html = System.Net.WebUtility.HtmlDecode(await r.Html());

        var day = clock.CurrentBriefDate == clock.Today ? "today" : "on " + Tide.Web.Components.Shared.Ui.ShortDay(clock.CurrentBriefDate);
        html.Should().MatchRegex($"(Presenting {day}:|Nobody is on the rota {day})", "on a weekend the header is about Monday, as Today is");
    }

    [Fact]
    public async Task TheSendDialog_OffersToEndTheMeeting_AndHoldsAnEarlyBrief()
    {
        var clock = app.Services.GetRequiredService<OrgClock>();
        var day = TestDb.SendDayFor(clock);
        await using (var held = await ComponentHarness.RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = day }))
            (await held.Html()).Should().NotContain("The meeting hasn't been ended");

        await using (var r = await ComponentHarness.RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = day, ["MeetingHeld"] = false }))
            (await r.Html()).Should().Contain("The meeting hasn't been ended").And.Contain($"href=\"present/{day:yyyy-MM-dd}\"")
                .And.Contain(">End the meeting</button>").And.Contain("Sent without a meeting");

        var early = WorkingDays.Weekdays.Next(clock.Today.AddDays(1));
        await using var later = await ComponentHarness.RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = early, ["MeetingHeld"] = false });
        (await later.Html()).Should().Contain($"The brief for {Ui.ShortDay(early)} can be sent from that day").And.NotContain(">End the meeting</button>");
    }
}
