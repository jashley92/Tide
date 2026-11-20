using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Metrics;
using Tide.TestSupport;
using Tide.Web.Components.Shared;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>
/// The day's numbers form (MetricEntry) with colleagues saving alongside: their saves are told in at most one toast every
/// few seconds (ColleagueNews), naming everyone; and a save that fails for a reason other than the brief going out is
/// marked on its box, rather than the box going on showing what was typed as though it were kept.
/// </summary>
public class MetricEntryNewsAndFailureTests(TideApp app) : IClassFixture<TideApp>
{
    private static Change By(string name) => new(ChangeFeed.Metrics, "dev:" + name.ToLowerInvariant(), name, Guid.NewGuid());

    private static string[] Told(ToastService toasts) => [.. toasts.Toasts.Select(t => t.Message)];

    [Fact]
    public void AColleaguesSave_IsToldAtOnce_AndTheRestOfTheWindowsSaves_InOneToastNamingEveryone()
    {
        var clock = new ManualClock();
        var toasts = new ToastService(new ManualClock());   // the toasts stay put: only the window moves
        using var news = new ColleagueNews(toasts, who => $"{who} updated the numbers.", clock);

        news.Heard([By("Sam")]);
        Told(toasts).Should().Equal("Sam updated the numbers.");

        news.Heard([By("Robin")]);
        news.Heard([By("Sam")]);
        news.Heard([By("Robin")]);
        Told(toasts).Should().HaveCount(1, "a toast went a moment ago");

        clock.Advance(ColleagueNews.Window);
        Told(toasts).Should().Equal("Sam updated the numbers.", "Robin and Sam updated the numbers.");

        // A quiet window: nothing more is told, and the next save is told at once.
        clock.Advance(ColleagueNews.Window);
        Told(toasts).Should().HaveCount(2);
        news.Heard([By("Ash")]);
        Told(toasts).Should().HaveCount(3).And.EndWith("Ash updated the numbers.");
    }

    [Fact]
    public void ColleaguesNews_DoesntClearAnErrorFromThisPersonsOwnAttempt()
    {
        var clock = new ManualClock();
        var toasts = new ToastService(new ManualClock());   // the toasts stay put: only the window moves
        using var news = new ColleagueNews(toasts, who => $"{who} updated the numbers.", clock);

        toasts.Error("That number is too large.");
        news.Heard([By("Robin")]);

        toasts.Toasts.Should().Contain(t => t.IsError);
    }

    [Fact]
    public void OnceDisposed_NothingMoreIsTold()
    {
        var clock = new ManualClock();
        var toasts = new ToastService(new ManualClock());   // the toasts stay put: only the window moves
        var news = new ColleagueNews(toasts, who => $"{who} updated the numbers.", clock);
        news.Heard([By("Sam")]);
        news.Heard([By("Robin")]);

        news.Dispose();
        clock.Advance(ColleagueNews.Window);
        news.Heard([By("Ash")]);

        Told(toasts).Should().Equal("Sam updated the numbers.");
    }

    [Fact]
    public async Task ASaveThatFails_MarksItsBox_WithWhatWasTypedAndWhy_UntilASaveWorks()
    {
        // An administrator retires the metric while the form is open: the save is refused, though the form stays open
        // (it isn't the brief going out).
        var day = new DateOnly(2027, 7, 13);
        Guid metric;
        MetricTile tile;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var svc = scope.ServiceProvider.GetRequiredService<MetricsService>();
            metric = await svc.SaveMetricAsync(new MetricForm { GroupId = await svc.AddGroupAsync("Failed saves"), Name = "Refused" });
            await svc.SaveValueAsync(metric, day.AddDays(-1), 3, null);   // kept when retired, rather than deleted
            tile = (await svc.GetDayAsync(day)).Groups.SelectMany(g => g.Tiles).Single(t => t.Metric.Id == metric);
        }
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day });
        await Retired(metric, true);
        await r.Click(c =>
        {
            c.Entering(metric, false);
            return c.SaveValue(tile, "12");
        });

        var row = Row(await r.Html(), metric);
        row.Should().Contain("value=\"12\"", "what was typed stays, marked").And.Contain("is-invalid").And.Contain("aria-invalid=\"true\"");
        row.Should().Contain($"<div id=\"metric-{metric:N}-error\" class=\"tide-metric-error\">Not saved. That metric is retired; bring it back to enter values.</div>");
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.IsError && t.Message.StartsWith("That metric is retired", StringComparison.Ordinal));

        // A note typed next tries again with the value typed, not the one last saved; once that works the mark goes.
        await Retired(metric, false);
        await r.Click(async c =>
        {
            await c.SaveNote(tile, "after the outage");
            await c.OnChangedElsewhere([]);   // the browser's change event renders the form; here, a re-read does
        });

        row = Row(await r.Html(), metric);
        row.Should().NotContain("Not saved.").And.NotContain("is-invalid").And.Contain("value=\"12\"");
        await using var check = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var saved = (await check.ServiceProvider.GetRequiredService<MetricsService>().GetDayAsync(day)).Groups.SelectMany(g => g.Tiles).Single(t => t.Metric.Id == metric);
        (saved.Value, saved.Note).Should().Be((12m, "after the outage"));
    }

    private async Task Retired(Guid metric, bool retired)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        await scope.ServiceProvider.GetRequiredService<MetricsService>().SetMetricRetiredAsync(metric, retired);
    }

    private static string Row(string html, Guid metric)
    {
        var at = html.IndexOf($"id=\"metric-{metric:N}\"", StringComparison.Ordinal);
        at.Should().BePositive();
        return html[at..html.IndexOf("tide-metric-note", at, StringComparison.Ordinal)];
    }
}
