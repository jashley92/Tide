using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>
/// The boxes that update as you type (DataTable's filter, SearchCombobox) never send older text over newer keystrokes:
/// they bind with @bind:get/@bind:set and @bind:event="oninput", so Blazor's copy of the box's value follows what's typed
/// and a render carrying that text sends nothing. A change from outside (a clear, a new ?q=, a parent emptying the box
/// after a pick) does reach the box, a second clear included. Rendered by a renderer that keeps what it would send.
/// </summary>
public class TypingBoxTests
{
    private sealed record Row(string Name);

    private static readonly IReadOnlyList<TableColumn<Row>> Columns = [new("name", "Name") { FilterBy = r => r.Name }];
    private static readonly IReadOnlyList<Row> Rows = [new("Block the domain"), new("Patch the VPN"), new("Hunt for the hash")];

    private static Dictionary<string, object?> Table(string? filter) => new()
    {
        ["Items"] = Rows,
        ["Columns"] = Columns,
        ["RowTemplate"] = (RenderFragment<Row>)(r => b => b.AddContent(0, r.Name)),
        ["One"] = "row",
        ["Many"] = "rows",
        ["Caption"] = "Rows",
        ["Filter"] = filter,
    };

    [Fact]
    public async Task DataTable_WhatsTyped_IsNeverSentBack()
    {
        await using var r = RecordingRenderer.Create();
        await r.Mount<DataTable<Row>>(Table(null));
        foreach (var text in new[] { "h", "ha", "has", "hash" }) await await r.Type(text);

        r.Shows("Patch the VPN").Should().BeFalse("the filter applied once typing paused");
        r.Shows("Hunt for the hash").Should().BeTrue();
        r.ValuesSent.Should().BeEmpty("a render after a keystroke would land over newer ones");
        r.Drawn.Should().Be("hash");
    }

    [Fact]
    public async Task DataTable_ANewFilterFromOutside_IsSent()
    {
        await using var r = RecordingRenderer.Create();
        await r.Mount<DataTable<Row>>(Table(null));
        await await r.Type("hash");
        await r.SetParameters(Table("vpn"));   // Back to another ?q=

        r.ValuesSent.Should().Equal("vpn");
    }

    [Fact]
    public async Task DataTable_AClear_ReachesTheBox_EveryTime()
    {
        await using var r = RecordingRenderer.Create();
        await r.Mount<DataTable<Row>>(Table(null));
        await await r.Type("hash");
        await r.Key("Escape");
        r.ValuesSent.Should().Equal([""], "the box was last drawn empty, but Blazor's copy followed the typing");

        // Typed and cleared again, before the pause this time: the second clear is sent too.
        var pending = await r.Type("vpn");
        await r.Key("Escape");
        await pending;
        r.ValuesSent.Should().Equal("", "");
        r.Drawn.Should().Be("");
    }

    /// <summary>A parent binding the combobox as the pickers do (@bind-Text), or one that's slow to follow.</summary>
    private sealed class Picker : ComponentBase
    {
        public string? Text { get; set; } = "";
        public bool Follow { get; set; } = true;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<SearchCombobox<string>>(0);
            builder.AddComponentParameter(1, "Id", "pick");
            builder.AddComponentParameter(2, "Text", Text);
            builder.AddComponentParameter(3, "TextChanged", EventCallback.Factory.Create<string?>(this, t => { if (Follow) Text = t; }));
            builder.AddComponentParameter(4, "Items", (IReadOnlyList<string>)[]);
            builder.AddComponentParameter(5, "ItemTemplate", (RenderFragment<string>)(s => b => b.AddContent(0, s)));
            builder.AddComponentParameter(6, "Key", (Func<string, object>)(s => s));
            builder.CloseComponent();
        }

        public void Set(string? text)
        {
            Text = text;
            StateHasChanged();
        }

        public void Redraw() => StateHasChanged();
    }

    [Fact]
    public async Task Combobox_WhatsTyped_IsNeverSentBack()
    {
        await using var r = RecordingRenderer.Create();
        var picker = await r.Mount<Picker>();
        foreach (var text in new[] { "A", "AP", "APT", "APT2", "APT29" }) await await r.Type(text);

        picker.Text.Should().Be("APT29", "the parent has each keystroke");
        r.ValuesSent.Should().BeEmpty();
        r.Drawn.Should().Be("APT29");
    }

    [Fact]
    public async Task Combobox_AParentRedrawingOlderText_DoesNotOverwriteNewerTyping()
    {
        await using var r = RecordingRenderer.Create();
        var picker = await r.Mount<Picker>();
        await await r.Type("APT");

        // The parent stops following (busy, or skipping renders) while more is typed, and redraws with what it has.
        await r.Do(() => picker.Follow = false);
        await await r.Type("APT2");
        await await r.Type("APT29");
        await r.Do(picker.Redraw);
        await r.Do(picker.Redraw);

        r.ValuesSent.Should().BeEmpty("the parent's text is the one it last passed, not a change");
        r.Drawn.Should().Be("APT29");
    }

    [Fact]
    public async Task Combobox_AParentClearingAfterAPick_IsSent_EveryTime()
    {
        await using var r = RecordingRenderer.Create();
        var picker = await r.Mount<Picker>();
        await await r.Type("APT29");
        await r.Do(() => picker.Set(null));
        r.ValuesSent.Should().Equal([""], "the parent emptied the box after a pick");

        await await r.Type("G0016");
        await r.Do(() => picker.Set(""));
        r.ValuesSent.Should().Equal("", "");
        r.Drawn.Should().Be("");
    }

    [Fact]
    public async Task Combobox_AParentSettingNewText_IsSent()
    {
        await using var r = RecordingRenderer.Create();
        var picker = await r.Mount<Picker>();
        await await r.Type("cozy");
        await r.Do(() => picker.Set("Cozy Bear"));

        r.ValuesSent.Should().Equal("Cozy Bear");
    }
}
