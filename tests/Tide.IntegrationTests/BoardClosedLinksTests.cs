using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The board's closed strip, in the services: Recently closed narrowed to one closed status, a reach (the strip's 7 or 30
/// days) and the reader's teams lists exactly what each chip counts, whichever of the board's views and filters it was
/// counted under.
/// </summary>
public class ClosedListReachServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public ClosedListReachServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private string Me => _db.User.UserId;

    private async Task Add(Guid team, ActionStatus status, int daysAgo, string? assignee = null, Priority priority = Priority.P3)
    {
        await using var ctx = _db.Context();
        var n = await ctx.Actions.IgnoreQueryFilters().MaxAsync(a => (int?)a.Number) ?? 0;
        var at = DateTimeOffset.UtcNow.AddDays(-daysAgo).AddMinutes(5);
        var a = new ActionItem { Number = n + 1, Title = $"Action {n + 1}", TeamId = team, Status = status, AssigneeId = assignee, Priority = priority };
        if (ActionItem.IsClosedStatus(status)) (a.ClosedAtUtc, a.ClosedDate) = (at, DateOnly.FromDateTime(at.UtcDateTime));
        ctx.Actions.Add(a);
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task RecentlyClosed_NarrowedToAStatusAReachAndMyTeams_ListsWhatEachChipCounts()
    {
        var mine = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var other = await _db.Teams().AddAsync("Vulnerability Management", null, null);
        await _db.Teams().AddMemberAsync(mine, Me, "Test Analyst");
        await Add(mine, ActionStatus.Done, 2, Me, Priority.P1);
        await Add(mine, ActionStatus.Done, 3);
        await Add(mine, ActionStatus.Cancelled, 1);
        await Add(other, ActionStatus.Done, 4, Me);
        await Add(other, ActionStatus.NotApplicable, 5);
        await Add(mine, ActionStatus.Done, 20);
        await Add(mine, ActionStatus.NotApplicable, 40);   // past the month: on no chip
        await Add(mine, ActionStatus.Open, 0);
        var svc = _db.Actions();

        foreach (var days in new[] { ActionService.BoardClosedDays, ActionService.BoardOlderDays })
        foreach (var board in new[]
                 {
                     new ActionFilter(ActionView.Mine), new ActionFilter(ActionView.MyTeams), new ActionFilter(ActionView.Open),
                     new ActionFilter(ActionView.Open) { Priorities = [Priority.P1] }, new ActionFilter(ActionView.Open, TeamId: other),
                 })
        {
            var strip = await svc.ViewCountsAsync(board, closedDays: days);
            foreach (var status in new[] { ActionStatus.Done, ActionStatus.Cancelled, ActionStatus.NotApplicable })
            {
                // The chip's link, as the page writes it: Mine is held by me, My teams is on my teams.
                var list = board with
                {
                    View = ActionView.Closed, Status = status, ClosedDays = days,
                    AssigneeId = board.View == ActionView.Mine ? Me : board.AssigneeId, OnMyTeams = board.View == ActionView.MyTeams,
                };
                var page = await svc.PageAsync(list);
                page.Total.Should().Be(strip.ClosedAs(status), $"{board} over {days} days: the {status} chip counts what its list shows");
                page.Rows.Should().OnlyContain(r => r.Status == status);
                page.Rows.Should().OnlyContain(r => r.ClosedDate >= _db.Clock.Today.AddDays(-days - 1));
            }
        }

        // Within the reach, and every closed one without it.
        (await svc.PageAsync(new ActionFilter(ActionView.Closed) { Status = ActionStatus.Done, ClosedDays = 7 })).Total.Should().Be(3);
        (await svc.PageAsync(new ActionFilter(ActionView.Closed) { Status = ActionStatus.Done, ClosedDays = 30 })).Total.Should().Be(4);
        (await svc.PageAsync(new ActionFilter(ActionView.Closed) { Status = ActionStatus.NotApplicable })).Total.Should().Be(2, "90 days, as before");
        (await svc.PageAsync(new ActionFilter(ActionView.Closed) { ClosedDays = 500 })).Total.Should().Be(7, "a reach past the list's 90 days is the 90 days");
        (await svc.PageAsync(new ActionFilter(ActionView.Closed) { OnMyTeams = true })).Total.Should().Be(5, "only my team's");

        // The Recently closed tab counts what the narrowed list shows.
        var narrowed = new ActionFilter(ActionView.Closed) { ClosedDays = 7, OnMyTeams = true };
        (await svc.ViewCountsAsync(narrowed)).Closed.Should().Be((await svc.PageAsync(narrowed)).Total).And.Be(3);
    }
}

/// <summary>
/// The board's closed chips are links as well as drop targets: each opens Recently closed on the list, at that status and
/// the strip's reach, with the board's filters. A closed action's rail on the list offers Reopen, through the same
/// checked, recorded move the board makes, to whoever may move it.
/// </summary>
public partial class BoardClosedLinksTests(TideApp app) : IClassFixture<TideApp>
{
    private static SignIn Owner() => new($"dev:bcl-owner-{Guid.NewGuid():N}", "Cal Owner", "ActionOwner");
    private static readonly SignIn Viewer = new("dev:bcl-viewer", "Vic Viewer", "Viewer");
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    private Task<Guid> Team(SignIn? member = null) => As(async sp =>
    {
        var teams = sp.GetRequiredService<TeamService>();
        var id = await teams.AddAsync($"BCL {Guid.NewGuid():N}"[..30], null, null);
        if (member is not null) await teams.AddMemberAsync(id, member.UserId, member.Name);
        return id;
    });

    // An action on the team, closed (as the board closes one) that many days ago, or left open.
    private Task<(int Number, Guid Id)> Action(Guid team, string title, ActionStatus status = ActionStatus.Open, int daysAgo = 0, string? note = null) => As(async sp =>
    {
        var actions = sp.GetRequiredService<ActionService>();
        var number = await actions.CreateAsync(new ActionForm { Title = title, Kind = ActionKind.Other, TeamId = team });
        var id = (await actions.GetAsync(number))!.Action.Id;
        if (status == ActionStatus.Open) return (number, id);
        var outcome = status == ActionStatus.Done ? ActionOutcome.Remediated : ActionOutcome.None;
        await actions.MoveAsync(id, ActionStatus.Open, status, outcome, note);
        if (daysAgo > 0)
        {
            await using var db = await sp.GetRequiredService<IAppDbContextFactory>().CreateAsync();
            var a = await db.Actions.FirstAsync(x => x.Id == id);
            var at = DateTimeOffset.UtcNow.AddDays(-daysAgo);
            (a.ClosedAtUtc, a.ClosedDate) = (at, DateOnly.FromDateTime(at.UtcDateTime));
            await db.SaveChangesAsync();
        }
        return (number, id);
    });

    private Task<ActionDetail> Detail(int number) => As(async sp => (await sp.GetRequiredService<ActionService>().GetAsync(number))!);

    // The chip for a status on the strip: from its drop target's opening tag to the end of its link.
    private static string Chip(string html, ActionStatus status)
    {
        var strip = html.IndexOf("class=\"tide-kstrip\"", StringComparison.Ordinal);
        strip.Should().BePositive("the board has its closed strip");
        var at = html.IndexOf($"data-status=\"{status}\"", strip, StringComparison.Ordinal);
        at.Should().BePositive($"the {status} chip is on the strip");
        var chip = html[html.LastIndexOf("<div", at, StringComparison.Ordinal)..html.IndexOf("</a>", at, StringComparison.Ordinal)];
        chip.Should().StartWith("<div class=\"tide-kchip").And.NotContain("role=\"group\"", "the link names the chip");
        return chip;
    }

    // Where a status's chip links.
    private static string ChipHref(string html, ActionStatus status) => Href().Match(Chip(html, status)).Groups[1].Value;

    [Fact]
    public async Task EachClosedChip_LinksToRecentlyClosed_AtItsStatusAndReach_WithTheBoardsFilters()
    {
        var token = $"bcl{Guid.NewGuid():N}"[..12];
        var team = await Team();
        await Action(team, $"{token} open");
        var (done, _) = await Action(team, $"{token} done", ActionStatus.Done, 2);
        var (old, _) = await Action(team, $"{token} done long ago", ActionStatus.Done, 20);
        var (na, _) = await Action(team, $"{token} not needed", ActionStatus.NotApplicable, 1);

        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=board&q={token}&team={team}");
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("tide-kstrip"));
        var chip = Chip(html, ActionStatus.NotApplicable);
        chip.Should().Contain("<a ").And.Contain("class=\"tide-kchip-link\"").And.Contain("draggable=\"false\"")
            .And.Contain("aria-label=\"Not applicable: 1 closed in the last 7 days, show them\"");
        var href = ChipHref(html, ActionStatus.NotApplicable);
        href.Should().StartWith("actions?").And.Contain("view=closed").And.Contain("layout=list").And.Contain("status=notapplicable")
            .And.Contain("closed=7").And.Contain($"team={team}").And.Contain($"q={token}");
        var doneHref = ChipHref(html, ActionStatus.Done);
        Chip(html, ActionStatus.Done).Should().Contain("aria-label=\"Done: 1 closed in the last 7 days, show them\"");

        // The list it opens shows only that status, within the reach.
        await using (var list = await RenderAsync<ActionsBoard>(app, "/" + href))
        {
            await list.HtmlOnce(token);
            list.Component.Rows.Select(x => x.Number).Should().Equal(na);
        }
        await using (var list = await RenderAsync<ActionsBoard>(app, "/" + doneHref))
        {
            var shown = WebUtility.HtmlDecode(await list.HtmlOnce(token));
            list.Component.Rows.Select(x => x.Number).Should().Equal([done], "not the one closed 20 days ago");
            shown.Should().Contain("Closed in the last 7 days").And.Contain("Done");
        }

        // At 30 days the chips say so, and link that far back.
        await r.Click(async p =>
        {
            await (Task)typeof(ActionsBoard).GetMethod("ShowOlder", Private)!.Invoke(p, null)!;
            typeof(ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(p, null);
        });
        var month = WebUtility.HtmlDecode(await r.HtmlOnce("Done: 2 closed in the last 30 days"));
        var monthHref = ChipHref(month, ActionStatus.Done);
        monthHref.Should().Contain("closed=30");
        await using var older = await RenderAsync<ActionsBoard>(app, "/" + monthHref);
        await older.HtmlOnce(token);
        older.Component.Rows.Select(x => x.Number).Should().BeEquivalentTo([done, old]);
    }

    [Fact]
    public async Task FromMineOrMyTeams_TheChipsListOnlyTheirClosedActions()
    {
        var owner = Owner();
        var token = $"bclm{Guid.NewGuid():N}"[..13];
        var team = await Team(owner);
        var (mineDone, mineId) = await Action(team, $"{token} mine", ActionStatus.Open);
        await As(async sp =>
        {
            var actions = sp.GetRequiredService<ActionService>();
            await actions.TakeAsync(mineId);
            await actions.MoveAsync(mineId, ActionStatus.InProgress, ActionStatus.Done, ActionOutcome.Remediated, null);
            return true;
        }, owner);
        var (teamDone, _) = await Action(team, $"{token} team's", ActionStatus.Done, 1);
        await Action(await Team(), $"{token} elsewhere", ActionStatus.Done, 1);

        foreach (var (view, want) in new[] { ("mine", new[] { mineDone }), ("myteams", new[] { mineDone, teamDone }) })
        {
            await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view={view}&layout=board&q={token}", @as: owner);
            var html = WebUtility.HtmlDecode(await r.HtmlOnce("tide-kstrip"));
            var href = ChipHref(html, ActionStatus.Done);
            await using var list = await RenderAsync<ActionsBoard>(app, "/" + href, @as: owner);
            await list.HtmlOnce(token);
            list.Component.Rows.Select(x => x.Number).Should().BeEquivalentTo(want, view);
        }
    }

    [Fact]
    public async Task AViewerFollowingAChip_SeesWhatTheListAlwaysShowsThem_NothingMore()
    {
        var token = $"bclv{Guid.NewGuid():N}"[..13];
        var team = await Team();
        var (done, _) = await Action(team, $"{token} done", ActionStatus.Done, 1);
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=open&layout=board&q={token}", @as: Viewer);
        var href = ChipHref(WebUtility.HtmlDecode(await r.HtmlOnce("tide-kstrip")), ActionStatus.Done);
        await using var list = await RenderAsync<ActionsBoard>(app, "/" + href, @as: Viewer);
        var html = WebUtility.HtmlDecode(await list.HtmlOnce(token));
        list.Component.Rows.Select(x => x.Number).Should().Equal(done);
        html.Should().NotContain("tide-rail-reopen", "a Viewer moves nothing");
    }

    [Fact]
    public async Task AClosedRowsRail_OffersReopen_WhichMovesItBackToOpen_KeepingItsClosingInTheHistory()
    {
        var owner = Owner();
        var token = $"bclr{Guid.NewGuid():N}"[..13];
        var team = await Team(owner);
        var (number, _) = await Action(team, $"{token} closed", ActionStatus.Done, 1, note: "Patched on both gateways.");

        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=closed&layout=list&q={token}", @as: owner);
        var rail = WebUtility.HtmlDecode(await r.HtmlOnce("tide-rail-reopen"));
        rail.Should().MatchRegex($"<button[^>]*class=\"[^\"]*tide-rail-reopen[^\"]*\"[^>]*aria-label=\"Reopen A-{number}\"");
        var row = r.Component.Rows.Single();

        await r.Click(p => p.Reopen(row));
        var after = await Detail(number);
        after.Action.Status.Should().Be(ActionStatus.Open);
        after.Action.ClosedAtUtc.Should().BeNull();
        var history = after.Action.Events.Select(e => e.Text).ToList();
        history.Should().Contain(t => t.Contains("Status: Open → Done (", StringComparison.Ordinal) && t.Contains("Patched on both gateways.", StringComparison.Ordinal),
            "the closing note and outcome stay in its history");
        history.Should().Contain("Status: Done → Open.");
        await using var scope = SignedIn(app, SignIn.Analyst);
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        var key = after.Action.Id.ToString();
        (await db.AuditLog.CountAsync(e => e.EntityType == nameof(ActionItem) && e.Action == "Modified" && e.EntityKey.Contains(key))).Should().BePositive("the move is audited");
    }

    [Fact]
    public async Task Reopen_IsOfferedOnlyToWhoeverMayMoveIt_AndRefusedWithout()
    {
        var token = $"bcln{Guid.NewGuid():N}"[..13];
        var team = await Team();
        var (number, id) = await Action(team, $"{token} closed", ActionStatus.Cancelled, 1);
        var outsider = Owner();   // works actions, but not on this team

        foreach (var who in new[] { Viewer, outsider })
        {
            await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=closed&layout=list&q={token}", @as: who);
            var html = WebUtility.HtmlDecode(await r.HtmlOnce($"About A-{number}"));
            html.Should().NotContain("tide-rail-reopen", who.Name);
            await r.Click(p => p.Reopen(r.Component.Rows.Single()));
            (await Detail(number)).Action.Status.Should().Be(ActionStatus.Cancelled, $"{who.Name} may not move it");
        }
        var refused = async () => await As(async sp =>
        {
            await sp.GetRequiredService<ActionService>().MoveAsync(id, ActionStatus.Cancelled, ActionStatus.Open);
            return true;
        }, outsider);
        await refused.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Reopen_IsRefused_WhenTheActionChangedSinceTheListWasRead()
    {
        var owner = Owner();
        var token = $"bclc{Guid.NewGuid():N}"[..13];
        var team = await Team(owner);
        var (number, id) = await Action(team, $"{token} closed", ActionStatus.NotApplicable, 1);
        await using var r = await RenderAsync<ActionsBoard>(app, $"/actions?view=closed&layout=list&q={token}", @as: owner);
        await r.HtmlOnce("tide-rail-reopen");
        var row = r.Component.Rows.Single();

        // Meanwhile a colleague reopens it and closes it another way.
        await As(async sp =>
        {
            var actions = sp.GetRequiredService<ActionService>();
            await actions.MoveAsync(id, ActionStatus.NotApplicable, ActionStatus.Open);
            await actions.MoveAsync(id, ActionStatus.Open, ActionStatus.Cancelled);
            return true;
        });
        await r.Click(p => p.Reopen(row));
        (await Detail(number)).Action.Status.Should().Be(ActionStatus.Cancelled, "the stale reopen is refused, not saved over the change");
        r.Scope.ServiceProvider.GetRequiredService<Tide.Web.Services.ToastService>().Toasts.Select(t => t.Message)
            .Should().Contain($"Someone else moved A-{number} to Cancelled in the meantime.");
    }

    [GeneratedRegex("href=\"([^\"]+)\"")]
    private static partial Regex Href();
}
