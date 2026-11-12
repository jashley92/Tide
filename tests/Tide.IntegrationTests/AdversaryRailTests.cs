using System.Net;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Adversaries;
using Tide.Application.Articles;
using Tide.Application.Detections;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Adversaries has a selected row, as Our stack has (named in the address, moved by J and K, Enter opening the profile),
/// and the selected profile's context in the rail; a profile's page has its facts and what can be done to it in the rail.
/// Both rails read what the profile's page reads, to the same people, never show its notes (AMBER), and change it only
/// through the profile page's own, audited calls, for those who keep the profiles.
/// </summary>
public class AdversaryRailTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Viewer = new("dev:ar-viewer", "Val Viewer", "Viewer");
    private static readonly SignIn Reader = new("dev:ar-reader", "Rea Reader", "Reader");
    private const string RailTag = "<aside id=\"tide-rail\"";

    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    private static string Name(string what) => $"Rail {what} {Guid.NewGuid():N}"[..26];

    /// <summary>What a profile was made with: its id, its product on Our stack, and the titles the tests look for.</summary>
    private sealed record Made(Guid Id, Guid Product, string Notes, string Clear, string Red, string OpenAction, string RedAction, string Closed);

    /// <summary>
    /// A watched profile named <paramref name="name"/> with notes; a TLP:CLEAR article tagged with it, about a product on Our
    /// stack, with two techniques; a TLP:RED article naming it (not tagged); an open and a closed action from the first and
    /// an open one from the RED article.
    /// </summary>
    private Task<Made> Profile(string name) => As(async sp =>
    {
        var adversaries = sp.GetRequiredService<AdversaryService>();
        var notes = $"Our {name} notes: internal.";
        var id = await adversaries.SaveAsync(new AdversaryForm
        {
            Name = name, Kind = AdversaryKind.Actor, MitreId = null, Motivation = "Financial", Sectors = "Retail, Telecoms", Notes = notes, Watched = true,
        });
        var product = await sp.GetRequiredService<StackService>().SaveAsync(new StackForm { Name = $"Prod {name[^8..]}" });
        var articles = sp.GetRequiredService<ArticleService>();
        var (clear, red) = ($"{name} hits retailers", $"{name} partner breach");
        var clearId = await articles.CreateAsync(clear, "Vendor", null, TlpLevel.Clear);
        var redId = await articles.CreateAsync(red, "Vendor", null, TlpLevel.Red);
        await adversaries.TagArticleAsync(clearId, id);
        await sp.GetRequiredService<StackService>().TagArticleAsync(clearId, product);
        await using (var db = await sp.GetRequiredService<IAppDbContextFactory>().CreateAsync())
        {
            db.ArticleTechniques.AddRange(new ArticleTechnique { ArticleId = clearId, TechniqueId = "T1190" }, new ArticleTechnique { ArticleId = redId, TechniqueId = "T1190" },
                new ArticleTechnique { ArticleId = clearId, TechniqueId = "T1566" });
            await db.SaveChangesAsync();
        }
        var team = await sp.GetRequiredService<TeamService>().AddAsync($"{name} team", null, null);
        var actions = sp.GetRequiredService<ActionService>();
        var (openTitle, redTitle, closedTitle) = ($"Block {name} rail-open", $"Hunt {name} rail-red", $"Patch {name} rail-closed");
        await actions.CreateAsync(new ActionForm { Title = openTitle, TeamId = team, ArticleId = clearId, Kind = ActionKind.Block });
        await actions.CreateAsync(new ActionForm { Title = redTitle, TeamId = team, ArticleId = redId, Kind = ActionKind.Block });
        var closed = await actions.CreateAsync(new ActionForm { Title = closedTitle, TeamId = team, ArticleId = clearId, Kind = ActionKind.Block });
        await actions.SetStatusAsync((await actions.GetAsync(closed))!.Action.Id, ActionStatus.Done, ActionOutcome.Blocked, null);
        return new Made(id, product, notes, clear, red, openTitle, redTitle, closedTitle);
    });

    /// <summary>Adversaries, with the row of <paramref name="id"/> selected, as a click on it would.</summary>
    private async Task<Rendered<AdversariesPage>> Open(Guid id, SignIn? who = null)
    {
        var r = await RenderAsync<AdversariesPage>(app, "/adversaries", @as: who);
        await r.Click(p =>
        {
            var at = p.View.ToList().FindIndex(x => x.Adversary.Id == id);
            at.Should().BeGreaterThanOrEqualTo(0, "the profile is on the list's first page");
            p.SelectAt(at);
            Redraw(p);
            return Task.CompletedTask;
        });
        return r;
    }

    private Task<Rendered<AdversariesPage>> Page(Guid id, SignIn? who = null) =>
        RenderAsync<AdversariesPage>(app, $"/adversaries/{id}", new() { ["Id"] = id }, @as: who);

    private static void Redraw(ComponentBase c) =>
        typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(c, null);

    /// <summary>The rail's markup once it shows <paramref name="text"/>.</summary>
    private static async Task<string> RailOnce<T>(Rendered<T> r, string text) where T : IComponent
    {
        var html = WebUtility.HtmlDecode(await r.HtmlOnce(text));
        var at = html.IndexOf(RailTag, StringComparison.Ordinal);
        at.Should().BePositive("the page has a context rail");
        return html[at..];
    }

    private static string Content(string html) => html[..html.IndexOf(RailTag, StringComparison.Ordinal)];

    [Fact]
    public async Task AnAnalyst_SeesTheSelectedProfile_InTheRail_WithEveryPart_AndWatch()
    {
        var name = Name("analyst");
        var m = await Profile(name);
        await using var r = await Open(m.Id);
        var rail = await RailOnce(r, "rail-werun-h\">We run <span");

        rail.Should().Contain($"aria-label=\"About {name}\"", "the rail is named for the selected profile");
        rail.Should().Contain("Watched").And.Contain("<dt>Kind</dt>").And.Contain("Actor").And.Contain("Financial").And.Contain("Retail, Telecoms");
        rail.Should().Contain($"href=\"adversaries/{m.Id}\"").And.Contain(">Open profile</a>").And.Contain(">Stop watching</button>");
        rail.Should().Contain("id=\"rail-lately-h\"").And.Contain(m.Clear).And.Contain(m.Red, "the profile's page names every article it lists, at any marking")
            .And.Contain("1 named but not tagged");
        rail.Should().Contain("id=\"rail-open-actions-h\"").And.Contain(m.OpenAction).And.Contain(m.RedAction, "an action from an article above the ceiling is named")
            .And.NotContain(m.Closed, "a closed action isn't open");
        rail.Should().Contain("id=\"rail-techniques-h\"").And.Contain("T1190").And.Contain("×2").And.Contain("T1566").And.Contain("×1");
        rail.Should().Contain("id=\"rail-werun-h\"").And.Contain($"href=\"stack/{m.Product}\"").And.Contain("1 article");
        rail.Should().NotContain(m.Notes, "the notes are AMBER: they stay on the profile's page").And.NotContain("TLP:AMBER");
    }

    [Fact]
    public async Task TheRail_ListsWhatTheProfilesPageLists_ToAViewer_WithoutTheButtonsToChangeIt()
    {
        var name = Name("viewer");
        var m = await Profile(name);
        var page = await As(async sp => (await sp.GetRequiredService<AdversaryService>().GetAsync(m.Id))!, Viewer);
        await using var r = await Open(m.Id, Viewer);
        var rail = await RailOnce(r, "rail-werun-h\">We run <span");

        rail.Should().Contain(">Open profile</a>").And.NotContain("Stop watching").And.NotContain("Watch in triage");
        foreach (var a in page.Articles.Take(3)) rail.Should().Contain(a.Title);
        foreach (var a in page.Actions.Where(a => !ActionItem.IsClosedStatus(a.Status)).Take(3)) rail.Should().Contain(a.Title);
        rail.Should().Contain(m.Red, "a Viewer reads the team edition at every marking, as the profile's page shows them").And.NotContain(m.Notes);
    }

    [Fact]
    public async Task SomeoneWhoDoesNotReadTheTeamEdition_GetsNothingOfTheRails()
    {
        var m = await Profile(Name("reader"));
        await FluentActions.Invoking(() => As(sp => sp.GetRequiredService<AdversaryService>().GetAsync(m.Id), Reader)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => As(sp => sp.GetRequiredService<StackService>().RunByArticlesAsync([Guid.NewGuid()]), Reader))
            .Should().ThrowAsync<ForbiddenException>();

        await using var r = await RenderAsync<AdversariesPage>(app, "/adversaries", @as: Reader);
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().NotContain(m.Clear).And.NotContain(m.OpenAction).And.NotContain("rail-lately-h");
        r.Component.SelectedRow.Should().BeNull();
    }

    [Fact]
    public async Task ARetiredProfile_OffersNoWatch_InTheListsRail()
    {
        var name = Name("retired");
        var m = await Profile(name);
        await As(async sp => { await sp.GetRequiredService<AdversaryService>().SetRetiredAsync(m.Id, true); return true; });
        await using var r = await RenderAsync<AdversariesPage>(app, "/adversaries");
        await r.Click(c => c.ToggleAll(new ChangeEventArgs { Value = true }));
        (await r.Until(p => p.View.Any(x => x.Adversary.Id == m.Id))).Should().BeTrue("retired profiles are on the list with Every profile ticked");
        await r.Click(p => { p.SelectAt(p.View.ToList().FindIndex(x => x.Adversary.Id == m.Id)); Redraw(p); return Task.CompletedTask; });
        var rail = await RailOnce(r, "rail-werun-h\">We run <span");
        rail.Should().Contain($"aria-label=\"About {name}\"").And.Contain("Retired").And.Contain(">Open profile</a>")
            .And.NotContain("Stop watching").And.NotContain("Watch in triage");
    }

    [Fact]
    public async Task Watch_FromTheListsRail_GoesThroughTheAuditedPath_AndTheProfileStaysSelected()
    {
        var name = Name("watch");
        var m = await Profile(name);
        await using var r = await Open(m.Id);
        await RailOnce(r, "rail-werun-h\">We run <span");
        await r.Click(p => p.Rail!.Watch(false));

        (await As(async sp => (await sp.GetRequiredService<AdversaryService>().GetAsync(m.Id))!.Adversary.Watched)).Should().BeFalse();
        var audit = await As(async sp =>
        {
            await using var db = await sp.GetRequiredService<IAppDbContextFactory>().CreateAsync();
            return await db.AuditLog.Where(e => e.EntityType == nameof(Adversary) && e.EntityKey.Contains(m.Id.ToString()) && e.Action == "Modified").ToListAsync();
        });
        audit.Should().ContainSingle().Which.ChangesJson.Should().Contain("Watched");
        (await r.Until(p => p.SelectedRow?.Adversary is { Watched: false } a && a.Id == m.Id)).Should().BeTrue("the list is read again, the same profile selected");
        (await RailOnce(r, ">Watch in triage</button>")).Should().Contain(">Watch in triage</button>");
    }

    [Fact]
    public async Task TheList_HasASelectedRow_NamedInTheAddress_ThatTheRailFollows_AndEnterOpens()
    {
        await Profile(Name("first"));
        await Profile(Name("second"));
        await using var r = await RenderAsync<AdversariesPage>(app, "/adversaries?row=1");
        r.Component.Selected.Should().Be(1, "?row= names the selected row");
        var selected = r.Component.View[1];
        var html = WebUtility.HtmlDecode(await r.Html());
        var cell = html.IndexOf($">{selected.Adversary.Name}</a>", StringComparison.Ordinal);
        var row = html[html.LastIndexOf("<tr", cell, StringComparison.Ordinal)..];
        row[..row.IndexOf('>')].Should().Contain("tide-adv-item").And.Contain("selected").And.Contain("aria-current=\"true\"").And.Contain("tabindex=\"0\"");
        (await RailOnce(r, $"About {selected.Adversary.Name}")).Should().Contain($"aria-label=\"About {selected.Adversary.Name}\"");

        var opened = await FluentActions.Invoking(() => r.Click(p => { p.OpenRow(1); return Task.CompletedTask; })).Should().ThrowAsync<NavigationException>();
        opened.Which.Location.Should().EndWith($"adversaries/{selected.Adversary.Id}", "Enter opens the selected profile");
    }

    [Fact]
    public async Task AProfile_HasItsFactsAndWhatCanBeDone_InTheRail_AndKeepsItsStory()
    {
        var name = Name("profile");
        var m = await Profile(name);
        await using var r = await Page(m.Id);
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("rail-werun-h"));
        var rail = await RailOnce(r, "rail-werun-h");
        var content = Content(html);

        rail.Should().Contain($"aria-label=\"About {name}\"").And.Contain("aria-label=\"Facts\"").And.Contain("<dt>Kind</dt>").And.Contain("<dt>MITRE id</dt>")
            .And.Contain("<dt>Motivation</dt>").And.Contain("Retail, Telecoms").And.Contain("this quarter · last reported");
        rail.Should().Contain("id=\"rail-do-h\"").And.Contain(">Stop watching</button>").And.Contain($"href=\"adversaries/{m.Id}/navigator.json\"").And.Contain(">Retire</button>");
        rail.Should().Contain("id=\"rail-link-h\"").And.Contain("1 article names it but isn't tagged").And.Contain($"adversaries/{m.Id}#adv-timeline-h");
        rail.Should().Contain("id=\"rail-werun-h\"").And.Contain($"href=\"stack/{m.Product}\"");
        rail.Should().NotContain(m.Notes, "the notes are AMBER: they stay in About it").And.NotContain("TLP:AMBER").And.NotContain("tide-tlp");

        content.Should().Contain("Timeline").And.Contain("Techniques seen in our articles").And.Contain("About it").And.Contain(m.Notes, "the page keeps the notes")
            .And.NotContain("Stop watching", "the controls moved to the rail").And.NotContain("navigator.json");

        var rules = await As(sp => sp.GetRequiredService<DetectionService>().ForTechniquesAsync(["T1190", "T1566"]));
        if (rules is null) rail.Should().NotContain("rail-hunting-h", "with no rules imported there's nothing to say");
        else
        {
            var covered = new[] { "T1190", "T1566" }.Count(t => rules.GetValueOrDefault(t) is { Enabled: > 0 });
            rail.Should().Contain("id=\"rail-hunting-h\"").And.Contain($"{covered} of 2 techniques").And.Contain("href=\"coverage\"");
        }
    }

    [Fact]
    public async Task AProfilesRail_ToAViewer_HasTheFactsAndTheLayer_ButNothingToChangeOrLink_NorTheRules()
    {
        var m = await Profile(Name("pviewer"));
        await using var r = await Page(m.Id, Viewer);
        var rail = await RailOnce(r, "rail-werun-h");
        rail.Should().Contain("aria-label=\"Facts\"").And.Contain($"href=\"adversaries/{m.Id}/navigator.json\"")
            .And.NotContain("Stop watching").And.NotContain(">Retire</button>").And.NotContain("rail-link-h").And.NotContain("rail-hunting-h").And.NotContain(m.Notes);
    }

    [Fact]
    public async Task RunByArticles_FindsTaggedAndNamedProducts_InUse_MostFirst()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (named, tagged, retired, articles) = await As(async sp =>
        {
            var stack = sp.GetRequiredService<StackService>();
            var named = await stack.SaveAsync(new StackForm { Name = $"Namedware {tag}", OtherNames = $"NW{tag}" });
            var tagged = await stack.SaveAsync(new StackForm { Name = $"Taggedware {tag}" });
            var retired = await stack.SaveAsync(new StackForm { Name = $"Oldware {tag}" });
            await stack.SetRetiredAsync(retired, true);
            var svc = sp.GetRequiredService<ArticleService>();
            var a1 = await svc.CreateAsync($"Flaw in NW{tag} and Oldware {tag}", "Vendor", null, TlpLevel.Clear);
            var a2 = await svc.CreateAsync($"Namedware {tag} again", "Vendor", null, TlpLevel.Red);
            var a3 = await svc.CreateAsync($"Something else {tag}", "Vendor", null, TlpLevel.Clear);
            await stack.TagArticleAsync(a3, tagged);
            return (named, tagged, retired, new List<Guid> { a1, a2, a3 });
        });

        var runs = await As(sp => sp.GetRequiredService<StackService>().RunByArticlesAsync(articles), Viewer);
        runs.Select(x => (x.Product.Id, x.Articles)).Should().Equal((named, 2), (tagged, 1));
        runs.Should().NotContain(x => x.Product.Id == retired, "a retired product isn't run any more");
        (await As(sp => sp.GetRequiredService<StackService>().RunByArticlesAsync([]))).Should().BeEmpty();
        (await As(sp => sp.GetRequiredService<StackService>().RunByArticlesAsync([articles[2], articles[2]]))).Should().ContainSingle()
            .Which.Product.Id.Should().Be(tagged);
    }
}
