using System.Net;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Our stack has a selected row, as the Actions list has (named in the address, moved by J and K, Enter opening the
/// product), and the selected product's context in the rail: its facts and contact, Edit and Start an outreach, what's in
/// triage now, its articles, its open actions and its notes, and Mark checked. Triage only for those who triage, articles
/// held to the team edition's ceiling, and the changes only for those who keep the list, through the checked, audited path.
/// </summary>
public class StackRailTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:sr-owner", "Rae Owner", "ActionOwner");
    private static readonly SignIn Viewer = new("dev:sr-viewer", "Val Viewer", "Viewer");
    private const string RailTag = "<aside id=\"tide-rail\"";

    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    /// <summary>
    /// A product named <paramref name="name"/> with a contact and notes; an article naming it (TLP:CLEAR) and one above the
    /// team edition's ceiling (TLP:RED); an open and a closed action from the first; and a new triage item naming it. Its id.
    /// </summary>
    private Task<Guid> Product(string name, DateOnly? checkedOn = null) => As(async sp =>
    {
        var stack = sp.GetRequiredService<StackService>();
        var id = await stack.SaveAsync(new StackForm
        {
            Name = name, Versions = "9.4-rail", ContactName = "Rail PSIRT", ContactEmail = "psirt@rail.example", Notes = $"Runs in the **{name}** DMZ.", CheckedOn = checkedOn,
        });
        var articles = sp.GetRequiredService<ArticleService>();
        var open = await articles.CreateAsync($"{name} zero-day exploited", "Vendor", null, TlpLevel.Clear);
        await articles.CreateAsync($"{name} breach at a partner", "Vendor", null, TlpLevel.Red);
        var team = await sp.GetRequiredService<TeamService>().AddAsync($"{name} team", null, null);
        var actions = sp.GetRequiredService<ActionService>();
        await actions.CreateAsync(new ActionForm { Title = $"Sweep for {name} rail-open", TeamId = team, ArticleId = open, Kind = ActionKind.Block });
        var closed = await actions.CreateAsync(new ActionForm { Title = $"Patch {name} rail-closed", TeamId = team, ArticleId = open, Kind = ActionKind.Block });
        await actions.SetStatusAsync((await actions.GetAsync(closed))!.Action.Id, ActionStatus.Done, ActionOutcome.Blocked, null);
        await using var db = await sp.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        var source = new Source { Name = $"{name} news", Url = $"https://{Guid.NewGuid():N}.example.com/feed.xml" };
        db.Sources.Add(source);
        var now = DateTimeOffset.UtcNow;
        db.FeedItems.Add(new FeedItem
        {
            SourceId = source.Id, Title = $"Gang exploits {name} in the wild", Url = $"https://{source.Id:N}.example.com/1", UrlHash = $"sr-{Guid.NewGuid():N}",
            PublishedAtUtc = now, FetchedAtUtc = now, Cves = "CVE-2026-7171",
        });
        if (!await db.CveRecords.AnyAsync(r => r.CveId == "CVE-2026-7171")) db.CveRecords.Add(new CveRecord { CveId = "CVE-2026-7171", IsKev = true });
        await db.SaveChangesAsync();
        return id;
    });

    private static string Name(string what) => $"Rail {what} {Guid.NewGuid():N}"[..24];

    /// <summary>Our stack, with the row of <paramref name="id"/> selected, as a click on it would.</summary>
    private async Task<Rendered<StackPage>> Open(Guid id, SignIn? who = null)
    {
        var r = await RenderAsync<StackPage>(app, "/stack", @as: who);
        await r.Click(p =>
        {
            var at = p.Shown.ToList().FindIndex(x => x.Product.Id == id);
            at.Should().BeGreaterThanOrEqualTo(0, "the product is on the list");
            p.SelectAt(at);
            Redraw(p);
            return Task.CompletedTask;
        });
        return r;
    }

    private static void Redraw(ComponentBase c) =>
        typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(c, null);

    /// <summary>The rail's markup once it shows <paramref name="text"/> (what it reads after the selection rests).</summary>
    private static async Task<string> RailOnce(Rendered<StackPage> r, string text)
    {
        var html = WebUtility.HtmlDecode(await r.HtmlOnce(text));
        var at = html.IndexOf(RailTag, StringComparison.Ordinal);
        at.Should().BePositive("Our stack has a context rail");
        return html[at..];
    }

    private static string RowOf(string html, string name)
    {
        html = WebUtility.HtmlDecode(html);
        var cell = html.IndexOf($">{name}</a>", StringComparison.Ordinal);
        cell.Should().BePositive();
        var row = html[html.LastIndexOf("<tr", cell, StringComparison.Ordinal)..];
        return row[..row.IndexOf('>')];
    }

    [Fact]
    public async Task AnAnalyst_SeesTheSelectedProduct_InTheRail_WithEveryPart_AndTheButtonsToChangeIt()
    {
        var name = Name("analyst");
        var id = await Product(name);
        await using var r = await Open(id);
        var rail = await RailOnce(r, "rail-triage-h");

        rail.Should().Contain($"aria-label=\"About {name}\"", "the rail is named for the selected product");
        rail.Should().Contain("aria-label=\"Facts\"").And.Contain("<dt>Versions</dt>").And.Contain("9.4-rail").And.Contain("<dt>Team</dt>").And.Contain("<dt>Checked</dt>")
            .And.Contain("<dt>Contact</dt>").And.Contain("Rail PSIRT, psirt@rail.example");
        rail.Should().Contain($"href=\"stack/{id}\"").And.Contain(">Edit</a>")
            .And.Contain($"href=\"actions/new?product={id}\"").And.Contain("Start an outreach").And.Contain(">Mark checked</button>");
        rail.Should().Contain("id=\"rail-triage-h\"").And.Contain("In triage now").And.Contain($"Gang exploits {name} in the wild").And.Contain("href=\"triage?at=")
            .And.Contain("KEV");
        rail.Should().Contain("id=\"rail-articles-h\"").And.Contain($"{name} zero-day exploited").And.Contain("1 article above TLP:AMBER+STRICT")
            .And.NotContain($"{name} breach at a partner", "an article above the team edition's ceiling is counted, not named");
        rail.Should().Contain("id=\"rail-open-actions-h\"").And.Contain($"Sweep for {name} rail-open")
            .And.NotContain($"Patch {name} rail-closed", "a closed action isn't open");
        rail.Should().Contain("id=\"rail-notes-h\"").And.Contain("<strong>" + name + "</strong>");
    }

    [Theory]
    [InlineData("ActionOwner")]
    [InlineData("Viewer")]
    public async Task SomeoneWhoDoesNotTriageOrKeepTheList_SeesTheContext_ButNotTriage_NorTheButtons(string role)
    {
        var who = role == "Viewer" ? Viewer : Owner;
        var name = Name(role);
        var id = await Product(name);
        await using var r = await Open(id, who);
        var rail = await RailOnce(r, "rail-open-actions-h");

        rail.Should().Contain($"aria-label=\"About {name}\"").And.Contain("Rail PSIRT, psirt@rail.example")
            .And.Contain($"{name} zero-day exploited").And.Contain($"Sweep for {name} rail-open").And.Contain("id=\"rail-notes-h\"");
        rail.Should().NotContain("rail-triage-h", "triage is the analysts'").And.NotContain("Gang exploits").And.NotContain("href=\"triage");
        rail.Should().NotContain(">Edit</a>").And.NotContain("Start an outreach").And.NotContain("Mark checked");
        rail.Should().NotContain($"{name} breach at a partner");
    }

    [Fact]
    public async Task TheList_HasASelectedRow_NamedInTheAddress_ThatTheRailFollows_AndEnterOpens()
    {
        var first = Name("first");
        var second = Name("second");
        await Product(first);
        await Product(second);
        await using var r = await RenderAsync<StackPage>(app, "/stack?row=1");
        var shown = r.Component.Shown;
        r.Component.Selected.Should().Be(1, "?row= names the selected row");
        var html = await r.Html();
        var selected = shown[1].Product.Name;
        RowOf(html, selected).Should().Contain("tide-stack-item").And.Contain("selected").And.Contain("aria-current=\"true\"").And.Contain("tabindex=\"0\"");
        RowOf(html, shown[0].Product.Name).Should().Contain("tabindex=\"-1\"").And.NotContain("aria-current");
        (await RailOnce(r, $"About {selected}")).Should().Contain($"aria-label=\"About {selected}\"");

        await r.Click(p => { p.SelectAt(0); Redraw(p); return Task.CompletedTask; });
        (await RailOnce(r, $"About {shown[0].Product.Name}")).Should().Contain($"aria-label=\"About {shown[0].Product.Name}\"");

        var opened = await FluentActions.Invoking(() => r.Click(p => { p.OpenRow(1); return Task.CompletedTask; })).Should().ThrowAsync<NavigationException>();
        opened.Which.Location.Should().EndWith($"stack/{shown[1].Product.Id}", "Enter opens the selected product");
    }

    [Fact]
    public async Task Filtering_SelectsTheFirstRowLeft()
    {
        var name = Name("filter");
        await Product(name);
        await using var r = await RenderAsync<StackPage>(app, "/stack?row=2");
        await r.Click(p => { p.SetWords(name); Redraw(p); return Task.CompletedTask; });
        r.Component.Selected.Should().Be(0);
        r.Component.SelectedRow!.Product.Name.Should().Be(name);
    }

    [Fact]
    public async Task MarkChecked_FromTheRail_SetsTodayThroughTheAuditedPath()
    {
        var name = Name("checked");
        var id = await Product(name);
        await using var r = await Open(id);
        await RailOnce(r, "rail-triage-h");
        await r.Click(p => p.Rail!.MarkChecked());

        var today = await As(sp => Task.FromResult(sp.GetRequiredService<OrgClock>().Today));
        var product = await As(async sp => (await sp.GetRequiredService<StackService>().GetAsync(id))!.Product);
        product.CheckedOn.Should().Be(today);
        var audit = await As(async sp =>
        {
            await using var db = await sp.GetRequiredService<IAppDbContextFactory>().CreateAsync();
            return await db.AuditLog.Where(e => e.EntityType == nameof(StackProduct) && e.EntityKey.Contains(id.ToString()) && e.Action == "Modified").ToListAsync();
        });
        audit.Should().ContainSingle("Mark checked writes one audit entry").Which.ChangesJson.Should().Contain("CheckedOn");
        (await r.Until(p => p.SelectedRow?.Product is { CheckedOn: { } on } checkedOne && checkedOne.Id == id && on == today))
            .Should().BeTrue("the list reads the product again, and it stays selected wherever it now sits");
        (await RailOnce(r, "tide-stack-rail-today")).Should().NotContain(">Mark checked</button>", "it's checked today");
    }

    [Fact]
    public async Task MarkChecked_OverAColleaguesSave_IsRefused_AndChangesNothing()
    {
        var name = Name("stale");
        var id = await Product(name);
        await using var r = await Open(id);
        await RailOnce(r, "rail-triage-h");
        await As(async sp =>
        {
            var stack = sp.GetRequiredService<StackService>();
            var form = StackForm.From((await stack.GetAsync(id))!.Product);
            form.Notes = "A colleague's note.";
            return await stack.SaveAsync(form);
        }, new SignIn("dev:sr-colleague", "Sam Colleague", "Analyst"));

        await r.Click(p => p.Rail!.MarkChecked());
        var product = await As(async sp => (await sp.GetRequiredService<StackService>().GetAsync(id))!.Product);
        product.CheckedOn.Should().BeNull("a check over a colleague's save is refused");
        product.Notes.Should().Be("A colleague's note.");
    }

    [Fact]
    public async Task MarkChecked_IsRefused_ToSomeoneWhoDoesNotKeepTheList_AndAStaleVersion()
    {
        var id = await Product(Name("refused"));
        var version = await As(async sp => (await sp.GetRequiredService<StackService>().GetAsync(id))!.Product.EditVersion);
        await FluentActions.Invoking(() => As(async sp => { await sp.GetRequiredService<StackService>().MarkCheckedAsync(id, version); return true; }, Owner))
            .Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => As(async sp => { await sp.GetRequiredService<StackService>().MarkCheckedAsync(id, Guid.NewGuid()); return true; }))
            .Should().ThrowAsync<StaleEditException>();
        (await As(async sp => (await sp.GetRequiredService<StackService>().GetAsync(id))!.Product.CheckedOn)).Should().BeNull();
    }

    [Fact]
    public async Task MarkChecked_IsRefused_OnARetiredProduct()
    {
        var id = await Product(Name("retired"));
        await As(async sp => { await sp.GetRequiredService<StackService>().SetRetiredAsync(id, true); return true; });
        var version = await As(async sp => (await sp.GetRequiredService<StackService>().GetAsync(id))!.Product.EditVersion);
        await FluentActions.Invoking(() => As(async sp => { await sp.GetRequiredService<StackService>().MarkCheckedAsync(id, version); return true; }))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*retired*");
        (await As(async sp => (await sp.GetRequiredService<StackService>().GetAsync(id))!.Product.CheckedOn)).Should().BeNull();
    }

    /// <summary>
    /// The row's Open actions and the rail's count the same actions, by the product page's rule: those raised from its
    /// articles and those whose title names it, an action with no article included. One raised from an article above the
    /// team edition's ceiling is named like any other, as the Actions list and the action page name it; that article
    /// itself stays counted, not named.
    /// </summary>
    [Fact]
    public async Task TheRowsOpenActions_CountWhatTheRailNames_AnActionWithNoArticleAndOneAboveTheCeilingIncluded()
    {
        var name = Name("count");
        var id = await Product(name);
        await As(async sp =>
        {
            var team = await sp.GetRequiredService<TeamService>().AddAsync($"{name} other", null, null);
            var actions = sp.GetRequiredService<ActionService>();
            await actions.CreateAsync(new ActionForm { Title = $"Block {name} at the proxy", TeamId = team, Kind = ActionKind.Block });
            var secret = await sp.GetRequiredService<ArticleService>().CreateAsync($"{name} hit at a supplier", "Vendor", null, TlpLevel.Red);
            return await actions.CreateAsync(new ActionForm { Title = "Contain the supplier's foothold", TeamId = team, ArticleId = secret, Kind = ActionKind.Block });
        });

        await using var r = await Open(id);
        r.Component.SelectedRow!.OpenActions.Should().Be(3, "the action raised from its article, the one naming it with no article, and the one above the ceiling");
        var rail = await RailOnce(r, "rail-open-actions-h");
        rail.Should().Contain("Open actions <span class=\"tide-count\">(3)</span>").And.Contain($"Block {name} at the proxy").And.Contain($"Sweep for {name} rail-open")
            .And.Contain("Contain the supplier's foothold", "an action is named wherever actions are shown")
            .And.NotContain("action from articles above").And.NotContain("actions from articles above");
        rail.Should().Contain("2 articles above TLP:AMBER+STRICT", "the articles above the ceiling stay counted")
            .And.NotContain($"{name} hit at a supplier", "and not named");

        await using var page = await RenderAsync<StackPage>(app, $"/stack/{id}", new() { ["Id"] = (Guid?)id });
        var html = WebUtility.HtmlDecode(await page.Html());
        html.Should().Contain("Contain the supplier's foothold", "the product's page names it too").And.NotContain("from articles above")
            .And.Contain("2 articles above TLP:AMBER+STRICT").And.NotContain($"{name} hit at a supplier");
    }

    [Fact]
    public async Task StartAnOutreach_OpensTheNewActionForm_AsAnOutreachAboutTheProduct()
    {
        var name = Name("outreach");
        var id = await Product(name);
        await using var r = await RenderAsync<ActionPage>(app, $"/actions/new?product={id}");
        r.Component.NewForm.Kind.Should().Be(ActionKind.Outreach);
        r.Component.NewForm.Title.Should().Be($"Ask Rail PSIRT about {name}", "the title names the product, so the Outreach panel offers its contact");
        r.Component.NewForm.TeamId.Should().Be(Guid.Empty, "the team is the person's to choose");
        WebUtility.HtmlDecode(await r.Html()).Should().Contain($"An outreach about <a href=\"stack/{id}\">{name}</a> on Our stack");
    }

    [Fact]
    public async Task InTriageNow_IsOnlyForThoseWhoTriage()
    {
        var name = Name("triage");
        var id = await Product(name);
        var items = await As(sp => sp.GetRequiredService<StackService>().InTriageAsync(id));
        items.Should().ContainSingle().Which.Should().Match<StackTriageItem>(i => i.Title == $"Gang exploits {name} in the wild" && i.Kev);
        await FluentActions.Invoking(() => As(sp => sp.GetRequiredService<StackService>().InTriageAsync(id), Viewer)).Should().ThrowAsync<ForbiddenException>();
    }
}
