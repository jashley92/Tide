using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Intake;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// A story's Add an existing article: Enter pressed right after typing, while the search for the words typed is still
/// waiting out its pause, must not add the first article the earlier words found.
/// </summary>
public class StoryAttachStaleEnterTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task EnterRightAfterTyping_DoesNotAddTheFirstArticleTheEarlierWordsFound()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        Guid story;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            story = await scope.ServiceProvider.GetRequiredService<StoryService>().CreateAsync($"Stale enter {tag}", null);
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            await articles.CreateAsync($"r5stale {tag} alpha", "Vendor", null, TlpLevel.Clear);
            await articles.CreateAsync($"r5stale {tag} bravo", "Vendor", null, TlpLevel.Clear);
        }
        await using var page = await RenderAsync<Stories>(app, $"/stories/{story}", new Dictionary<string, object?> { [nameof(Stories.Id)] = (Guid?)story });

        await page.Click(p => p.StartAttach());
        await page.Click(p => p.FindCandidates($"r5stale {tag}"));
        page.Component.Candidates.Should().HaveCount(2);
        var first = page.Component.Candidates[0];
        var wanted = page.Component.Candidates[1];

        // The rest of the wanted title typed, and Enter at once: the search for it is still waiting out its pause.
        Task pending = Task.CompletedTask;
        await page.Renderer.Dispatcher.InvokeAsync(() =>
        {
            page.Scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = page.Signedin;
            var typed = typeof(Stories).GetMethod("Typed", Private)!;
            pending = ((IHandleEvent)page.Component).HandleEventAsync(new EventCallbackWorkItem((Func<Task>)(() =>
            {
                typeof(Stories).GetField("_find", Private)!.SetValue(page.Component, wanted.Title);
                return (Task)typed.Invoke(page.Component, null)!;
            })), null);
        });
        var combo = (SearchCombobox<StoryCandidate>)typeof(Stories).GetField("_findBox", Private)!.GetValue(page.Component)!;
        await page.Click(_ => combo.OnKey(new KeyboardEventArgs { Key = "Enter" }));
        await pending;

        await using var check = SignedIn(app, SignIn.Analyst);
        var detail = await check.ServiceProvider.GetRequiredService<StoryService>().GetAsync(story);
        detail!.Articles.Select(a => a.Id).Should().NotContain(first.Id, $"'{first.Title}' isn't what was typed when Enter was pressed ('{wanted.Title}')");
    }
}
