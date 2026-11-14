using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Preferences;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The Actions list: a count on each view tab, the No owner pill (in the address and in saved views, as Held by:
/// nobody always was), Take it on the rows the person may take, each row's checklist progress, and an empty Mine that
/// says when the person's teams have work nobody owns. The board: its closed statuses in one Closed column, each still
/// a place to drop a card, and its cards' article, KEV flag and time in column.
/// </summary>
public partial class ActionsListBoardPageTests(TideApp app) : IClassFixture<TideApp>
{
    private static SignIn Owner() => new($"dev:al-owner-{Guid.NewGuid():N}", "Ola Owner", "ActionOwner");
    private static readonly SignIn Viewer = new("dev:al-viewer", "Vic Viewer", "Viewer");

    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    private Task<Guid> Team(string name, SignIn? member = null) => As(async sp =>
    {
        var teams = sp.GetRequiredService<TeamService>();
        var id = await teams.AddAsync($"{name} {Guid.NewGuid():N}"[..40], null, null);
        if (member is not null) await teams.AddMemberAsync(id, member.UserId, member.Name);
        return id;
    });

    private Task<int> Raise(Guid team, string title) =>
        As(sp => sp.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = title, TeamId = team }));

    private Task<ActionDetail> Detail(int number) => As(async sp => (await sp.GetRequiredService<ActionService>().GetAsync(number))!);

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // What a filter change wrote to the address (outside a circuit, writing it throws, which says where it went).
    private static async Task<string> Went(Rendered<ActionsBoard> r, Func<ActionsBoard, Task> what)
    {
        try
        {
            await r.Click(async c =>
            {
                await what(c);
                typeof(ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(c, null);
            });
        }
        catch (NavigationException e) { return Uri.UnescapeDataString(e.Location); }
        throw new InvalidOperationException("The change didn't write the address.");
    }

    private static Task PressPill(ActionsBoard a, string key)
    {
        FilterBarHtml.Press(ActionsBoard.Pills, a.Defs, key);
        return a.Refilter();
    }

    [GeneratedRegex("<span>([^<]+)</span>\\s*<span class=\"tide-tabbar-count\">(\\d+)</span>")]
    private static partial Regex TabCount();

    [Fact]
    public async Task EachViewTab_SaysHowManyActionsItShows()
    {
        var counts = await As(sp => sp.GetRequiredService<ActionService>().ViewCountsAsync(new ActionFilter(ActionView.Open)));
        await using var r = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list");
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("tide-tabbar-count"));
        var views = html[html.IndexOf("aria-label=\"Views\"", StringComparison.Ordinal)..];
        views = views[..views.IndexOf("</div>", StringComparison.Ordinal)];
        TabCount().Matches(views).ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Equal(new Dictionary<string, int>
            {
                ["Mine"] = counts.Mine, ["My teams"] = counts.MyTeams, ["All open"] = counts.Open, ["Overdue"] = counts.Overdue, ["Recently closed"] = counts.Closed,
            });
    }

    [Fact]
    public async Task NoOwner_IsAPill_InTheAddress_AndInASavedView()
    {
        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list"))
        {
            FilterBarHtml.Pressed(WebUtility.HtmlDecode(await r.HtmlOnce("action")), "No owner").Should().BeFalse();
            (await Went(r, a => PressPill(a, "noowner"))).Should().Contain("view=open").And.Contain("assignee=none");
        }

        // What Held by: nobody always wrote, a link and a view saved before the pill, opens with the pill on.
        var who = new SignIn($"dev:al-views-{Guid.NewGuid():N}", "Views tester", "Analyst");
        await As(sp => sp.GetRequiredService<SavedViewService>().SaveAsync("Nobody's", "view=open&layout=list&assignee=none"), who);
        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list&assignee=none", @as: who))
        {
            var html = WebUtility.HtmlDecode(await r.HtmlOnce("action"));
            FilterBarHtml.Pressed(html, "No owner").Should().BeTrue();
            html.Should().NotContain("Held by nobody", "the pill says it");
            r.Component.ActiveSaved!.Name.Should().Be("Nobody's");
            (await Went(r, a => PressPill(a, "noowner"))).Should().NotContain("assignee=", "pressed again it's off");
        }

        // From Mine (the person's own, which nobody's never are), it shows the person's teams' work nobody owns.
        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions?view=mine&layout=list"))
            (await Went(r, a => PressPill(a, "noowner"))).Should().Contain("view=myteams").And.Contain("assignee=none");
    }

    [Theory]
    [InlineData("/actions?view=mine&layout=list&assignee=none", "MyTeams", "none")]
    [InlineData("/actions?view=mine&layout=list&assignee=dev:someone-else", "Mine", null)]
    public async Task Mine_FromAnAddress_IsHeldToTheSameRule_AsChoosingIt(string path, string view, string? assignee)
    {
        // Mine is what the person holds: nobody's is never Mine (it's their teams' work nobody has), and another holder's
        // would show nothing. An address (a link, a bookmark) opens what choosing the tab and the filter would show.
        await using var r = await RenderAsync<ActionsBoard>(app, path);
        var shown = typeof(ActionsBoard).GetField("_view", Private)!.GetValue(r.Component)!.ToString();
        var held = (string?)typeof(ActionsBoard).GetField("_assignee", Private)!.GetValue(r.Component);
        (shown, held).Should().Be((view, assignee));
    }

    [Fact]
    public async Task TakeIt_IsOnTheRowsThePersonMayTake_AndTakesIt()
    {
        var owner = Owner();
        var token = $"altake{Guid.NewGuid():N}"[..14];
        var ours = await Raise(await Team("AL ours", owner), $"{token} ours");
        var theirs = await Raise(await Team("AL theirs"), $"{token} theirs");

        await using (var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}", @as: owner))
        {
            var html = WebUtility.HtmlDecode(await r.HtmlOnce(token));
            html.Should().Contain($"aria-label=\"Take A-{ours} on: {token} ours\"").And.NotContain($"Take A-{theirs} on", "another team's action isn't theirs to take");
            await r.Click(p => p.Take(p.Rows.Single(x => x.Number == ours)));
        }
        var taken = (await Detail(ours)).Action;
        (taken.AssigneeId, taken.Status).Should().Be((owner.UserId, ActionStatus.InProgress));

        await using var viewer = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}", @as: Viewer);
        (await viewer.HtmlOnce(token)).Should().NotContain("Take it", "a viewer works no action");
    }

    // An action's history and comments put back some days, and when it was raised: nothing has happened to it since.
    private async Task Quiet(int number, int days)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        var a = await db.Actions.Include(x => x.Events).SingleAsync(x => x.Number == number);
        var then = DateTimeOffset.UtcNow.AddDays(-days);
        a.CreatedAtUtc = then;
        foreach (var e in a.Events) e.AtUtc = then;
        await db.SaveChangesAsync();
    }

    private static string Row(string html, string title)
    {
        var row = html[html.LastIndexOf("<tr", html.IndexOf($"{title}</a>", StringComparison.Ordinal), StringComparison.Ordinal)..];
        return row[..row.IndexOf("</tr>", StringComparison.Ordinal)];
    }

    [Fact]
    public async Task EachRow_ShowsItsProgress_TheChecklist_ThenHowItsHuntWent_OrHowLongSinceAnUpdate()
    {
        var token = $"alprog{Guid.NewGuid():N}"[..14];
        var team = await Team("AL progress");
        var patch = await As(sp => sp.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = $"{token} checklist", Kind = ActionKind.PatchRemediate, TeamId = team }));
        var sweep = await As(sp => sp.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = $"{token} sweep", Kind = ActionKind.IocSweep, TeamId = team }));
        await As(async sp =>
        {
            var actions = sp.GetRequiredService<ActionService>();
            var id = (await actions.GetAsync(patch))!.Action.Id;
            await actions.AddChecklistItemAsync(id, "One");
            await actions.AddChecklistItemAsync(id, "Two");
            var first = (await actions.GetAsync(patch))!.Action.Checklist[0];
            await actions.SetChecklistItemAsync(first.Id, true);
            return true;
        });
        await Quiet(patch, 3);
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}");
        var html = WebUtility.HtmlDecode(await r.HtmlOnce(token));
        var head = html[html.IndexOf("<thead", StringComparison.Ordinal)..html.IndexOf("</thead>", StringComparison.Ordinal)];
        HeaderCell().Matches(head).Select(m => m.Groups[1].Value).Should().Equal(["Action", "Kind", "Team and owner", "Progress", "Due", "Status"]);

        var checklist = Row(html, $"{token} checklist");
        var progress = Cell().Matches(checklist)[3].Groups[1].Value;
        progress.Should().Contain("Checklist 1 of 2").And.Contain("No update for 3 days");
        var sweeping = Cell().Matches(Row(html, $"{token} sweep"))[3].Groups[1].Value;
        sweeping.Should().Contain("Hunt not run", "a sweep says how its hunt went, as the board's card does").And.NotContain("Checklist");
    }

    [Theory]
    [InlineData("list")]
    [InlineData("board")]
    public async Task NoOwner_SaysHowManyItWouldShow(string layout)
    {
        await Raise(await Team("AL count"), "AL count nobody's");
        var counts = await As(sp => sp.GetRequiredService<ActionService>().ViewCountsAsync(new ActionFilter(ActionView.Open)));
        counts.NoOwner(ActionView.Open).Should().BePositive();
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout={layout}");
        FilterBarHtml.PillCount(WebUtility.HtmlDecode(await r.HtmlOnce("No owner")), "No owner").Should().Be(counts.NoOwner(ActionView.Open));
    }

    [Fact]
    public async Task TheBoardsTabs_CountOpenActions_AsTheListsDo()
    {
        var id = (await Detail(await Raise(await Team("AL closed"), "AL closed one"))).Action.Id;
        await As(async sp =>
        {
            await sp.GetRequiredService<ActionService>().MoveAsync(id, ActionStatus.Open, ActionStatus.Cancelled);
            return true;
        });
        async Task<Dictionary<string, int>> Tabs(string layout)
        {
            await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout={layout}");
            var html = WebUtility.HtmlDecode(await r.HtmlOnce("tide-tabbar-count"));
            var views = html[html.IndexOf("aria-label=\"Views\"", StringComparison.Ordinal)..];
            views = views[..views.IndexOf("</div>", StringComparison.Ordinal)];
            return TabCount().Matches(views).ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        var list = await Tabs("list");
        (await Tabs("board")).Should().Equal(new Dictionary<string, int> { ["Mine"] = list["Mine"], ["My teams"] = list["My teams"], ["All open"] = list["All open"] },
            "\"All open\" is the same number on the board as in the list: closed actions are counted under the lanes");
    }

    [Fact]
    public async Task NoOwner_IsCountedAsAFilter_LikeTheOthers()
    {
        await Raise(await Team("AL matching"), "AL matching nobody's");
        await using var r = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list&assignee=none");
        WebUtility.HtmlDecode(await r.HtmlOnce("tide-dt-count")).Should().MatchRegex("class=\"tide-dt-count\" role=\"status\">[^<]*matching action");
    }

    // Whether the list can put focus on this row's title now: a ref to it is held (a test render can't move focus itself).
    private static Task<bool> CanFocus(Rendered<ActionsBoard> r, int number) => r.Renderer.Dispatcher.InvokeAsync(() =>
    {
        var table = (ActionTable)typeof(ActionsBoard).GetField("_table", Private)!.GetValue(r.Component)!;
        return table.FocusRowAsync(r.Component.Rows.Single(x => x.Number == number).Id);
    });

    [Fact]
    public async Task AfterTakeIt_FocusGoesToTheNextRow_ThePreviousOne_OrTheHeadingWhenTheListIsEmpty()
    {
        var token = $"alfocus{Guid.NewGuid():N}"[..15];
        var team = await Team("AL focus", SignIn.Analyst);   // only a member takes them on
        var first = await Raise(team, $"{token} first");
        var second = await Raise(team, $"{token} second");
        var third = await Raise(team, $"{token} third");
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&assignee=none&q={token}");
        await r.HtmlOnce(token);
        await r.Click(p => p.Take(p.Rows.Single(x => x.Number == first)));
        r.Component.Rows.Select(x => x.Number).Should().Equal([second, third], "the row taken has an owner now, so No owner leaves it out");
        r.Component.Focusing.Should().Be($"A-{second}", "focus goes to the next row, not the page");
        (await CanFocus(r, second)).Should().BeTrue("the row is still drawn, so its title can take focus");

        await r.Click(p => p.Take(p.Rows.Single(x => x.Number == third)));
        r.Component.Focusing.Should().Be($"A-{second}", "the last row taken: focus goes to the one before");
        (await CanFocus(r, second)).Should().BeTrue();

        await r.Click(p => p.Take(p.Rows.Single(x => x.Number == second)));
        r.Component.Focusing.Should().Be("heading", "with no rows left, to the list's heading");
    }

    [Fact]
    public async Task AfterTakeIt_OnARowThatStaysListed_FocusStaysOnIt()
    {
        var token = $"alstay{Guid.NewGuid():N}"[..14];
        var team = await Team("AL stay");
        var first = await Raise(team, $"{token} first");
        await Raise(team, $"{token} second");
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}");
        await r.HtmlOnce(token);
        await r.Click(p => p.Take(p.Rows.Single(x => x.Number == first)));
        r.Component.Rows.Select(x => x.Number).Should().Contain(first, "All open still lists it, now held");
        r.Component.Focusing.Should().Be($"A-{first}");
        (await CanFocus(r, first)).Should().BeTrue("its title is still drawn: focus goes there, not to the heading");
    }

    // The link's address arriving, as the router hands the page the new query when the browser follows it.
    private static Task Arrive(Rendered<ActionsBoard> r, string view, string? assignee) => r.Click(async p =>
    {
        (p.ViewParam, p.AssigneeParam) = (view, assignee);
        await (Task)typeof(ComponentBase).GetMethod("OnParametersSetAsync", Private)!.Invoke(p, null)!;
    });

    [Fact]
    public async Task ShowThem_IsALinkThatNavigates_AndOnlyAPlainClickMovesFocusOnArrival()
    {
        var owner = Owner();
        var number = await Raise(await Team("AL show", owner), "AL show nobody has this");
        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions?view=mine&layout=list", @as: owner))
        {
            WebUtility.HtmlDecode(await r.HtmlOnce("mine-unowned")).Should().Contain("href=\"actions?view=myteams&layout=list&assignee=none\"");
            // Ctrl, Cmd or Shift+click, or another button: the browser opens it elsewhere, and this page's focus is left alone.
            foreach (var modified in new[] { new MouseEventArgs { CtrlKey = true }, new MouseEventArgs { MetaKey = true }, new MouseEventArgs { ShiftKey = true }, new MouseEventArgs { Button = 1 } })
                await r.Click(p => { p.ShowThemClicked(modified); return Task.CompletedTask; });
            r.Component.Focusing.Should().BeNull();
        }

        // A plain click (or Enter): the browser follows the link here, and once the list arrives focus goes to its first row.
        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions?view=mine&layout=list", @as: owner))
        {
            await r.HtmlOnce("mine-unowned");
            await r.Click(p => { p.ShowThemClicked(new MouseEventArgs { Button = 0, Detail = 1 }); return Task.CompletedTask; });
            await Arrive(r, "myteams", "none");
            r.Component.Rows.Select(x => x.Number).Should().Equal(number);
            r.Component.Focusing.Should().Be($"A-{number}", "focus lands on the list's first row");
        }

        // The address can arrive before the click is heard: focus still goes there.
        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions?view=mine&layout=list", @as: owner))
        {
            await r.HtmlOnce("mine-unowned");
            await Arrive(r, "myteams", "none");
            r.Component.Focusing.Should().BeNull("nothing asked for focus yet");
            await r.Click(p => { p.ShowThemClicked(new MouseEventArgs { Button = 0 }); return Task.CompletedTask; });
            r.Component.Focusing.Should().Be($"A-{number}");
        }
    }

    [Fact]
    public async Task AClearedSearch_IsNoFilter_AndLeavesTheAddress()
    {
        await using var r = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list&q=MFA");
        await r.HtmlOnce("tide-dt-count");
        var went = await Went(r, async p =>
        {
            typeof(ActionsBoard).GetField("_search", Private)!.SetValue(p, "");   // the box emptied, as typing does
            await (Task)typeof(ActionsBoard).GetMethod("SearchChanged", Private)!.Invoke(p, null)!;
        });
        went.Should().NotContain("q=", "a blank search is no search");
        r.Component.ShownQuery().Should().NotContain("q=");
        await r.Click(c => { typeof(ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(c, null); return Task.CompletedTask; });
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().NotMatchRegex("class=\"tide-dt-count\" role=\"status\">[^<]*matching", "nothing narrows the list");
        r.Component.Counts!.Open.Should().Be(int.Parse(OpenCount().Match(html).Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture), "the All open tab counts what the list shows");
    }

    // Actions nobody holds, due today, on a team of their own, found by the token in their titles.
    private async Task<List<int>> DueToday(string token, int count, SignIn? member = null)
    {
        var team = await Team("AL due today", member);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var numbers = new List<int>();
        for (var i = 0; i < count; i++)
            numbers.Add(await As(sp => sp.GetRequiredService<ActionService>().CreateAsync(new ActionForm
            {
                Title = $"{token} {i}", Kind = ActionKind.Block, TeamId = team,
                DueDate = sp.GetRequiredService<Tide.Application.Org.OrgClock>().Today,
            })));
        return numbers;
    }

    [Theory]
    [InlineData("open")]
    [InlineData("myteams")]
    public async Task AllOpenAndMyTeams_AreGroupedByWhenDue_EachHeadingSayingHowManyHaveNoOwner(string view)
    {
        var token = $"algroup{Guid.NewGuid():N}"[..15];
        await DueToday(token, 2, SignIn.Analyst);
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view={view}&layout=list&q={token}");
        var html = WebUtility.HtmlDecode(await r.HtmlOnce(token));
        html.Should().MatchRegex("<th colspan=\"\\d+\" scope=\"rowgroup\">\\s*Due today <span class=\"tide-count\">2</span>[\\s\\S]*?2 have no owner",
            "the group's heading says how many of it nobody has");
    }

    [Fact]
    public async Task TheStatusCell_IsAMenu_ForWhoeverMayWorkTheAction_AndAPlainBadgeForEveryoneElse()
    {
        var owner = Owner();
        var token = $"alstat{Guid.NewGuid():N}"[..14];
        var ours = await Raise(await Team("AL status ours", owner), $"{token} ours");
        var theirs = await Raise(await Team("AL status theirs"), $"{token} theirs");

        await using (var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}", @as: owner))
        {
            var html = WebUtility.HtmlDecode(await r.HtmlOnce(token));
            html.Should().MatchRegex($"<button type=\"button\" class=\"[^\"]*tide-status-menu[^\"]*\"[^>]*aria-haspopup=\"menu\"[^>]*aria-label=\"Status of A-{ours}: Open\"");
            html.Should().NotContain($"Status of A-{theirs}", "another team's action isn't theirs to move");
        }
        await using var viewer = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}", @as: Viewer);
        (await viewer.HtmlOnce(token)).Should().NotContain("tide-status-menu", "a viewer works no action");
    }

    [Fact]
    public async Task TheStatusMenu_MovesTheActionThroughTheBoardsCheckedPath_AsksHowItClosed_AndRefusesAStaleRow()
    {
        var token = $"almove{Guid.NewGuid():N}"[..14];
        var team = await Team("AL move");
        var number = await Raise(team, $"{token} move");
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}");
        await r.HtmlOnce(token);

        await r.Click(p => p.MoveRow(p.Rows.Single(x => x.Number == number), ActionStatus.Blocked));
        var moved = (await Detail(number)).Action;
        moved.Status.Should().Be(ActionStatus.Blocked);
        moved.Events.Should().Contain(e => e.Text == "Status: Open → Blocked.", "recorded as a move on the board is");
        r.Component.Rows.Single(x => x.Number == number).Status.Should().Be(ActionStatus.Blocked, "the list is read again");

        // Closing asks how, as the board does.
        await r.Press(p => p.MoveRow(p.Rows.Single(x => x.Number == number), ActionStatus.Done));
        WebUtility.HtmlDecode(await r.HtmlOnce("Move to Done")).Should().Contain($"Move A-{number} to Done");

        // A row someone else moved meanwhile: refused, nothing changes, and the list is read again.
        var stale = r.Component.Rows.Single(x => x.Number == number);
        await As(async sp => { await sp.GetRequiredService<ActionService>().MoveAsync(stale.Id, ActionStatus.Blocked, ActionStatus.Tracking); return true; });
        await r.Click(p => p.MoveRow(stale, ActionStatus.InProgress));
        (await Detail(number)).Action.Status.Should().Be(ActionStatus.Tracking, "a stale row moves nothing");
        r.Component.Rows.Single(x => x.Number == number).Status.Should().Be(ActionStatus.Tracking);
    }

    [Fact]
    public async Task ARow_SaysKev_BesideItsArticle()
    {
        var token = $"alkev{Guid.NewGuid():N}"[..13];
        var cve = $"CVE-2099-{Random.Shared.Next(10000, 99999)}";
        Guid article;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            var a = new Tide.Domain.Entities.Article { Title = $"{token} article", Tlp = TlpLevel.Amber, AuthorId = "dev:x", AuthorName = "X" };
            db.Articles.Add(a);
            db.ArticleCves.Add(new Tide.Domain.Entities.ArticleCve { ArticleId = a.Id, CveId = cve });
            db.CveRecords.Add(new Tide.Domain.Entities.CveRecord { CveId = cve, IsKev = true });
            await db.SaveChangesAsync();
            article = a.Id;
        }
        var team = await Team("AL kev flags");
        await As(sp => sp.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = $"{token} action", Kind = ActionKind.Block, TeamId = team, ArticleId = article }));
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}");
        Row(WebUtility.HtmlDecode(await r.HtmlOnce(token)), $"{token} action").Should().MatchRegex($"{token} article\\s*<span class=\"tide-kev\"[^>]*>KEV</span>");
    }

    [Fact]
    public async Task TheList_HasASelectedRow_TheAddressNamesIt_TTakesIt_AndTheKeysAreListed()
    {
        var owner = Owner();
        var token = $"alkeys{Guid.NewGuid():N}"[..14];
        var team = await Team("AL keys", owner);
        var first = await Raise(team, $"{token} 0");
        var second = await Raise(team, $"{token} 1");
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}&row=1", @as: owner);
        var html = WebUtility.HtmlDecode(await r.HtmlOnce(token));
        r.Component.Selected.Should().Be(1, "?row= names the selected row by its place");
        var row = html[html.LastIndexOf("<tr", html.IndexOf($"{token} 1</a>", StringComparison.Ordinal), StringComparison.Ordinal)..];
        row[..row.IndexOf('>')].Should().Contain("tide-action-row").And.Contain("selected").And.Contain("aria-current=\"true\"").And.Contain("tabindex=\"0\"");
        var other = html[html.LastIndexOf("<tr", html.IndexOf($"{token} 0</a>", StringComparison.Ordinal), StringComparison.Ordinal)..];
        other[..other.IndexOf('>')].Should().Contain("tabindex=\"-1\"").And.NotContain("aria-current");
        html.Should().MatchRegex("<kbd class=\"tide-kbd\">J</kbd>\\s*<kbd class=\"tide-kbd\">K</kbd> move ·\\s*<kbd class=\"tide-kbd\">T</kbd>\\s*<span>take it ·</span>\\s*<kbd class=\"tide-kbd\">Enter</kbd> open",
            "the keys are said beside the heading");

        var numbers = r.Component.Rows.Select(x => x.Number).ToList();
        await r.Click(p => p.RowKey("take"));
        (await Detail(numbers[1])).Action.AssigneeId.Should().Be(owner.UserId, "T takes the selected row on");
        (await Detail(numbers[0])).Action.AssigneeId.Should().BeNull();
        new[] { first, second }.Should().Contain(numbers);

        var keys = Tide.Web.Components.Layout.Shortcuts.For("actions?view=open&layout=list").SelectMany(g => g.Keys).ToList();
        keys.Should().Contain(k => k.Keys.Contains("J") && k.Letter).And.Contain(k => k.Keys.Contains("K") && k.Letter)
            .And.Contain(k => k.Keys.Contains("T") && k.Letter && k.What.Contains("Take", StringComparison.Ordinal))
            .And.Contain(k => k.Keys.Contains("Enter") && k.What.Contains("Open", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheListsAddress_NamesTheSelectedActionByItsNumber_SoALinkStillFindsItOnceRowsMove()
    {
        // ?row= was a place on the page: a shared link selected whatever action had come to be there.
        var owner = Owner();
        var token = $"alcard{Guid.NewGuid():N}"[..14];
        var team = await Team("AL card", owner);
        await Raise(team, $"{token} 0");
        await Raise(team, $"{token} 1");
        await Raise(team, $"{token} 2");
        int[] numbers;
        await using (var all = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}", @as: owner))
        {
            await all.HtmlOnce(token);
            numbers = [.. all.Component.Rows.Select(x => x.Number)];
        }

        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}&card={numbers[2]}&row=0", @as: owner);
        await r.HtmlOnce(token);

        r.Component.Selected.Should().Be(2, "?card= names the action, as on the board, ahead of any ?row=");
        var address = r.Component.RowAddress(1);
        address.Should().Contain($"card={numbers[1]}").And.NotContain("row=");
        r.Component.RowAddress(0).Should().NotContain("card=").And.NotContain("row=", "the first row is left out of the address");
    }

    [Fact]
    public async Task TheTakeKey_IsOfferedOnlyToSomeoneWhoMayTakeActions()
    {
        await using (var viewer = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list", @as: Viewer))
        {
            var html = WebUtility.HtmlDecode(await viewer.HtmlOnce("tide-actions-keys"));
            html.Should().Contain("move").And.Contain(">Enter</kbd> open").And.NotContain("take it", "a viewer takes nothing on");
        }
        await using (var owner = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list", @as: Owner()))
            WebUtility.HtmlDecode(await owner.HtmlOnce("tide-actions-keys")).Should().Contain(">T</kbd>").And.Contain("take it");

        static IEnumerable<Tide.Web.Components.Layout.Shortcut> Keys(bool canTake) =>
            Tide.Web.Components.Layout.Shortcuts.For("actions?view=open&layout=list", canTake: canTake).SelectMany(g => g.Keys);
        Keys(canTake: false).Should().NotContain(k => k.Keys.Contains("T")).And.Contain(k => k.Keys.Contains("J")).And.Contain(k => k.Keys.Contains("Enter"));
        Keys(canTake: true).Should().Contain(k => k.Keys.Contains("T"));
    }

    [Fact]
    public async Task AfterTheStatusMenuMovesARowOutOfTheList_FocusGoesToTheNextRow()
    {
        var token = $"alleave{Guid.NewGuid():N}"[..15];
        var team = await Team("AL leaves the list");
        var first = await Raise(team, $"{token} 0");
        var second = await Raise(team, $"{token} 1");
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}&status=open");
        await r.HtmlOnce(token);
        var numbers = r.Component.Rows.Select(x => x.Number).ToList();
        await r.Click(p => p.MoveRow(p.Rows[0], ActionStatus.Blocked));
        r.Component.Rows.Select(x => x.Number).Should().Equal([numbers[1]], "Blocked isn't Open: the row left the list");
        r.Component.Focusing.Should().Be($"A-{numbers[1]}", "focus goes to the next row, not the page");
        new[] { first, second }.Should().BeEquivalentTo(numbers);
    }

    [Fact]
    public async Task TheListsColumns_SayTeamAndOwner_AndItsKevFlagIsThePill()
    {
        await using var r = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list");
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("<thead"));
        var head = html[html.IndexOf("<thead", StringComparison.Ordinal)..html.IndexOf("</thead>", StringComparison.Ordinal)];
        head.Should().Contain(">Team and owner<", "the cell shows both");
    }

    [Fact]
    public async Task AnEmptyMine_SaysWhenThePersonsTeamsHaveWorkNobodyOwns()
    {
        var owner = Owner();
        await Raise(await Team("AL unowned", owner), "AL nobody has this");
        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions?view=mine&layout=list", @as: owner))
        {
            var html = WebUtility.HtmlDecode(await r.HtmlOnce("mine-unowned"));
            html.Should().Contain("id=\"mine-unowned\"").And.Contain("Your teams have 1 action with no owner")
                .And.Contain("href=\"actions?view=myteams&layout=list&assignee=none\"");
        }

        var busy = Owner();
        await Team("AL nothing", busy);
        await using var none = await RenderAsync<ActionsBoard>(app, "/actions?view=mine&layout=list", @as: busy);
        (await none.HtmlOnce("Nothing assigned to you")).Should().NotContain("mine-unowned", "nothing of theirs is unowned");
    }

    [GeneratedRegex("<td[^>]*>([\\s\\S]*?)</td>")]
    private static partial Regex Cell();

    [GeneratedRegex("<th[^>]*>(?:<[^>]+>)*\\s*([A-Za-z ]+?)\\s*<")]
    private static partial Regex HeaderCell();

    [GeneratedRegex("class=\"tide-dt-count\" role=\"status\">(\\d+) action")]
    private static partial Regex OpenCount();
}
