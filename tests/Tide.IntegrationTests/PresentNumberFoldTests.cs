using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Metrics;
using Tide.Domain.Enums;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// A number entered from the presenter's rail that folds its group away (the last one missing, entered as 0): the
/// presenter stays on the slide that now holds the group's line, where the room is sent, rather than on the slide that
/// took its number. A tile's note saved meanwhile stays. Its own app, since metrics are global.
/// </summary>
public class PresentNumberFoldTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TheLastMissingNumberEnteredAs0_KeepsThePresenterWithTheRoom_AndTheTilesNote()
    {
        var day = await TwoArticleBrief(app, 230, "Fold");
        Guid missing;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var metrics = scope.ServiceProvider.GetRequiredService<MetricsService>();
            var a = await metrics.AddGroupAsync("Fold a");
            var shown = await metrics.SaveMetricAsync(new MetricForm { GroupId = a, Name = "Cases", Direction = MetricDirection.LowerIsBetter });
            await metrics.SaveValueAsync(shown, day, 5, null);
            var b = await metrics.AddGroupAsync("Fold b");
            missing = await metrics.SaveMetricAsync(new MetricForm { GroupId = b, Name = "Escalations", Direction = MetricDirection.LowerIsBetter });
        }
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await pat.Click(p => p.OnPresentKey("next"));
        await pat.Click(p => p.OnPresentKey("next"));   // Fold b's slide, its number missing
        (await pat.Html()).Should().Contain("Slide 3 of 6:");
        await using (var scope = SignedIn(app, SignIn.Analyst))   // a colleague's count and note, a moment before
            await scope.ServiceProvider.GetRequiredService<MetricsService>().SaveValueAsync(missing, day, 3, "Waiting on the night shift's count.");

        await Do(pat, p => p.EnterMissingNumber(missing, "0"));   // Fold b is all 0 now: a line on the first numbers slide

        var live = app.Services.GetRequiredService<LiveMeetings>().Get(day)!;
        live.SlideKey.Should().StartWith(MetricSlides.KeyPrefix, "the room stays on the numbers");
        (await pat.Html()).Should().Contain("Slide 2 of 5:", "the presenter is on the slide with Fold b's line, with the room").And.NotContain("Slide 3 of 5:");
        await using (var scope = SignedIn(app, SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<MetricsService>().GetDayAsync(day)).Groups.SelectMany(g => g.Tiles)
                .Single(t => t.Metric.Id == missing).Note.Should().Be("Waiting on the night shift's count.", "entering the number keeps the tile's note");
        EndMeeting(app, day);
    }
}
