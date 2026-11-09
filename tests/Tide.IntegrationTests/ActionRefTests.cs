using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// An action in a list or on a card leads with its title (the link, or the button that opens it over a slide) and ends with
/// its number, small and muted, to quote; and an article's Actions tab says how many recommended lines aren't raised yet.
/// </summary>
public class ActionRefTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TheTitleLeads_AsALinkAButtonOrText_AndTheNumberFollows()
    {
        await using (var link = await RenderAsync<ActionRef>(app, "/", new() { ["Number"] = 1234, ["Title"] = "Patch the gateways" }))
        {
            var html = await link.Html();
            html.Should().Contain("<a class=\"tide-action-title \" href=\"actions/1234\">Patch the gateways</a>")
                .And.Contain("<span class=\"tide-action-num tide-action-num-after\">A-1234</span>");
            html.IndexOf("Patch the gateways", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("A-1234", StringComparison.Ordinal), "the title first");
        }

        var opened = 0;
        await using (var button = await RenderAsync<ActionRef>(app, "/", new()
        {
            ["Number"] = 7, ["Title"] = "Sweep the logs", ["Link"] = false,
            ["OnOpen"] = EventCallback.Factory.Create(this, () => opened++),
        }))
        {
            (await button.Html()).Should().Contain("<button type=\"button\" class=\"tide-action-title-btn \" aria-label=\"Open A-7, Sweep the logs\">Sweep the logs</button>",
                "on a slide the title opens the action, and says which");
        }

        await using (var text = await RenderAsync<ActionRef>(app, "/", new() { ["Number"] = 9, ["Title"] = "Gone", ["Link"] = false }))
            (await text.Html()).Should().Contain("<span>Gone</span>").And.NotContain("href").And.Contain(">A-9</span>");
    }

    [Fact]
    public async Task TheArticlesActionsTab_ListsThemTitleFirst_AndCountsTheLinesNotRaised()
    {
        Guid article, team;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var t = db.Teams.First();
            team = t.Id;
            var a = new Article
            {
                Title = "Actions tab check", AuthorId = "x", AuthorName = "X", Tlp = TlpLevel.Clear, Bluf = "Exploited.", SourceName = "Vendor",
                RecommendedActions = "- Patch the gateways\n- Hunt for the indicators\n- Reset the service account",
            };
            db.Articles.Add(a);
            await db.SaveChangesAsync();
            article = a.Id;
        }
        int number;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            number = await scope.ServiceProvider.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = "Patch the gateways", TeamId = team, ArticleId = article });

        var html = System.Net.WebUtility.HtmlDecode(await app.CreateClient().GetStringAsync($"/articles/{article}?side=actions"));
        html.Should().Contain($"href=\"actions/{number}\">Patch the gateways</a>").And.Contain($"A-{number}</span>")
            .And.Contain("2 recommended lines not raised yet.", "one of the three became an action");
        html.Should().Contain("<span>Actions</span>").And.Contain("tide-tabbar-count\">1</span>", "the tab carries the count");
    }
}
