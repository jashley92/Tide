using AwesomeAssertions;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>
/// The ATT&amp;CK matrix picker's filter box: a keystroke waits out the typing pause before the matrix changes, and until
/// then the picker doesn't render at all (the box already shows what's typed), so the matrix isn't diffed per letter.
/// When it does render, what was typed isn't written back to the box, so newer keystrokes aren't lost; a clear is.
/// </summary>
public class AttackMatrixPickerTypingTests
{
    [Fact]
    public async Task AKeystroke_DoesNotRenderTheMatrix_UntilTypingPauses_AndIsNeverWrittenBack()
    {
        await using var r = RecordingRenderer.Create();
        await r.Mount<AttackMatrixPicker>(new() { ["Current"] = (IReadOnlyCollection<string>)[] });
        r.Shows("tide-atkm-col-hidden").Should().BeFalse("with nothing typed every tactic shows");

        // The first letter brings the Clear button, so it draws at once; the matrix filters once typing pauses.
        var first = await r.Type("phish");
        r.Shows("Clear").Should().BeTrue();
        r.Shows("tide-atkm-filterline").Should().BeFalse();
        await first;
        r.Shows("tide-atkm-filterline").Should().BeTrue();
        r.Shows("tide-atkm-col-hidden").Should().BeTrue();
        r.ValuesSent.Should().BeEmpty("what's typed isn't written back to the box");
        r.Drawn.Should().Be("phish", "Blazor's copy of the box follows the typing");

        // A later keystroke: nothing renders while it pauses, not the box's value and not the matrix.
        var redraws = r.Redraws;
        var second = await r.Type("phishing");
        r.Redraws.Should().Be(redraws, "the keystroke doesn't re-render the picker before the pause");
        await second;

        // Two quick keystrokes: the first is superseded and draws nothing; the second filters.
        var a = await r.Type("T1566");
        var b = await r.Type("T1566.001");
        await Task.WhenAll(a, b);
        r.Shows("T1566.001").Should().BeTrue();
        r.ValuesSent.Should().BeEmpty("still nothing typed is sent back");

        // Clear empties the box: a change Blazor sends.
        await r.Click("Clear");
        r.ValuesSent.Should().Equal("");
        r.Shows("tide-atkm-filterline").Should().BeFalse();

        // Typed again and cleared again: the second clear reaches the box too.
        await await r.Type("lure");
        await r.Click("Clear");
        r.ValuesSent.Should().Equal("", "");
    }
}
