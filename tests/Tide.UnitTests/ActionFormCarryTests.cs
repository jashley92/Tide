using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>An open edit carried onto the action after the editor's own step on it moved its version.</summary>
public class ActionFormCarryTests
{
    private static ActionItem Action() => new()
    {
        Id = Guid.NewGuid(), EditVersion = Guid.NewGuid(), Title = "Sweep the logs", Kind = ActionKind.IocSweep, Priority = Priority.P3,
        TeamId = Guid.NewGuid(),
    };

    [Fact]
    public void FieldsTheEditorLeftAlone_TakeTheirValueNow_AndTheirOwnChangesStay()
    {
        var a = Action();
        var opened = ActionForm.From(a);
        var form = ActionForm.From(a);
        form.Title = "Mine";
        // Take it: the assignee and the version move.
        (a.AssigneeId, a.AssigneeName, a.EditVersion) = ("me", "Me", Guid.NewGuid());

        form.CarryOnto(opened, a).Should().BeTrue();

        form.Title.Should().Be("Mine");
        (form.AssigneeId, form.AssigneeName).Should().Be(("me", "Me"), "a save mustn't put back the assignee the step changed");
        form.EditVersion.Should().Be(a.EditVersion);
    }

    [Fact]
    public void AFieldTheEditorChanged_ThatMovedToSomethingElseToo_IsAClash_AndLeavesTheFormAsItWas()
    {
        var a = Action();
        var opened = ActionForm.From(a);
        var form = ActionForm.From(a);
        form.Priority = Priority.P1;
        var version = form.EditVersion;
        (a.Priority, a.Title, a.EditVersion) = (Priority.P2, "Theirs", Guid.NewGuid());

        form.CarryOnto(opened, a).Should().BeFalse();

        (form.Priority, form.Title, form.EditVersion).Should().Be((Priority.P1, "Sweep the logs", version));
    }
}
