using AwesomeAssertions;
using Tide.Application.Operations;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>
/// Every tour step, guide step and page tip points at a control, so none is left in a corner of the window, away from
/// what it's about.
/// </summary>
public class StepTargetsTests
{
    private static Func<Permission, bool> Everything => _ => true;

    // Every role group's tour, every step once.
    private static readonly RoleGroup[] AllGroups =
        [RoleGroup.Analysts, RoleGroup.Presenters, RoleGroup.Administrators, RoleGroup.ViewersAndActionOwners];

    [Fact]
    public void EveryTourStep_PointsAtAControl() =>
        Tours.For(AllGroups, Everything).Should().NotBeEmpty().And.OnlyContain(s => !string.IsNullOrWhiteSpace(s.Target),
            "a step without a control sits in the corner, away from what it's about");

    [Fact]
    public void EveryGuideStep_PointsAtAControl() =>
        Guides.For(Everything).SelectMany(g => g.Steps).Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Target));

    [Fact]
    public void EveryPageTip_PointsAtAControl() =>
        PageTips.All.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t.Target));

    [Theory]
    [InlineData("present", "a[href^=\"present/\"]")]
    [InlineData("meeting", ".tide-live-banner")]
    [InlineData("admin", ".sidebar a[href=\"admin\"]")]
    public void TheMeetingAndAdministrationSteps_PointAtTheirOwnControls(string key, string control) =>
        Tours.For(AllGroups, Everything).Single(s => s.Key == key).Target.Should().Contain(control);
}
