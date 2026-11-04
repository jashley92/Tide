using System.Text;
using FluentAssertions;
using Tide.Application.Adversaries;
using Tide.Application.Indicators;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>Bug pass 9: MITRE's groups matched to profiles whatever the order, and a range told from a host and path.</summary>
public class BugPass9Tests : IDisposable
{
    private readonly TestDb _db = new();

    public BugPass9Tests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private Task Import(string objects) =>
        _db.Adversaries().ImportMitreAsync(new MemoryStream(Encoding.UTF8.GetBytes($$"""{ "type": "bundle", "objects": [ {{objects}} ] }""")));

    private static string Group(string name, string id, params string[] aliases) =>
        $$"""{ "type": "intrusion-set", "name": "{{name}}", "aliases": [{{string.Join(", ", aliases.Prepend(name).Select(a => $"\"{a}\""))}}], "external_references": [ { "source_name": "mitre-attack", "external_id": "{{id}}" } ] }""";

    [Fact]
    public async Task AProfileListingMitresNameAmongItsOtherNames_GetsThatGroup()
    {
        // I9-1: the analyst's "Sandworm" lists "Sandworm Team", MITRE's name for it.
        await _db.Adversaries().SaveAsync(new AdversaryForm { Name = "Sandworm", Aliases = "Sandworm Team; Voodoo Bear" });

        await Import(Group("Sandworm Team", "G0034", "ELECTRUM", "Voodoo Bear"));

        var all = (await _db.Adversaries().ListAsync()).Select(r => r.Adversary).ToList();
        all.Should().ContainSingle().Which.Should().Match<Tide.Domain.Entities.Adversary>(a => a.Name == "Sandworm" && a.MitreId == "G0034");
    }

    [Fact]
    public async Task WhichProfileAGroupTakes_DoesntDependOnTheBundlesOrder()
    {
        // I9-2: Lotus Blossom comes first and lists Thrip's name; the analyst's "Thrip" is still Thrip.
        await _db.Adversaries().SaveAsync(new AdversaryForm { Name = "Thrip" });

        await Import(Group("Lotus Blossom", "G0030", "Spring Dragon", "Thrip") + ", " + Group("Thrip", "G0076"));

        var all = (await _db.Adversaries().ListAsync()).Select(r => r.Adversary).ToList();
        all.Single(a => a.Name == "Thrip").MitreId.Should().Be("G0076");
        all.Single(a => a.Name == "Lotus Blossom").MitreId.Should().Be("G0030");
    }

    [Theory]
    [InlineData("payload at evil[.]com/24", "evil.com")]
    [InlineData("served from evil[.]com/404", "evil.com")]
    [InlineData("C2 at 45.77.12[.]3:8443/1", "45.77.12.3")]
    public void AHostWithAShortPath_IsStillTakenForItsHost(string text, string host) =>
        IocExtractor.Extract(text).Indicators.Select(i => i.Value).Should().Contain(host);
}
