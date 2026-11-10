using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// EP-85 on the team libraries (tabs of the Library page): Saved queries and Outreach templates list 20 entries a page
/// with "1–20 of 34", the page in the address, a filter starting again from the first page, and a delete that takes a
/// page away showing the last.
/// </summary>
public class LibraryPagingTests(TideApp app) : IClassFixture<TideApp>
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // 15 KQL queries and 10 FQL ones, all with the test's own word in their names; their ids, KQL first.
    private async Task<List<Guid>> SeedQueries(string tag)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var svc = scope.ServiceProvider.GetRequiredService<SavedQueryService>();
        var ids = new List<Guid>();
        for (var i = 0; i < 25; i++)
            ids.Add(await svc.SaveAsync(new SavedQueryForm { Name = $"Paged {tag} {i:00}", Dialect = i < 15 ? "kql" : "fql", Text = "DeviceProcessEvents | take 10" }));
        return ids;
    }

    private async Task<List<Guid>> SeedTemplates(string tag)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var svc = scope.ServiceProvider.GetRequiredService<OutreachTemplateService>();
        var ids = new List<Guid>();
        for (var i = 0; i < 25; i++)
            ids.Add(await svc.SaveAsync(new OutreachTemplateForm { Name = $"Paged {tag} {i:00}", Subject = "Are we affected?", Body = "Hello, are we affected by {{article.title}}?" }));
        return ids;
    }

    // Types the words into Find, as the box's change does (the first page, then what's left), and the address follows
    // (the first page's has no ?page=), as the live page's does.
    private static async Task Find<T>(Rendered<T> r, string words) where T : Microsoft.AspNetCore.Components.IComponent
    {
        await r.Click(c =>
        {
            typeof(T).GetField("_words", Private)!.SetValue(c, words);
            typeof(T).GetMethod("Refilter", Private)!.Invoke(c, null);
            return Task.CompletedTask;
        });
        await Go(r, 1);
    }

    // Moves to a page as a pager link does: the address's page, read as it changes.
    private static Task Go<T>(Rendered<T> r, int page) where T : Microsoft.AspNetCore.Components.IComponent => r.Click(c =>
    {
        typeof(T).GetProperty("PageParam")!.SetValue(c, page == 1 ? null : page.ToString(System.Globalization.CultureInfo.InvariantCulture));
        typeof(T).GetMethod("OnParametersSet", Private)!.Invoke(c, null);
        return Task.CompletedTask;
    });

    [Fact]
    public async Task SavedQueries_Show20APage_ThePageFromTheAddress_EachLanguageSaidOnEveryPage()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        await SeedQueries(tag);
        int total;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            total = (await scope.ServiceProvider.GetRequiredService<SavedQueryService>().ListAsync()).Count;

        await using (var second = await RenderAsync<SavedQueriesPage>(app, "/library/queries?page=2"))
        {
            second.Component.Page.Should().Be(2, "the page is the address's");
            var html = WebUtility.HtmlDecode(await second.Html());
            html.Should().Contain($"21–{Math.Min(40, total)} of {total} saved queries");
            html.Should().Contain("<nav class=\"tide-pager\" aria-label=\"Pages of saved queries\">")
                .And.Contain("aria-current=\"page\" aria-label=\"Page 2, this page\"")
                .And.Contain("href=\"http://localhost/library/queries\" aria-label=\"Page 1\"", "the first page's link has no page in it");
        }

        await using var r = await RenderAsync<SavedQueriesPage>(app, "/library/queries?page=2");
        await Find(r, tag);
        r.Component.Page.Should().Be(1, "a new filter starts from the first page");
        r.Component.Shown.Should().HaveCount(25);
        r.Component.OnPage.Should().HaveCount(20);
        r.Component.OnPage.Take(15).Should().OnlyContain(q => q.Query.Dialect == "kql", "a language's queries come together");

        await Go(r, 2);
        r.Component.OnPage.Should().HaveCount(5).And.OnlyContain(q => q.Query.Dialect == "fql");
        await r.Click(p => p.OnChangedElsewhere([]));   // a colleague's change: read again, on the same page
        r.Component.Page.Should().Be(2);
        var two = WebUtility.HtmlDecode(await r.Html());
        two.Should().Contain("21–25 of 25 saved queries");
        Regex.Matches(two, "<span class=\"tide-library-row-meta\">([^<]*)</span>").Select(m => m.Groups[1].Value).Should()
            .HaveCount(5).And.OnlyContain(meta => meta == "Falcon · Not used yet", "each row says its language, on whichever page it is");

        // The language filter starts from the first page too.
        await r.Click(p =>
        {
            typeof(SavedQueriesPage).GetField("_language", Private)!.SetValue(p, "kql");
            typeof(SavedQueriesPage).GetMethod("Refilter", Private)!.Invoke(p, null);
            return Task.CompletedTask;
        });
        r.Component.Page.Should().Be(1);
        r.Component.Shown.Should().HaveCount(15);
    }

    [Fact]
    public async Task SavedQueries_ALanguageRunningOnToTheNextPage_IsSaidThereToo()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        await SeedQueries(tag);
        // 25 KQL ones more: 40 KQL and 10 FQL, so KQL runs on to page 2.
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var svc = scope.ServiceProvider.GetRequiredService<SavedQueryService>();
            for (var i = 25; i < 50; i++) await svc.SaveAsync(new SavedQueryForm { Name = $"Paged {tag} {i:00}", Dialect = "kql", Text = "DeviceEvents | take 1" });
        }
        await using var r = await RenderAsync<SavedQueriesPage>(app, "/library/queries");
        await Find(r, tag);
        await Go(r, 2);
        await r.Click(p => p.OnChangedElsewhere([]));
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("21–40 of 50 saved queries");
        Regex.Matches(html, "<span class=\"tide-library-row-meta\">([^<]*)</span>").Select(m => m.Groups[1].Value).Should()
            .HaveCount(20).And.OnlyContain(meta => meta.StartsWith("KQL · ", StringComparison.Ordinal), "KQL runs on from page 1, and each row says so");
        html.Should().Contain("Microsoft Sentinel / Defender (KQL)</span>", "the query shown in full names its language whole");
    }

    [Fact]
    public async Task SavedQueries_ADeleteThatTakesThePageAway_ShowsTheLastPage()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var ids = await SeedQueries(tag);
        await using var r = await RenderAsync<SavedQueriesPage>(app, "/library/queries");
        await Find(r, tag);
        await Go(r, 2);
        r.Component.OnPage.Should().HaveCount(5);

        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var svc = scope.ServiceProvider.GetRequiredService<SavedQueryService>();
            foreach (var id in ids.Skip(20)) await svc.DeleteAsync(id);
        }
        await r.Click(p => p.OnChangedElsewhere([]));
        r.Component.Page.Should().Be(1, "page 2 is gone, so the last page shows");
        r.Component.OnPage.Should().HaveCount(20);
        WebUtility.HtmlDecode(await r.Html()).Should().Contain("20 saved queries").And.NotContain("Pages of saved queries", "one page needs no pager");
    }

    [Fact]
    public async Task OutreachTemplates_Show20APage_ThePageFromTheAddress_AFilterGoesBackToPage1_AndAGonePageShowsTheLast()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var ids = await SeedTemplates(tag);
        int total;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            total = (await scope.ServiceProvider.GetRequiredService<OutreachTemplateService>().ListAsync()).Count;

        await using var r = await RenderAsync<OutreachTemplatesPage>(app, "/library/templates?page=2");
        r.Component.Page.Should().Be(2);
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain($"21–{Math.Min(40, total)} of {total} templates").And.Contain("aria-label=\"Pages of templates\"")
            .And.Contain("aria-current=\"page\" aria-label=\"Page 2, this page\"");

        await Find(r, tag);
        r.Component.Page.Should().Be(1);
        (r.Component.Shown.Count, r.Component.OnPage.Count).Should().Be((25, 20));

        await Go(r, 2);
        r.Component.OnPage.Should().HaveCount(5);
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var svc = scope.ServiceProvider.GetRequiredService<OutreachTemplateService>();
            foreach (var id in ids.Take(5)) await svc.SetRetiredAsync(id, true);
        }
        await r.Click(p => p.OnChangedElsewhere([]));
        r.Component.Page.Should().Be(1, "retiring five took page 2 away");
        r.Component.OnPage.Should().HaveCount(20);
    }
}
