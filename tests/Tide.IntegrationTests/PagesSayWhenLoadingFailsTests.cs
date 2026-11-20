using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tide.Application.Abstractions;
using Tide.Application.Intake;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>The app with a database a test can take down (<see cref="Down"/>): every read made while it's down throws,
/// as one does when the server can't be reached.</summary>
public class DatabaseOutageApp : TideApp
{
    public bool Down { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder, services =>
        {
            services.RemoveAll<IAppDbContextFactory>();
            services.AddScoped<IAppDbContextFactory>(sp => new Outage(this,
                new AppDbContextFactoryAdapter(sp.GetRequiredService<IDbContextFactory<AppDbContext>>())));
        });
    }

    // Asks the app each time, so a scope made before the outage fails too once it starts.
    private sealed class Outage(DatabaseOutageApp app, IAppDbContextFactory inner) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateAsync(CancellationToken ct = default) =>
            new FlakyDbs(inner) { Down = app.Down }.CreateAsync(ct);
    }
}

/// <summary>A page whose read fails says it couldn't load, rather than loading for ever or failing the whole page.</summary>
public class PagesSayWhenLoadingFailsTests(DatabaseOutageApp app) : IClassFixture<DatabaseOutageApp>, IAsyncLifetime
{
    public Task InitializeAsync()
    {
        _ = app.Services;   // started (and its database made) before any outage
        app.Down = false;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        app.Down = false;
        return Task.CompletedTask;
    }

    private async Task<string> FailedRender<T>(string path, string says, Dictionary<string, object?>? values = null) where T : Microsoft.AspNetCore.Components.IComponent
    {
        app.Down = true;
        await using var r = await ComponentHarness.RenderAsync<T>(app, path, values);
        var html = await r.HtmlOnce(says);
        app.Down = false;
        html.Should().Contain(says).And.NotContain("tide-spinner");
        return html;
    }

    [Fact]
    public async Task Reports_WhenTheDatabaseIsDown_SaysTheReportFailedToLoad() =>
        await FailedRender<Reports>("/reports", "Failed to load the report");

    [Fact]
    public async Task MetricHistory_WhenTheDatabaseIsDown_SaysItFailedToLoad() =>
        await FailedRender<MetricsHistory>("/metrics", "Failed to load the metric history");

    [Fact]
    public async Task BriefArchive_WhenTheDatabaseIsDown_SaysItFailedToLoad_InsteadOfFailingThePage() =>
        await FailedRender<BriefsArchive>("/briefs", "Failed to load the brief archive");

    [Fact]
    public async Task PublishedList_WhenTheDatabaseIsDown_SaysItFailedToLoad_InsteadOfFailingThePage() =>
        await FailedRender<Published>("/published", "Failed to load the published briefs");

    [Fact]
    public async Task PublishedDay_WhenTheDatabaseIsDown_SaysItFailedToLoad_InsteadOfFailingThePage() =>
        await FailedRender<Published>("/published/2026-11-09", "Failed to load the published brief", new() { ["Day"] = "2026-11-09" });

    [Fact]
    public async Task Search_WhenTheDatabaseIsDown_SaysTheResultsFailedToLoad() =>
        await FailedRender<Search>("/search?q=ransomware", "Failed to load the search results");

    [Fact]
    public async Task Search_PressedAgainOnTheSameWords_RunsTheSearchAgain()
    {
        var word = "Zebrafinch" + Guid.NewGuid().ToString("N")[..6];
        await using var r = await ComponentHarness.RenderAsync<Search>(app, "/search?q=" + word);
        Stories(r.Component).Should().BeEmpty("nothing has the word yet");

        Guid story;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            story = await scope.ServiceProvider.GetRequiredService<StoryService>().CreateAsync(word + " campaign", null);

        await r.Click(c => c.Go());
        Stories(r.Component).Should().Equal([story], "Search on the same words reads afresh");
    }

    private static IEnumerable<Guid> Stories(Search page)
    {
        var results = (Tide.Application.Search.SearchResults?)typeof(Search)
            .GetField("_results", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page);
        return results is null ? [] : results.Stories.Items.Select(s => s.Id);
    }
}
