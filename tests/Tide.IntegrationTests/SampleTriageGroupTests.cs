using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Intake;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The development samples' triage inbox: the two reports of the edge VPN ransomware story come from two outlets, so
/// triage offers them as one event, and that event is covered by the briefed VPN article, so the whole group can be
/// attached to it in one step.
/// </summary>
public class SampleTriageGroupTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TheSampleRansomwarePair_IsOneCoveredGroup()
    {
        app.CreateClient().Dispose();   // starts the app, which seeds the samples
        await using var scope = SignedIn(app, SignIn.Analyst);
        var inbox = await scope.ServiceProvider.GetRequiredService<IntakeService>().InboxAsync(new InboxFilter());

        var first = inbox.Single(r => r.Item.Title.StartsWith("Ransomware gang exploits edge VPN", StringComparison.Ordinal));
        var again = inbox.Single(r => r.Item.Title.StartsWith("Manufacturers hit as ransomware gang", StringComparison.Ordinal));
        again.Source.Id.Should().NotBe(first.Source.Id, "the second report is another outlet's");
        again.Group.Should().NotBeNull("two outlets' reports of one event are offered as one group");
        again.Group!.Ids.Should().BeEquivalentTo([first.Item.Id, again.Item.Id]);
        first.Group.Should().Be(again.Group);
        first.Coverage.Should().Contain(c => c.Attachable, "the group is covered by the briefed article, so it can be attached as one");
    }
}
