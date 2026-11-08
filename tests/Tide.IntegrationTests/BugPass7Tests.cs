using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Adversaries;
using Tide.Application.Indicators;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>Bug pass 7: a member leaving a team, and a hunt that searches nothing.</summary>
public class BugPass7Tests : IDisposable
{
    private TestDb _db = new();
    private const string Owner = "S-1-5-21-OWNER";

    public BugPass7Tests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task SomeoneTakenOffATeam_HandsTheirOpenActionsBackToIt(string provider)
    {
        _db = _db.On(provider);
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        await _db.Teams().AddMemberAsync(team, $" {Owner} ", "Owner");
        await _db.Teams().AddMemberAsync(team, Owner, "Owner");   // the same person, so not added twice
        var open = await _db.Actions().CreateAsync(new ActionForm { Title = "Block the domain", Kind = ActionKind.Block, Priority = Priority.P2, TeamId = team, AssigneeId = Owner, AssigneeName = "Owner" });
        var done = await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, Priority = Priority.P3, TeamId = team, AssigneeId = Owner, AssigneeName = "Owner" });
        await using (var ctx = _db.Context())
            await ctx.Actions.Where(a => a.Number == done).ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, ActionStatus.Done));

        (await _db.Teams().RemoveMemberAsync(team, Owner)).Should().Be(1, "only the open one goes back");

        await using var read = _db.Context();
        (await read.TeamMembers.CountAsync(m => m.TeamId == team)).Should().Be(0);
        var actions = await read.Actions.ToDictionaryAsync(a => a.Number);
        actions[open].AssigneeId.Should().BeNull();
        actions[done].AssigneeId.Should().Be(Owner, "a closed action keeps who did it");
        (await read.ActionEvents.Where(e => e.ActionId == actions[open].Id).Select(e => e.Text).ToListAsync())
            .Should().Contain("Back to Cyber Defense, unassigned: Owner left the team.");
    }

    [Fact]
    public async Task MitreGroups_ThatShareAnAlias_StaySeparateProfiles()
    {
        var bundle = """
            { "type": "bundle", "objects": [
              { "type": "intrusion-set", "name": "Saint Bear", "aliases": ["Saint Bear", "UAC-0056"], "external_references": [ { "source_name": "mitre-attack", "external_id": "G1031" } ] },
              { "type": "intrusion-set", "name": "Ember Bear", "aliases": ["Ember Bear", "UAC-0056", "Bleeding Bear"], "external_references": [ { "source_name": "mitre-attack", "external_id": "G1003" } ] } ] }
            """;

        (await _db.Adversaries().ImportMitreAsync(new MemoryStream(Encoding.UTF8.GetBytes(bundle)))).Added.Should().Be(2);

        var all = (await _db.Adversaries().ListAsync()).Select(r => r.Adversary).ToList();
        all.Single(a => a.MitreId == "G1003").Name.Should().Be("Ember Bear");
        all.Single(a => a.MitreId == "G1031").Aliases.Should().NotContain("Bleeding Bear");
    }

    [Theory]
    [InlineData("C2 at 45.77.12[.]3:8443 today", "45.77.12.3")]
    [InlineData("beacons to update-check[.]net:443", "update-check.net")]
    [InlineData("payload from evil[.]com/payload.exe", "evil.com")]
    [InlineData("see hxxp[://]bad[.]ru/x", "http://bad.ru/x")]
    public void ReportsUsualIndicatorForms_AreExtracted(string text, string expected) =>
        IocExtractor.Extract(text).Indicators.Select(i => i.Value).Should().Contain(expected);

    [Fact]
    public void AHuntThatSearchesNothing_ClaimsNoWindow()
    {
        var q = HuntQueryBuilder.Build(QueryDialects.Find("fql")!, [new ArticleIndicator { Type = IndicatorType.Url, Value = "https://evil.example/a" }], days: 30);

        HuntQueryBuilder.WindowOf(q).Should().BeNull();
        q.Should().Contain("with nothing it can search");
    }
}
