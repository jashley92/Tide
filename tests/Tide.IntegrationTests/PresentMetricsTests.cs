using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Pages.Admin;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Present mode's metric slides: one per group, its numbers over the chart an administrator chose, an all-0 group said
/// in a line on the first; and a meeting's followers landing on the same group. Its own app, since metrics are global.
/// </summary>
public class PresentMetricsTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:metrics-owner", "Olive Owner", "ActionOwner");

    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private static DateOnly Before(DateOnly day, int n)
    {
        for (var i = 0; i < n; i++) day = WorkingDays.Weekdays.Previous(day);
        return day;
    }

    // Four groups (made once): Cases (a trend, with history and a note), Email triage (a donut), Hunts (a trend with
    // only the day's values), Vulnerabilities (bars, all 0). The day is a past one, with a brief of one article.
    private static readonly SemaphoreSlim Once = new(1, 1);
    private static DateOnly? _day;

    private async Task<DateOnly> Setup()
    {
        await Once.WaitAsync();
        try
        {
            if (_day is { } done) return done;
            var day = Before(Today, 40);
            await using var scope = SignedIn(app, SignIn.Analyst);
            var svc = scope.ServiceProvider.GetRequiredService<MetricsService>();
            async Task<Guid[]> Group(string name, MetricChart chart, params string[] metrics)
            {
                var g = await svc.AddGroupAsync(name);
                await svc.SetGroupChartAsync(g, chart);
                var ids = new List<Guid>();
                foreach (var m in metrics) ids.Add(await svc.SaveMetricAsync(new MetricForm { GroupId = g, Name = m, Direction = MetricDirection.LowerIsBetter, WarnThreshold = 10 }));
                return [.. ids];
            }
            var cases = await Group("Cases", MetricChart.Trend, "Open", "Reopened");
            var email = await Group("Email triage", MetricChart.Share, "Waiting", "Triaged");
            var hunts = await Group("Hunts", MetricChart.Trend, "Running");
            var vulns = await Group("Vulnerabilities", MetricChart.Bars, "Past SLA", "Due this week");
            for (var back = 1; back <= 6; back++)
            {
                if (back == 3) continue;   // a missed day: a gap in the line
                await svc.SaveValueAsync(cases[0], Before(day, back), 4 + back, null);
                await svc.SaveValueAsync(cases[1], Before(day, back), back, null);
            }
            await svc.SaveValueAsync(cases[0], day, 12, null);
            await svc.SaveValueAsync(cases[1], day, 2, "Two reopened after the customer replied");
            await svc.SaveValueAsync(email[0], day, 30, null);
            await svc.SaveValueAsync(email[1], day, 90, null);
            await svc.SaveValueAsync(hunts[0], day, 3, null);
            foreach (var v in vulns) await svc.SaveValueAsync(v, day, 0, null);

            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var brief = await briefs.GetOrStartAsync(day);
            await briefs.AddArticleAsync(brief.Id, await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Metric slides article", "Vendor", null, TlpLevel.Clear));
            _day = day;
            return day;
        }
        finally { Once.Release(); }
    }

    private Task<Rendered<Present>> Open(DateOnly day, SignIn? who = null) =>
        RenderAsync<Present>(app, $"/present/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" }, who);

    private static Task Do(Rendered<Present> r, Func<Present, Task> what) =>
        r.Click(async p =>
        {
            await what(p);
            await p.OnPresentKey("render");
        });

    [Fact]
    public async Task EachGroup_GetsItsOwnSlide_ItsNumbersOverItsChart_AndAnAllZeroGroupIsALineOnTheFirst()
    {
        var day = await Setup();
        await using var r = await Open(day);
        var html = await r.Html();
        html.Should().Contain("title=\"Numbers: Cases\"").And.Contain("title=\"Numbers: Email triage\"").And.Contain("title=\"Numbers: Hunts\"")
            .And.NotContain("Numbers: Vulnerabilities", "an all-0 group has no slide of its own")
            .And.Contain("Slide 1 of 6", "title, three groups, the article and the wrap-up");

        await r.Click(p => p.OnPresentKey("next"));
        html = await r.Html();
        html.Should().Contain("<h2>Cases</h2>").And.Contain("1 of 3").And.Contain("each compared with its last earlier value")
            .And.Contain("tide-mchart-trend").And.Contain("<polyline").And.Contain("tide-mchart-ring", "12 is past the warning threshold")
            .And.Contain("Vulnerabilities: all 0")
            .And.Contain("class=\"tide-mtile-note\" title=\"Two reopened after the customer replied\">Two reopened after the customer replied</div>")
            .And.NotContain("Email triage</h2>");
        html.Split("tide-mtile-note").Should().HaveCount(2, "a tile without a note has no note line");

        await r.Click(p => p.OnPresentKey("next"));
        html = await r.Html();
        html.Should().Contain("<h2>Email triage</h2>").And.Contain("tide-mchart-share").And.Contain(">120</text>").And.Contain("75%").And.Contain("25%")
            .And.Contain("no earlier values to compare with").And.NotContain("no earlier value</span>", "said once for the slide, not on every tile")
            .And.NotContain("Vulnerabilities: all 0", "the line is on the first metrics slide only");

        await r.Click(p => p.OnPresentKey("next"));
        (await r.Html()).Should().Contain("<h2>Hunts</h2>").And.Contain("No history yet");
    }

    [Fact]
    public async Task AFollower_LandsOnTheSameGroup_ByItsKey()
    {
        var day = await Setup();
        var meetings = app.Services.GetRequiredService<LiveMeetings>();
        await using var pat = await Open(day);
        await Do(pat, p => p.StartMeeting());
        try
        {
            await pat.Click(p => p.OnPresentKey("next"));
            await pat.Click(p => p.OnPresentKey("next"));
            meetings.Get(day)!.Should().Match<LiveMeetingView>(v => v.Slide == 2 && v.SlideKey == "metrics:email-triage");

            await using var owner = await Open(day, Owner);
            await Do(owner, p => p.JoinIfLive());
            (await owner.Html()).Should().Contain("<h2>Email triage</h2>").And.Contain("Following Tester");

            await pat.Click(p => p.OnPresentKey("next"));
            await Do(owner, p => p.Follow());
            (await owner.Html()).Should().Contain("<h2>Hunts</h2>");
        }
        finally { await Do(pat, p => p.EndMeeting()); }
    }

    [Fact]
    public async Task TheCompanyWideEdition_HasNoMetricSlides()
    {
        var day = await Setup();
        await using var r = await Open(day);
        await r.Click(p =>
        {
            p.SetEdition(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = nameof(Edition.Public) });
            return p.OnPresentKey("render");
        });
        (await r.Html()).Should().NotContain("Numbers: ").And.Contain("Slide 1 of 3");
    }

    [Fact]
    public async Task TheChartSetting_Saves_AndTheSetUpPageShowsIt()
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var svc = scope.ServiceProvider.GetRequiredService<MetricsService>();
        var g = await svc.AddGroupAsync("Chart setting check");   // no metrics, so it has no slide
        (await svc.ListAsync()).Single(x => x.Id == g).Chart.Should().Be(MetricChart.Trend, "the default");

        await svc.SetGroupChartAsync(g, MetricChart.Bars);
        (await svc.ListAsync()).Single(x => x.Id == g).Chart.Should().Be(MetricChart.Bars);
        var bad = () => svc.SetGroupChartAsync(g, (MetricChart)42);
        await bad.Should().ThrowAsync<ArgumentException>();

        await using var page = await RenderAsync<MetricsAdmin>(app, "/admin/metrics");
        (await page.Html()).Should().Contain($"id=\"chart-{g}\"").And.Contain("Share of the whole").And.Contain("Side by side");
    }

    [Fact]
    public async Task OnlyAnAdministrator_SetsTheChart()
    {
        await using var scope = SignedIn(app, new SignIn("dev:metrics-analyst", "Ana Analyst", "Analyst"));
        var act = () => scope.ServiceProvider.GetRequiredService<MetricsService>().SetGroupChartAsync(Guid.NewGuid(), MetricChart.None);
        await act.Should().ThrowAsync<Tide.Application.Security.ForbiddenException>();
    }

    [Fact]
    public async Task EachChartKind_Renders_IncludingWithNoHistoryAndAllZeros()
    {
        MetricTile Tile(string name, decimal? v, params decimal?[] trend) =>
            new(new MetricDefinition { Name = name, Key = name }, v, null, null, MetricLevel.Normal, []) { Trend = trend };
        async Task<string> Chart(MetricChart kind, params MetricTile[] tiles)
        {
            var group = new MetricGroupDay(new MetricGroup { Name = "Cases", Key = "cases", Chart = kind }, tiles);
            await using var r = await RenderAsync<MetricGroupChart>(app, "/present", new() { ["Group"] = group, ["Days"] = (IReadOnlyList<DateOnly>)MetricMath.WorkingDaysWithin(Today, 7) });
            return await r.Html();
        }

        (await Chart(MetricChart.Trend, Tile("Open", 2, 1, 2, 3, 2, 2))).Should().Contain("<polyline").And.Contain("Open <strong>2</strong>");
        (await Chart(MetricChart.Trend, Tile("Open", 2))).Should().Contain("No history yet").And.NotContain("<svg");
        (await Chart(MetricChart.Share, Tile("Open", 1), Tile("Closed", 3))).Should().Contain("<path").And.Contain("25%").And.Contain(">4</text>");
        (await Chart(MetricChart.Share, Tile("Open", 0), Tile("Closed", 0))).Should().Contain("no value is above 0").And.NotContain("<path");
        (await Chart(MetricChart.Bars, Tile("Open", 4), Tile("Closed", 2))).Should().Contain("width: 100%").And.Contain("width: 50%");
        (await Chart(MetricChart.Bars, Tile("Open", 0), Tile("Closed", 0))).Should().Contain("width: 0%").And.Contain("<strong>0</strong>");
        (await Chart(MetricChart.None, Tile("Open", 4))).Trim().Should().BeEmpty();
    }
}
