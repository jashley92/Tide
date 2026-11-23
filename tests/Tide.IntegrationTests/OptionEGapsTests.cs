using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Org;
using Tide.Application.Settings;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Small gaps between the pages and their Option E designs: Triage's order and present mode's edition are the segmented
/// switch used everywhere else for one of a few; present mode's footer shows the time gone against the meeting's length
/// (an organization setting); and the Actions board's header is New action alone.
/// </summary>
public class OptionEGapsTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TriagesOrder_IsTheSegmentedSwitch_WithTheChoicePressed()
    {
        await using var triage = await RenderAsync<Triage>(app, "/triage");
        var html = await triage.Html();
        const string group = "<div class=\"tide-seg tide-seg-tool\" role=\"group\" aria-label=\"Order\">";
        html.Should().Contain(group);
        var order = html[html.IndexOf(group, StringComparison.Ordinal)..];
        order = order[..order.IndexOf("</div>", StringComparison.Ordinal)];
        order.Should().Contain("class=\"tide-seg-opt on\" aria-pressed=\"true\"").And.Contain("Newest").And.Contain("Likely used")
            .And.NotContain("btn-group").And.NotContain("btn-secondary");
    }

    [Fact]
    public async Task PresentMode_ShowsTheEditionAsASwitch_AndTheTimeAgainstTheMeetingsLength()
    {
        var day = await TwoArticleBrief(app, 3, "Option E gaps");
        await using var r = await Open(app, day);
        var html = await r.Html();
        html.Should().Contain("<div class=\"tide-seg\" role=\"group\" aria-label=\"Edition shown\">")
            .And.NotContain("<select class=\"form-select form-select-sm\" aria-label=\"Edition shown\"");
        html.Should().MatchRegex(@"<span>\d+ of 30 min</span>", "the default meeting is 30 minutes");

        await Do(r, p => p.SetEdition(new() { Value = "Public" }));
        (await r.Html()).Should().Contain("class=\"tide-seg-opt on\" aria-pressed=\"true\">Company-wide</button>");
    }

    [Fact]
    public async Task TheMeetingsLength_IsAnOrganizationSetting_FiveTo180Minutes()
    {
        OperationalSettings.Problem(OperationalSettings.MeetingMinutes, "4").Should().NotBeNull("5 to 180");
        OperationalSettings.Problem(OperationalSettings.MeetingMinutes, "181").Should().NotBeNull();
        OperationalSettings.Problem(OperationalSettings.MeetingMinutes, "45").Should().BeNull();
        app.Services.GetRequiredService<OrgClock>().MeetingMinutes.Should().Be(OrgOptions.DefaultMeetingMinutes);

        await using (var admin = SignedIn(app, SignIn.Analyst))
            await admin.ServiceProvider.GetRequiredService<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?>
            {
                [OperationalSettings.MeetingMinutes] = "45",
            });
        try
        {
            app.Services.GetRequiredService<OrgClock>().MeetingMinutes.Should().Be(45, "it takes effect at once");
            var day = await TwoArticleBrief(app, 4, "Option E gaps, a longer meeting");
            await using var r = await Open(app, day);
            (await r.Html()).Should().MatchRegex(@"<span>\d+ of 45 min</span>");
        }
        finally
        {
            await using var admin = SignedIn(app, SignIn.Analyst);
            await admin.ServiceProvider.GetRequiredService<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?>
            {
                [OperationalSettings.MeetingMinutes] = OrgOptions.DefaultMeetingMinutes.ToString(CultureInfo.InvariantCulture),
            });
        }
    }

    [Fact]
    public async Task TheBoardsHeader_IsNewActionAlone_TheListKeepsThePagesBesideIt()
    {
        await using (var board = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=board"))
        {
            var head = Head(await board.Html());
            head.Should().Contain("href=\"actions/new\"").And.NotContain("href=\"workload\"").And.NotContain("href=\"library/board-rules\"");
        }
        await using var list = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list");
        Head(await list.Html()).Should().Contain("href=\"workload\"").And.Contain("href=\"library/board-rules\"").And.Contain("href=\"actions/new\"");

        static string Head(string html)
        {
            var at = html.IndexOf("<div class=\"tide-page-head\">", StringComparison.Ordinal);
            return html[at..html.IndexOf("aria-label=\"Views\"", at, StringComparison.Ordinal)];
        }
    }
}
