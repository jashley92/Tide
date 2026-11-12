using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Briefs;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Present mode's leftovers: a refused choice in the Assign and "Hand off to…" selects is drawn afresh, and a follower's
/// quick-add title stays with the article it was typed for when the presenter moves the slide.
/// <para>What the browser shows in a select after a refusal can't be read from the HTML these tests render: the markup
/// was always the server's value, and the bug was that, the value not having changed, Blazor sent the browser nothing,
/// so the select kept the name picked. The fix redraws the select (a new <c>@key</c>, as the due box beside it does);
/// these tests check that the key moves on a refusal and stays put otherwise.</para>
/// </summary>
public class PresentLeftoverTests(MeetingClockApp app) : IClassFixture<MeetingClockApp>
{
    private static readonly SignIn Robin = new("dev:leftover-robin", "Robin Analyst", "Analyst");

    private async Task<Guid> ArticleOn(DateOnly day, int which)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var brief = await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(day);
        return brief!.Items.OrderBy(i => i.SortOrder).ElementAt(which).ArticleId;
    }

    [Fact]
    public async Task ARefusedAssignee_RedrawsThatActionsSelect_AndOnlyThatOne()
    {
        var day = await TwoArticleBrief(app, 171, "Leftover assign");
        Guid team, id;
        int number;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var teams = scope.ServiceProvider.GetRequiredService<TeamService>();
            team = await teams.AddAsync("Leftover team " + Guid.NewGuid().ToString("N")[..6], null, null);
            await teams.AddMemberAsync(team, "u:leftover-sam", "Sam");
            var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
            number = await actions.CreateAsync(new ActionForm { Title = "Block the domain", Kind = ActionKind.IocSweep, TeamId = team });
            id = (await actions.GetAsync(number))!.Action.Id;
        }
        await using var r = await Open(app, day);
        var row = new EditionAction(number, "Block the domain", "Team", null, ActionStatus.Open, id, team);

        // Someone who left the team a moment ago, still in the page's list: refused, and the select drawn again.
        await r.Click(p => p.Assign(row, "u:leftover-gone"));
        r.Component.AssignResets(id).Should().Be(1, "the select is drawn afresh, showing who holds it");

        await r.Click(p => p.Assign(row, "u:leftover-sam"));
        r.Component.AssignResets(id).Should().Be(1, "a choice that was kept is what the select already shows");
    }

    [Fact]
    public async Task ARefusedHandOff_RedrawsTheSelect()
    {
        var day = await TwoArticleBrief(app, 172, "Leftover handoff");
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());

        // Robin left a moment ago (no seat now), still in the presenter's list.
        await Do(pat, p => p.HandOff("dev:leftover-not-here"));
        pat.Component.HandOffResets.Should().Be(1, "the select is drawn afresh, back on Hand off to…");

        await Do(pat, p => p.HandOff(Robin.UserId));
        pat.Component.HandOffResets.Should().Be(1);
        EndMeeting(app, day);
    }

    [Fact]
    public async Task AQuickAddTitle_StaysWithTheArticleItWasTypedFor_WhenTheSlideMoves()
    {
        var day = await TwoArticleBrief(app, 173, "Leftover quick add");
        var (first, second) = (await ArticleOn(day, 0), await ArticleOn(day, 1));
        await using var r = await Open(app, day);

        // Typed (each keystroke, as the box binds) on the first article's slide; then the slide moves on.
        await Do(r, p => p.TypeQuickAddTitle(first, "Sweep the proxy logs"));
        await Do(r, p => p.OnPresentKey("next"));
        r.Component.QuickAddTitle(second).Should().BeNull("the next article's box starts empty");
        await Do(r, p => p.OnPresentKey("prev"));
        r.Component.QuickAddTitle(first).Should().Be("Sweep the proxy logs", "what was typed is still there when they come back");
    }
}
