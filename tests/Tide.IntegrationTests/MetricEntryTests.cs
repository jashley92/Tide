using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Metrics;
using Tide.Web.Components.Shared;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>The day's numbers form (MetricEntry): what's beside each box before and after a value is typed, and a value
/// that isn't a number. Its own app, so the metric it adds doesn't reach other tests' briefs.</summary>
public class MetricEntryTests(TideApp app) : IClassFixture<TideApp>
{
    // Far from the demo's briefs, so no sent brief locks the numbers.
    private static readonly DateOnly Thu = new(2027, 2, 25), Fri = new(2027, 2, 26), Mon = new(2027, 3, 1), Tue = new(2027, 3, 2);

    // A colleague, in another circuit (scope) of their own.
    private static readonly ComponentHarness.SignIn Robin = new("dev:robin", "Robin", "Analyst");

    private async Task<Guid> Metric(string name)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var svc = scope.ServiceProvider.GetRequiredService<MetricsService>();
        var group = await svc.AddGroupAsync("Queue " + name);
        return await svc.SaveMetricAsync(new MetricForm { GroupId = group, Name = name });
    }

    private async Task Value(Guid id, DateOnly day, decimal value)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        await scope.ServiceProvider.GetRequiredService<MetricsService>().SaveValueAsync(id, day, value, null);
    }

    private async Task ValueAs(ComponentHarness.SignIn who, Guid id, DateOnly day, decimal? value, string? note = null)
    {
        await using var scope = ComponentHarness.SignedIn(app, who);
        await scope.ServiceProvider.GetRequiredService<MetricsService>().SaveValueAsync(id, day, value, note);
    }

    private async Task<MetricTile> Tile(Guid id, DateOnly day)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<MetricsService>().GetDayAsync(day))
            .Groups.SelectMany(g => g.Tiles).Single(t => t.Metric.Id == id);
    }

    // The whole row, the note of a change elsewhere included.
    private static string WholeRow(string html, Guid id)
    {
        var at = html.IndexOf("id=\"metric-" + id.ToString("N") + "\"", StringComparison.Ordinal);
        at.Should().BePositive();
        var end = html.IndexOf("class=\"tide-metric-row\"", at, StringComparison.Ordinal);
        return end < 0 ? html[at..] : html[at..end];
    }

    private static string Row(string html, Guid id)
    {
        var at = html.IndexOf("id=\"metric-" + id.ToString("N") + "\"", StringComparison.Ordinal);
        at.Should().BePositive();
        var end = html.IndexOf("tide-metric-note", at, StringComparison.Ordinal);
        return html[at..end];
    }

    [Fact]
    public async Task ThePreviousValue_IsBesideAnEmptyBox_NamedByItsDate_WhenItIsntTheDayBefore()
    {
        var skipped = await Metric("Skipped a day");
        await Value(skipped, Thu, 12);
        var usual = await Metric("Usual");
        await Value(usual, Fri, 40);

        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = Mon });
        var html = await r.Html();

        Row(html, skipped).Should().Contain("value=\"\"").And.Contain("tide-metric-ref-date\">Feb 25</span><span class=\"tide-metric-ref-value\">12</span>", "Friday had no value: Thursday is named by its date")
            .And.NotContain("tide-trend-", "there's no change until today's value is typed");
        Row(html, usual).Should().Contain("tide-metric-ref-date\">Fri</span><span class=\"tide-metric-ref-value\">40</span>");

        await Value(usual, Mon, 43);
        await using var typed = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = Mon });
        Row(await typed.Html(), usual).Should().Contain("tide-metric-ref-date\">Fri</span><span class=\"tide-metric-ref-value\">40</span>").And.Contain("tide-trend-neutral", "the change shows once there's a value");
    }

    [Fact]
    public async Task AValueThatIsntANumber_IsMarkedOnItsBox_UntilItIsOne()
    {
        var id = await Metric("Typed badly");
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = Mon });
        MetricTile tile;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            tile = (await scope.ServiceProvider.GetRequiredService<MetricsService>().GetDayAsync(Mon))
                .Groups.SelectMany(g => g.Tiles).Single(t => t.Metric.Id == id);

        await r.Click(c => c.SaveValue(tile, "abc"));
        var row = Row(await r.Html(), id);
        var error = "metric-" + id.ToString("N") + "-error";
        row.Should().Contain("value=\"abc\"").And.Contain("aria-invalid=\"true\"").And.Contain($"aria-describedby=\"{error}\"")
            .And.Contain($"<div id=\"{error}\" class=\"tide-metric-error\">Not a number.</div>");
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.Message.Contains("'abc' isn't a number"));

        await r.Click(c => c.SaveValue(tile, "7"));
        Row(await r.Html(), id).Should().Contain("value=\"7\"").And.NotContain("aria-invalid").And.NotContain("tide-metric-error");
    }

    [Fact]
    public async Task AColleaguesSave_IsReadInPlace_AndNamesThem()
    {
        var id = await Metric("Seen live");
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = Tue });
        Row(await r.Html(), id).Should().Contain("value=\"\"");

        await ValueAs(Robin, id, Tue, 517);

        Row(await r.HtmlOnce("value=\"517\""), id).Should().Contain("value=\"517\"");
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.Message == "Robin updated the numbers.");
    }

    [Fact]
    public async Task ABoxBeingTypedIn_OrHoldingWhatIsntANumber_KeepsItsText_AndSaysTheyChangedIt()
    {
        var bad = await Metric("Held invalid");
        var typing = await Metric("Held typing");
        var other = await Metric("Not held");
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = Tue });
        var badTile = await Tile(bad, Tue);
        await r.Click(c => c.SaveValue(badTile, "abc"));
        await r.Click(c => { c.Entering(typing, false); return Task.CompletedTask; });

        await ValueAs(Robin, bad, Tue, 9);
        await ValueAs(Robin, typing, Tue, 44);
        await ValueAs(Robin, other, Tue, 31);

        var html = await r.HtmlOnce("value=\"31\"");
        WholeRow(html, bad).Should().Contain("value=\"abc\"").And.Contain("aria-invalid=\"true\"")
            .And.Contain("tide-metric-elsewhere").And.Contain("Robin changed Held invalid while you were editing it (now 9)");
        WholeRow(html, typing).Should().Contain("value=\"\"", "the box being typed in isn't written over")
            .And.Contain("Robin changed Held typing while you were editing it (now 44)");
        WholeRow(html, other).Should().NotContain("tide-metric-elsewhere");

        await r.Click(c => { c.Left(typing, false); return Task.CompletedTask; });
        WholeRow(await r.Html(), typing).Should().Contain("value=\"44\"", "left without saving, the box shows theirs").And.NotContain("tide-metric-elsewhere");
        await r.Click(c => c.SaveValue(badTile, "10"));
        WholeRow(await r.Html(), bad).Should().Contain("value=\"10\"").And.NotContain("tide-metric-elsewhere");
    }
}
