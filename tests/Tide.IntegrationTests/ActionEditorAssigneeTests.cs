using System.Net;
using AwesomeAssertions;
using Tide.Application.Actions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Domain.Entities;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>The action editor's assignee picker offers the team's members only; a holder from before the rule, not on the
/// team, is still shown as the choice it has; an external team offers nobody.</summary>
public class ActionEditorAssigneeTests(TideApp app) : IClassFixture<TideApp>
{
    private static Team TeamOf(string name, bool external = false, params (string Id, string Name)[] members)
    {
        var team = new Team { Id = Guid.NewGuid(), Name = name, IsExternal = external };
        foreach (var (id, who) in members) team.Members.Add(new TeamMember { TeamId = team.Id, UserId = id, DisplayName = who });
        return team;
    }

    private async Task<string> Picker(ActionForm form, params Team[] teams)
    {
        await using var editor = await ComponentHarness.RenderAsync<ActionEditor>(app, "/actions/new",
            new() { ["Form"] = form, ["Teams"] = teams, ["IsNew"] = form.Id is null });
        var html = WebUtility.HtmlDecode(await editor.Html());
        var at = html.IndexOf("id=\"a-assignee\"", StringComparison.Ordinal);
        return html[at..html.IndexOf("</select>", at, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task ItOffersTheTeamsMembers_AndNobodyElse()
    {
        var team = TeamOf("Cyber Defense", members: [("dev:robin", "Robin"), ("dev:alex", "Alex")]);
        var picker = await Picker(new ActionForm { Title = "Sweep", TeamId = team.Id }, team);
        picker.Should().Contain("<optgroup label=\"Cyber Defense members\">").And.Contain(">Robin</option>").And.Contain(">Alex</option>")
            .And.NotContain("Everyone else").And.NotContain("label=\"People\"").And.NotContain(">Tester</option>", "the analyst isn't on the team");
    }

    [Fact]
    public async Task TheNewActionPage_OffersAnAnalystNoOneButTheTeamsMembers()
    {
        // An analyst used to be offered everyone known, after the team's members.
        var html = WebUtility.HtmlDecode(await app.CreateClient().GetStringAsync("/actions/new"));
        var at = html.IndexOf("id=\"a-assignee\"", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, "the page draws the editor");
        var picker = html[at..html.IndexOf("</select>", at, StringComparison.Ordinal)];
        picker.Should().NotContain("Everyone else").And.NotContain("label=\"People\"");
    }

    [Fact]
    public async Task TheActionPage_OffersTakeIt_OnlyToAMemberOfTheTeam()
    {
        const string devUser = "S-1-5-21-DEV-1001";   // the dev sign-in the app's client uses: an analyst
        Guid team;
        int number;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            team = await scope.ServiceProvider.GetRequiredService<TeamService>().AddAsync("Take it page team " + Guid.NewGuid().ToString("N")[..6], null, null);
            number = await scope.ServiceProvider.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = "Unowned sweep", TeamId = team });
        }

        var page = await app.CreateClient().GetStringAsync($"/actions/{number}");
        page.Should().Contain("Unowned sweep").And.NotContain(">Take it<", "an analyst not on the team doesn't take it on")
            .And.NotContain("Take it.", "nor does the Next line say to");

        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<TeamService>().AddMemberAsync(team, devUser, "Dev Analyst");
        (await app.CreateClient().GetStringAsync($"/actions/{number}")).Should().Contain("Take it", "on the team, they may");
    }

    [Fact]
    public async Task AHolderNotOnTheTeam_IsStillShownAsTheChoice_SoSavingKeepsThem()
    {
        var team = TeamOf("Cyber Defense", members: [("dev:robin", "Robin")]);
        var picker = await Picker(new ActionForm { Id = Guid.NewGuid(), Title = "Sweep", TeamId = team.Id, AssigneeId = "dev:outsider", AssigneeName = "Outsider" }, team);
        picker.Should().Contain("value=\"dev:outsider\"").And.Contain(">Outsider</option>").And.Contain(">Robin</option>");
    }

    [Fact]
    public async Task AnExternalTeam_OffersNobody()
    {
        var team = TeamOf("Managed SOC", external: true);
        var picker = await Picker(new ActionForm { Title = "Block", TeamId = team.Id }, team);
        picker.Should().Contain("None: an external team").And.NotContain("<optgroup");
    }
}
