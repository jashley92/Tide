using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tide.Web.Components.Layout;

namespace Tide.IntegrationTests;

/// <summary>A page in <typeparamref name="TLayout"/>, with a context rail when it's given a title for one. The
/// layout's AuthorizeViews are told who's signed in, as the circuit tells them in the app.</summary>
internal sealed class PageIn<TLayout> : ComponentBase where TLayout : LayoutComponentBase
{
    [Inject] private IHttpContextAccessor Http { get; set; } = default!;
    [Parameter] public string? RailTitle { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
        builder.AddComponentParameter(1, "Value", Task.FromResult(new AuthenticationState(Http.HttpContext!.User)));
        builder.AddComponentParameter(2, "ChildContent", (RenderFragment)InLayout);
        builder.CloseComponent();
    }

    private void InLayout(RenderTreeBuilder builder)
    {
        builder.OpenComponent<LayoutView>(0);
        builder.AddComponentParameter(1, nameof(LayoutView.Layout), typeof(TLayout));
        builder.AddComponentParameter(2, nameof(LayoutView.ChildContent), (RenderFragment)(page =>
        {
            page.AddMarkupContent(0, "<h1>A page</h1>");
            if (RailTitle is null) return;
            page.OpenComponent<ContextRail>(1);
            page.AddComponentParameter(2, nameof(ContextRail.Title), RailTitle);
            page.AddComponentParameter(3, nameof(ContextRail.ChildContent), (RenderFragment)(rail => rail.AddMarkupContent(0, "<p>What we know about it.</p>")));
            page.CloseComponent();
        }));
        builder.CloseComponent();
    }
}

/// <summary>
/// The context rail: the region on the right of the main layout that a page fills by placing a ContextRail. A page
/// that places none gets no rail and no button, in either layout; one that does gets a labelled region and its button.
/// </summary>
public class ContextRailTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<string> Page<TLayout>(string? railTitle) where TLayout : LayoutComponentBase
    {
        await using var page = await ComponentHarness.RenderAsync<PageIn<TLayout>>(app, "/articles", new() { ["RailTitle"] = railTitle });
        return await page.Html();
    }

    private const string Content = "<article id=\"main-content\" tabindex=\"-1\" class=\"content\">";

    /// <summary>The markup from <paramref name="start"/> to the first <paramref name="end"/> after it, both included.</summary>
    private static string Between(string html, string start, string end)
    {
        var from = html.IndexOf(start, StringComparison.Ordinal);
        from.Should().BeGreaterThanOrEqualTo(0, $"the page has {start}");
        return html[from..(html.IndexOf(end, from, StringComparison.Ordinal) + end.Length)];
    }

    private static string ContentArea(string html) => Between(html, Content, "</article>");

    [Fact]
    public async Task APageWithoutRailContent_HasNoRail_AndNoButton()
    {
        var html = await Page<MainLayout>(railTitle: null);
        html.Should().Contain(Content).And.Contain("<h1>A page</h1>");
        html.Should().NotContain("tide-rail").And.NotContain("<aside");
    }

    [Fact]
    public async Task EveryRealPage_StillHasNoRail()
    {
        var client = app.CreateClient();
        foreach (var path in new[] { "/", "/articles", "/actions", "/admin" })
        {
            var html = await client.GetStringAsync(path);
            html.Should().Contain(Content, path);
            html.Should().NotContain("tide-rail", path).And.NotContain("<aside", path);
        }
    }

    [Fact]
    public async Task APageWithRailContent_GetsALabelledRegionBesideTheContent_AndItsButtonInTheTopBar()
    {
        var html = await Page<MainLayout>("About this item");

        Between(html, "<aside", "</aside>").Should().StartWith("<aside id=\"tide-rail\" class=\"tide-rail\" aria-label=\"About this item\" tabindex=\"-1\">")
            .And.Contain("<h2 class=\"tide-rail-title\">About this item</h2>")
            .And.Contain("<p>What we know about it.</p>");
        html.IndexOf("<aside", StringComparison.Ordinal).Should().BeGreaterThan(html.IndexOf("</article>", StringComparison.Ordinal), "it follows the content");
        html.Split("<aside").Should().HaveCount(2, "one rail");
        ContentArea(html).Should().Be(ContentArea(await Page<MainLayout>(railTitle: null)), "the content area is as it is without a rail");

        Between(html, "<button type=\"button\" id=\"tide-rail-toggle\"", "</button>").Should().Contain("aria-expanded=\"false\"").And.Contain("aria-controls=\"tide-rail\"").And.Contain("aria-keyshortcuts=\"]\"")
            .And.Contain(">Context<").And.Contain("<kbd");
        html.IndexOf("tide-rail-toggle", StringComparison.Ordinal).Should()
            .BeGreaterThan(html.IndexOf("class=\"top-row\"", StringComparison.Ordinal))
            .And.BeLessThan(html.IndexOf(Content, StringComparison.Ordinal), "the button is in the top bar");
    }

    [Fact]
    public async Task PresentModesLayout_NeverDrawsARail()
    {
        var html = await Page<BlankLayout>("About this item");
        html.Should().Contain("<h1>A page</h1>");
        html.Should().NotContain("tide-rail").And.NotContain("<aside").And.NotContain("What we know about it.");
    }

    [Fact]
    public void TheRailsKey_IsListed_OnlyOnAPageWithARail_AsALetterShortcut()
    {
        static IEnumerable<Shortcut> Keys(bool rail) => Shortcuts.For("articles", rail).SelectMany(g => g.Keys);
        Keys(rail: false).Should().NotContain(k => k.Keys.Contains("]"), "] does nothing on a page with no rail");
        var key = Keys(rail: true).Should().ContainSingle(k => k.Keys.Contains("]")).Subject;
        key.Letter.Should().BeTrue("a single character, so the letter switch turns it off");
        Shortcuts.For("articles", rail: true).Select(g => g.Title).Should().Equal(Shortcuts.For("articles").Select(g => g.Title), "the key joins Everywhere");
    }

    // The "?" overlay opened on a page, in a browser whose page has a rail or hasn't (theme.js's tideRail.has).
    private sealed class Browser(bool rail) : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(identifier is "tideRail.has" or "tideShortcuts.on" ? (TValue)(object)(identifier == "tideShortcuts.on" || rail) : default!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheShortcutsOverlay_ListsTheRailsKey_OnlyWhenThePageHasARail(bool rail)
    {
        await using var sp = new ServiceCollection().AddSingleton<Microsoft.JSInterop.IJSRuntime>(new Browser(rail))
            .AddSingleton<NavigationManager, AtArticles>()
            .AddSingleton<Tide.Application.Abstractions.ICurrentUser>(new FakeUser(Tide.Domain.Enums.AppRole.Analyst)).BuildServiceProvider();
        using var renderer = new RecordingRenderer(sp);
        var overlay = await renderer.Mount<ShortcutsOverlay>();
        await renderer.Dispatcher.InvokeAsync(overlay.Open);

        renderer.Shows("Keyboard shortcuts").Should().BeTrue("the overlay opened");
        renderer.Shows("Go to a page, article or action").Should().BeTrue();
        renderer.Shows("Show or hide the context rail").Should().Be(rail, rail ? "this page has a rail for ] to show" : "] does nothing here");
    }

    private sealed class AtArticles : NavigationManager
    {
        public AtArticles() => Initialize("http://localhost/", "http://localhost/articles");
    }
}
