using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Tide.Domain.Entities;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>
/// A browser can hold an older copy of a script than the server expects (a cached file whose ?v= wasn't bumped, or a
/// tab left open across a release). A focus call the older copy lacks must not throw out of the component: an
/// exception after render reaches the page's error boundary, which replaces the whole page with its error.
/// </summary>
public class StaleScriptTests
{
    // A script that's loaded but older: these functions aren't on it.
    private sealed class OlderScript(params string[] missing) : IJSRuntime
    {
        public List<string> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add(identifier);
            return missing.Contains(identifier)
                ? ValueTask.FromException<TValue>(new JSException($"Could not find '{identifier}'"))
                : ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    // Renders as the server does, keeping what reaches the error boundary instead of throwing it.
    private sealed class Capturing(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public List<Exception> Errors { get; } = [];
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override void HandleException(Exception exception) { lock (Errors) Errors.Add(exception); }
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;

        public Task<T> Mount<T>(Dictionary<string, object?> parameters) where T : IComponent => Dispatcher.InvokeAsync(async () =>
        {
            var component = (T)InstantiateComponent(typeof(T));
            await RenderRootComponentAsync(AssignRootComponentId(component), ParameterView.FromDictionary(parameters));
            return component;
        });
    }

    [Theory]
    [InlineData("Fix", "tideOrder.focusTitle")]
    [InlineData("Remove", "tideOrder.focusUndo")]
    public async Task TheRunningOrder_KeepsThePage_WhenTheBrowsersScriptLacksAFocusCall(string action, string call)
    {
        var js = new OlderScript(call);
        await using var services = new ServiceCollection().AddSingleton<IJSRuntime>(js).BuildServiceProvider();
        using var renderer = new Capturing(services);
        var item = new BriefItem { Article = new Article { Title = "Edge VPN zero-day" } };
        var order = await renderer.Mount<RunningOrder>(new() { [nameof(RunningOrder.Items)] = new[] { item }, [nameof(RunningOrder.CanFix)] = true });

        var method = typeof(RunningOrder).GetMethod(action, BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] args = action == "Fix" ? [item, Tide.Application.Articles.ArticleFix.WhyItMattersAmber] : [item];
        await renderer.Dispatcher.InvokeAsync(() => (Task)method.Invoke(order, args)!);
        for (var i = 0; i < 50 && !js.Calls.Contains(call); i++) await Task.Delay(10);
        await renderer.Dispatcher.InvokeAsync(() => { });   // the after-render work that follows the call

        js.Calls.Should().Contain(call, "the fix moves focus, as it does in the browser");
        renderer.Errors.Should().BeEmpty("an older script only means focus stays where it was");
    }
}
