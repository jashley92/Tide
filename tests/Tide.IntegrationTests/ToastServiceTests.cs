using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Tide.IntegrationTests.Audit;
using Tide.Web.Security;
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
    public void AConfirmation_GoesAfterFourSeconds_OneWithUndoAfterTen_AndAnErrorStays()
    {
        var clock = new ManualClock();
        var toasts = new ToastService(clock);
        toasts.Error("It failed.");
        toasts.Error("It failed again.");
        toasts.Show("Saved.");                                      // clears the errors
        toasts.Show("Took it off.", () => Task.CompletedTask);
        clock.Advance(TimeSpan.FromSeconds(4) - TimeSpan.FromTicks(1));
        toasts.Toasts.Select(t => t.Message).Should().Equal("Saved.", "Took it off.");
        clock.Advance(TimeSpan.FromTicks(1));
        Poll(() => toasts.Toasts.Count == 1);
        toasts.Toasts.Select(t => t.Message).Should().Equal("Took it off.");
        toasts.Error("It failed.");
        clock.Advance(TimeSpan.FromSeconds(6));
        Poll(() => toasts.Toasts.Count == 1);
        toasts.Toasts.Should().ContainSingle().Which.Message.Should().Be("It failed.", "an error stays until dismissed");

        // The dismissal is a continuation of the timer's task, so it lands just after the clock moves.
        static void Poll(Func<bool> done) => SpinWait.SpinUntil(done, TimeSpan.FromSeconds(15)).Should().BeTrue();
    }

    [Fact]
    public void Held_NothingGoes_AndLetGo_EachGetsItsFullTimeAgain()
    {
        var clock = new ManualClock();
        var toasts = new ToastService(clock);
        toasts.Show("Saved.");
        clock.Advance(TimeSpan.FromSeconds(3));
        toasts.Hold(true);                                          // the pointer comes onto the toasts
        toasts.Show("Took it off.", () => Task.CompletedTask);      // one arriving while held waits too
        clock.Advance(TimeSpan.FromMinutes(5));
        toasts.Toasts.Select(t => t.Message).Should().Equal("Saved.", "Took it off.");
        clock.Pending.Should().Be(0, "every timer is stopped while held");

        toasts.Hold(false);                                         // and leaves
        clock.Advance(TimeSpan.FromSeconds(4) - TimeSpan.FromTicks(1));
        toasts.Toasts.Should().HaveCount(2, "the 3 seconds gone before the hold don't count against it");
        clock.Advance(TimeSpan.FromTicks(1));
        toasts.Toasts.Select(t => t.Message).Should().Equal("Took it off.");
        clock.Advance(TimeSpan.FromSeconds(6));
        toasts.Toasts.Should().BeEmpty("an Undo's 10 seconds started when the hold ended");
        clock.Pending.Should().Be(0);
    }

    [Fact]
    public void Undoable_IsTheNewestWithAnUndo_AndAToastPushedOutTakesItsTimerWithIt()
    {
        var clock = new ManualClock();
        var toasts = new ToastService(clock);
        toasts.Show("First.", () => Task.CompletedTask);
        toasts.Show("Second.", () => Task.CompletedTask);
        toasts.Show("Plain.");
        toasts.Undoable!.Message.Should().Be("Second.");
        for (var i = 0; i < 3; i++) toasts.Show($"More {i}.");
        toasts.Toasts.Should().HaveCount(4);
        clock.Pending.Should().Be(4, "the ones pushed out don't leave timers behind");
        toasts.Undoable.Should().BeNull();
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

    [Fact]
    public void For_AnUnexpectedError_IsLoggedWithItsException_UnderTheReferenceThePersonIsGiven()
    {
        var errors = new RecordingLogger();
        var boom = new TimeoutException("the relay hung");
        string message;
        using (AmbientUser.Begin(null, accessLog: null, errorLog: errors)) message = UserFacingError.For(boom, operation: "the send button");

        var reference = System.Text.RegularExpressions.Regex.Match(message, @"\(reference ([0-9A-F]{8})\)").Groups[1].Value;
        reference.Should().NotBeEmpty("the person is given a reference to quote");
        message.Should().StartWith("Something went wrong.").And.NotContain("relay");
        errors.Entries.Should().ContainSingle().Which.Should().Match<(LogLevel Level, Exception? Exception, string Message)>(e =>
            e.Level == LogLevel.Error && e.Exception == boom && e.Message.Contains(reference) && e.Message.Contains("the send button"));

        // Errors raised on purpose tell the person what happened, so they aren't logged as failures.
        using (AmbientUser.Begin(null, accessLog: null, errorLog: errors))
        {
            UserFacingError.For(new ArgumentException("Give the action a title.")).Should().Be("Give the action a title.");
            UserFacingError.For(new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("x"));
        }
        errors.Entries.Should().HaveCount(1);

        // Outside a request or circuit activity there's no log, and a log that throws doesn't reach the person.
        UserFacingError.For(boom).Should().StartWith("Something went wrong.");
        using (AmbientUser.Begin(null, accessLog: null, errorLog: new RecordingLogger { Throws = true }))
            FluentActions.Invoking(() => UserFacingError.For(boom)).Should().NotThrow();
    }

    [Fact]
    public async Task ACircuitActivity_LogsUnexpectedErrorsToTheHostsErrorCategory()
    {
        var log = new CapturingLogger();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(log));
        var handler = new AmbientUserCircuitHandler(new FakeUser { UserId = "S-1-5-21-ANALYST" }, loggers);
        await handler.CreateInboundActivityHandler(_ =>
        {
            UserFacingError.For(new IOException("disk full"), operation: "a click");
            return Task.CompletedTask;
        })(null!);
        log.Lines.Should().ContainSingle(l => l.Category == AmbientUser.ErrorLogCategory && l.Level == LogLevel.Error
            && l.Message.Contains("S-1-5-21-ANALYST") && l.Message.Contains("a click"));
        AmbientUser.ErrorLog.Should().BeNull("the activity's scope ended");
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];
        public bool Throws { get; init; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Throws) throw new InvalidOperationException("The log is gone with its host.");
            Entries.Add((logLevel, exception, formatter(state, exception)));
        }
    }
}
