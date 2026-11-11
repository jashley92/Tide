using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

public class BoardDragsTests
{
    private static readonly Guid MyTab = Guid.NewGuid(), RobinsTab = Guid.NewGuid(), SamsTab = Guid.NewGuid();
    private static readonly Guid Card = Guid.NewGuid(), OtherCard = Guid.NewGuid();

    [Fact]
    public void Others_AreTheCardsOtherPeopleHold_AndTheColumnTheyreOver()
    {
        var drags = new BoardDrags(TimeProvider.System);
        drags.Report(MyTab, "c-me", OtherCard, "S-ME", "Me", null);
        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", ActionStatus.InProgress);

        drags.Others("S-ME").Should().ContainSingle()
            .Which.Should().Match<BoardDrag>(d => d.ActionId == Card && d.Name == "Robin" && d.Over == ActionStatus.InProgress);
        drags.Others("S-ROBIN").Single().ActionId.Should().Be(OtherCard);
    }

    [Fact]
    public void Report_SaysSoOnlyWhenTheCardOrColumnChanges()
    {
        var drags = new BoardDrags(TimeProvider.System);
        var heard = 0;
        drags.Changed += _ => heard++;

        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", null);
        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", null);
        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", ActionStatus.Blocked);
        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", ActionStatus.Blocked);

        heard.Should().Be(2, "picking it up and crossing into Blocked; the repeats are no news");
        drags.Others("S-ME").Single().Over.Should().Be(ActionStatus.Blocked);
    }

    [Fact]
    public void End_LetsGo_OfThatTabsCardOnly()
    {
        var drags = new BoardDrags(TimeProvider.System);
        var heard = 0;
        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", null);
        drags.Report(SamsTab, "c-sam", OtherCard, "S-SAM", "Sam", null);
        drags.Changed += _ => heard++;

        drags.End(RobinsTab);
        drags.End(RobinsTab);   // let go twice: once

        drags.Others("S-ME").Select(d => d.Name).Should().Equal("Sam");
        heard.Should().Be(1);
    }

    [Fact]
    public void ATabThatClosesMidDrag_LetsGo_WithItsCircuit()
    {
        var drags = new BoardDrags(TimeProvider.System);
        var circuit = new PresenceCircuit(new Presence(), drags);
        circuit.Opened("c-robin");
        drags.Report(RobinsTab, circuit.CircuitId, Card, "S-ROBIN", "Robin", ActionStatus.Done);
        drags.Report(SamsTab, "c-sam", OtherCard, "S-SAM", "Sam", null);

        circuit.Closed("c-robin");
        drags.RemoveCircuit("");   // a prerender's empty id clears nothing

        drags.Others("S-ME").Select(d => d.Name).Should().Equal("Sam");
    }

    [Fact]
    public void ADragNotHeardFromInAWhile_StopsShowing()
    {
        var clock = new TestClock(new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero));
        var drags = new BoardDrags(clock);
        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", null);

        clock.Now += BoardDrags.Stale - TimeSpan.FromSeconds(1);
        drags.Others("S-ME").Should().ContainSingle();
        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", ActionStatus.Blocked);   // word from the board
        clock.Now += TimeSpan.FromSeconds(2);
        drags.Others("S-ME").Should().ContainSingle("it was heard from a moment ago");
        clock.Now += BoardDrags.Stale;
        drags.Others("S-ME").Should().BeEmpty("its end must have been lost");
    }
}

/// <summary>
/// The board's four working lanes, then a strip under them: "Closed, last 7 days", with a chip for each way of closing
/// (Done, Cancelled, Not applicable) saying how many closed that way. Closed cards aren't drawn, only counted. Each chip is
/// a place to drop a card: its own drop target (board.js drops on the nearest lane or chip), which a colleague's board
/// lights as the card is carried over it, and which asks for the outcome as a drop on a closed status always has. The
/// Move menu, the keyboard's way, still offers all three. A card says its article, KEV flag, checklist progress, how its
/// hunt went (a sweep or hunt) and how long it has been in its lane.
/// </summary>
public class BoardClosedStripTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Robin = new("dev:board-robin", "Robin", "Analyst");

    private async Task<(Guid Id, int Number, ActionBoard Board)> OpenCard()
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var team = await sp.GetRequiredService<Tide.Application.Actions.TeamService>().AddAsync($"Board closed {Guid.NewGuid():N}", null, null);
        var actions = sp.GetRequiredService<ActionService>();
        var number = await actions.CreateAsync(new ActionForm { Title = "Board closed drop", TeamId = team });
        var id = (await actions.GetAsync(number))!.Action.Id;
        return (id, number, await actions.BoardAsync(new ActionFilter(ActionView.Open) { Search = "A-" + number }));
    }

    private static readonly ActionViewCounts Closed = ActionViewCounts.None with { BoardDone = 4, BoardCancelled = 1, BoardNotApplicable = 0 };

    private Task<Rendered<ActionKanban>> Render(ActionBoard board, int closedDays = ActionService.BoardClosedDays) =>
        RenderAsync<ActionKanban>(app, "/actions?layout=board", new()
        {
            ["Board"] = board, ["Today"] = DateOnly.FromDateTime(DateTime.UtcNow), ["Counts"] = Closed, ["ClosedDays"] = closedDays,
        });

    [Fact]
    public async Task FourLanes_ThenAClosedStrip_WithAChipCountingEachWayOfClosing()
    {
        var (_, _, board) = await OpenCard();
        await using var r = await Render(board);
        var html = WebUtility.HtmlDecode(await r.Html());
        var strip = html.IndexOf("class=\"tide-kstrip\"", StringComparison.Ordinal);
        strip.Should().BePositive("the closed statuses are a strip under the lanes");
        foreach (var s in new[] { ActionStatus.Open, ActionStatus.InProgress, ActionStatus.Blocked, ActionStatus.Tracking })
            html.IndexOf($"<section class=\"tide-kcol\" data-status=\"{s}\"", StringComparison.Ordinal).Should().BeInRange(0, strip, $"{s} is a lane, above the strip");
        Regex.Count(html[..strip], "class=\"tide-kcol-drop\"").Should().Be(4, "every lane has a place to drop under its cards");
        html.Should().NotContain("data-status=\"Done\" aria-labelledby", "no lane for a closed status");

        var closed = html[strip..];
        closed.Should().Contain("Closed, last 7 days").And.Contain("Drop a card on one to close it that way").And.Contain("Show the last 30 days");
        foreach (var (status, name, n) in new[] { (ActionStatus.Done, "Done", 4), (ActionStatus.Cancelled, "Cancelled", 1), (ActionStatus.NotApplicable, "Not applicable", 0) })
            closed.Should().MatchRegex($"<div class=\"tide-kchip[^\"]*\" data-status=\"{status}\"[^>]*aria-label=\"{name}: {n} closed in the last 7 days\"",
                $"{name} is a drop target saying its count");
    }

    [Fact]
    public async Task AClosedCard_IsCounted_NotDrawn()
    {
        var done = new ActionRow(Guid.NewGuid(), 9102, "Already done", ActionKind.Other, Priority.P3, Guid.NewGuid(), "SOC", null, null, null,
            ActionStatus.Done, ActionOutcome.None, DateOnly.FromDateTime(DateTime.UtcNow), null, null, null, 0, DateTimeOffset.UtcNow);
        await using var r = await Render(new ActionBoard([done], true, new HashSet<Guid>()));
        (await r.Html()).Should().NotContain("Already done");
    }

    [Fact]
    public async Task ShowTheLast30Days_AsksThePageToCountFurtherBack_AndThenGoes()
    {
        var older = 0;
        var (_, _, board) = await OpenCard();
        await using (var r = await RenderAsync<ActionKanban>(app, "/actions?layout=board", new()
        {
            ["Board"] = board, ["Today"] = DateOnly.FromDateTime(DateTime.UtcNow), ["Counts"] = Closed,
            ["OnShowOlder"] = EventCallback.Factory.Create(new object(), () => older++),
        }))
        {
            await r.Click(k => (Task)typeof(ActionKanban).GetMethod("ShowOlder", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(k, null)!);
            older.Should().Be(1, "the page counts again, further back");
        }
        await using var month = await Render(board, ActionService.BoardOlderDays);
        var html = WebUtility.HtmlDecode(await month.Html());
        html.Should().Contain("Closed, last 30 days").And.NotContain("Show the last 30 days");
        html.Should().Contain("aria-label=\"Done: 4 closed in the last 30 days\"");
    }

    [Theory]
    [InlineData(ActionStatus.Done, "Done")]
    [InlineData(ActionStatus.Cancelled, "Cancelled")]
    [InlineData(ActionStatus.NotApplicable, "Not applicable")]
    public async Task ACardDroppedOnEachChip_AsksForItsOutcome_AsBefore(ActionStatus to, string name)
    {
        var (id, number, board) = await OpenCard();
        await using var r = await Render(board);

        // Carried over it: everyone else's board says where it's going.
        await r.Click(k => { k.OnDragging(id.ToString(), to.ToString()); return Task.CompletedTask; });
        var drags = r.Scope.ServiceProvider.GetRequiredService<BoardDrags>();
        drags.Others(Robin.UserId).Should().Contain(d => d.ActionId == id && d.Over == to);
        await r.Click(k => { k.OnDragEnded(); return Task.CompletedTask; });

        await r.Press(k => k.OnCardDropped(id.ToString(), nameof(ActionStatus.Open), to.ToString()));
        WebUtility.HtmlDecode(await r.HtmlOnce($"Move to {name}")).Should().Contain($"Move A-{number} to {name}");
    }

    [Fact]
    public async Task AChip_LightsWhileAColleagueCarriesACardOverIt()
    {
        var (id, _, board) = await OpenCard();
        await using var r = await Render(board);
        var drags = r.Scope.ServiceProvider.GetRequiredService<BoardDrags>();
        drags.Report(Guid.NewGuid(), "robin-circuit", id, Robin.UserId, Robin.Name, ActionStatus.Cancelled);
        try
        {
            var html = WebUtility.HtmlDecode(await r.HtmlOnce("is moving a card here"));
            html.Should().MatchRegex("<div class=\"tide-kchip tide-kcol-incoming\" data-status=\"Cancelled\"");
            html.Should().Contain("title=\"Robin is moving a card here\"");
        }
        finally { drags.RemoveCircuit("robin-circuit"); }
    }

    [Fact]
    public async Task TheMoveMenu_StillOffersEachClosedStatus()
    {
        var (id, _, board) = await OpenCard();
        await using var r = await Render(board);
        await r.Click(k =>
        {
            typeof(ActionKanban).GetMethod("ToggleMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(k, [id]);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(k, null);
            return Task.CompletedTask;
        });
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("role=\"menu\""));
        foreach (var name in new[] { "In progress", "Blocked", "Tracking", "Done", "Cancelled", "Not applicable" })
            html.Should().MatchRegex($"role=\"menuitem\"[^>]*>{name}</button>");
    }

    [Fact]
    public async Task ACard_SaysItsArticle_ItsKevFlag_ItsProgress_AndHowLongItsBeenInItsColumn()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var card = new ActionRow(Guid.NewGuid(), 9101, "Patch the edge VPN", ActionKind.PatchRemediate, Priority.P1, Guid.NewGuid(), "Network",
            null, null, null, ActionStatus.InProgress, ActionOutcome.None, null, Guid.NewGuid(), "Edge VPN zero-day exploited in the wild", null, 0,
            DateTimeOffset.UtcNow.AddDays(-10), ChecklistDone: 1, ChecklistTotal: 4, ArticleKev: true, InStatusSinceUtc: DateTimeOffset.UtcNow.AddDays(-3));
        await using var r = await RenderAsync<ActionKanban>(app, "/actions?layout=board", new()
        {
            ["Board"] = new ActionBoard([card], false, new HashSet<Guid>()), ["Today"] = today,
        });
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("class=\"tide-kcard-article\"").And.Contain("Edge VPN zero-day exploited in the wild");
        html.Should().Contain("class=\"tide-kev\"").And.Contain(">KEV<");
        html.Should().Contain("1/4");
        html.Should().MatchRegex("In column [34]d");
        html.Should().Contain("title=\"In progress since ").And.NotContain("In In progress", "the status names itself: \"In progress since\", \"Blocked since\"");
        html.Should().NotContain("Hunt", "a patch has no hunt");
    }

    [Theory]
    [InlineData(ActionKind.IocSweep, null, "Hunt not run")]
    [InlineData(ActionKind.IocSweep, HuntResult.NotRun, "Hunt not run")]
    [InlineData(ActionKind.ThreatHunt, HuntResult.NoHits, "Hunt: no hits")]
    [InlineData(ActionKind.IocSweep, HuntResult.Hits, "Hunt: hits")]
    public async Task ASweepOrHuntCard_SaysHowItsHuntWent_BesideItsChecklist(ActionKind kind, HuntResult? hunted, string says)
    {
        var card = new ActionRow(Guid.NewGuid(), 9103, "Sweep the logs", kind, Priority.P1, Guid.NewGuid(), "SOC", null, null, null,
            ActionStatus.Open, ActionOutcome.None, null, null, null, null, 0, DateTimeOffset.UtcNow, ChecklistDone: 1, ChecklistTotal: 7, Hunted: hunted);
        await using var r = await RenderAsync<ActionKanban>(app, "/actions?layout=board", new()
        {
            ["Board"] = new ActionBoard([card], false, new HashSet<Guid>()), ["Today"] = DateOnly.FromDateTime(DateTime.UtcNow),
        });
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain($">{says}<").And.Contain("1/7", "the checklist's progress stays");
    }
}
