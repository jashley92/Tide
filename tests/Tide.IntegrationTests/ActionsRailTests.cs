using System.Net;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Preferences;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The Actions list and board put the selected action in the context rail: what to do next, Take it where the person
/// may take it (Assign on the list, Move to on the board) and Open, its facts, its hunt query for a sweep or hunt (a
/// preview saved only when it's copied), the article it came from and its last activity; on the list, links about the
/// page. The board has a selected card, as the list has a selected row: named in the address, moved by the keys, and
/// taken on with T.
/// </summary>
public class ActionsRailTests(TideApp app) : IClassFixture<TideApp>
{
    private static SignIn Owner() => new($"dev:ar-owner-{Guid.NewGuid():N}", "Rae Owner", "ActionOwner");
    private static readonly SignIn Viewer = new("dev:ar-viewer", "Val Viewer", "Viewer");
    private const string RailTag = "<aside id=\"tide-rail\"";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    /// <summary>A team, with <paramref name="member"/> on it.</summary>
    private Task<Guid> Team(string name, SignIn? member = null) => As(async sp =>
    {
        var teams = sp.GetRequiredService<TeamService>();
        var id = await teams.AddAsync($"{name} {Guid.NewGuid():N}"[..40], null, null);
        if (member is not null) await teams.AddMemberAsync(id, member.UserId, member.Name);
        return id;
    });

    /// <summary>An article (TLP:AMBER, a CVE, an IP) and an action of <paramref name="kind"/> raised from it on
    /// <paramref name="team"/>; the action's number and id.</summary>
    private Task<(int Number, Guid Id)> Raise(Guid team, string title, ActionKind kind = ActionKind.IocSweep) => As(async sp =>
    {
        var articles = sp.GetRequiredService<ArticleService>();
        var article = await articles.CreateAsync(title + " (article)", "Vendor", null, TlpLevel.Amber);
        var form = ArticleForm.From((await articles.GetAsync(article))!);
        form.Bluf = "Exploited for initial access.";
        form.Cves = ["CVE-2026-0001"];
        form.Indicators = [new() { Type = IndicatorType.IpAddress, Value = "203.0.113.45" }];
        await articles.SaveAsync(form);
        var actions = sp.GetRequiredService<ActionService>();
        var number = await actions.CreateAsync(new ActionForm { Title = title, Kind = kind, TeamId = team, ArticleId = article });
        return (number, (await actions.GetAsync(number))!.Action.Id);
    });

    private Task<(int Hunts, int Events)> Trace(Guid id) => As(async sp =>
    {
        await using var db = await sp.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        return (await db.HuntQueries.CountAsync(h => h.ActionId == id), await db.ActionEvents.CountAsync(e => e.ActionId == id));
    });

    private Task<ActionDetail> Detail(int number) => As(async sp => (await sp.GetRequiredService<ActionService>().GetAsync(number))!);

    /// <summary>The rail's markup, from where MainLayout draws it.</summary>
    private static string RailOf(string html)
    {
        html = WebUtility.HtmlDecode(html);
        var at = html.IndexOf(RailTag, StringComparison.Ordinal);
        at.Should().BePositive("the Actions page has a context rail");
        return html[at..];
    }

    private static async Task<string> RailOnce(Rendered<ActionsBoard> r, string text)
    {
        await r.HtmlOnce(text);
        return RailOf(await r.Html());
    }

    [Fact]
    public async Task TheList_ShowsTheSelectedRow_InTheRail_WithTakeItAssignAndOpen_ForSomeoneWhoWorksIt()
    {
        var owner = Owner();
        var token = $"arlist{Guid.NewGuid():N}"[..14];
        var team = await Team("AR list", owner);
        var (first, _) = await Raise(team, $"{token} first", ActionKind.Block);
        var (second, _) = await Raise(team, $"{token} second", ActionKind.Block);
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}&row=1", @as: owner);
        var selected = r.Component.Rows[1].Number;
        var rail = await RailOnce(r, "rail-article-h");

        rail.Should().Contain($"aria-label=\"About A-{selected}\"", "the rail is named for the selected action");
        rail.Should().Contain("data-rail-next").And.Contain("Nobody has it");
        rail.Should().Contain("tide-rail-take").And.Contain($"aria-label=\"Take A-{selected} on\"").And.Contain("aria-keyshortcuts=\"T\"").And.Contain(">T</kbd>");
        rail.Should().Contain($"href=\"actions/{selected}?do=edit\"").And.Contain("Assign…").And.NotContain("Move to", "the list's rail assigns; the board's moves");
        rail.Should().Contain($"href=\"actions/{selected}\"").And.Contain(">Open</a>");
        rail.Should().Contain("<dt>Kind</dt>").And.Contain("<dt>Team</dt>").And.Contain("<dt>In Open</dt>").And.Contain("<dt>Checklist</dt>");
        rail.Should().Contain("id=\"rail-article-h\"").And.Contain("CVE-2026-0001").And.Contain("TLP:AMBER");
        rail.Should().Contain("id=\"rail-activity-h\"").And.Contain("id=\"rail-about-h\"").And.Contain("href=\"workload\"").And.Contain("href=\"library/board-rules\"");
        new[] { first, second }.Should().Contain(selected);
    }

    [Fact]
    public async Task TheRail_FollowsTheSelectedRow()
    {
        var token = $"arfollow{Guid.NewGuid():N}"[..16];
        var team = await Team("AR follow");
        await Raise(team, $"{token} one", ActionKind.Block);
        await Raise(team, $"{token} two", ActionKind.Block);
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}");
        var rows = r.Component.Rows;
        (await RailOnce(r, "rail-article-h")).Should().Contain($"aria-label=\"About A-{rows[0].Number}\"");
        await r.Click(p =>
        {
            p.SelectRow(rows[1]);
            typeof(ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(p, null);
            return Task.CompletedTask;
        });
        (await RailOnce(r, $"About A-{rows[1].Number}")).Should().Contain($"aria-label=\"About A-{rows[1].Number}\"");
    }

    [Fact]
    public async Task AViewer_SeesTheRail_ButNoTakeAssignOrMove()
    {
        var token = $"arviewer{Guid.NewGuid():N}"[..16];
        var (number, _) = await Raise(await Team("AR viewer"), $"{token} it", ActionKind.Block);
        foreach (var layout in new[] { "list", "board" })
        {
            await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout={layout}&q={token}", @as: Viewer);
            var rail = await RailOnce(r, "rail-article-h");
            rail.Should().Contain($"aria-label=\"About A-{number}\"", layout).And.Contain("Nobody has it").And.Contain(">Open</a>")
                .And.Contain("<dt>Kind</dt>").And.Contain("id=\"rail-article-h\"");
            rail.Should().NotContain("tide-rail-take", layout).And.NotContain("Assign…").And.NotContain("Move to");
        }
    }

    [Fact]
    public async Task TheBoard_HasASelectedCard_NamedInTheAddress_WithTheRailForIt()
    {
        var owner = Owner();
        var token = $"arboard{Guid.NewGuid():N}"[..15];
        var team = await Team("AR board", owner);
        var (one, _) = await Raise(team, $"{token} one", ActionKind.Block);
        var (two, _) = await Raise(team, $"{token} two", ActionKind.Block);
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=board&q={token}&card={two}", @as: owner);
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("rail-article-h"));
        var card = html[html.LastIndexOf("<article", html.IndexOf($"{token} two<", StringComparison.Ordinal), StringComparison.Ordinal)..];
        card[..card.IndexOf('>')].Should().Contain("tide-kcard").And.Contain("selected").And.Contain("aria-current=\"true\"").And.Contain("tabindex=\"0\"");
        var other = html[html.LastIndexOf("<article", html.IndexOf($"{token} one<", StringComparison.Ordinal), StringComparison.Ordinal)..];
        other[..other.IndexOf('>')].Should().Contain("tabindex=\"-1\"").And.NotContain("aria-current");

        var rail = RailOf(html);
        rail.Should().Contain($"aria-label=\"About A-{two}\"").And.Contain("tide-rail-take").And.Contain("Move to").And.NotContain("Assign…")
            .And.NotContain("rail-about-h", "About this page is the list's");
        r.Component.SelectedCard?.Number.Should().Be(two);
    }

    [Fact]
    public async Task OnTheBoard_TheKeysMoveTheSelection_AcrossLanes_AndTTakesTheCard()
    {
        var owner = Owner();
        var token = $"arkeys{Guid.NewGuid():N}"[..14];
        var team = await Team("AR keys", owner);
        var (open1, _) = await Raise(team, $"{token} a", ActionKind.Block);
        var (open2, _) = await Raise(team, $"{token} b", ActionKind.Block);
        var (blocked, blockedId) = await Raise(team, $"{token} c", ActionKind.Block);
        await As(async sp =>
        {
            await sp.GetRequiredService<ActionService>().SetStatusAsync(blockedId, ActionStatus.Blocked, ActionOutcome.None, null);
            return true;
        });

        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=board&q={token}", @as: owner);
        await r.HtmlOnce(token);
        var lane = new[] { open1, open2 };
        lane.Should().Contain(r.Component.SelectedCard!.Number, "the first card is selected when the address names none");
        var top = r.Component.SelectedCard!.Number;
        await r.Click(p => p.BoardKey("right"));
        r.Component.SelectedCard!.Number.Should().Be(blocked, "right skips the empty In progress lane");
        await r.Click(p => p.BoardKey("left"));
        r.Component.SelectedCard!.Number.Should().Be(top, "left comes back to the same place in the Open lane");

        await r.Click(p => p.BoardKey("take"));
        var taken = await Detail(top);
        taken.Action.AssigneeId.Should().Be(owner.UserId, "T takes the selected card on");
        r.Component.SelectedCard!.Number.Should().Be(top, "the selection stays on the card, now in In progress");
    }

    [Fact]
    public async Task ASweepsRail_PreviewsItsQuery_SavingNothing_UntilCopyRecordsIt()
    {
        var owner = Owner();
        var token = $"arhunt{Guid.NewGuid():N}"[..14];
        var (number, id) = await Raise(await Team("AR hunt", owner), $"{token} sweep");
        var before = await Trace(id);

        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}", @as: owner);
        var rail = await RailOnce(r, "rail-hunt-query");
        rail.Should().Contain("id=\"rail-hunt-h\"").And.Contain("<pre id=\"rail-hunt-query\"").And.Contain("203.0.113.45")
            .And.Contain("data-copy-target=\"rail-hunt-query\"").And.Contain("Copy query").And.Contain("203.0.113[.]45");
        rail.Should().Contain("Take it, then copy the query");
        (await Trace(id)).Should().Be(before, "showing the rail saves no query and logs nothing");

        await r.Click(p => p.Rail!.CopyPreview());
        var after = await Trace(id);
        after.Hunts.Should().Be(before.Hunts + 1, "Copy keeps the query on the action, as the action page's Copy does");
        after.Events.Should().BeGreaterThan(before.Events);
        (await Detail(number)).Action.Hunts.Should().ContainSingle();
    }

    [Fact]
    public async Task AViewer_IsShownNoHuntPreview()
    {
        var token = $"arhv{Guid.NewGuid():N}"[..12];
        var (_, id) = await Raise(await Team("AR hunt viewer"), $"{token} sweep");
        var before = await Trace(id);
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}", @as: Viewer);
        var rail = await RailOnce(r, "rail-article-h");
        rail.Should().NotContain("rail-hunt-query", "only whoever may work it previews its query").And.NotContain("203.0.113[.]45");
        (await Trace(id)).Should().Be(before);
    }

    [Fact]
    public async Task OverdueToBoard_KeepsTheOverdueActions_AsAllOpenWithTheOverduePill()
    {
        await using var r = await RenderAsync<ActionsBoard>(app, "/actions?view=overdue&layout=list");
        try { await r.Click(c => (Task)typeof(ActionsBoard).GetMethod("SetLayout", Private)!.Invoke(c, ["board"])!); }
        catch (NavigationException) { }
        typeof(ActionsBoard).GetField("_view", Private)!.GetValue(r.Component).Should().Be(ActionView.Open);
        typeof(ActionsBoard).GetField("_due", Private)!.GetValue(r.Component).Should().Be(ActionDue.Overdue, "the board shows the same overdue actions, with the pill on");

        await using var linked = await RenderAsync<ActionsBoard>(app, "/actions?view=overdue&layout=board");
        typeof(ActionsBoard).GetField("_due", Private)!.GetValue(linked.Component).Should().Be(ActionDue.Overdue, "a link to the Overdue board is read the same way");
    }

    [Fact]
    public async Task OpenHereByDefault_KeepsTheLayoutToo()
    {
        var who = new SignIn($"dev:ar-default-{Guid.NewGuid():N}", "Default tester", "Analyst");
        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list", @as: who))
        {
            (await r.Html()).Should().Contain("Open here by default");
            await r.Click(c => c.SetDefaultView(UserPreferences.AllOpen));
        }
        await using var scope = SignedIn(app, who);
        var prefs = scope.ServiceProvider.GetRequiredService<UserPreferenceService>();
        (await prefs.GetAsync(UserPreferences.ActionsView)).Should().Be(UserPreferences.AllOpen);
        (await prefs.GetAsync(UserPreferences.ActionsLayout)).Should().Be(UserPreferences.List, "the list was shown, so the list opens");

        await using var board = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=board", @as: who);
        (await board.Html()).Should().Contain("Open here by default", "the board isn't where Actions opens for them");
    }
}
