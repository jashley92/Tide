using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The Library page: the team's saved queries, outreach templates, playbooks and the board rules as four tabs of one
/// page, each tab an address of its own. The pages' old addresses go to their tabs; each tab counts its entries; a
/// library tab lists its entries by name and shows the selected one in full, the selection in the address.
/// </summary>
public partial class LibraryTests(TideApp app) : IClassFixture<TideApp>
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private HttpClient Client() => app.CreateClient(new() { AllowAutoRedirect = false });

    // Does what a control would, then draws again, as the page does after its own events.
    private static Task Do<T>(Rendered<T> r, Func<T, Task> what) where T : IComponent => r.Click(async c =>
    {
        await what(c);
        typeof(ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(c, null);
    });

    [Theory]
    [InlineData("/hunts/saved", "/library/queries")]
    [InlineData("/outreach/templates", "/library/templates")]
    [InlineData("/playbooks", "/library/playbooks")]
    [InlineData("/board-rules", "/library/board-rules")]
    [InlineData("/admin/playbooks", "/library/playbooks")]
    [InlineData("/hunts/saved?page=2", "/library/queries?page=2")]
    [InlineData("/Playbooks?page=3", "/library/playbooks?page=3")]
    public async Task AnOldAddress_GoesToItsTab_WithItsPage(string old, string tab)
    {
        var res = await Client().GetAsync(old);
        res.StatusCode.Should().Be(HttpStatusCode.Redirect, old);
        new Uri(new Uri("http://localhost/"), res.Headers.Location!).PathAndQuery.Should().Be(tab);
    }

    [Fact]
    public void EveryOldAddress_IsARouteOfTheRedirect_AndNoneIsAPageOfItsOwn()
    {
        var routes = typeof(LibraryMoved).GetCustomAttributes<RouteAttribute>().Select(r => r.Template.TrimStart('/')).ToList();
        routes.Should().BeEquivalentTo(LibraryTabs.All.SelectMany(t => t.Was));
        foreach (var page in new[] { typeof(SavedQueriesPage), typeof(OutreachTemplatesPage), typeof(PlaybooksPage), typeof(BoardRulesPage) })
            page.GetCustomAttributes<RouteAttribute>().Should().BeEmpty($"{page.Name} is a tab of the Library now");
    }

    [GeneratedRegex("<nav class=\"tide-tabbar tide-tabbar-page\" aria-label=\"Library\">.*?</nav>", RegexOptions.Singleline)]
    private static partial Regex TabBar();

    [GeneratedRegex("<a class=\"tide-tabbar-item ?(active)?\" href=\"([^\"]*)\"(?: aria-current=\"(page)\")?>.*?<span>([^<]*)</span>\\s*(?:<span class=\"tide-tabbar-count\">(\\d+)</span>)?", RegexOptions.Singleline)]
    private static partial Regex TabItem();

    // The tab bar as drawn: each tab's name, address, count and whether it's the current one.
    private static List<(string Label, string Href, int? Count, bool Current)> Tabs(string html)
    {
        var bar = TabBar().Match(html).Value;
        bar.Should().NotBeEmpty("the Library has a tab bar");
        return TabItem().Matches(bar).Select(m => (m.Groups[4].Value, m.Groups[2].Value,
            m.Groups[5].Success ? int.Parse(m.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture) : (int?)null, m.Groups[3].Success)).ToList();
    }

    [Fact]
    public async Task TheLibrary_IsOnePageOfFourTabs_EachWithItsCount()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        Guid retired;
        int queries, templates, playbooks, rules;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            await sp.GetRequiredService<SavedQueryService>().SaveAsync(new SavedQueryForm { Name = "Counted " + tag, Dialect = "kql", Text = "DeviceEvents | take 1" });
            retired = await sp.GetRequiredService<SavedQueryService>().SaveAsync(new SavedQueryForm { Name = "Retired " + tag, Dialect = "kql", Text = "DeviceEvents | take 1" });
            await sp.GetRequiredService<SavedQueryService>().SetRetiredAsync(retired, true);
            await sp.GetRequiredService<OutreachTemplateService>().SaveAsync(new OutreachTemplateForm { Name = "Counted " + tag, Subject = "Are we affected?", Body = "Hello" });
            await sp.GetRequiredService<CardRuleService>().SaveAsync(new ActionCardRule { Name = "Counted " + tag, Colour = CardColour.Red, Unassigned = true });
            queries = (await sp.GetRequiredService<SavedQueryService>().ListAsync()).Count;
            templates = (await sp.GetRequiredService<OutreachTemplateService>().ListAsync()).Count;
            playbooks = (await sp.GetRequiredService<PlaybookService>().ListAsync()).Count;
            rules = (await sp.GetRequiredService<CardRuleService>().ListAsync()).Count;
        }

        var html = WebUtility.HtmlDecode(await app.CreateClient().GetStringAsync("/library"));
        html.Should().Contain("<h1>Library</h1>");
        html.Split("<h1").Length.Should().Be(2, "one page, one title");
        Tabs(html).Should().Equal(
            ("Saved queries", "library/queries", queries, true),
            ("Outreach templates", "library/templates", templates, false),
            ("Playbooks", "library/playbooks", playbooks, false),
            ("Board rules", "library/board-rules", rules, false));
        html.Should().Contain("Counted " + tag, "the first tab is shown").And.NotContain("Retired " + tag, "a retired entry isn't counted or listed");
    }

    [Theory]
    [InlineData("queries", "Saved queries", "id=\"sq-find\"")]
    [InlineData("templates", "Outreach templates", "id=\"ot-find\"")]
    [InlineData("playbooks", "Playbooks", "id=\"pb-find\"")]
    [InlineData("board-rules", "Board rules", "Administrators set them.")]
    public async Task EachTab_HasItsOwnAddress_AndItsLinksCarryNothingOfAnotherTabs(string key, string label, string marker)
    {
        // The page and the row are this tab's: a link to another tab starts that tab from its own first page.
        var html = WebUtility.HtmlDecode(await app.CreateClient().GetStringAsync($"/library/{key}?page=2&row={Guid.NewGuid()}"));
        html.Should().Contain(marker).And.Contain($"<h2>{label}</h2>").And.NotContain("Something went wrong on this page");
        var tabs = Tabs(html);
        tabs.Single(t => t.Current).Label.Should().Be(label);
        tabs.Select(t => t.Href).Should().Equal("library/queries", "library/templates", "library/playbooks", "library/board-rules");
        foreach (var other in new[] { "id=\"sq-find\"", "id=\"ot-find\"", "id=\"pb-find\"", "Administrators set them." }.Where(m => m != marker))
            html.Should().NotContain(other, "one tab's content at a time");
    }

    [Fact]
    public async Task AnAddressWithATabThatIsntOne_ShowsTheFirstTab()
    {
        Tabs(await app.CreateClient().GetStringAsync("/library/nothing-here")).Single(t => t.Current).Href.Should().Be("library/queries");
    }

    [Fact]
    public async Task TheSelectedEntry_IsTheAddresss_OnWhicheverPageItIs_AndItsLinksKeepToTheirTab()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var ids = new List<Guid>();
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var svc = scope.ServiceProvider.GetRequiredService<OutreachTemplateService>();
            for (var i = 0; i < 25; i++)
                ids.Add(await svc.SaveAsync(new OutreachTemplateForm { Name = $"Picked {tag} {i:00}", Description = "For the vendor " + i, Subject = "Are we affected?", Body = "Body of " + i }));
        }

        // A link to an entry opens the page of the list it's on, with it selected and shown in full.
        await using var r = await RenderAsync<OutreachTemplatesPage>(app, "/library/templates?row=" + ids[24]);
        r.Component.Selected!.Template.Id.Should().Be(ids[24]);
        r.Component.OnPage.Should().Contain(x => x.Template.Id == ids[24]);
        await Do(r, c =>
        {
            typeof(OutreachTemplatesPage).GetField("_words", Private)!.SetValue(c, tag);
            c.Refilter();
            return Task.CompletedTask;
        });
        r.Component.Page.Should().Be(1, "a filter starts from the first page");
        r.Component.Selected!.Template.Id.Should().Be(ids[0], "the entry isn't on this page, so the first is selected");

        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("<ul class=\"tide-library-list\" aria-label=\"Outreach templates\">");
        Regex.Matches(html, "<li class=\"tide-library-row").Should().HaveCount(20, "the list is the page's entries");
        Regex.Matches(html, "aria-current=\"true\"").Should().HaveCount(1);
        html.Should().MatchRegex($"<li class=\"tide-library-row selected\" aria-current=\"true\" tabindex=\"0\">\\s*<span class=\"tide-library-row-name\">Picked {tag} 00</span>")
            .And.MatchRegex($"<li class=\"tide-library-row\" tabindex=\"-1\">\\s*<span class=\"tide-library-row-name\">Picked {tag} 01</span>", "the selected row is the one tab stop")
            .And.Contain("<span class=\"tide-library-row-about\">For the vendor 1</span>").And.Contain("TLP:CLEAR · Not used yet");
        html.Should().Contain("Body of 0</pre>").And.NotContain("Body of 1</pre>", "only the selected entry is shown in full");
        html.Should().Contain("href=\"http://localhost/library/templates?page=2\" aria-label=\"Page 2\"", "a page's link is the tab's own, without the row");
        r.Component.AddressShown.Should().Be($"http://localhost/library/templates?row={ids[0]}", "the address names the entry shown, not the one the link named");

        // Choosing another, as a click or the keys do.
        await r.Click(c => { c.Pick(ids[3]); return Task.CompletedTask; });
        r.Component.Selected!.Template.Id.Should().Be(ids[3]);
        WebUtility.HtmlDecode(await r.Html()).Should().Contain("Body of 3</pre>").And.NotContain("Body of 0</pre>");

        // Another page: its first entry, unless the address names one.
        await Do(r, c =>
        {
            c.PageParam = "2";
            c.RowParam = null;
            typeof(OutreachTemplatesPage).GetMethod("OnParametersSet", Private)!.Invoke(c, null);
            return Task.CompletedTask;
        });
        r.Component.Selected!.Template.Id.Should().Be(ids[20]);
        r.Component.AddressShown.Should().Be($"http://localhost/library/templates?row={ids[20]}&page=2", "an entry's address is on its page");

        // A filter that matches nothing: nothing is selected, the address names no entry, and the empty state can take focus.
        await Do(r, c =>
        {
            typeof(OutreachTemplatesPage).GetField("_words", Private)!.SetValue(c, "zzqx-" + tag);
            c.Refilter();
            return Task.CompletedTask;
        });
        r.Component.Selected.Should().BeNull();
        r.Component.AddressShown.Should().Be("http://localhost/library/templates");
        (await r.Html()).Should().Contain("<div class=\"tide-card\" tabindex=\"-1\">").And.Contain("No template matches these filters.");
    }

    [Fact]
    public async Task ANewEntry_IsSelectedOnceSaved_AndRetiringTheSelectedOneSelectsAnother()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        await using var r = await RenderAsync<SavedQueriesPage>(app, "/library/queries");
        await r.Click(c => { typeof(SavedQueriesPage).GetMethod("New", Private)!.Invoke(c, null); return Task.CompletedTask; });
        (r.Component.Form!.Name, r.Component.Form.Dialect, r.Component.Form.Text) = ("Just saved " + tag, "xql", "dataset = xdr_data | limit 1");
        await Do(r, c => c.Save());
        var saved = r.Component.Selected!.Query;
        saved.Name.Should().Be("Just saved " + tag, "what was just saved is shown");
        (await r.Html()).Should().Contain("dataset = xdr_data | limit 1</pre>");

        await r.Click(c => c.Retire(saved, true));
        r.Component.Selected?.Query.Id.Should().NotBe(saved.Id, "it left the list");
        r.Component.AddressShown.Should().NotContain(saved.Id.ToString(), "the address names the entry shown now")
            .And.Contain("row=" + r.Component.Selected!.Query.Id);
    }

    [Fact]
    public async Task SomeoneWhoMayNotReadActions_GetsNoTab_AndNothingIsRead()
    {
        // Rendered past the page's own policy, as if it weren't there: each tab still asks for what its page asked.
        await using var r = await RenderAsync<Library>(app, "/library/playbooks", new() { ["Tab"] = "playbooks" }, @as: new SignIn("dev:reader-rae", "Rae Reader", "Reader"));
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().NotContain("tide-tabbar").And.NotContain("library/queries").And.NotContain("id=\"pb-find\"").And.NotContain("tide-library-list");
        r.Scope.ServiceProvider.GetRequiredService<Tide.Web.Services.ToastService>().Toasts.Should().BeEmpty("nothing was asked for, so nothing was refused");

        LibraryTabs.All.Should().OnlyContain(t => t.Requires == Permission.ReadActions, "each page asked for ReadActions");
        typeof(Library).GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()!.Policy.Should().Be(Tide.Application.Security.Policies.ReadActions);
        typeof(LibraryMoved).GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()!.Policy.Should().Be(Tide.Application.Security.Policies.ReadActions);
    }

    [Fact]
    public async Task AViewer_ReadsEveryTab_WithNothingToChange()
    {
        foreach (var tab in LibraryTabs.All)
        {
            await using var r = await RenderAsync<Library>(app, "/" + tab.Href, new() { ["Tab"] = tab.Key }, @as: new SignIn("dev:viewer-val", "Val Viewer", "Viewer"));
            var html = WebUtility.HtmlDecode(await r.Html());
            Tabs(html).Should().HaveCount(4, tab.Key);
            html.Should().NotContain(">New ").And.NotContain("aria-label=\"Edit ").And.NotContain("aria-label=\"Retire ").And.NotContain("aria-label=\"Delete ").And.NotContain("Edit card rules");
        }
    }
}

/// <summary>A Visitor (no ReadActions) over HTTP: the Library and each address that led to one of its pages are refused, and the sidebar has no link.</summary>
public class LibraryVisitorTests(VisitorApp app) : IClassFixture<VisitorApp>
{
    [Theory]
    [InlineData("/library")]
    [InlineData("/library/queries")]
    [InlineData("/library/templates")]
    [InlineData("/library/playbooks")]
    [InlineData("/library/board-rules")]
    [InlineData("/hunts/saved")]
    [InlineData("/outreach/templates")]
    [InlineData("/playbooks")]
    [InlineData("/board-rules")]
    public async Task AVisitor_IsRefusedTheLibrary_AndItsOldAddresses(string path)
    {
        var page = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(path);
        page.StatusCode.Should().Be(HttpStatusCode.Redirect, path);
        page.Headers.Location!.OriginalString.Should().MatchRegex("/(access-denied|published)", path);
    }

    [Fact]
    public async Task AVisitorsSidebar_HasNoLibrary()
    {
        (await app.CreateClient().GetStringAsync("/published")).Should().NotContain("href=\"library");
    }
}
