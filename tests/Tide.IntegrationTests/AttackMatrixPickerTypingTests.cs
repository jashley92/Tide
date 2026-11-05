using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The ATT&amp;CK matrix picker's filter box: a keystroke waits out the typing pause before the matrix changes, and until
/// then the picker doesn't render at all (the box already shows what's typed), so the matrix isn't diffed per letter.
/// </summary>
public class AttackMatrixPickerTypingTests(TideApp app) : IClassFixture<TideApp>
{
    // As Blazor delivers an @oninput: through the component's own event handling, which renders as the handler first
    // waits and again when it's done. Hands back the handler's task, still waiting out its pause.
    private static async Task<Task> Type(Rendered<AttackMatrixPicker> r, string text)
    {
        Task pending = Task.CompletedTask;
        await r.Renderer.Dispatcher.InvokeAsync(() =>
        {
            pending = ((IHandleEvent)r.Component).HandleEventAsync(
                new EventCallbackWorkItem((Func<ChangeEventArgs, Task>)r.Component.OnFilter), new ChangeEventArgs { Value = text });
        });
        return pending;
    }

    [Fact]
    public async Task AKeystroke_DoesNotRenderTheMatrix_UntilTypingPauses()
    {
        var r = await RenderAsync<AttackMatrixPicker>(app, "/articles", new() { ["Current"] = (IReadOnlyCollection<string>)[] });
        try
        {
            await TypeAndWatch(r);
        }
        finally
        {
            // Closing hands focus back through dialog.js, which a statically rendered picker can't call.
            try { await r.DisposeAsync(); }
            catch (InvalidOperationException) { }
        }
    }

    private static async Task TypeAndWatch(Rendered<AttackMatrixPicker> r)
    {
        (await r.Html()).Should().NotContain("tide-atkm-col-hidden", "with nothing typed every tactic shows");

        // The first letter brings the Clear button, so it draws at once; the matrix filters once typing pauses.
        var first = await Type(r, "phish");
        (await r.Html()).Should().Contain(">Clear</button>").And.NotContain("tide-atkm-filterline");
        await first;
        var filtered = await r.Html();
        filtered.Should().Contain("value=\"phish\"").And.Contain("tide-atkm-filterline").And.Contain("tide-atkm-col-hidden");

        // A later keystroke: nothing renders while it pauses, not the box's value and not the matrix.
        var second = await Type(r, "phishing");
        (await r.Html()).Should().Be(filtered, "the keystroke doesn't re-render the picker before the pause");
        await second;
        (await r.Html()).Should().Contain("value=\"phishing\"");

        // Two quick keystrokes: the first is superseded and draws nothing; the second filters.
        var a = await Type(r, "T1566");
        var b = await Type(r, "T1566.001");
        var mid = await r.Html();
        mid.Should().Contain("value=\"phishing\"").And.NotContain("value=\"T1566");
        await Task.WhenAll(a, b);
        (await r.Html()).Should().Contain("value=\"T1566.001\"").And.Contain("T1566.001");
    }
}
