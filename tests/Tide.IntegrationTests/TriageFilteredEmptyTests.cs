using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>
/// Triage filtered to nothing: the list says the filters hide the tab's items and offers to clear them, rather than
/// calling the tab empty ("Inbox zero") while its count says otherwise.
/// </summary>
public class TriageFilteredEmptyTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<string> SeedDismissed()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var item = new FeedItem
        {
            Source = new Source { Name = "Filtered feed " + key, Url = $"https://filtered.example/{key}/feed" },
            Status = FeedItemStatus.Dismissed, Title = $"Filtered {key}", Url = $"https://filtered.example/{key}/1",
            UrlHash = $"filtered-{key}", PublishedAtUtc = now, FetchedAtUtc = now,
        };
        db.Add(item);
        await db.SaveChangesAsync();
        return item.Title;
    }

    [Fact]
    public async Task FilteredToNothing_SaysTheFiltersHideTheTabsItems_AndClearShowsThemAgain()
    {
        var title = await SeedDismissed();
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage?tab=dismissed&q=" + Uri.EscapeDataString("no-such-words-" + Guid.NewGuid().ToString("N")));

        var html = WebUtility.HtmlDecode(await r.HtmlOnce("Nothing matches these filters"));
        html.Should().MatchRegex(@"\d[\d,]* items? in Dismissed (is|are) hidden by them\.").And.Contain("Clear the filters")
            .And.NotContain("Nothing here.", "the tab has items; the filters hide them");

        // Clear, then draw the page as a click would (the harness has no circuit to do it).
        await r.Click(async c =>
        {
            await c.ClearFilters();
            typeof(Microsoft.AspNetCore.Components.ComponentBase).GetMethod("StateHasChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(c, null);
        });
        (await r.HtmlOnce(title)).Should().NotContain("Nothing matches these filters");
    }
}
