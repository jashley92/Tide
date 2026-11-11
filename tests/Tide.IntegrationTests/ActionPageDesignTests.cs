using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The action page as designed: the header says the action's kind, and its due date with how it stands in one badge; the
/// hunt drawer puts Write one by hand, Add a saved query and Add indicators beside Copy, and "What did it find?" (with
/// Attach a screenshot) under them; the rail's "Also raised from it" rows lead with the number and end with the status;
/// and the Files heading holds Add files, with the whole page taking a drop, for whoever may add files and no one else.
/// </summary>
public class ActionPageDesignTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Viewer = new("dev:design-viewer", "Design Viewer", "Viewer");
    private static readonly SignIn Owner = new("dev:design-owner", "Design Owner", "ActionOwner");
    private const string Rail = "<aside id=\"tide-rail\"";

    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    /// <summary>An article with an indicator, and a team; their ids.</summary>
    private Task<(Guid Article, Guid Team)> Article(string title) => As(async sp =>
    {
        var articles = sp.GetRequiredService<ArticleService>();
        var article = await articles.CreateAsync(title, "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await articles.GetAsync(article))!);
        form.Indicators = [new() { Type = IndicatorType.IpAddress, Value = "198.51.100.7" }];
        await articles.SaveAsync(form);
        return (article, await sp.GetRequiredService<TeamService>().AddAsync(title + " team", null, null));
    });

    private Task<int> Raise(Guid team, string title, Guid? article, ActionKind kind = ActionKind.IocSweep, DateOnly? due = null) =>
        As(sp => sp.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = title, TeamId = team, Kind = kind, ArticleId = article, DueDate = due }));

    private Task<DateOnly> Today() => As(sp => Task.FromResult(sp.GetRequiredService<ActionService>().Today));

    private Task<Rendered<ActionPage>> Open(int number, SignIn? who = null) =>
        RenderAsync<ActionPage>(app, $"/actions/{number}", new() { ["Number"] = (int?)number }, who);

    private static (string Page, string Rail) Split(string html)
    {
        html = WebUtility.HtmlDecode(html);
        var at = html.IndexOf(Rail, StringComparison.Ordinal);
        at.Should().BePositive("the action page has a context rail");
        return (html[..at], html[at..]);
    }

    private static int At(string html, string text)
    {
        var at = html.IndexOf(text, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"the page has \"{text}\"");
        return at;
    }

    [Fact]
    public async Task TheHeader_SaysItsKind_AndItsDueDateWithHowItStands_InOneBadge()
    {
        var (article, team) = await Article("Design header article");
        var today = await Today();
        var number = await Raise(team, "Design header sweep", article, due: today);

        await using var page = await Open(number);
        var (inPage, _) = Split(await page.Html());
        var head = inPage[At(inPage, "class=\"tide-action-badges\"")..At(inPage, "tide-action-who")];
        head.Should().Contain($"tide-action-head-kind\">{ActionService.KindName(ActionKind.IocSweep)}</span>");
        head.Should().Contain($"tide-action-due\" title=\"Due {Ui.Day(today)}\"><span class=\"bi bi-clock-history\" aria-hidden=\"true\"></span>Due {Ui.ShortDay(today)} · due soon</span>");
        head.Should().NotContain(">Due soon<", "the date and how it stands are one badge");
    }

    [Fact]
    public async Task AnActionWithNoDueDate_SaysSoOnceInTheRail()
    {
        var (article, team) = await Article("Design no due article");
        var number = await Raise(team, "Design no due sweep", article);
        await As(async sp =>
        {
            var actions = sp.GetRequiredService<ActionService>();
            var form = ActionForm.From((await actions.GetAsync(number))!.Action);
            form.DueDate = null;
            await actions.SaveAsync(form);
            return true;
        });

        await using var page = await Open(number);
        var (_, rail) = Split(await page.Html());
        System.Text.RegularExpressions.Regex.Matches(rail, "No due date").Should().HaveCount(1, "the value says it; no badge says it again");
    }

    [Fact]
    public async Task AlsoRaisedFromIt_LeadsWithTheNumber_AndEndsWithTheStatus()
    {
        var (article, team) = await Article("Design raised article");
        var number = await Raise(team, "Design raised the action", article);
        var sibling = await Raise(team, "Design raised a sibling", article, ActionKind.PatchRemediate);

        await using var page = await Open(number);
        var (_, rail) = Split(await page.Html());
        var row = rail[At(rail, "id=\"rail-raised-h\"")..];
        row = row[At(row, "<li")..At(row, "</li>")];
        At(row, $">{Ui.ActionLabel(sibling)}</span>").Should().BeLessThan(At(row, ">Design raised a sibling</a>"), "the number leads");
        row.TrimEnd().Should().EndWith($">{ActionService.StatusName(ActionStatus.Open)}</span></span>", "the status ends the row");
        At(row, "tide-rail-raised-status").Should().BeGreaterThan(At(row, "Design raised article team"), "the status is after the team");
    }

    [Fact]
    public async Task AddFiles_IsInTheFilesHeading_AndThePageTakesADrop_OnlyForWhoeverMayAddFiles()
    {
        var (article, team) = await Article("Design files article");
        var number = await Raise(team, "Design files sweep", article);

        await using (var page = await Open(number))
        {
            var (inPage, rail) = Split(await page.Html());
            var head = rail[At(rail, "class=\"tide-files-head\"")..];
            head = head[..At(head, "</div>")];
            head.Should().Contain("id=\"files-h\"").And.Contain("data-pick=\"files-input\"").And.Contain("Add files</button>");
            rail.Should().Contain("Drop them anywhere on the page, or paste.");
            inPage.Should().Contain("class=\"tide-page-drop\"", "a hint to show while files are dragged over the page");
        }

        foreach (var who in new[] { Viewer, Owner })
        {
            await using var page = await Open(number, who);
            var html = WebUtility.HtmlDecode(await page.Html());
            html.Should().Contain("id=\"files-h\"", who.Name).And.NotContain("data-pick=", "neither may add files to this team's action")
                .And.NotContain("tide-page-drop").And.NotContain("Drop them anywhere").And.NotContain("Attach a screenshot");
        }
    }

    [Fact]
    public async Task TheHuntDrawer_PutsTheWaysToAddAQueryBesideCopy_AndWhatDidItFindUnderThem()
    {
        var (article, team) = await Article("Design hunt article");
        var number = await Raise(team, "Design hunt sweep", article);

        // The preview's Copy, then a query kept on the action: each has its adders beside Copy and the result under them.
        foreach (var kept in new[] { false, true })
        {
            if (kept)
            {
                var id = (await As(async sp => (await sp.GetRequiredService<ActionService>().GetAsync(number))!)).Action.Id;
                await As(sp => sp.GetRequiredService<ActionService>().AddHandWrittenHuntAsync(id, QueryDialects.All[0].Key, "search 198.51.100.7"));
            }
            await using var page = await Open(number);
            var (inPage, _) = Split(await page.Html());
            var copy = At(inPage, "data-copy-target=\"hunt-");
            var (write, saved, iocs) = (At(inPage, ">Write one by hand</button>"), At(inPage, ">Add a saved query</button>"), At(inPage, ">Add indicators</button>"));
            write.Should().BeGreaterThan(copy, kept ? "kept" : "preview");
            saved.Should().BeGreaterThan(write);
            iocs.Should().BeGreaterThan(saved);
            var found = At(inPage, "<h3 class=\"tide-hunt-found-h\">What did it find?</h3>");
            found.Should().BeGreaterThan(iocs);
            At(inPage, "No hits, close").Should().BeGreaterThan(found);
            var attach = At(inPage, "Attach a screenshot</button>");
            attach.Should().BeGreaterThan(found);
            inPage[inPage.LastIndexOf("<button", attach, StringComparison.Ordinal)..attach].Should().Contain("data-pick=\"files-input\"", "it opens the Files picker");
        }
    }
}
