using FluentAssertions;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

public class ToastServiceTests
{
    [Fact]
    public void Toasts_IsACopy_SoATimerDismissWhileRenderingCantBreakIt()
    {
        var toasts = new ToastService();
        toasts.Show("one");
        toasts.Show("two");
        var rendering = toasts.Toasts;
        foreach (var t in rendering) toasts.Dismiss(t.Id);   // what a timer thread does mid-render
        rendering.Should().HaveCount(2);
        toasts.Toasts.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUndo_RunsOnce_AndTakesItsToastAway()
    {
        var toasts = new ToastService();
        var undone = 0;
        toasts.Show("Took it off the brief.", () => { undone++; return Task.CompletedTask; });
        var toast = toasts.Toasts.Single();
        toast.Undo.Should().NotBeNull();

        await toasts.UndoAsync(toast.Id);
        await toasts.UndoAsync(toast.Id);   // a double click

        undone.Should().Be(1);
        toasts.Toasts.Should().BeEmpty();
        toasts.Show("Saved.");
        toasts.Toasts.Single().Undo.Should().BeNull("a plain confirmation has nothing to undo");
    }

    [Fact]
    public void AConfirmation_ClearsEarlierErrors_AndAtMostFourShow()
    {
        var toasts = new ToastService();
        toasts.Error("It isn't ready yet.");
        toasts.Show("Saved.");
        toasts.Toasts.Select(t => t.Message).Should().Equal("Saved.");
        for (var i = 0; i < 6; i++) toasts.Error($"e{i}");
        toasts.Toasts.Select(t => t.Message).Should().Equal("e2", "e3", "e4", "e5");
    }

    [Fact]
    public async Task ConcurrentAddsAndDismisses_DontThrow()
    {
        var toasts = new ToastService();
        var work = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                toasts.Error("x");
                foreach (var t in toasts.Toasts) toasts.Dismiss(t.Id);
            }
        }));
        await Task.WhenAll(work);
        toasts.Toasts.Count.Should().BeLessThanOrEqualTo(4);
    }
}

public class UserFacingErrorTests
{
    [Fact]
    public void For_ALostSaveRace_SaysSomeoneElseChangedIt_NotSomethingWentWrong()
    {
        var message = UserFacingError.For(new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("database operation expected to affect 1 row(s)"));

        message.Should().Contain("Someone else changed this").And.NotContain("row(s)");
        UserFacingError.For(new TimeoutException("internal detail")).Should().NotContain("internal detail");
    }
}
