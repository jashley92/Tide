using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>A meeting's record read back after the meeting, on the brief's own page: who attended, and the notes.</summary>
public class MeetingNotesTests(TideApp app) : IClassFixture<TideApp>
{
    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private static DateOnly BusinessDaysBefore(DateOnly day, int n)
    {
        for (var i = 0; i < n; i++) day = WorkingDays.Weekdays.Previous(day);
        return day;
    }

    private async Task<DateOnly> BriefWithNotes(int daysBack, params string[] notes)
    {
        var day = BusinessDaysBefore(Today, daysBack);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var meetings = scope.ServiceProvider.GetRequiredService<MeetingService>();
        var brief = await briefs.GetOrStartAsync(day);
        foreach (var note in notes) await meetings.AddNoteAsync(brief.Id, null, note);
        if (notes.Length > 0)
        {
            await meetings.JoinedAsync(brief.Id);
            await meetings.AddAttendeeAsync(brief.Id, "Sam from the SOC");
        }
        return day;
    }

    [Theory]
    [InlineData("/articles?page=abc")]
    [InlineData("/articles?page=99999999999")]
    [InlineData("/articles?page=2147483647")]
    [InlineData("/admin/system/audit?page=abc")]
    public async Task AListPageNumberThatIsntOne_ShowsAPage_NotAnError(string path)
    {
        var res = await app.CreateClient().GetAsync(path);
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, path);
        (await res.Content.ReadAsStringAsync()).Should().NotContain("Something went wrong on this page");
    }

    [Fact]
    public async Task TheBriefPage_ShowsWhoAttended_AndEachNoteByItsAuthor_AndNothingWhenThereAreNone()
    {
        var day = await BriefWithNotes(43, "Takeaway: patch the VPN\nand the jump hosts", "Follow-up: ask the SOC lead about the new detections");
        var html = await app.CreateClient().GetStringAsync(Ui.BriefUrl(day));
        html.Should().Contain("Attended</h3>").And.Contain("Sam from the SOC").And.Contain("Tester")
            .And.Contain("Meeting notes</h3>")
            .And.Contain("The meeting</div>", "notes on no article are the meeting's")
            .And.Contain("Takeaway: patch the VPN&#xA;and the jump hosts", "a note keeps its line break (shown pre-wrap)")
            .And.Contain("Follow-up: ask the SOC lead about the new detections");

        var bare = await BriefWithNotes(44);
        (await app.CreateClient().GetStringAsync(Ui.BriefUrl(bare))).Should().NotContain("Meeting notes</h3>").And.NotContain("Attended</h3>");
    }
}
