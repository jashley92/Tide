using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tide.Application.Abstractions;
using Tide.Application.Metrics;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>Bug round 1 on the day's numbers form (MetricEntry): another day's form doesn't carry the last day's typing,
/// and a group being typed in doesn't fold on its own save. Its own app, so the groups are only this class's.</summary>
public class EntryRound1BugTests(TideApp app) : IClassFixture<TideApp>
{
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

    private async Task<MetricTile> Tile(Guid id, DateOnly day)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var d = await scope.ServiceProvider.GetRequiredService<MetricsService>().GetDayAsync(day);
        return d.Groups.SelectMany(g => g.Tiles).Single(t => t.Metric.Id == id);
    }

    private static string Panel(string html, Guid group)
    {
        var at = html.IndexOf("-g-" + group.ToString("N") + "\" aria-labelledby", StringComparison.Ordinal);
        at.Should().BePositive();
        var end = html.IndexOf("<section", at, StringComparison.Ordinal);
        return end < 0 ? html[at..] : html[at..end];
    }

    [Fact]
    public async Task AGroupBeingTypedIn_DoesntFold_WhenItsOwnValueSaves()
    {
        var day = new DateOnly(2027, 5, 4);
        var g = await Group("Firewall", "Denied");
        await Group("Wireless", "Rogue");
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day });

        await r.Click(c => { c.Entering(g.Metrics[0], false); return Task.CompletedTask; });
        var denied = await Tile(g.Metrics[0], day);
        await r.Click(c => c.SaveValue(denied, "3"));

        Panel(await r.Html(), g.Group).Should().Contain("tide-metric-row").And.NotContain("tide-mentry-folded");
    }

    [Fact]
    public async Task AnotherDay_DoesntCarryTheLastDaysTyping()
    {
        // The brief page's previous and next links keep the form and change its date.
        var monday = new DateOnly(2027, 5, 10);
        var tuesday = new DateOnly(2027, 5, 11);
        var g = await Group("Endpoint", "Quarantined");
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = monday });

        var tile = await Tile(g.Metrics[0], monday);
        await r.Click(c => c.SaveValue(tile, "lots"));
        Panel(await r.Html(), g.Group).Should().Contain("value=\"lots\"");

        await r.Click(c => c.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Date"] = tuesday,
            ["Collapsible"] = false,
        })));
        var html = await r.Html();
        html.Should().Contain("Numbers for " + Ui.ShortDay(tuesday));
        Panel(html, g.Group).Should().NotContain("lots", "Monday's typing isn't Tuesday's").And.NotContain("aria-invalid");
    }
}

/// <summary>The app with a database that answers a moment later, as SQL Server does (SQLite answers at once), so an event
/// can arrive while a save is still going.</summary>
public class SlowDatabaseTideApp : TideApp
{
    private sealed class Yielding(IDbContextFactory<AppDbContext> inner) : IAppDbContextFactory
    {
        public async Task<IAppDbContext> CreateAsync(CancellationToken ct = default)
        {
            await Task.Yield();
            return await inner.CreateDbContextAsync(ct);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder, services =>
        {
            services.RemoveAll<IAppDbContextFactory>();
            services.AddScoped<IAppDbContextFactory, Yielding>();
        });
    }
}

/// <summary>Bug round 1: a group finished as focus moves on to another group still folds.</summary>
public class EntryRound1FoldTests(SlowDatabaseTideApp app) : IClassFixture<SlowDatabaseTideApp>
{
    private static string Panel(string html, Guid group)
    {
        var at = html.IndexOf("-g-" + group.ToString("N") + "\" aria-labelledby", StringComparison.Ordinal);
        at.Should().BePositive();
        var end = html.IndexOf("<section", at, StringComparison.Ordinal);
        return end < 0 ? html[at..] : html[at..end];
    }

    [Fact]
    public async Task AGroupFinished_AsFocusMovesToAnotherGroup_FoldsOnceItsLastValueIsSaved()
    {
        // A click from a group's last box into another group: the browser sends the box's change, then the focus on the
        // other box, and the focus is heard while the value is still saving, when the group wasn't finished yet.
        var day = new DateOnly(2027, 5, 3);
        Guid first, firstMetric, next, nextMetric;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var svc = scope.ServiceProvider.GetRequiredService<MetricsService>();
            first = await svc.AddGroupAsync("Mail gateway");
            firstMetric = await svc.SaveMetricAsync(new MetricForm { GroupId = first, Name = "Held" });
            next = await svc.AddGroupAsync("Proxy");
            nextMetric = await svc.SaveMetricAsync(new MetricForm { GroupId = next, Name = "Blocked" });
        }
        MetricTile held;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            held = (await scope.ServiceProvider.GetRequiredService<MetricsService>().GetDayAsync(day)).Groups.SelectMany(g => g.Tiles).Single(t => t.Metric.Id == firstMetric);
        await using var r = await ComponentHarness.RenderAsync<MetricEntry>(app, "/", new() { ["Date"] = day });

        await r.Click(c => { c.Entering(firstMetric, false); return Task.CompletedTask; });
        await r.Click(async c =>
        {
            var saving = c.SaveValue(held, "7");
            c.Entering(nextMetric, false);
            await saving;
        });

        var html = await r.Html();
        Panel(html, first).Should().Contain("tide-mentry-folded", "focus has left it and it's finished").And.NotContain("tide-metric-row");
        Panel(html, next).Should().Contain("tide-metric-row", "the group focus is in stays open");
    }
}
