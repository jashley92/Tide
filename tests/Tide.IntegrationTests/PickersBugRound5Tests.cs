using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Adversaries;
using Tide.Application.Articles;
using Tide.Application.History;
using Tide.Application.Intake;
using Tide.Application.Stack;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Bug round 5 (pickers): Enter (or a click) while the search for what was just typed is still waiting out its pause
/// acted on the list for what was typed before; and an article's history didn't say which threat actors or products
/// were tagged, now that tagging is the one place to name them.
/// </summary>
public class PickersBugRound5Tests(TideApp app) : IClassFixture<TideApp>
{
    // As Blazor delivers a keystroke: the box's text set, and the card's Typed started, still waiting out its pause.
    private static async Task<Task> Keystroke<T>(Rendered<T> r, string text, Action<T, string> set) where T : IComponent
    {
        Task pending = Task.CompletedTask;
        await r.Renderer.Dispatcher.InvokeAsync(() =>
        {
            var typed = typeof(T).GetMethod("Typed", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? typeof(T).BaseType!.GetMethod("Typed", BindingFlags.Instance | BindingFlags.NonPublic)!;
            r.Scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = r.Signedin;
            // Through the component's own event handling, which renders as the handler first waits (as @bind-Text:after does).
            pending = ((IHandleEvent)r.Component).HandleEventAsync(new EventCallbackWorkItem((Func<Task>)(() =>
            {
                set(r.Component, text);
                return (Task)typed.Invoke(r.Component, null)!;
            })), null);
        });
        return pending;
    }

    private static Task Enter(SearchCombobox<Tide.Application.Search.TagPick> combo) => combo.OnKey(new KeyboardEventArgs { Key = "Enter" });

    private async Task<(Guid Article, Dictionary<string, Guid> Ids)> Profiles(string title, params (string Name, string? Mitre)[] profiles)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var svc = scope.ServiceProvider.GetRequiredService<AdversaryService>();
        var ids = new Dictionary<string, Guid>();
        foreach (var (name, mitre) in profiles) ids[name] = await svc.SaveAsync(new AdversaryForm { Name = name, MitreId = mitre });
        var article = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(title, "Vendor", null, TlpLevel.Clear);
        return (article, ids);
    }

    private async Task<IReadOnlyList<string>> TaggedActors(Guid article)
    {
        await using var check = SignedIn(app, SignIn.Analyst);
        return (await check.ServiceProvider.GetRequiredService<AdversaryService>().ForArticleAsync(article)).Tagged.Select(a => a.Name).ToList();
    }

    [Fact]
    public async Task EnterWhileTheSearchWaits_DoesNotTagWhatTheEarlierWordsFound()
    {
        var (article, ids) = await Profiles("Round 5 stale enter", ("r5pick APT28", null), ("r5pick APT29", null));
        await using var r = await RenderAsync<ArticleAdversaryCard>(app, $"/articles/{article}", new() { ["ArticleId"] = article, ["CanWrite"] = true });
        await r.Click(c =>
        {
            c._combo!._focused = true;
            c._text = "r5pick APT2";
            return c.Find();
        });
        r.Component.Listed.Select(p => p.Name).Should().Equal("r5pick APT28", "r5pick APT29");

        // "9" typed, and Enter at once: the search for "r5pick APT29" hasn't run yet.
        var pending = await Keystroke(r, "r5pick APT29", (c, t) => c._text = t);
        await r.Click(c => Enter(c._combo!));
        await pending;

        (await TaggedActors(article)).Should().NotContain("r5pick APT28", "APT28 doesn't match what was typed when Enter was pressed");
        r.Component.Listed.Select(p => p.Id).Should().NotContain(ids["r5pick APT28"]);
    }

    [Fact]
    public async Task AMitreIdPastedAndEntered_AtOnce_TagsItsProfile_RatherThanAddingAProfileCalledThat()
    {
        var (article, ids) = await Profiles("Round 5 pasted id", ("r5pick Cozy", "G0916"));
        await using var r = await RenderAsync<ArticleAdversaryCard>(app, $"/articles/{article}", new() { ["ArticleId"] = article, ["CanWrite"] = true });
        await r.Click(c =>
        {
            c._combo!._focused = true;
            return Task.CompletedTask;
        });

        var pending = await Keystroke(r, "G0916", (c, t) => c._text = t);
        await r.Click(c => Enter(c._combo!));
        await pending;

        await using var check = SignedIn(app, SignIn.Analyst);
        (await check.ServiceProvider.GetRequiredService<AppDbContext>().Adversaries.CountAsync(a => a.Name == "G0916"))
            .Should().Be(0, "a profile already goes by that MITRE id");
        r.Component.Listed.Select(p => p.Id).Should().Equal([ids["r5pick Cozy"]], "the list now shows what was typed");
        r.Component.CanAdd.Should().BeFalse();
    }

    [Fact]
    public async Task AStoryNameTypedAndEntered_AtOnce_DoesNotLinkTheStoryOfferedBeforeTyping()
    {
        Guid article, offered;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var stories = scope.ServiceProvider.GetRequiredService<StoryService>();
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            offered = await stories.CreateAsync("r5story Offered campaign", null);
            await stories.LinkAsync(await articles.CreateAsync("Round 5 offered story's article", "Vendor", null, TlpLevel.Clear), offered);
            article = await articles.CreateAsync("Round 5 story enter", "Vendor", null, TlpLevel.Clear);
        }
        await using var r = await RenderAsync<StoryPicker>(app, $"/articles/{article}", new() { ["ArticleId"] = article });
        r.Component._found.Select(s => s.Id).Should().Contain(offered, "with nothing typed, the newest open stories are offered");
        r.Component._found[0].Id.Should().Be(offered);

        var pending = await Keystroke(r, "r5story Volt Typhoon wave", (c, t) => c._text = t);
        await r.Click(c => c._combo!.OnKey(new KeyboardEventArgs { Key = "Enter" }));
        await pending;

        await using var check = SignedIn(app, SignIn.Analyst);
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Articles.SingleAsync(a => a.Id == article)).StoryId.Should().NotBe(offered, "that story isn't what was typed");
        r.Component.CanStart.Should().BeTrue("the list now shows what was typed: no story by that name, so one can be started");
    }

    [Fact]
    public async Task AnArticlesHistory_SaysWhichThreatActorsAndProductsWereTaggedAndUntagged()
    {
        Guid article, actor, product;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            actor = await sp.GetRequiredService<AdversaryService>().SaveAsync(new AdversaryForm { Name = "r5hist Scattered Spider" });
            product = await sp.GetRequiredService<StackService>().SaveAsync(new StackForm { Name = "r5hist Edge Gateway" });
            article = await sp.GetRequiredService<ArticleService>().CreateAsync("Round 5 history", "Vendor", null, TlpLevel.Clear);
            await sp.GetRequiredService<AdversaryService>().TagArticleAsync(article, actor);
            await sp.GetRequiredService<StackService>().TagArticleAsync(article, product);
            await sp.GetRequiredService<AdversaryService>().UntagArticleAsync(article, actor);
        }

        await using var read = SignedIn(app, SignIn.Analyst);
        var page = await read.ServiceProvider.GetRequiredService<HistoryService>().ArticleAsync(article);

        page.Entries.Should().Contain(e => e.What == "Added a threat actor" && e.Changes.Single().After == "r5hist Scattered Spider")
            .And.Contain(e => e.What == "Removed a threat actor" && e.Changes.Single().After == "r5hist Scattered Spider")
            .And.Contain(e => e.What == "Added a product we run" && e.Changes.Single().After == "r5hist Edge Gateway");
    }
}
