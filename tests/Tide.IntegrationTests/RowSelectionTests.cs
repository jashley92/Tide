using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>
/// The shared row selection (<see cref="RowSelection"/>, <see cref="RowKeys"/>, <c>rows.js</c>), on a list that is
/// nothing but its adoption: the row the address names, the one row that is selected, <c>aria-current</c> and the tab
/// stop, J and K and the ends of a page, Enter, and the address following the selection.
/// </summary>
public class RowSelectionTests(TideApp app) : IClassFixture<TideApp>
{
    /// <summary>
    /// A list page that adopts the selection, and does nothing else. In Razor it is the query parameter, a number, and:
    /// <code>
    /// &lt;li class="fruit-row @RowSelection.Css(i == _selected)" aria-current="@RowSelection.Current(i == _selected)"
    ///     tabindex="@RowSelection.TabIndex(i == _selected)" @onclick="() => _selected = i" @onfocus="() => _selected = i"&gt;
    /// &lt;RowKeys RowClass="fruit-row" Count="Shown.Count" @bind-Selected="_selected" OnOpen="Open" Address="Href"
    ///          Page="_page" Pages="Pages" OnPage="Turn" /&gt;
    /// </code>
    /// </summary>
    private sealed class Fruit : ComponentBase
    {
        internal static readonly string[] All = ["Apple", "Banana", "Cherry", "Damson", "Elderberry"];

        [SupplyParameterFromQuery(Name = RowSelection.Param)] public string? Row { get; set; }
        [Parameter] public int PageSize { get; set; } = All.Length;

        private int _selected, _page = 1;
        internal RowKeys Keys = default!;
        internal string? Opened;

        private int Pages => (All.Length + PageSize - 1) / PageSize;
        private List<string> Shown => All.Skip((_page - 1) * PageSize).Take(PageSize).ToList();

        protected override void OnInitialized() => _selected = RowSelection.Clamp(RowSelection.From(Row), Shown.Count);

        internal void Pick(int i)
        {
            _selected = i;
            StateHasChanged();
        }

        private void Turn(RowMove move)
        {
            _page += move == RowMove.NextPage ? 1 : -1;
            _selected = move == RowMove.PreviousPage ? RowSelection.Last(Shown.Count) : 0;
        }

        private static string Href(int row) => "fruit" + (RowSelection.Query(row) is { } q ? "?" + q : "");

        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            var shown = Shown;
            b.OpenElement(0, "ul");
            for (var i = 0; i < shown.Count; i++)
            {
                var (at, selected) = (i, i == _selected);
                b.OpenElement(1, "li");
                b.SetKey(shown[i]);
                b.AddAttribute(2, "class", "fruit-row " + RowSelection.Css(selected));
                b.AddAttribute(3, "aria-current", RowSelection.Current(selected));
                b.AddAttribute(4, "tabindex", RowSelection.TabIndex(selected));
                b.AddAttribute(5, "onclick", EventCallback.Factory.Create(this, () => _selected = at));
                b.AddAttribute(6, "onfocus", EventCallback.Factory.Create(this, () => _selected = at));
                b.AddContent(7, shown[i]);
                b.CloseElement();
            }
            b.CloseElement();
            b.OpenComponent<RowKeys>(8);
            b.AddComponentParameter(9, nameof(RowKeys.RowClass), "fruit-row");
            b.AddComponentParameter(10, nameof(RowKeys.Count), shown.Count);
            b.AddComponentParameter(11, nameof(RowKeys.Selected), _selected);
            b.AddComponentParameter(12, nameof(RowKeys.SelectedChanged), EventCallback.Factory.Create<int>(this, i => _selected = i));
            b.AddComponentParameter(13, nameof(RowKeys.OnOpen), EventCallback.Factory.Create<int>(this, i => Opened = Shown[i]));
            b.AddComponentParameter(14, nameof(RowKeys.Address), (Func<int, string>)Href);
            b.AddComponentParameter(15, nameof(RowKeys.Page), _page);
            b.AddComponentParameter(16, nameof(RowKeys.Pages), Pages);
            b.AddComponentParameter(17, nameof(RowKeys.OnPage), EventCallback.Factory.Create<RowMove>(this, Turn));
            b.AddComponentReferenceCapture(18, k => Keys = (RowKeys)k);
            b.CloseComponent();
        }
    }

    private static List<string> Rows(string html) => Regex.Matches(html, "<li[^>]*>[^<]*</li>").Select(m => m.Value).ToList();

    // Exactly the row at `at` is selected, and the rows are these.
    private static void OnlySelected(string html, int at, params string[] names)
    {
        var rows = Rows(html);
        rows.Should().HaveCount(names.Length);
        for (var i = 0; i < rows.Count; i++)
            rows[i].Should().Be(i == at
                ? $"<li class=\"fruit-row selected\" aria-current=\"true\" tabindex=\"0\">{names[i]}</li>"
                : $"<li class=\"fruit-row \" tabindex=\"-1\">{names[i]}</li>");
    }

    private Task<ComponentHarness.Rendered<Fruit>> List(string query = "", int? pageSize = null) =>
        ComponentHarness.RenderAsync<Fruit>(app, "/fruit" + query, pageSize is { } size ? new() { ["PageSize"] = size } : null);

    [Theory]
    [InlineData("", 0)]
    [InlineData("?row=3", 3)]
    [InlineData("?row=4", 4)]
    [InlineData("?row=77", 4)]
    [InlineData("?row=-1", 0)]
    [InlineData("?row=two", 0)]
    [InlineData("?row=%22%3E%3Cb%3Ex", 0)]
    public async Task TheRowInTheAddress_IsTheOneSelected_AndTheListsOneTabStop(string query, int expected)
    {
        await using var r = await List(query);
        var html = await r.Html();

        OnlySelected(html, expected, Fruit.All);
        html.Should().NotContain("<b>x");
    }

    [Fact]
    public async Task JAndK_MoveTheSelection_AndStopAtTheEndsOfAListOfOnePage()
    {
        await using var r = await List("?row=3");

        await r.Click(f => f.Keys.OnRowKey("next"));
        OnlySelected(await r.Html(), 4, Fruit.All);
        await r.Click(f => f.Keys.OnRowKey("next"));
        OnlySelected(await r.Html(), 4, Fruit.All);

        for (var i = 0; i < 4; i++) await r.Click(f => f.Keys.OnRowKey("prev"));
        OnlySelected(await r.Html(), 0, Fruit.All);
        await r.Click(f => f.Keys.OnRowKey("prev"));
        OnlySelected(await r.Html(), 0, Fruit.All);
    }

    [Fact]
    public async Task OnAPagedList_JOnTheLastRowGoesOnToTheNextPage_AndKOnTheFirstBackToThePreviousPagesLast()
    {
        await using var r = await List(pageSize: 2);
        OnlySelected(await r.Html(), 0, "Apple", "Banana");

        await r.Click(f => f.Keys.OnRowKey("next"));
        OnlySelected(await r.Html(), 1, "Apple", "Banana");
        await r.Click(f => f.Keys.OnRowKey("next"));
        OnlySelected(await r.Html(), 0, "Cherry", "Damson");

        await r.Click(f => f.Keys.OnRowKey("prev"));
        OnlySelected(await r.Html(), 1, "Apple", "Banana");

        for (var i = 0; i < 3; i++) await r.Click(f => f.Keys.OnRowKey("next"));
        OnlySelected(await r.Html(), 0, "Elderberry");
        await r.Click(f => f.Keys.OnRowKey("next"));
        OnlySelected(await r.Html(), 0, "Elderberry");   // the last row of the last page: J stops
    }

    [Fact]
    public async Task Enter_OpensTheSelectedRow_AndAKeyThatIsNotTheListsDoesNothing()
    {
        await using var r = await List("?row=2");

        await r.Click(f => f.Keys.OnRowKey("open"));
        r.Component.Opened.Should().Be("Cherry");

        await r.Click(f => f.Keys.OnRowKey("shortlist"));
        OnlySelected(await r.Html(), 2, Fruit.All);
    }

    // The script calls a list makes, kept with what they were sent.
    private sealed class Scripts : IJSRuntime
    {
        public List<(string Call, object?[] Args)> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add((identifier, args ?? []));
            return ValueTask.FromResult(identifier == "tideRows.start" ? (TValue)(object)7 : default!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public IEnumerable<object?> Sent(string call) => Calls.Where(c => c.Call == call).Select(c => c.Args.LastOrDefault());
    }

    [Fact]
    public async Task InABrowser_TheListTakesTheKeysOnce_TheAddressFollowsTheSelection_AndTheKeysAreGivenBack()
    {
        var scripts = new Scripts();
        var r = new RecordingRenderer(new ServiceCollection().AddSingleton<IJSRuntime>(scripts).BuildServiceProvider());
        var fruit = await r.Mount<Fruit>();

        scripts.Calls.Select(c => c.Call).Should().Equal("tideRows.start");
        scripts.Calls[0].Args[1].Should().BeEquivalentTo(new { row = "fruit-row" });

        // By a key, by a click or focus (the page setting it), and back to the first row, which the address leaves out.
        await r.Dispatcher.InvokeAsync(() => fruit.Keys.OnRowKey("next"));
        await r.Do(() => fruit.Pick(3));
        await r.Do(() => fruit.Pick(3));
        await r.Do(() => fruit.Pick(0));
        scripts.Sent("tideRows.remember").Should().Equal("fruit?row=1", "fruit?row=3", "fruit");
        r.Shows("selected").Should().BeTrue();

        await r.DisposeAsync();
        scripts.Sent("tideRows.stop").Should().Equal([7], "the list gives back the keys it was given, and no other list's");
        scripts.Calls.Count(c => c.Call == "tideRows.start").Should().Be(1);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("0", 0)]
    [InlineData("12", 12)]
    [InlineData("-4", 0)]
    [InlineData("1e3", 0)]
    [InlineData("99999999999", 0)]
    [InlineData(" 3", 0)]
    [InlineData("li.selected", 0)]
    public void From_ReadsAPlace_OrTheFirstRow(string? row, int expected) => RowSelection.From(row).Should().Be(expected);

    [Fact]
    public void Query_NamesTheRow_UnlessItIsTheFirst()
    {
        RowSelection.Query(3).Should().Be("row=3");
        RowSelection.Query(0).Should().BeNull();
    }

    [Theory]
    // next, selected, count, page, pages => move, selected after
    [InlineData(true, 0, 3, 1, 1, RowMove.Within, 1)]
    [InlineData(true, 2, 3, 1, 1, RowMove.Within, 2)]
    [InlineData(true, 2, 3, 1, 2, RowMove.NextPage, 2)]
    [InlineData(true, 2, 3, 2, 2, RowMove.Within, 2)]
    [InlineData(false, 1, 3, 2, 2, RowMove.Within, 0)]
    [InlineData(false, 0, 3, 2, 2, RowMove.PreviousPage, 0)]
    [InlineData(false, 0, 3, 1, 2, RowMove.Within, 0)]
    [InlineData(true, 0, 0, 1, 1, RowMove.Within, 0)]    // an empty list
    [InlineData(false, 0, 0, 1, 1, RowMove.Within, 0)]
    [InlineData(true, 0, 0, 1, 2, RowMove.NextPage, 0)]
    public void Step_MovesWithinThePage_OrAsksForTheNextOne(bool next, int selected, int count, int page, int pages, RowMove move, int after)
    {
        RowSelection.Step(next, ref selected, count, page, pages).Should().Be(move);
        selected.Should().Be(after);
    }

    [Fact]
    public void Clamp_HoldsThePlaceToTheRows()
    {
        RowSelection.Clamp(9, 4).Should().Be(3);
        RowSelection.Clamp(2, 4).Should().Be(2);
        RowSelection.Clamp(5, 0).Should().Be(0);
    }
}
