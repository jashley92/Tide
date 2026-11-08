using System.Text;
using AwesomeAssertions;
using Tide.Application.Adversaries;
using Tide.Application.Indicators;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>Bug pass 8: a hand-made profile and MITRE's groups, ranges in indicators, and the rota's member ids.</summary>
public class BugPass8Tests : IDisposable
{
    private readonly TestDb _db = new();

    public BugPass8Tests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AHandMadeProfile_IsMatchedByItsOwnName_NotByAnotherGroupsAlias()
    {
        // I8-1: the analyst's Lazarus Group lists Andariel among its other names; MITRE's Andariel comes first.
        await _db.Adversaries().SaveAsync(new AdversaryForm { Name = "Lazarus Group", Aliases = "HIDDEN COBRA; Andariel" });
        var bundle = """
            { "type": "bundle", "objects": [
              { "type": "intrusion-set", "name": "Andariel", "aliases": ["Andariel", "Silent Chollima"], "external_references": [ { "source_name": "mitre-attack", "external_id": "G0138" } ] },
              { "type": "intrusion-set", "name": "Lazarus Group", "aliases": ["Lazarus Group", "HIDDEN COBRA"], "external_references": [ { "source_name": "mitre-attack", "external_id": "G0032" } ] } ] }
            """;

        await _db.Adversaries().ImportMitreAsync(new MemoryStream(Encoding.UTF8.GetBytes(bundle)));

        var all = (await _db.Adversaries().ListAsync()).Select(r => r.Adversary).ToList();
        all.Where(a => a.Name == "Lazarus Group").Should().ContainSingle().Which.MitreId.Should().Be("G0032");
        all.Single(a => a.Name == "Andariel").MitreId.Should().Be("G0138");
    }

    [Theory]
    [InlineData("blocked 10.0.0.0/8 at the edge")]
    [InlineData("the 185.220.101.0/24 range")]
    [InlineData("link-local fe80::1/64")]
    public void ANetworkRange_IsNotTakenForAnAddress(string text) =>
        IocExtractor.Extract(text).Indicators.Should().BeEmpty();

    [Fact]
    public async Task ARotaMember_AddedWithAStraySpace_IsTheSamePerson()
    {
        await _db.Rotation().AddMemberAsync("dev:robin", "Robin");
        await _db.Rotation().AddMemberAsync(" dev:robin ", "Robin");

        (await _db.Rotation().MembersAsync()).Should().ContainSingle(m => m.UserId == "dev:robin");
    }
}
