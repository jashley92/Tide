using FluentAssertions;
using Tide.Application.Intake;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>Sponsored items on a poll (<c>Intake:SkipSponsored</c>): left out and counted as filtered out, or kept with the switch off.</summary>
public class SponsoredIntakeTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SponsoredLog _log = new();
    private const string FeedUrl = "https://www.bleepingcomputer.com/feed/";

    // As BleepingComputer writes it: the marker is only in dc:creator; the tag and the title look editorial.
    private const string Feed = """
        <rss version="2.0" xmlns:dc="http://purl.org/dc/elements/1.1/"><channel><title>BleepingComputer</title>
          <item>
            <title>From Beginner to Bug Hunter: Learning Cybersecurity in the AI Era.</title>
            <link>https://www.bleepingcomputer.com/news/security/from-beginner-to-bug-hunter-learning-cybersecurity-in-the-ai-era/</link>
            <dc:creator>Sponsored by TryHackMe</dc:creator>
            <category><![CDATA[ Security ]]></category>
            <description><![CDATA[ AI-generated vulnerability reports are overwhelming ... [...] ]]></description>
          </item>
          <item>
            <title>State-sponsored hackers breach telecom giants</title>
            <link>https://www.bleepingcomputer.com/news/security/state-sponsored-hackers-breach-telecom-giants/</link>
            <dc:creator>Bill Toulas</dc:creator>
            <category><![CDATA[ Security ]]></category>
          </item>
          <item>
            <title>[Partner] How MDR cuts dwell time</title>
            <link>https://www.bleepingcomputer.com/news/security/how-mdr-cuts-dwell-time/</link>
            <dc:creator>Lawrence Abrams</dc:creator>
          </item>
        </channel></rss>
        """;

    public SponsoredIntakeTests()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        _db.Http.Set(FeedUrl, Feed);
    }

    public void Dispose() => _db.Dispose();

    private IntakeService Intake(IntakeOptions options) =>
        new(_db, _db.User, _db.Http, _db.Articles(), new StaticOptionsMonitor<IntakeOptions>(options), TimeProvider.System, _log);

    private async Task<(IntakeService Intake, Guid Id)> Source(IntakeOptions options)
    {
        var intake = Intake(options);
        return (intake, await intake.SaveSourceAsync(new SourceForm { Name = "BleepingComputer", Url = FeedUrl, PollMinutes = 30 }));
    }

    private static IntakeOptions On(string? markers = null) => new() { Enabled = true, MinPollMinutes = 5, SponsoredMarkers = markers };

    [Fact]
    public async Task WithTheSwitchOn_TheSponsoredItemIsntTakenIn_AndCountsAsFilteredOut()
    {
        var (intake, id) = await Source(On());

        (await intake.PollNowAsync(id)).Should().BeEquivalentTo(new { New = 2, Seen = 3, FilteredOut = 1, Sponsored = 1 });

        (await intake.InboxAsync(new InboxFilter())).Select(r => r.Item.Title).Should()
            .BeEquivalentTo("State-sponsored hackers breach telecom giants", "[Partner] How MDR cuts dwell time");
        (await intake.SourcesAsync()).Single().LastFilteredOut.Should().Be(1);
        _log.For(id).Sample.Should().Equal(new SponsoredSkip("From Beginner to Bug Hunter: Learning Cybersecurity in the AI Era.", "Sponsored by", "Author"));
    }

    [Fact]
    public async Task AnAdminsOwnMarker_CatchesAnotherConvention()
    {
        var (intake, id) = await Source(On("Sponsored by; [Partner]"));

        (await intake.PollNowAsync(id)).Should().BeEquivalentTo(new { New = 1, FilteredOut = 2, Sponsored = 2 });
        _log.For(id).Sample.Select(s => s.Marker).Should().Equal("Sponsored by", "[Partner]");
    }

    [Fact]
    public async Task WithTheSwitchOff_EverythingIsKept()
    {
        var (intake, id) = await Source(On());
        var off = Intake(new IntakeOptions { Enabled = true, MinPollMinutes = 5, SkipSponsored = false });

        (await off.PollNowAsync(id)).Should().BeEquivalentTo(new { New = 3, FilteredOut = 0, Sponsored = 0 });
        (await intake.SourcesAsync()).Single().LastFilteredOut.Should().Be(0);
        _log.For(id).Count.Should().Be(0);
    }
}
