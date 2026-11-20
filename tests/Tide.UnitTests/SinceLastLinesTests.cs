using AwesomeAssertions;
using Tide.Application.History;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>How "Since you last looked" words and orders its lines (ADR 0030).</summary>
public class SinceLastLinesTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 19, 15, 0, 0, TimeSpan.Zero);

    private static ActivityService.ActionSeen Action(DateTimeOffset? raised = null, DateTimeOffset? handed = null, DateTimeOffset? changed = null,
        string? lastEvent = null, int comments = 0) =>
        new(12, "Sweep VPN logs", "Cyber Defense", raised, raised is null ? null : "Sam", handed, handed is null ? null : "Kim",
            changed, changed is null ? null : "Lee", lastEvent, comments, comments > 0 ? T : null, comments > 0 ? "Ash" : null);

    [Fact]
    public void AnActionsLine_SaysTheWeightiestReason_ThenItsComments()
    {
        ActivityService.ActionLine(Action(handed: T, raised: T, changed: T, comments: 2)).Detail
            .Should().Be("Handed to Cyber Defense by Kim · 2 comments, the latest from Ash");
        ActivityService.ActionLine(Action(raised: T)).Detail.Should().Be("Raised by Sam");
        ActivityService.ActionLine(Action(changed: T, lastEvent: "Status: Open → Blocked.")).Detail.Should().Be("Changed by Lee: Status: Open → Blocked.");
        ActivityService.ActionLine(Action(comments: 1)).Detail.Should().Be("A comment from Ash");
        ActivityService.ActionLine(Action(raised: T)).Should().Match<SinceLine>(l => l.Title == "A-12 Sweep VPN logs" && l.Href == "actions/12" && l.Group == SinceGroup.ForYou);
    }

    [Fact]
    public void SomeoneTheSignInMirrorDoesntName_ReadsAsAColleague()
    {
        var a = Action(changed: T) with { ChangedBy = null };
        ActivityService.ActionLine(a).Detail.Should().Be("Changed by a colleague");
    }

    [Fact]
    public void AnArticleReviewed_IsForYou_AndOpensTheReview_AnEditAloneIsYourWork()
    {
        var id = Guid.NewGuid();
        var reviewed = ActivityService.ArticleLine(new(id, "Edge VPN", T, "Sam", ReviewStatus.Approved, T, "Kim", 2, T));
        (reviewed.Group, reviewed.Detail, reviewed.Href).Should().Be((SinceGroup.ForYou, "Approved by Kim · 2 review comments · edited by Sam", $"articles/{id}/review"));
        var edited = ActivityService.ArticleLine(new(id, "Edge VPN", T, "Sam", null, null, null, 0, null));
        (edited.Group, edited.Detail, edited.Href).Should().Be((SinceGroup.YourWork, "Edited by Sam", $"articles/{id}"));
    }

    [Fact]
    public void BriefsSent_AreOneLineABrief_NamingItsEditions()
    {
        var day = new DateOnly(2026, 11, 19);
        var lines = ActivityService.SendLines([new(day, Edition.Public, "Sam", T), new(day, Edition.Team, "Sam", T.AddMinutes(-5))]).ToList();
        lines.Should().ContainSingle().Which.Should().Match<SinceLine>(l =>
            l.Title == "Brief for Thu Nov 19 sent" && l.Detail == "Team edition and company-wide, by Sam" && l.Href == "briefs/2026-11-19" && l.AtUtc == T);
    }

    [Fact]
    public void TheKevLine_CountsTheItems_AndIsNoneWithoutThem()
    {
        ActivityService.KevLine([]).Should().BeNull();
        ActivityService.KevLine([new("Old", T.AddHours(-1)), new("Newest", T)])!.Should().Match<SinceLine>(l =>
            l.Title == "2 new triage items on KEV" && l.Detail == "The latest: Newest" && l.Group == SinceGroup.TheDay);
    }

    [Fact]
    public void Lines_ComeForYouFirst_ThenYourWork_ThenTheDay_NewestFirst_AndAreCutWithACountOfTheRest()
    {
        SinceLine L(SinceGroup g, int minutes) => new(g, $"{g} {minutes}", "", "", T.AddMinutes(minutes));
        var (shown, more) = ActivityService.Compose([L(SinceGroup.TheDay, 9), L(SinceGroup.ForYou, 1), L(SinceGroup.YourWork, 5), L(SinceGroup.ForYou, 3)], 3);
        shown.Select(l => l.Title).Should().Equal("ForYou 3", "ForYou 1", "YourWork 5");
        more.Should().Be(1);
    }

    [Fact]
    public void TheMark_IsWrittenAndReadOneWay()
    {
        SeenMark.Parse(SeenMark.Format(T)).Should().Be(T);
        SeenMark.Parse("").Should().BeNull();
        SeenMark.Parse("yesterday").Should().BeNull();
    }
}
