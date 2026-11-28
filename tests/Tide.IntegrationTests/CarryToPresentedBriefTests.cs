using AwesomeAssertions;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Ending a past day's meeting after the next working day's meeting has already been held: the undiscussed articles
/// must not go onto that brief, whose running order has already been presented.
/// </summary>
public class CarryToPresentedBriefTests : IDisposable
{
    private readonly TestDb _db = new();
    private static readonly DateOnly Monday = new(2026, 10, 26);
    private static readonly DateOnly Tuesday = Monday.AddDays(1);

    public CarryToPresentedBriefTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task EndingAMeeting_DoesNotCarryOntoANextBriefWhoseMeetingWasAlreadyHeld()
    {
        var articles = _db.Articles();
        var briefs = _db.Briefs();
        var onTuesday = await articles.CreateAsync("On Tuesday's brief", "Vendor", null, TlpLevel.Clear);
        var tuesday = await briefs.GetOrStartAsync(Tuesday);
        await briefs.AddArticleAsync(tuesday.Id, onTuesday);
        await briefs.MarkPresentedAsync(tuesday.Id);

        var undiscussed = await articles.CreateAsync("Ran out of time on Monday", "Vendor", null, TlpLevel.Clear);
        var monday = await briefs.GetOrStartAsync(Monday);
        await briefs.AddArticleAsync(monday.Id, undiscussed);

        var end = () => briefs.MarkPresentedAsync(monday.Id, carry: [undiscussed]);

        (await end.Should().ThrowAsync<InvalidOperationException>("Tuesday's meeting is over, so nobody would present the article there"))
            .WithMessage("*has been presented, so nothing can be carried to it*");
        (await briefs.GetAsync(Tuesday))!.Items.Select(i => i.ArticleId).Should().NotContain(undiscussed,
            "an article carried onto a running order that has already been presented is never discussed");
        (await briefs.GetAsync(Monday))!.Status.Should().NotBe(BriefStatus.Presented, "the refused end is one save, so Monday's meeting is still open");
        (await briefs.MarkPresentedAsync(monday.Id)).Should().BeNull("ending it without the carry still works");
    }
}
