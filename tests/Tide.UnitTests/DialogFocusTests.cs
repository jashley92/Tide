using System.Text.RegularExpressions;
using FluentAssertions;

namespace Tide.UnitTests;

/// <summary>
/// A dialog's keys are heard on the dialog itself (its <c>@onkeydown</c>), so they reach it only while focus is inside.
/// A click on its text (a message, a heading, the send log) moved focus out to the page: Escape then did nothing, and the
/// dialog stayed open. Each modal that listens for keys takes focus itself when clicked (<c>tabindex="-1"</c>).
/// </summary>
public partial class DialogFocusTests
{
    [Fact]
    public void EveryModalThatHearsKeys_TakesFocusWhenItsTextIsClicked()
    {
        var src = Path.Combine(DeployScriptTests.RepoRoot(), "src", "Tide.Web", "Components");
        var modals = Directory.GetFiles(src, "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => ModalTag().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Tag: m.Value)))
            .Where(x => x.Tag.Contains("@onkeydown", StringComparison.Ordinal))
            // The lightbox closes on any click, so there's no text in it to click.
            .Where(x => x.File != "Lightbox.razor")
            .ToList();

        modals.Select(x => x.File).Should().Contain(["ConfirmDialog.razor", "LeaveDialog.razor", "ShortcutsOverlay.razor", "ScreenshotDialog.razor", "AttackMatrixPicker.razor"]);
        modals.Where(x => !x.Tag.Contains("tabindex=\"-1\"", StringComparison.Ordinal)).Select(x => x.File)
            .Should().BeEmpty("a click on the dialog's text keeps focus in it, so Escape still closes it");
    }

    // An element opening tag with aria-modal="true", across lines.
    [GeneratedRegex("""<div\b[^>]*aria-modal="true"[^>]*>""", RegexOptions.Singleline)]
    private static partial Regex ModalTag();
}
