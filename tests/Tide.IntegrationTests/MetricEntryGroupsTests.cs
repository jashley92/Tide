using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Metrics;
using Tide.Domain.Entities;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>The day's numbers form with many groups (MetricEntry): the progress bar's counts and chips, each group's panel,
/// and finished groups folding once they're left. Its own app, so the counts are only this class's metrics; each test
/// enters numbers on a day of its own.</summary>
public class MetricEntryGroupsTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly ComponentHarness.SignIn Robin = new("dev:robin", "Robin", "Analyst");
    private static readonly ComponentHarness.SignIn Owner = new("dev:olive", "Olive", "ActionOwner");

    private sealed record Made(Guid Group, Guid[] Metrics);

    private async Task<Made> Group(string name, params string[] metrics)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var svc = scope.ServiceProvider.GetRequiredService<MetricsService>();
        var group = await svc.AddGroupAsync(name);
        var ids = new List<Guid>();
        foreach (var m in metrics) ids.Add(await svc.SaveMetricAsync(new MetricForm { GroupId = group, Name = m }));
        return new Made(group, [.. ids]);
    }

    private async Task Value(Guid id, DateOnly day, decimal? value, ComponentHarness.SignIn? who = null)
    {
        await using var scope = ComponentHarness.SignedIn(app, who ?? ComponentHarness.SignIn.Analyst);
        await scope.ServiceProvider.GetRequiredService<MetricsService>().SaveValueAsync(id, day, value, null);
    }

    private async Task<MetricDay> DayOf(DateOnly day)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        return await scope.ServiceProvider.GetRequiredService<MetricsService>().GetDayAsync(day);
    }

    // Read before a Click, not in it: its own sign-in would replace the one the form saves with.
    private async Task<MetricTile> Tile(Guid id, DateOnly day) => (await DayOf(day)).Groups.SelectMany(g => g.Tiles).Single(t => t.Metric.Id == id);

    // A group's panel, from its section to the next.
    private static string Panel(string html, Guid group)
    {
        var at = html.IndexOf("-g-" + group.ToString("N") + "\" aria-labelledby", StringComparison.Ordinal);
        at.Should().BePositive();
        var end = html.IndexOf("<section", at, StringComparison.Ordinal);
        return end < 0 ? html[at..] : html[at..end];
    }

    private static string Chip(string html, string name)
    {
        var at = html.IndexOf("aria-label=\"" + name + ",", StringComparison.Ordinal);
        at.Should().BePositive();
        var start = html.LastIndexOf('<', at);
        return html[start..html.IndexOf("</button>", at, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task TheBar_CountsTheDay_AndEachGroup_WithAChipNamedForScreenReaders()
    {
        var day = new DateOnly(2027, 4, 5);
        var email = await Group("Email triage", "Reported", "Clicked", "Blocked", "Quarantined");
        var cases = await Group("Cases", "Opened", "Closed");
        await Value(email.Metrics[0], day, 12);
        await Value(email.Metrics[1], day, 0);
        foreach (var m in cases.Metrics) await Value(m, day, 3);

        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day });
        var html = await r.Html();

        var all = (await DayOf(day)).Groups.SelectMany(g => g.Tiles).ToList();
        html.Should().Contain($">{all.Count(t => t.Value is not null)} of {all.Count} entered<");
        Chip(html, "Email triage").Should().Contain("aria-label=\"Email triage, 2 of 4 entered\"").And.Contain("2/4").And.NotContain("bi-check-lg");
        Chip(html, "Cases").Should().Contain("aria-label=\"Cases, 2 of 2 entered\"").And.Contain("is-done").And.Contain("bi-check-lg", "a finished group's chip has a tick");
        Panel(html, email.Group).Should().Contain(">2 of 4").And.Contain("tide-metric-row");
        Panel(html, cases.Group).Should().Contain(">2 of 2").And.Contain("tide-mentry-folded", "finished when the form opened, it's folded")
            .And.Contain("aria-expanded=\"false\"").And.Contain("Opened").And.NotContain("tide-metric-row");
    }

    [Fact]
    public async Task AFinishedGroup_FoldsOnlyWhenLeft_UnfoldsByHand_AndUnfoldsWhenAValueIsCleared()
    {
        var day = new DateOnly(2027, 4, 6);
        var first = await Group("Vulnerabilities", "Critical", "High");
        var next = await Group("Endpoint alerts", "New");
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day });

        await r.Click(c => { c.Entering(first.Metrics[0], false); return Task.CompletedTask; });
        Chip(await r.Html(), "Vulnerabilities").Should().Contain("aria-current=\"true\"", "the chip of the group being typed in is highlighted");
        var critical = await Tile(first.Metrics[0], day);
        await r.Click(c => c.SaveValue(critical, "4"));
        await r.Click(c => { c.Left(first.Metrics[0], false); c.Entering(first.Metrics[1], false); return Task.CompletedTask; });
        var high = await Tile(first.Metrics[1], day);
        await r.Click(c => c.SaveValue(high, "9"));

        var typing = Panel(await r.Html(), first.Group);
        typing.Should().Contain(">2 of 2").And.Contain("tide-metric-row", "focus is still in it: it doesn't fold under the cursor").And.NotContain("tide-mentry-folded");

        await r.Click(c => { c.Left(first.Metrics[1], false); c.Entering(next.Metrics[0], false); return Task.CompletedTask; });
        var html = await r.Html();
        Panel(html, first.Group).Should().Contain("tide-mentry-folded").And.Contain("aria-expanded=\"false\"").And.NotContain("tide-metric-row");
        Chip(html, "Endpoint alerts").Should().Contain("aria-current=\"true\"");
        Chip(html, "Vulnerabilities").Should().NotContain("aria-current");

        await r.Click(c => { c.Toggle(first.Group); return Task.CompletedTask; });
        Panel(await r.Html(), first.Group).Should().Contain("aria-expanded=\"true\"").And.Contain("tide-metric-row");

        var highNow = await Tile(first.Metrics[1], day);
        await r.Click(c => c.SaveValue(highNow, ""));
        await r.Click(c => { c.Entering(next.Metrics[0], false); return Task.CompletedTask; });
        Panel(await r.Html(), first.Group).Should().Contain(">1 of 2").And.Contain("tide-metric-row", "cleared, it isn't complete, so it doesn't fold")
            .And.NotContain("aria-expanded");
    }

    [Fact]
    public async Task AColleagueClearingAValue_UnfoldsAFoldedGroup()
    {
        var day = new DateOnly(2027, 4, 7);
        var g = await Group("Phish reports", "Received", "Actioned");
        foreach (var m in g.Metrics) await Value(m, day, 5);
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day });
        Panel(await r.Html(), g.Group).Should().Contain("tide-mentry-folded");

        await Value(g.Metrics[1], day, null, Robin);
        var html = await r.HtmlOnce(">1 of 2");
        Panel(html, g.Group).Should().Contain(">1 of 2").And.Contain("tide-metric-row").And.NotContain("tide-mentry-folded");
    }

    [Fact]
    public async Task AGroupHoldingWhatIsntANumber_NeverFolds()
    {
        var day = new DateOnly(2027, 4, 8);
        var g = await Group("Identity", "Lockouts", "Resets");
        var other = await Group("Network", "Blocks");
        await Value(g.Metrics[0], day, 1);
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day });

        await r.Click(c => { c.Entering(g.Metrics[1], false); return Task.CompletedTask; });
        var resets = await Tile(g.Metrics[1], day);
        await r.Click(c => c.SaveValue(resets, "2"));
        var lockouts = await Tile(g.Metrics[0], day);
        await r.Click(c => c.SaveValue(lockouts, "lots"));
        await r.Click(c => { c.Entering(other.Metrics[0], false); return Task.CompletedTask; });

        Panel(await r.Html(), g.Group).Should().Contain("value=\"lots\"").And.Contain("aria-invalid=\"true\"").And.NotContain("tide-mentry-folded");
    }

    [Fact]
    public async Task AChip_OpensAFoldedGroup()
    {
        var day = new DateOnly(2027, 4, 9);
        var g = await Group("Cloud", "Findings");
        await Group("Data loss", "Events");
        await Value(g.Metrics[0], day, 8);
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day });
        Panel(await r.Html(), g.Group).Should().Contain("tide-mentry-folded");

        await r.Click(c => c.Jump(g.Group));
        var html = await r.Html();
        Panel(html, g.Group).Should().Contain("tide-metric-row").And.Contain("value=\"8\"");
        Chip(html, "Cloud").Should().Contain("aria-current=\"true\"");
    }

    [Fact]
    public async Task SomeoneWhoCantEnterNumbers_SeesTheCountsAndEveryValue_WithoutButtons()
    {
        var day = new DateOnly(2027, 4, 12);
        var g = await Group("Web filtering", "Blocked sites");
        await Value(g.Metrics[0], day, 31);
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day }, Owner);
        var html = await r.Html();

        html.Should().Contain("<span class=\"visually-hidden\">Web filtering, 1 of 1 entered</span>").And.NotContain("<button type=\"button\" class=\"tide-mentry-chip");
        Panel(html, g.Group).Should().Contain("31").And.NotContain("tide-mentry-folded", "nothing folds when there's nothing to type").And.NotContain("<input");
    }

    [Fact]
    public async Task TheEditionsNumbers_ShowADaysNote_UnderItsValue_AndNothingWithoutOne()
    {
        var group = new MetricGroup { Name = "Email triage" };
        var noted = new MetricTile(new MetricDefinition { Name = "Reported", Group = group }, 14, "Two campaigns from the same sender", null, MetricLevel.Normal, []);
        var plain = new MetricTile(new MetricDefinition { Name = "Clicked", Group = group }, 2, null, null, MetricLevel.Normal, []);
        var day = new MetricDay(new DateOnly(2027, 4, 13), [new MetricGroupDay(group, [noted, plain])]);

        await using var r = await ComponentHarness.RenderAsync<EditionNumbers>(app, "/", new() { ["Day"] = day });
        var html = await r.Html();

        html.Should().Contain("<div class=\"tide-ed-numbers-note\" title=\"Two campaigns from the same sender\"><span aria-hidden=\"true\">Two campaigns from the same sender</span><span class=\"visually-hidden\">Note: Two campaigns from the same sender</span></div>");
        var clicked = html[html.IndexOf(">Clicked<", StringComparison.Ordinal)..];
        clicked[..clicked.IndexOf("</tr>", StringComparison.Ordinal)].Should().NotContain("tide-ed-numbers-note");
        html.Split("tide-ed-numbers-note").Length.Should().Be(2, "one note, on one row");
    }
}
