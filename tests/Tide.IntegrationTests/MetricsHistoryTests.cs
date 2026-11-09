using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>The metric history page (MetricsHistory) with several groups. The table: a heading per group over its columns,
/// a line between groups, the group filter kept in the address, and a day's note marked on its value. The chart: the
/// metrics picked in the address, on one chart or a chart each, with each one's latest change and its threshold bands.
/// Its own app, so the columns are only this class's metrics.</summary>
public class MetricsHistoryTests(TideApp app) : IClassFixture<TideApp>
{
    private sealed record Made(Guid Group, string Key, Guid[] Metrics);

    private static List<Made>? _made;
    private static readonly SemaphoreSlim Once = new(1, 1);

    // Three groups (3, 2 and 1 metrics) with a value each on the last two brief days (5, then 7), the last one with a
    // note on Clicked. Vulnerabilities' Critical is lower-is-better with a warning at 6.
    private async Task<List<Made>> Groups()
    {
        await Once.WaitAsync();
        try
        {
            if (_made is not null) return _made;
            await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
            var svc = scope.ServiceProvider.GetRequiredService<MetricsService>();
            var clock = scope.ServiceProvider.GetRequiredService<OrgClock>();
            var day = clock.CurrentBriefDate;
            var made = new List<Made>();
            foreach (var (name, metrics) in new[] { ("Email triage", new[] { "Reported", "Clicked", "Blocked" }), ("Cases", ["Opened", "Closed"]), ("Vulnerabilities", ["Critical"]) })
            {
                var group = await svc.AddGroupAsync(name);
                var ids = new List<Guid>();
                foreach (var m in metrics)
                {
                    var form = new MetricForm { GroupId = group, Name = m };
                    if (m == "Critical") (form.Direction, form.WarnThreshold) = (MetricDirection.LowerIsBetter, 6);
                    var id = await svc.SaveMetricAsync(form);
                    await svc.SaveValueAsync(id, clock.Days.Previous(day), 5, null);
                    await svc.SaveValueAsync(id, day, 7, m == "Clicked" ? "One user, already reset" : null);
                    ids.Add(id);
                }
                made.Add(new Made(group, "", [.. ids]));
            }
            var history = await svc.HistoryAsync(day.AddDays(-1), day);
            return _made = [.. made.Select(m => m with { Key = history.Metrics.First(d => d.GroupId == m.Group).Group!.Key })];
        }
        finally { Once.Release(); }
    }

    private static string Head(string html) => html[html.IndexOf("<thead>", StringComparison.Ordinal)..html.IndexOf("</thead>", StringComparison.Ordinal)];

    [Fact]
    public async Task EachGroup_HeadsItsOwnColumns_WithALineBetweenGroups()
    {
        await Groups();
        await using var r = await ComponentHarness.RenderAsync<MetricsHistory>(app, "/metrics");
        var html = await r.HtmlOnce("<thead>");
        var head = Head(html);

        head.Should().Contain("<th scope=\"col\" rowspan=\"2\" class=\"tide-mh-day\">Day</th>")
            .And.Contain("<th scope=\"colgroup\" colspan=\"3\" class=\"tide-mh-group\"><span>Email triage</span></th>")
            .And.Contain("<th scope=\"colgroup\" colspan=\"2\" class=\"tide-mh-group\"><span>Cases</span></th>")
            .And.Contain("<th scope=\"colgroup\" colspan=\"1\" class=\"tide-mh-group\"><span>Vulnerabilities</span></th>");
        head.Split("tide-mh-gstart").Length.Should().Be(4, "each group's first column starts with a line");
        html.Should().Contain("<colgroup span=\"3\"></colgroup>").And.Contain("class=\"tide-mh-scroll\"");
        html.Split("<th scope=\"row\" class=\"tide-mh-day").Length.Should().BeGreaterThan(1, "each day heads its row");
    }

    [Fact]
    public async Task TheFilter_InTheAddress_ShowsOnlyTheGroupsChosen()
    {
        var made = await Groups();
        await using var r = await ComponentHarness.RenderAsync<MetricsHistory>(app, $"/metrics?groups={made[1].Key},{made[2].Key}");
        var html = await r.HtmlOnce("<thead>");
        var head = Head(html);

        head.Should().NotContain(">Email triage<").And.Contain("<span>Cases</span>").And.Contain("<span>Vulnerabilities</span>");
        var filter = html[html.IndexOf("tide-filterbar", StringComparison.Ordinal)..html.IndexOf("tide-mh-scroll", StringComparison.Ordinal)];
        filter.Should().Contain("Groups shown");
        filter.Should().Contain("tide-filterbar-clear", "Clear shows every group again");
        Pressed(filter, "Email triage").Should().BeFalse();
        Pressed(filter, "Cases").Should().BeTrue();
        Pressed(filter, "Vulnerabilities").Should().BeTrue();
    }

    [Fact]
    public async Task WithNoFilter_OrOneNamingNoGroupItHas_EveryGroupShows()
    {
        await Groups();
        foreach (var path in new[] { "/metrics", "/metrics?groups=gone" })
        {
            await using var r = await ComponentHarness.RenderAsync<MetricsHistory>(app, path);
            var html = await r.HtmlOnce("<thead>");
            Head(html).Should().Contain("<span>Email triage</span>").And.Contain("<span>Cases</span>").And.Contain("<span>Vulnerabilities</span>");
            var filter = html[html.IndexOf("tide-filterbar", StringComparison.Ordinal)..];
            Pressed(filter, "Cases").Should().BeFalse("no pill on is every group");
            filter.Should().NotContain("tide-filterbar-clear");
        }
    }

    [Fact]
    public async Task ADaysNote_IsMarkedOnItsValue_InATitleAndForScreenReaders()
    {
        await Groups();
        await using var r = await ComponentHarness.RenderAsync<MetricsHistory>(app, "/metrics");
        var html = await r.HtmlOnce("<thead>");

        html.Should().Contain("<span class=\"bi bi-chat-left-text tide-mh-note\" title=\"One user, already reset\" aria-hidden=\"true\"></span><span class=\"visually-hidden\">Note: One user, already reset</span>");
        html.Split("tide-mh-note\"").Length.Should().Be(2, "only the value with a note is marked");
    }

    // The chart's card: from its heading to the table's.
    private static string ChartCard(string html) => html[html.IndexOf("tide-card-title", StringComparison.Ordinal)..html.IndexOf("<h2>Values", StringComparison.Ordinal)];

    [Fact]
    public async Task AGroupInTheAddress_IsCharted_OnOneChart_WithEachLinesLatestChange()
    {
        var made = await Groups();
        await using var r = await ComponentHarness.RenderAsync<MetricsHistory>(app, $"/metrics?chart=group:{made[0].Key}");
        var html = await r.HtmlOnce("<thead>");
        var chart = ChartCard(html);

        chart.Should().Contain("3 metrics");
        chart.Split("<polyline").Length.Should().Be(4, "a line per metric of the group");
        chart.Should().Contain("aria-label=\"Take Reported off the chart\"").And.Contain("aria-label=\"Take Blocked off the chart\"");
        chart.Should().Contain("&#x25B2; &#x2B;2</span>", "each key entry has its latest change, ▲ +2");
        chart.Should().NotContain("tide-mc-cell");
        html.Should().Contain("Chart: Email triage", "the picker names the group charted");
    }

    [Fact]
    public async Task AChartEach_DrawsASmallChartPerMetric_WithItsLatestValue()
    {
        var made = await Groups();
        await using var r = await ComponentHarness.RenderAsync<MetricsHistory>(app, $"/metrics?chart=group:{made[0].Key},group:{made[1].Key}&view=grid");
        var chart = ChartCard(await r.HtmlOnce("<thead>"));

        chart.Split("class=\"tide-mc-cell\"").Length.Should().Be(6, "five metrics, a chart each");
        chart.Split("tide-chart-compact").Length.Should().Be(6);
        chart.Should().Contain("aria-pressed=\"true\"").And.Contain("A chart each");
        chart.Should().Contain("<strong class=\"\">7</strong>");
    }

    [Fact]
    public async Task AMetricWithAThreshold_ShadesWhereItTrips_AndColoursItsChangeByItsDirection()
    {
        await Groups();
        await using var r = await ComponentHarness.RenderAsync<MetricsHistory>(app, "/metrics?chart=critical");
        var chart = ChartCard(await r.HtmlOnce("<thead>"));

        chart.Should().Contain("Vulnerabilities: Critical").And.Contain("lower is better");
        // The axis runs 5 to 7; a value of 6 or more trips the warning: the top half.
        chart.Should().Contain("<rect class=\"tide-chart-band-warn\" x=\"0\" width=\"1000\" y=\"0\" height=\"150\"");
        chart.Should().Contain("class=\"tide-trend-worse\"", "up is worse for it");
    }

    [Fact]
    public async Task AFilteredTable_OffersToChartItsGroups()
    {
        var made = await Groups();
        await using var r = await ComponentHarness.RenderAsync<MetricsHistory>(app, $"/metrics?groups={made[1].Key}");
        (await r.HtmlOnce("<thead>")).Should().Contain("Chart the groups shown");
        await using var all = await ComponentHarness.RenderAsync<MetricsHistory>(app, "/metrics");
        (await all.HtmlOnce("<thead>")).Should().NotContain("Chart the groups shown");
    }

    // Whether the filter's chip for a name is pressed.
    private static bool Pressed(string filter, string name)
    {
        var at = filter.IndexOf(">" + name, StringComparison.Ordinal);
        at.Should().BePositive();
        var button = filter[filter.LastIndexOf("<button", at, StringComparison.Ordinal)..at];
        return button.Contains("aria-pressed=\"true\"", StringComparison.Ordinal);
    }
}
