using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Intake;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Bug pass (final 1), services: a story's form saved over a colleague's save, and pasted text past what a
/// paste box takes.</summary>
public class BugPassFinal1ServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public BugPassFinal1ServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AStorysForm_SavedAfterAColleaguesSave_IsRefused_AndTheirsKept()
    {
        var stories = _db.Stories();
        var id = await stories.CreateAsync("Edge VPN appliance exploitation", "Exploitation of remote-access gateways.");
        var opened = (await stories.GetAsync(id))!.Story.EditVersion;   // both open Edit on this

        await stories.UpdateAsync(id, "Edge VPN appliance exploitation", "RECHECK: a new summary.", StoryStatus.Closed, opened);

        await FluentActions.Invoking(() => stories.UpdateAsync(id, "Edge VPN exploitation", "Exploitation of remote-access gateways.", StoryStatus.Active, opened))
            .Should().ThrowAsync<StaleEditException>().WithMessage("Someone else saved Edge VPN appliance exploitation since you opened it*");
        var story = (await stories.GetAsync(id))!.Story;
        story.Summary.Should().Be("RECHECK: a new summary.", "the colleague's save stands");
        story.Status.Should().Be(StoryStatus.Closed);
        story.Name.Should().Be("Edge VPN appliance exploitation");

        // Opened again, on top of theirs, it saves.
        await stories.UpdateAsync(id, "Edge VPN exploitation", story.Summary, story.Status, story.EditVersion);
        (await stories.GetAsync(id))!.Story.Name.Should().Be("Edge VPN exploitation");
    }

    [Fact]
    public async Task LinkingAnArticle_DoesntMakeAnOpenStoryFormStale()
    {
        var stories = _db.Stories();
        var id = await stories.CreateAsync("Ransomware wave", null);
        var opened = (await stories.GetAsync(id))!.Story.EditVersion;
        await stories.LinkAsync(await _db.Articles().CreateAsync("Wave report", "Vendor", null, TlpLevel.Clear), id);

        await stories.UpdateAsync(id, "Ransomware wave, autumn", null, StoryStatus.Active, opened);
        (await stories.GetAsync(id))!.Story.Name.Should().Be("Ransomware wave, autumn");
    }

    [Fact]
    public void PastedTextPastWhatAPasteBoxTakes_IsRefusedWithASentence_NotSearchedPartly()
    {
        var text = string.Join("\n", Enumerable.Range(0, ArticleService.MaxPaste / 10 + 1).Select(i => $"10.0.{i / 250 % 250}.{i % 250}"));
        text.Length.Should().BeGreaterThan(ArticleService.MaxPaste);

        FluentActions.Invoking(() => ArticleService.Suggest(text, [], []))
            .Should().Throw<ArgumentException>().WithMessage($"*{ArticleService.MaxPaste:N0} characters*");
        ArticleService.Suggest(text[..ArticleService.MaxPaste], [], []).Indicators.Should().NotBeEmpty();
    }
}

/// <summary>Bug pass (final 1), pages: the story page's banner and refusal, the size of what the browser may send, every
/// text box's limit, and the pickers' keys.</summary>
public partial class BugPassFinal1PageTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Colleague = new("dev:bpf1-robin", "Robin", "Analyst");

    private static List<string> Toasts<T>(Rendered<T> page) =>
        page.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Select(t => t.Message).ToList();

    [Fact]
    public async Task AStoryEditedMeanwhile_SaysWhoSavedIt_KeepsWhatsTyped_AndRefusesTheSave()
    {
        Guid id;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            id = await scope.ServiceProvider.GetRequiredService<StoryService>().CreateAsync("BPF1 story " + Guid.NewGuid().ToString("N")[..6], "The original summary.");
        await using var page = await RenderAsync<Stories>(app, $"/stories/{id}", new() { [nameof(Stories.Id)] = (Guid?)id });
        await page.Click(p => { p.StartEdit(); p.TypedName = "Typed, not saved"; return Task.CompletedTask; });

        await using (var robin = SignedIn(app, Colleague))
        {
            // Robin saves through the page's own origin, so the page hears it only when the test hands it over.
            var svc = ActivatorUtilities.CreateInstance<StoryService>(robin.ServiceProvider, page.Scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>());
            var s = (await svc.GetAsync(id))!.Story;
            await svc.UpdateAsync(id, s.Name, "Robin's summary.", StoryStatus.Closed, s.EditVersion);
        }
        await page.Click(p => p.OnChangedElsewhere([new Change(ChangeFeed.Story(id), Colleague.UserId, Colleague.Name, Guid.NewGuid())]));

        page.Component.ChangedBy.Should().Be("Robin");
        page.Component.TypedName.Should().Be("Typed, not saved", "what's typed stays");
        (await page.Html()).Should().Contain("Robin saved this story while you were editing.");

        await page.Click(p => p.Save());
        Toasts(page).Should().Contain(m => m.StartsWith("Someone else saved"));
        await using (var check = SignedIn(app, SignIn.Analyst))
        {
            var story = (await check.ServiceProvider.GetRequiredService<StoryService>().GetAsync(id))!.Story;
            story.Summary.Should().Be("Robin's summary.", "Robin's save stands");
            story.Status.Should().Be(StoryStatus.Closed);
        }

        // Reload shows theirs in the form, and then a save goes through.
        await page.Click(p => p.ReloadTheirs());
        page.Component.ChangedBy.Should().BeNull();
        page.Component.TypedName.Should().NotBe("Typed, not saved");
        await page.Click(p => { p.TypedName = "Renamed after reload"; return p.Save(); });
        await using (var check = SignedIn(app, SignIn.Analyst))
            (await check.ServiceProvider.GetRequiredService<StoryService>().GetAsync(id))!.Story.Name.Should().Be("Renamed after reload");
    }

    [Fact]
    public void WhatTheBrowserMaySendInOneMessage_HoldsTheLongestTextBox()
    {
        // A box's text goes to the server in one message; past the hub's limit the connection drops and the text is lost.
        // Each character is at most a few bytes as the browser sends it (UTF-8, with JSON's escapes).
        var hub = typeof(Microsoft.AspNetCore.Components.Server.CircuitOptions).Assembly.GetType("Microsoft.AspNetCore.Components.Server.ComponentHub", true)!;
        var options = (IOptions<HubOptions>)app.Services.GetRequiredService(typeof(IOptions<>).MakeGenericType(typeof(HubOptions<>).MakeGenericType(hub)));
        var longest = Math.Max(ArticleService.MaxPaste, HuntQuery.MaxText);
        options.Value.MaximumReceiveMessageSize.Should().BeGreaterThanOrEqualTo(4L * longest);
    }

    [Fact]
    public void EveryTextBox_HasALimit()
    {
        // A box without one takes a paste of any size, which drops the connection (and the text) past the hub's limit.
        var root = EditorRailLayoutTests.WebRoot();
        var missing = Directory.EnumerateFiles(Path.Combine(root, "Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => TextBox().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Tag: m.Value)))
            .Where(t => !t.Tag.Contains("maxlength", StringComparison.OrdinalIgnoreCase) && !NotText().IsMatch(t.Tag))
            .Select(t => $"{t.File}: {t.Tag[..Math.Min(120, t.Tag.Length)]}")
            .ToList();
        missing.Should().BeEmpty();
    }

    // A tag, its quoted values whole (a lambda's => or a comparison's > inside one doesn't end it).
    [GeneratedRegex(@"<(textarea|input)\b(?:[^>""']|""[^""]*""|'[^']*')*>", RegexOptions.Singleline)]
    private static partial Regex TextBox();

    [GeneratedRegex(@"type=""(checkbox|radio|number|date|file|hidden|time|range|color|month|week|datetime-local|button|submit)""")]
    private static partial Regex NotText();

    [Fact]
    public async Task ThePasteBox_SaysWhenTextIsPastWhatItTakes_RatherThanFindingNothing()
    {
        await using var r = await RenderAsync<IndicatorAdd>(app, "/articles", new()
        {
            [nameof(IndicatorAdd.Adding)] = (Func<IndicatorAdd.Addition, Task<bool>>)(_ => Task.FromResult(true)),
        });
        (await r.Html()).Should().Contain($"maxlength=\"{ArticleService.MaxPaste}\"");

        await r.Click(c => { c.FindIn(new string('x', ArticleService.MaxPaste + 1)); return Task.CompletedTask; });
        var html = await r.Html();
        html.Should().NotContain("Nothing new found.").And.Contain($"{ArticleService.MaxPaste:N0} characters");

        // A paste the box cut at its limit, with nothing in the part kept: Find says how far it looked.
        await r.Click(c => { c.FindIn(new string('x', ArticleService.MaxPaste)); return Task.CompletedTask; });
        (await r.Html()).Should().Contain($"Nothing new found in the first {ArticleService.MaxPaste:N0} characters.");
        await r.Click(c => { c.FindIn("nothing to find here"); return Task.CompletedTask; });
        (await r.Html()).Should().Contain("Nothing new found.").And.NotContain("in the first");
    }

    [Fact]
    public async Task ALengthNote_CountsNearTheLimit_AndSaysWhenItsReached()
    {
        await using var quiet = await RenderAsync<LengthNote>(app, "/", new() { ["Value"] = "short", ["Max"] = 600 });
        (await quiet.Html()).Should().NotContain("of 600");

        await using var near = await RenderAsync<LengthNote>(app, "/", new() { ["Value"] = new string('a', 550), ["Max"] = 600 });
        (await near.Html()).Should().Contain("550 of 600 characters");

        await using var full = await RenderAsync<LengthNote>(app, "/", new() { ["Value"] = new string('a', 600), ["Max"] = 600 });
        (await full.Html()).Should().Contain("600 of 600 characters").And.Contain("anything longer was cut");

        await using var over = await RenderAsync<LengthNote>(app, "/", new() { ["Value"] = new string('a', 601), ["Max"] = 600 });
        (await over.Html()).Should().Contain("601 of 600 characters").And.Contain("too long to save").And.NotContain("was cut");

        // A box that takes more than can be saved (a saved query's), filled to its cap by a paste: too long, and cut.
        await using var cut = await RenderAsync<LengthNote>(app, "/", new() { ["Value"] = new string('a', 2000), ["Max"] = 600, ["Cap"] = 2000 });
        (await cut.Html()).Should().Contain("2,000 of 600 characters").And.Contain("too long to save, and anything past 2,000 characters was cut");
    }

    [Fact]
    public void ASavedQuerysBox_TakesAWholePaste_SoALongOneIsRefusedRatherThanCutToNearlyFit()
    {
        // Cut at just past what can be saved, deleting a character would save a query cut short without a word.
        var root = EditorRailLayoutTests.WebRoot();
        foreach (var file in new[] { "Pages/SavedQueriesPage.razor", "Shared/HuntPanel.razor" })
            File.ReadAllText(Path.Combine(root, "Components", file)).Should()
                .Contain("maxlength=\"@ArticleService.MaxPaste\"").And.Contain("Max=\"SavedQuery.MaxText\" Cap=\"ArticleService.MaxPaste\"");
    }

    private async Task<(Guid Article, Guid Product)> ProductAndArticle(string name)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var product = await scope.ServiceProvider.GetRequiredService<StackService>().SaveAsync(new StackForm { Name = name + " VPN", Versions = "1.0" });
        var article = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(name + " picker", "Vendor", null, TlpLevel.Clear);
        return (article, product);
    }

    // The article's We run card, with words typed in its picker.
    private async Task<Rendered<ArticleStackCard>> CardTyped(Guid article, string text)
    {
        var r = await RenderAsync<ArticleStackCard>(app, $"/articles/{article}", new()
        {
            ["ArticleId"] = article, ["CanWrite"] = true,
            ["OnMention"] = Microsoft.AspNetCore.Components.EventCallback.Factory.Create<string>(this, _ => { }),
        });
        await r.Click(c => { c._combo!._focused = true; c._text = text; return c.Find(); });
        return r;
    }

    [Fact]
    public async Task ThePicker_HighlightsNothingUntilAnArrow_SoDownThenEnterTagsTheFirstMatch_NotANewProduct()
    {
        var name = "bpf1-" + Guid.NewGuid().ToString("N")[..6];
        var (article, product) = await ProductAndArticle(name);
        await using var r = await CardTyped(article, name);
        var html = await r.Html();
        html.Should().Contain($"Add “{name}” to Our stack").And.NotContain("aria-activedescendant", "nothing is chosen until an arrow chooses it");

        await r.Click(c => c._combo!.OnKey(new KeyboardEventArgs { Key = "ArrowDown" }));
        await r.Click(c => c._combo!.OnKey(new KeyboardEventArgs { Key = "Enter" }));

        await using var check = SignedIn(app, SignIn.Analyst);
        (await check.ServiceProvider.GetRequiredService<StackService>().ForArticleAsync(article)).Tagged.Select(p => p.Id).Should().Equal(product);
        (await check.ServiceProvider.GetRequiredService<AppDbContext>().StackProducts.CountAsync(p => p.Name == name))
            .Should().Be(0, "adding a product takes choosing Add itself");
    }

    [Fact]
    public async Task ThePicker_EnterWithNothingHighlighted_NeverAddsANewOne()
    {
        var name = "bpf1-none-" + Guid.NewGuid().ToString("N")[..6];
        var (article, _) = await ProductAndArticle("bpf1-other-" + Guid.NewGuid().ToString("N")[..6]);
        await using var r = await CardTyped(article, name);
        r.Component.Listed.Should().BeEmpty();

        await r.Click(c => c._combo!.OnKey(new KeyboardEventArgs { Key = "Enter" }));

        await using var check = SignedIn(app, SignIn.Analyst);
        (await check.ServiceProvider.GetRequiredService<AppDbContext>().StackProducts.CountAsync(p => p.Name == name)).Should().Be(0);
    }
}
