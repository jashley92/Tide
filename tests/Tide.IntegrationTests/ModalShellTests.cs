using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>
/// The frame every modal dialog shares (ModalShell): named by its title, a dialog or an alert, closed by Escape and by a
/// click outside where that's allowed, and drawn only while open.
/// </summary>
public partial class ModalShellTests(TideApp app) : IClassFixture<TideApp>
{
    [GeneratedRegex("""aria-labelledby="(?<id>[^"]+)".*<h2 class="tide-modal-title" id="(?<title>[^"]+)">""", RegexOptions.Singleline)]
    private static partial Regex LabelledBy();

    private Task<ComponentHarness.Rendered<ModalShell>> Render(Dictionary<string, object?> values) =>
        ComponentHarness.RenderAsync<ModalShell>(app, "/", values);

    private static EventCallback Counts(Action count) => new(null, count);

    [Fact]
    public async Task AClosedDialog_DrawsNothing()
    {
        await using var r = await Render(new() { ["Open"] = false, ["Title"] = "Delete draft?" });
        (await r.Html()).Should().BeEmpty();
    }

    [Fact]
    public async Task AnOpenDialog_IsAModal_NamedByItsTitle_AndDescribedByItsMessage()
    {
        await using var r = await Render(new() { ["Open"] = true, ["Title"] = "Delete draft?", ["Role"] = "alertdialog", ["DescribedBy"] = "msg" });
        var html = await r.Html();
        html.Should().StartWith("<div class=\"tide-modal-scrim\"><div class=\"tide-modal\" tabindex=\"-1\" role=\"alertdialog\" aria-modal=\"true\"")
            .And.Contain("aria-describedby=\"msg\"").And.Contain(">Delete draft?</h2>");
        var m = LabelledBy().Match(html);
        m.Success.Should().BeTrue();
        m.Groups["id"].Value.Should().Be(m.Groups["title"].Value).And.Be(r.Component.TitleId);
    }

    [Fact]
    public async Task ADialog_IsADialog_UnlessItsAnAlert_AndDescribedByNothingUnlessGiven()
    {
        await using var r = await Render(new() { ["Open"] = true, ["Title"] = "Keyboard shortcuts" });
        (await r.Html()).Should().Contain("role=\"dialog\"").And.NotContain("aria-describedby");
    }

    [Fact]
    public async Task ItsParts_GoWhereTheyreAsked_WideAndClassed()
    {
        RenderFragment Text(string s) => b => b.AddContent(0, s);
        await using var r = await Render(new()
        {
            ["Open"] = true, ["Title"] = "Screenshot", ["Wide"] = true, ["Class"] = "tide-shot-dialog", ["HeadClass"] = "tide-shot-dialog-head",
            ["HeadContent"] = Text("the name"), ["ChildContent"] = Text("the image"), ["Actions"] = Text("the buttons"), ["ActionsClass"] = "tide-modal-actions-wrap",
        });
        var html = await r.Html();
        html.Should().Contain("<div class=\"tide-modal tide-modal-wide tide-shot-dialog\"")
            .And.Contain($"<div class=\"tide-shot-dialog-head\"><h2 class=\"tide-modal-title\" id=\"{r.Component.TitleId}\">Screenshot</h2>")
            .And.Contain("the name</div>the image")
            .And.Contain("<div class=\"tide-modal-actions tide-modal-actions-wrap\">the buttons</div>");
    }

    [Fact]
    public async Task WithoutActions_ThereIsNoActionsRow()
    {
        await using var r = await Render(new() { ["Open"] = true, ["Title"] = "Keyboard shortcuts" });
        (await r.Html()).Should().NotContain("tide-modal-actions");
    }

    [Fact]
    public async Task Escape_Closes_AndOtherKeysGoToTheDialog()
    {
        var (closed, keys) = (0, new List<string>());
        await using var r = await Render(new()
        {
            ["Open"] = true, ["Title"] = "Screenshot", ["OnClose"] = Counts(() => closed++),
            ["OnKeyDown"] = new EventCallback<KeyboardEventArgs>(null, (Action<KeyboardEventArgs>)(e => keys.Add(e.Key))),
        });
        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.OnKey(new KeyboardEventArgs { Key = "ArrowLeft" }));
        closed.Should().Be(0);
        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.OnKey(new KeyboardEventArgs { Key = "Escape" }));
        closed.Should().Be(1);
        keys.Should().Equal("ArrowLeft");
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task AClickOutside_Closes_WhereThatsAllowed(bool allowed, int closes)
    {
        var closed = 0;
        await using var r = await Render(new() { ["Open"] = true, ["Title"] = "Delete draft?", ["OnClose"] = Counts(() => closed++), ["CloseOnScrim"] = allowed });
        await r.Renderer.Dispatcher.InvokeAsync(r.Component.ScrimClick);
        closed.Should().Be(closes);
    }

    [Fact]
    public async Task EachDialog_HasItsOwnTitleId_OrTheOneItsGiven()
    {
        await using var a = await Render(new() { ["Open"] = true, ["Title"] = "A" });
        await using var b = await Render(new() { ["Open"] = true, ["Title"] = "B" });
        a.Component.TitleId.Should().NotBe(b.Component.TitleId).And.EndWith("-title");
        (await a.Html()).Should().Contain($"id=\"{a.Component.TitleId}\"");

        await using var given = await Render(new() { ["Open"] = true, ["Title"] = "C", ["Id"] = "leave-1a2b3c4d" });
        given.Component.TitleId.Should().Be("leave-1a2b3c4d-title");
    }
}
