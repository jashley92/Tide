using AwesomeAssertions;
using Tide.Application.Security;
using Tide.Domain.Enums;
using Tide.Web.Security;

namespace Tide.IntegrationTests;

/// <summary>The Manager role: what it holds, and that it's named, mapped and signed in like the others.</summary>
public class ManagerRoleTests
{
    [Fact]
    public void AManager_ReadsWhatAViewerReads_AndManagesPeople_ButWritesNothing()
    {
        var held = RoleDefinitions.PermissionsFor([AppRole.Manager]);
        held.Should().BeEquivalentTo(RoleDefinitions.PermissionsFor([AppRole.Viewer]).Append(Permission.ManagePeople));
        held.Should().NotContain([Permission.WriteArticles, Permission.WorkActions, Permission.PresentBriefs, Permission.Administer]);
        RoleDefinitions.PermissionsFor([AppRole.Analyst]).Should().NotContain(Permission.ManagePeople);
        RoleDefinitions.PermissionsFor([AppRole.Admin]).Should().Contain(Permission.ManagePeople);
        ((int)AppRole.Manager, (int)Permission.ManagePeople).Should().Be((6, 10), "appended, nothing renumbered");
        RoleDefinitions.Label(AppRole.Manager).Should().Be("Manager");
    }

    [Fact]
    public void TheDevSignIn_AndARoleMapping_NameItLikeTheOthers()
    {
        DevAuthenticationHandler.ParseRoles("manager,Analyst").Should().Equal("Manager", "Analyst");
        RoleDefinitions.TryParseRole("Manager", out var role).Should().BeTrue();
        role.Should().Be(AppRole.Manager);
        RoleDefinitions.PermissionsForRoleNames(["Manager"]).Should().Contain(Permission.ManagePeople);
    }

    [Fact]
    public void BriefTimelinessPolicy_IsMetByOwnDaysOrEveryones()
    {
        Policies.AnyOf[Policies.BriefTimeliness].Should().BeEquivalentTo([Permission.PresentBriefs, Permission.ManagePeople]);
        Policies.Required[Policies.ManagePeople].Should().Be(Permission.ManagePeople);
    }
}
