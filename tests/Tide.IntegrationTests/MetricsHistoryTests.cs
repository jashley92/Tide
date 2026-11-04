using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>The metric history table (MetricsHistory) with several groups: a heading per group over its columns, a line
/// between groups, the group filter kept in the address, and a day's note marked on its value. Its own app, so the
/// columns are only this class's metrics.</summary>
public class MetricsHistoryTests(TideApp app) : IClassFixture<TideApp>
{
    private sealed record Made(Guid Group, string Key, Guid[] Metrics);

    private static List<Made>? _made;
    private static readonly SemaphoreSlim Once = new(1, 1);

    // Three groups (3, 2 and 1 metrics) with a value each on the last brief day, one of them with a note.
    private async Task<List<Made>> Groups()
    {
        await Once.WaitAsync();
        try
        {
            if (_made is not null) return _made;
            await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
            var svc = scope.ServiceProvider.GetRequiredService<MetricsService>();
            var day = scope.ServiceProvider.GetRequiredService<OrgClock>().CurrentBriefDate;
            var made = new List<Made>();
            foreach (var (name, metrics) in new[] { ("Email triage", new[] { "Reported", "Clicked", "Blocked" }), ("Cases", ["Opened", "Closed"]), ("Vulnerabilities", ["Critical"]) })
            {
                var group = await svc.AddGroupAsync(name);
                var ids = new List<Guid>();
                foreach (var m in metrics)
                {
                    var id = await svc.SaveMetricAsync(new MetricForm { GroupId = group, Name = m });
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
        var filter = html[html.IndexOf("tide-mh-filter", StringComparison.Ordinal)..html.IndexOf("tide-mh-scroll", StringComparison.Ordinal)];
        filter.Should().Contain("Groups shown");
        Pressed(filter, "All groups").Should().BeFalse();
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
            var filter = html[html.IndexOf("tide-mh-filter", StringComparison.Ordinal)..];
            Pressed(filter, "All groups").Should().BeTrue();
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

    // Whether the filter's chip for a name is pressed.
    private static bool Pressed(string filter, string name)
    {
        var at = filter.IndexOf(">" + name, StringComparison.Ordinal);
        at.Should().BePositive();
        var button = filter[filter.LastIndexOf("<button", at, StringComparison.Ordinal)..at];
        return button.Contains("aria-pressed=\"true\"", StringComparison.Ordinal);
    }
}
