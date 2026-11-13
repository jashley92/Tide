using AwesomeAssertions;
using Tide.Application.Operations;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>
/// Every tour step, guide step and page tip points at a control, so none is left in a corner of the window, away from
/// what it's about. A step meant to be centred, as an introduction, would be listed here by key; there are none.
/// </summary>
public class StepTargetsTests
{
    private static readonly HashSet<string> CentredIntros = [];

    private static Func<Permission, bool> Everything => _ => true;

    [Fact]
    public void EveryTourStep_PointsAtAControl()
    {
        var steps = Tours.For([RoleGroup.Analysts, RoleGroup.Presenters, RoleGroup.Administrators, RoleGroup.ViewersAndActionOwners], Everything);
        steps.Should().NotBeEmpty();
        steps.Where(s => !CentredIntros.Contains(s.Key)).Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Target),
            "a step without a control sits in the corner, away from what it's about");
    }

    [Fact]
    public void EveryGuideStep_PointsAtAControl() =>
        Guides.For(Everything).SelectMany(g => g.Steps.Select(s => (g.Key, s))).Where(x => !CentredIntros.Contains(x.s.Key))
            .Should().OnlyContain(x => !string.IsNullOrWhiteSpace(x.s.Target));

    [Fact]
    public void EveryPageTip_PointsAtAControl() =>
        PageTips.All.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t.Target));

    [Theory]
    [InlineData("present", "a[href^=\"present/\"]")]
    [InlineData("meeting", ".tide-live-banner")]
    [InlineData("admin", ".sidebar a[href=\"admin\"]")]
    public void TheMeetingAndAdministrationSteps_PointAtTheirOwnControls(string key, string control) =>
        Tours.For([RoleGroup.Analysts, RoleGroup.Administrators, RoleGroup.ViewersAndActionOwners], Everything)
            .Single(s => s.Key == key).Target.Should().Contain(control);
}
