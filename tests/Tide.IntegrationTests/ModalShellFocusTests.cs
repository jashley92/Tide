using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>
/// What ModalShell asks dialog.js as a dialog shows and hides: the open that puts focus where the dialog wants it, once
/// however often the page redraws, and close (focus back to the opener) before the dialog's own after-close step. The
/// static renderer the other tests use runs no after-render work, so these draw with a renderer that does.
/// </summary>
public class ModalShellFocusTests
{
    /// <summary>Notes each call to dialog.js (and anything else the test logs) in order.</summary>
    private sealed class RecordingJs(List<string> log) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            log.Add(identifier);
            return ValueTask.FromResult<TValue>(default!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    /// <summary>A renderer that, as the browser's does, runs each component's after-render work once a batch is drawn.</summary>
    private sealed class LiveRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public Task<T> Draw<T>(Dictionary<string, object?>? values = null) where T : IComponent, new() => Dispatcher.InvokeAsync(async () =>
        {
            var component = new T();
            await RenderRootComponentAsync(AssignRootComponentId(component), ParameterView.FromDictionary(values ?? []));
            return component;
        });

        protected override void HandleException(Exception exception) => throw exception;

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }

    /// <summary>A page holding a ModalShell, as a dialog does: it opens and closes it, and redraws on its own.</summary>
    private sealed class Page : ComponentBase
    {
        public bool Open { get; set; }
        public List<string> Log { get; set; } = [];
        private int _draws;

        public Task Redraw() => InvokeAsync(StateHasChanged);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            _draws++;
            builder.OpenComponent<ModalShell>(0);
            builder.AddComponentParameter(1, nameof(ModalShell.Open), Open);
            builder.AddComponentParameter(2, nameof(ModalShell.Title), "Draw " + _draws);
            builder.AddComponentParameter(3, nameof(ModalShell.AfterClose), (Func<Task>)(() => { Log.Add("AfterClose"); return Task.CompletedTask; }));
            builder.CloseComponent();
        }
    }

    private static (LiveRenderer Renderer, List<string> Log) Live()
    {
        var log = new List<string>();
        var services = new ServiceCollection().AddSingleton<IJSRuntime>(new RecordingJs(log)).BuildServiceProvider();
        return (new LiveRenderer(services), log);
    }

    private static RenderFragment Body => b => b.AddContent(0, "the body");

    [Theory]
    [InlineData(true, true, true, "tideDialog.openFirstControl")]   // CloseOnly comes before Form
    [InlineData(true, false, true, "tideDialog.openFirstControl")]
    [InlineData(false, true, true, "tideDialog.openFirst")]
    [InlineData(false, false, true, "tideDialog.open")]
    [InlineData(true, true, false, "tideDialog.open")]               // no body: Cancel (or Close) takes focus
    [InlineData(false, true, false, "tideDialog.open")]
    public async Task AConfirmation_OpensWithFocusWhereItsKindWantsIt(bool closeOnly, bool form, bool body, string opened)
    {
        var (renderer, log) = Live();
        var values = new Dictionary<string, object?> { ["CloseOnly"] = closeOnly, ["Form"] = form };
        if (body) values["ChildContent"] = Body;
        var dialog = await renderer.Draw<ConfirmDialog>(values);
        log.Should().BeEmpty("closed, it asks nothing");

        await renderer.Dispatcher.InvokeAsync(() => { _ = dialog.Ask("Move A-1 to Done", null, "Move to Done"); });
        log.Should().Equal(opened);

        // As its Cancel button does: the handler, then the dialog redrawn.
        await renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)dialog).HandleEventAsync(new EventCallbackWorkItem((Action)dialog.Cancel), null));
        log.Should().Equal(opened, "tideDialog.close");
    }

    [Fact]
    public async Task ItOpensOnce_HoweverOftenThePageRedraws()
    {
        var (renderer, log) = Live();
        var page = await renderer.Draw<Page>();
        page.Open = true;
        await page.Redraw();
        await page.Redraw();
        await page.Redraw();
        log.Should().Equal("tideDialog.open");
    }

    [Fact]
    public async Task ItClosesOnce_ThenRunsTheDialogsAfterCloseStep()
    {
        var (renderer, log) = Live();
        var page = await renderer.Draw<Page>();
        page.Log = log;
        page.Open = true;
        await page.Redraw();
        page.Open = false;
        await page.Redraw();
        await page.Redraw();
        log.Should().Equal("tideDialog.open", "tideDialog.close", "AfterClose");
    }
}
