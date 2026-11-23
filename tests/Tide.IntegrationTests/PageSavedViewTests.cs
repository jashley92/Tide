using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Preferences;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Saved views on Triage, Articles and Search, as on Actions: a pill each over the filters, the one shown marked, and
/// Save view at the end of the row. Triage's view is its tab and filters, never the row it has selected or its page,
/// and opens in place.
/// </summary>
public class PageSavedViewTests(TideApp app) : IClassFixture<TideApp>
{
    // Each test signs in as its own person, so what one saves can't reach another (or the rest of the suite's tester).
    private static SignIn Person(string role = "Analyst") => new($"dev:page-views-{Guid.NewGuid():N}", "Page views tester", role);

    private async Task<SavedViewEntry> Save(SignIn who, SavedViewPage page, string name, string query)
    {
        await using var scope = SignedIn(app, who);
        return await scope.ServiceProvider.GetRequiredService<SavedViewService>().SaveAsync(page, name, query);
    }

    private static void Redraw(Microsoft.AspNetCore.Components.ComponentBase c) =>
        typeof(Microsoft.AspNetCore.Components.ComponentBase).GetMethod("StateHasChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(c, null);

    [Fact]
    public async Task Triage_AViewIsItsTabAndFilters_NotTheRowOrPage_AndOpensInPlace()
    {
        var who = Person();
        await Save(who, SavedViewPage.Triage, "Dismissed KEV", "tab=dismissed&cve=kev");

        await using var r = await RenderAsync<Triage>(app, $"/triage?tab=new&named=true&page=2&row=3&at={Guid.NewGuid()}", @as: who);
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("Dismissed KEV"));
        html.Should().Contain("aria-label=\"Saved views\"").And.Contain("href=\"triage?tab=dismissed&cve=kev\"").And.Contain("id=\"save-view\"");
        html.Should().NotContain("href=\"triage?tab=dismissed&cve=kev\" aria-current=\"page\"", "it isn't what's shown");
        (await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.ShownQuery)).Should().Be("tab=new&named=true", "the row selected and the page aren't part of a view");

        // Its pill opens it here: the page writes its own address, so the router's link alone wouldn't.
        await r.Click(async p =>
        {
            await p.OpenView("tab=dismissed&cve=kev");
            Redraw(p);   // as the pill's click handler does after it
        });
        (await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.ShownQuery)).Should().Be("tab=dismissed&cve=kev");
        WebUtility.HtmlDecode(await r.Html()).Should().Contain("href=\"triage?tab=dismissed&cve=kev\" aria-current=\"page\"");
    }

    [Fact]
    public async Task Articles_AViewIsItsFilters_MarkedWhileShown()
    {
        var who = Person();
        await Save(who, SavedViewPage.Articles, "My drafts", "author=me&status=draft");

        await using (var r = await RenderAsync<Articles>(app, "/articles?status=draft&author=me&page=2", @as: who))
        {
            var html = WebUtility.HtmlDecode(await r.HtmlOnce("My drafts"));
            html.Should().Contain("href=\"articles?author=me&status=draft\" aria-current=\"page\">My drafts</a>", "the page isn't part of a view")
                .And.Contain("My drafts: rename or delete");
        }

        await using (var r = await RenderAsync<Articles>(app, "/articles?status=ready", @as: who))
            WebUtility.HtmlDecode(await r.HtmlOnce("My drafts")).Should().NotContain("aria-current=\"page\">My drafts</a>");

        await using (var other = await RenderAsync<Articles>(app, "/articles?status=draft&author=me", @as: Person()))
            WebUtility.HtmlDecode(await other.Html()).Should().NotContain("My drafts", "someone else's views aren't shown");
    }

    [Fact]
    public async Task Search_AViewIsASearch_AndThereIsNothingToSaveBeforeOne()
    {
        var who = Person("Viewer");
        await Save(who, SavedViewPage.Search, "LockBit", "q=lockbit");

        await using (var r = await RenderAsync<Search>(app, "/search?q=lockbit", @as: who))
            WebUtility.HtmlDecode(await r.HtmlOnce("LockBit")).Should().Contain("href=\"search?q=lockbit\" aria-current=\"page\">LockBit</a>");

        await using (var r = await RenderAsync<Search>(app, "/search", @as: who))
            WebUtility.HtmlDecode(await r.HtmlOnce("LockBit")).Should().Contain("Nothing to keep yet");
    }
}
