using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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

    [Fact]
    public async Task EachRow_ShowsItsChecklistProgress_UnderItsStatus_WithoutAColumnOfItsOwn()
    {
        var token = $"alprog{Guid.NewGuid():N}"[..14];
        var number = await Raise(await Team("AL progress"), $"{token} checklist");
        await As(async sp =>
        {
            var actions = sp.GetRequiredService<ActionService>();
            var id = (await actions.GetAsync(number))!.Action.Id;
            await actions.AddChecklistItemAsync(id, "One");
            await actions.AddChecklistItemAsync(id, "Two");
            var first = (await actions.GetAsync(number))!.Action.Checklist[0];
            await actions.SetChecklistItemAsync(first.Id, true);
            return true;
        });
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=list&q={token}");
        var html = WebUtility.HtmlDecode(await r.HtmlOnce(token));
        html.Should().NotMatchRegex(@"<th[^>]*>\s*Checklist\s*</th>", "most rows have no checklist, so a column of its own would only narrow the title");
        var row = html[html.LastIndexOf("<tr", html.IndexOf($"{token} checklist</a>", StringComparison.Ordinal), StringComparison.Ordinal)..];
        row = row[..row.IndexOf("</tr>", StringComparison.Ordinal)];
        Regex.Count(row, "<td").Should().Be(5);
        var status = row[row.LastIndexOf("<td", StringComparison.Ordinal)..];
        status.Should().Contain("Open").And.Contain("tide-row-progress").And.Contain("aria-label=\"Checklist: 1 of 2 done\"");
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
        var team = await Team("AL focus");
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
        r.Component.Counts!.Open.Should().Be(int.Parse(Regex.Match(html, "class=\"tide-dt-count\" role=\"status\">(\\d+) action").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture), "the All open tab counts what the list shows");
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
}
