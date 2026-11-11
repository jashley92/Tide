using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The action page's context rail: the facts, the article it was raised from (its summary, badges and CVEs), the other
/// actions raised from that article and the Files are drawn in the rail, not in the page, for every kind of action; the
/// page keeps the header, the tools and what's happened.
/// </summary>
public class ActionRailTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Viewer = new("dev:rail-viewer", "Rail Viewer", "Viewer");
    private static readonly SignIn Owner = new("dev:rail-owner", "Rail Owner", "ActionOwner");
    private const string Rail = "<aside id=\"tide-rail\"";

    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    /// <summary>A threat article (P1, TLP:AMBER, a bottom line and a CVE) and a team; their ids.</summary>
    private Task<(Guid Article, Guid Team)> Article(string title) => As(async sp =>
    {
        var articles = sp.GetRequiredService<ArticleService>();
        var article = await articles.CreateAsync(title, "Vendor", null, TlpLevel.Amber);
        var form = ArticleForm.From((await articles.GetAsync(article))!);
        form.Bluf = title + ": the bottom line.";
        form.Classification = Classification.Threat;
        form.Priority = Priority.P1;
        form.Cves = ["CVE-2026-0001"];
        form.Indicators = [new() { Type = IndicatorType.IpAddress, Value = "203.0.113.45" }];
        await articles.SaveAsync(form);
        return (article, await sp.GetRequiredService<TeamService>().AddAsync(title + " team", null, null));
    });

    private Task<int> Raise(Guid team, string title, Guid? article, ActionKind kind = ActionKind.Block, Guid? parent = null) =>
        As(sp => sp.GetRequiredService<ActionService>().CreateAsync(new ActionForm
        {
            Title = title, TeamId = team, Kind = kind, ArticleId = article, ParentId = parent, Description = title + " details",
        }));

    private Task<ActionDetail> Detail(int number, SignIn? who = null) =>
        As(async sp => (await sp.GetRequiredService<ActionService>().GetAsync(number))!, who);

    private Task<Rendered<ActionPage>> Open(int number, SignIn? who = null) =>
        RenderAsync<ActionPage>(app, $"/actions/{number}", new() { ["Number"] = (int?)number }, who);

    /// <summary>The page's markup and its rail's, split where MainLayout draws the rail.</summary>
    private static (string Page, string Rail) Split(string html)
    {
        html = WebUtility.HtmlDecode(html);
        var at = html.IndexOf(Rail, StringComparison.Ordinal);
        at.Should().BePositive("the action page has a context rail");
        return (html[..at], html[at..]);
    }

    [Theory]
    [InlineData(ActionKind.IocSweep)]
    [InlineData(ActionKind.Block)]
    public async Task TheFactsTheArticleWhatElseWasRaisedAndTheFiles_AreInTheRail_AndNotInThePage(ActionKind kind)
    {
        var (article, team) = await Article($"Rail article {kind}");
        var number = await Raise(team, $"Rail action {kind}", article, kind);
        await Raise(team, $"Rail sibling {kind}", article, ActionKind.PatchRemediate);
        var id = (await Detail(number)).Action.Id;
        await As(sp => sp.GetRequiredService<ActionFileService>().AddAsync(id, [new ActionFileUpload("rail-proxy.log", Samples.Text("log"))]));

        await using var page = await Open(number);
        var (inPage, rail) = Split(await page.Html());

        rail.Should().StartWith(Rail + " class=\"tide-rail\" aria-label=\"About this action\"");
        rail.Should().Contain("aria-label=\"Facts\"").And.Contain("<dt>Team</dt>").And.Contain("<dt>Kind</dt>")
            .And.Contain("id=\"rail-article-h\"").And.Contain($">Rail article {kind}</a>").And.Contain($"Rail article {kind}: the bottom line.")
            .And.Contain("TLP:AMBER").And.Contain("Threat").And.Contain("title=\"Priority P1\"").And.Contain("CVE-2026-0001")
            .And.Contain("id=\"rail-raised-h\"").And.Contain($"Rail sibling {kind}")
            .And.Contain("id=\"files-h\"").And.Contain("rail-proxy.log");
        rail.Should().NotContain($">Rail action {kind}<", "the action isn't listed as raised beside itself");

        inPage.Should().NotContain("aria-label=\"Facts\"").And.NotContain("rail-article-h").And.NotContain("rail-raised-h")
            .And.NotContain("id=\"files-h\"").And.NotContain("tide-action-side", "the page has one column");
        inPage.Should().Contain("class=\"tide-card tide-drawer\"", "the tools stay in the page, for every kind")
            .And.Contain("id=\"feed-h\"").And.Contain("tide-action-head");
    }

    [Fact]
    public async Task AlsoRaisedFromIt_ListsTheArticlesOtherActions_ButNotItself_ItsLineage_OrADeletedOne()
    {
        var (article, team) = await Article("Rail siblings article");
        var number = await Raise(team, "Rail siblings the action", article);
        var open = await Raise(team, "Rail siblings an open one", article);
        var closed = await Raise(team, "Rail siblings a closed one", article);
        var gone = await Raise(team, "Rail siblings a deleted one", article);
        var elsewhere = await Raise(team, "Rail siblings another article's", null);
        await As(async sp =>
        {
            var actions = sp.GetRequiredService<ActionService>();
            await actions.SetStatusAsync((await actions.GetAsync(closed))!.Action.Id, ActionStatus.Done, ActionOutcome.Blocked, null);
            await actions.DeleteAsync((await actions.GetAsync(gone))!.Action.Id, "A duplicate.");
            return true;
        });
        var id = (await Detail(number)).Action.Id;
        var part = await Raise(team, "Rail siblings a part of it", article, parent: id);

        var detail = await Detail(number);
        detail.RaisedWith.Select(r => r.Number).Should().Equal([open, closed], "open first, then by number");
        (await Detail(part)).RaisedWith.Select(r => r.Number).Should().Equal([open, closed], "a part's parent is in its lineage, not here");
        (await Detail(elsewhere)).RaisedWith.Should().BeEmpty("raised from no article");

        await using var page = await Open(number);
        var (_, rail) = Split(await page.Html());
        rail.Should().Contain("Rail siblings an open one").And.Contain("Rail siblings a closed one")
            .And.NotContain("Rail siblings a deleted one").And.NotContain("Rail siblings a part of it").And.NotContain("another article's");

        await using var none = await Open(elsewhere);
        Split(await none.Html()).Rail.Should().NotContain("rail-article-h").And.NotContain("rail-raised-h").And.Contain("aria-label=\"Facts\"");
    }

    [Fact]
    public async Task AViewerAndAnActionOwner_SeeTheRail_AndOnlyWhoeverWorksItAddsFiles()
    {
        var (article, team) = await Article("Rail roles article");
        var number = await Raise(team, "Rail roles the action", article);
        await Raise(team, "Rail roles a sibling", article);

        foreach (var who in new[] { Viewer, Owner })
        {
            await using var page = await Open(number, who);
            var (inPage, rail) = Split(await page.Html());
            rail.Should().Contain("aria-label=\"Facts\"", who.Name).And.Contain("Rail roles article: the bottom line.").And.Contain("Rail roles a sibling")
                .And.Contain("id=\"files-h\"").And.NotContain("Add files to this action", "neither works this team's actions");
            inPage.Should().NotContain("id=\"files-h\"");
        }
    }
}
