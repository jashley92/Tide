using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Assist;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>The article page's draft pane folds itself away once every suggestion is decided and one went in.</summary>
public class DraftPaneFoldTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private async Task<ComponentHarness.Rendered<DraftAssist>> Pane(int bullets)
    {
        Guid id;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            id = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Draft pane fold test", "Vendor", "https://vendor.example.com/fold");
        var pane = await ComponentHarness.RenderAsync<DraftAssist>(app, $"/articles/{id}", new() { ["ArticleId"] = id, ["Form"] = new ArticleForm() });
        var draft = new DraftResult(Guid.NewGuid(), new Suggestions("The bottom line.",
            [.. Enumerable.Range(1, bullets).Select(n => new SuggestedBullet($"Bullet {n}", []))], null, null, [], [], [], []), "the source");
        await pane.Renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(DraftAssist).GetMethod("Show", Private)!.Invoke(pane.Component, [draft, "Pasted answer"]);
            typeof(DraftAssist).GetField("_open", Private)!.SetValue(pane.Component, true);
        });
        return pane;
    }

    private static bool Open(ComponentHarness.Rendered<DraftAssist> pane) => (bool)typeof(DraftAssist).GetField("_open", Private)!.GetValue(pane.Component)!;

    [Fact]
    public async Task WhenTheLastSuggestionIsDecided_AndOneWasAccepted_ThePaneFolds()
    {
        await using var pane = await Pane(bullets: 1);
        await pane.Click(c => c.Accept("bluf", () => { }));
        Open(pane).Should().BeTrue("a bullet is still to decide");

        await pane.Click(c => c.Reject("bullet:0"));
        Open(pane).Should().BeFalse("everything is decided and the bottom line went in");
        ((string)typeof(DraftAssist).GetMethod("Summary", Private)!.Invoke(pane.Component, [])!).Should().Be("done · 1 accepted · 1 rejected");
    }

    [Fact]
    public async Task WhenEverySuggestionIsRejected_ThePaneStaysOpen()
    {
        await using var pane = await Pane(bullets: 0);
        await pane.Click(c => c.Reject("bluf"));
        Open(pane).Should().BeTrue("nothing went into the article, so the draft hasn't done its job");
    }
}
