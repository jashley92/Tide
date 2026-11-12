using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Tide.Application.Intake;
using Tide.Domain.Enums;
using Tide.Web.Components.TriageRail;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Triage's "Already covered" rail, read while the analyst moves on: a slow read of the item they left never lands on
/// the item they're on, so its class, open actions and story are always the shown article's.
/// </summary>
public class TriageCoveredOrderTests(TideApp app) : IClassFixture<TideApp>
{
    /// <summary>The rail, with each article's read held until the test lets it go.</summary>
    public sealed class Held : TriageCovered
    {
        public static readonly Dictionary<Guid, TaskCompletionSource> Gates = [];

        protected override async Task<Known> ReadAsync(Coverage main)
        {
            if (Gates.TryGetValue(main.ArticleId, out var gate)) await gate.Task;
            return new Known(null, null, new StoryRow(Guid.NewGuid(), $"Story of {main.ArticleTitle}", StoryStatus.Active, 1, null));
        }
    }

    private static Task Set(Rendered<Held> r, Coverage c) =>
        r.Component.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(TriageCovered.Coverage)] = new[] { c } }));

    [Fact]
    public async Task ASlowReadOfTheItemLeft_NeverLandsOnTheItemShown()
    {
        var a = new Coverage("cve", Guid.NewGuid(), "Article A", null);
        var b = new Coverage("cve", Guid.NewGuid(), "Article B", null);
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Held.Gates[a.ArticleId] = slow;
        await using var r = await RenderAsync<Held>(app, "/triage", new() { [nameof(TriageCovered.Coverage)] = Array.Empty<Coverage>() });

        await r.Renderer.Dispatcher.InvokeAsync(() => { _ = Set(r, a); });   // A's read is in flight
        await r.Renderer.Dispatcher.InvokeAsync(() => Set(r, b));          // j: B, read at once
        (await r.Html()).Should().Contain("Story of Article B");

        slow.SetResult();                                                   // A's read lands late
        await Task.Delay(200);
        var html = await r.Html();
        html.Should().Contain("Article B").And.Contain("Story of Article B").And.NotContain("Story of Article A");
        Held.Gates.Remove(a.ArticleId);
    }
}
