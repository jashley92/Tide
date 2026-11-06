using FluentAssertions;
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
