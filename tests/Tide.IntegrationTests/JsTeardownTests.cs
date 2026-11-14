using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.JSInterop;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>A component's disposal that throws ends the whole circuit, so a teardown call to the page's script must never
/// throw when the call can't happen.</summary>
public partial class JsTeardownTests
{
    [Theory]
    [InlineData(typeof(JSDisconnectedException))]
    [InlineData(typeof(TaskCanceledException))]   // the circuit cancelling the call on its way down
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(InvalidOperationException))]   // a prerender
    public async Task StopAsync_SwallowsACallThatCantHappen(Type failure)
    {
        var js = new FailingJs((Exception)Activator.CreateInstance(failure, "gone")!);
        await FluentActions.Awaiting(async () => await js.StopAsync("tideOrder.stop")).Should().NotThrowAsync();
        js.Called.Should().Be("tideOrder.stop");
    }

    [Theory]
    [InlineData(typeof(JSDisconnectedException))]
    [InlineData(typeof(TaskCanceledException))]   // the browser not answering in time (present mode's share of its place)
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(JSException))]
    public async Task BestEffortAsync_SwallowsEveryFailure(Type failure)
    {
        var js = new FailingJs((Exception)Activator.CreateInstance(failure, "gone")!);
        await FluentActions.Awaiting(async () => await js.BestEffortAsync("tidePresent.share")).Should().NotThrowAsync();
        js.Called.Should().Be("tidePresent.share");
    }

    [Fact]
    public async Task StopAsync_StillRaisesAScriptError()
    {
        var js = new FailingJs(new JSException("tideOrder is not defined"));
        await FluentActions.Awaiting(async () => await js.StopAsync("tideOrder.stop")).Should().ThrowAsync<JSException>(
            "a broken script is a bug to see, not a teardown to skip");
    }

    [Fact]
    public void EveryComponentsDisposal_CallsTheScriptThroughStopAsync()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Tide.sln"))) dir = Path.GetDirectoryName(dir)!;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(dir, "src", "Tide.Web", "Components"), "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var dispose = DisposeAsyncBody().Match(text);
            if (dispose.Success && dispose.Groups["body"].Value.Contains("JS.Invoke")) offenders.Add(Path.GetFileName(file));
        }
        offenders.Should().BeEmpty("a disposal's script call goes through JsTeardown.StopAsync, which can't end the circuit");
    }

    private sealed class FailingJs(Exception failure) : IJSRuntime
    {
        public string? Called { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Called = identifier;
            return ValueTask.FromException<TValue>(failure);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    [GeneratedRegex(@"ValueTask DisposeAsync\(\)\s*\{(?<body>(?:[^{}]|\{(?<d>)|\}(?<-d>))*)\}")]
    private static partial Regex DisposeAsyncBody();
}
