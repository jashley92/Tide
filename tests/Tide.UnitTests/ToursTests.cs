using AwesomeAssertions;
using Tide.Application.Operations;
using Tide.Application.Preferences;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>The first-sign-in tour: the main pages and controls each role uses, and only those.</summary>
public class ToursTests
{
    private static IReadOnlyList<TourStep> For(params AppRole[] roles)
    {
        var can = RoleDefinitions.PermissionsFor(roles);
        return Tours.For(RoleGroups.For(roles), can.Contains);
    }

    [Fact]
    public void AnAnalystsTour_StepsThroughTheirPages_AndTheContextRail()
    {
        var steps = For(AppRole.Analyst);
        steps.Select(s => s.Key).Should().ContainInOrder("today", "rail", "triage", "actions", "search", "help");
        steps.Should().Contain(s => s.Key == "present").And.NotContain(s => s.Key == "admin");
        steps.Single(s => s.Key == "rail").Text.Should().Contain("]").And.Contain("After the tour",
            "] does nothing while a step is up, so the step doesn't ask for it now");
        steps.Should().OnlyContain(s => !s.Text.Contains("use Context"), "a narrow window shows the button as its icon alone");
    }

    [Fact]
    public void AnAdminsTour_AddsAdministration()
    {
        For(AppRole.Admin).Should().Contain(s => s.Key == "admin" && s.Route == "admin");
    }

    [Fact]
    public void AViewersTour_NeverGoesWhereTheyCant()
    {
        var steps = For(AppRole.Viewer);
        steps.Select(s => s.Key).Should().Contain(["today", "rail", "actions", "meeting", "help"]);
        steps.Should().NotContain(s => s.Key == "triage" || s.Key == "admin" || s.Key == "present");
        var can = RoleDefinitions.PermissionsFor([AppRole.Viewer]);
        steps.Should().OnlyContain(s => can.Contains(s.Needs));
    }

    [Theory]
    [InlineData(AppRole.Reader)]
    [InlineData(AppRole.Visitor)]
    public void ReadersAndVisitors_HaveNoTour(AppRole role) => For(role).Should().BeEmpty();

    [Fact]
    public void NoStep_IsInPresentMode_AndEachIsOnce()
    {
        var steps = For(AppRole.Analyst, AppRole.Admin, AppRole.Viewer);
        steps.Should().OnlyHaveUniqueItems(s => s.Key);
        steps.Should().OnlyContain(s => !s.Route.StartsWith("present", StringComparison.Ordinal) && s.Title.Length > 0 && s.Text.Length > 0);
    }

    [Fact]
    public void TheTourState_IsDue_ThenDone()
    {
        var d = UserPreferences.For(UserPreferences.TourState);
        d.Default.Should().BeEmpty("someone who used TIDE before the tour existed has none, and gets What's new instead");
        d.Allows(UserPreferences.TourDue).Should().BeTrue();
        d.Allows(UserPreferences.TourDone).Should().BeTrue();
        d.Allows("1.13.0").Should().BeFalse();
    }
}
