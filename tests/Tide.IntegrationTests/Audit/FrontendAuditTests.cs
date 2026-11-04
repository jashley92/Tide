using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Briefs;
using Tide.Application.Intake;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests.Audit;

/// <summary>
/// Frontend audit: pages that stay open from one record to the next (the router keeps the component and hands it new
/// parameters) start each record afresh, a confirmation acts on what it counted, and a step can't run twice at once.
/// </summary>
public class FrontendAuditTests(TideApp app) : IClassFixture<TideApp>
{
    private static void SetField<T>(T target, string name, object? value) =>
        typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? GetField<T>(T target, string name) =>
        typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    // What the router does when a link (Ctrl+K, a parent action, the stepper) opens another record on the same page.
    private static Task Open<T>(Rendered<T> r, string name, object? value) where T : IComponent =>
        r.Click(c => c.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { [name] = value })));

    private async Task<Guid> NewStory(string name)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return await scope.ServiceProvider.GetRequiredService<StoryService>().CreateAsync(name, "Summary of " + name);
    }

    private async Task<string> StoryName(Guid id)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<StoryService>().GetAsync(id))!.Story.Name;
    }

    [Fact]
    public async Task AnotherStory_OpensWithTheEditFormClosed_SoOneStorysEditCantBeSavedOntoIt()
    {
        Guid a = await NewStory("Audit story A"), b = await NewStory("Audit story B");
        await using var r = await RenderAsync<Stories>(app, "/stories/" + a, new() { ["Id"] = (Guid?)a });
        r.Component.StartEdit();
        await Open(r, "Id", (Guid?)a);   // drawn again, as the Edit click would
        (await r.Html()).Should().Contain("id=\"st-name\"").And.Contain("value=\"Audit story A\"");

        await Open(r, "Id", (Guid?)b);
        var html = await r.Html();
        html.Should().Contain("<h1>Audit story B</h1>").And.NotContain("id=\"st-name\"", "the edit form was A's");

        await r.Click(s => s.Save());   // whatever is pressed now, B keeps its name
        (await StoryName(b)).Should().Be("Audit story B");
        (await StoryName(a)).Should().Be("Audit story A");

        // To the list and back: the New story form isn't prefilled with the story last edited.
        r.Component.StartEdit();
        await Open(r, "Id", (Guid?)null);
        (await r.Html()).Should().NotContain("value=\"Audit story B\"");
    }

    [Fact]
    public async Task Stories_ASecondClickWhileAStepRuns_DoesNothing()
    {
        await using var r = await RenderAsync<Stories>(app, "/stories");
        SetField(r.Component, "_name", "Audit double click");
        SetField(r.Component, "_busy", true);   // the first click's create still running
        await Open(r, "Id", (Guid?)null);
        await r.Click(s => s.Create());
        (await r.Html()).Should().MatchRegex("<button[^>]*disabled[^>]*>Add story</button>");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Stories.Count(s => s.Name == "Audit double click").Should().Be(0);
    }

    private async Task<ActionItem> NewAction(string title)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
        var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0];
        return (await actions.GetAsync(await actions.CreateAsync(new ActionForm { Title = title, Kind = ActionKind.Block, TeamId = team.Id })))!.Action;
    }

    [Fact]
    public async Task AnotherAction_DropsTheLastOnesEditFormAndCommentDraft()
    {
        ActionItem a = await NewAction("Audit action one"), b = await NewAction("Audit action two");
        await using var r = await RenderAsync<ActionPage>(app, "/actions/" + a.Number, new() { ["Number"] = (int?)a.Number });
        r.Component.StartEdit();
        SetField(r.Component, "_comment", "A draft for action one");
        await Open(r, "Number", (int?)a.Number);
        var before = await r.Html();
        before.Should().Contain("value=\"Audit action one\"").And.Contain("A draft for action one");

        await Open(r, "Number", (int?)b.Number);
        var html = await r.Html();
        html.Should().Contain("Audit action two")
            .And.NotContain("value=\"Audit action one\"", "action one's edit form doesn't follow to action two")
            .And.NotContain("A draft for action one", "nor does its comment draft, which would post on action two");
    }

    [Fact]
    public async Task TheArticlePage_AfterAMissingArticle_ShowsTheNextOne()
    {
        Guid id;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var article = new Article { Title = "Audit article after a missing one", AuthorId = "x", AuthorName = "X", Bluf = "Exploited." };
            db.Articles.Add(article);
            await db.SaveChangesAsync();
            id = article.Id;
        }
        var missing = Guid.NewGuid();
        await using var r = await RenderAsync<ArticleEditorPanel>(app, $"/articles/{missing}", new()
        {
            ["Id"] = missing, ["Aside"] = (RenderFragment<ArticleEditorPanel.State>)(_ => b => b.AddContent(0, "The preview")),
        });
        (await r.Html()).Should().Contain("That article doesn't exist");

        await Open(r, "Id", id);   // Ctrl+K to a real article: the page stays open
        var html = await r.Html();
        html.Should().NotContain("That article doesn't exist").And.Contain("Audit article after a missing one");
    }

    private async Task SeedItem(string key, int minutesAhead)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = db.Sources.FirstOrDefault(s => s.Name == "Audit source")
                     ?? db.Add(new Source { Name = "Audit source", Url = "https://audit.example/feed", DefaultTlp = TlpLevel.Clear }).Entity;
        var at = DateTimeOffset.UtcNow.AddMinutes(minutesAhead);
        db.Add(new FeedItem
        {
            Source = source, Title = "audit-dismiss " + key, Url = "https://audit.example/" + key, UrlHash = "audit-dismiss-" + key,
            PublishedAtUtc = at, FetchedAtUtc = at, Status = FeedItemStatus.New,
        });
        await db.SaveChangesAsync();
    }

    private FeedItemStatus StatusOf(string key)
    {
        using var scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().FeedItems.Single(i => i.UrlHash == "audit-dismiss-" + key).Status;
    }

    [Fact]
    public async Task DismissAll_DismissesWhatItCounted_NotWhatArrivedWhileItAsked()
    {
        await SeedItem("shown", 5);
        await using var r = await RenderAsync<Triage>(app, "/triage");
        await r.HtmlOnce("audit-dismiss shown");

        await r.Press(t => t.AskDismissAll());
        await r.HtmlOnce("Dismiss them as");

        // A poll brings an item while the question is open: the list reloads under it.
        await SeedItem("late", 60);
        app.Services.GetRequiredService<ChangeFeed>().Publish(ChangeFeed.Triage);
        (await r.HtmlOnce("audit-dismiss late")).Should().Contain("audit-dismiss late");

        var dialog = (ConfirmDialog)GetField(r.Component, "_dismissAll")!;
        // The confirm click, on the page's thread (as the button would, keeping the sign-in the question was asked with).
        await r.Renderer.Dispatcher.InvokeAsync(dialog.Confirm);
        for (var i = 0; i < 50 && StatusOf("shown") == FeedItemStatus.New; i++) await Task.Delay(100);

        StatusOf("shown").Should().Be(FeedItemStatus.Dismissed);
        StatusOf("late").Should().Be(FeedItemStatus.New, "it arrived after the question counted what it would dismiss");
    }

    private int QuickActions(Guid articleId)
    {
        using var scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Actions.Count(x => x.ArticleId == articleId && x.Title == "Audit quick action");
    }

    [Fact]
    public async Task Present_ASecondAddWhileTheFirstRuns_DoesNothing()
    {
        Guid articleId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var article = new Article { Title = "Audit present article", AuthorId = "x", AuthorName = "X", Bluf = "Exploited." };
            db.Articles.Add(article);
            await db.SaveChangesAsync();
            articleId = article.Id;
        }
        string day;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var brief = await briefs.GetOrStartAsync(briefs.CurrentDate);
            await briefs.AddArticleAsync(brief.Id, articleId);
            day = briefs.CurrentDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
        await using var r = await RenderAsync<Present>(app, "/present/" + day, new() { ["Day"] = day });
        SetField(r.Component, "_qaTitle", "Audit quick action");
        SetField(r.Component, "_busy", true);   // the first click's add still running
        await r.Click(p => p.QuickAdd(articleId));

        QuickActions(articleId).Should().Be(0);

        SetField(r.Component, "_busy", false);
        await r.Click(p => p.QuickAdd(articleId));
        QuickActions(articleId).Should().Be(1, "one click adds it once");
    }

    // ---- Round 2: what sits outside the page's error boundary, and double clicks on admin and send buttons ----

    [Fact]
    public async Task ThePalette_AFailedSearch_SaysSoInsteadOfEndingTheSession()
    {
        await using var r = await RenderAsync<Tide.Web.Components.Layout.CommandPalette>(app, "/");
        await r.Click(p => { p.Open(); return Task.CompletedTask; });
        SetField(r.Component, "_query", "vpn");
        Task? searching = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { searching = r.Pressed(p => p.OnQueryChanged()); });
        // While it waits for typing to pause, the person loses access, so the search is refused when it runs: a failed
        // search, which must not escape the palette (it sits outside the page's error boundary).
        r.Signedin.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        await searching!;
        await r.Click(p => { p.OnKey("down"); return Task.CompletedTask; });   // drawn again
        (await r.Html()).Should().Contain("The search didn't answer");
    }

    [Fact]
    public async Task AccessAdmin_ASecondGrantWhileOneRuns_DoesNothing()
    {
        await using var r = await RenderAsync<Tide.Web.Components.Pages.Admin.AccessAdmin>(app, "/admin/access");
        SetField(r.Component, "_group", "AuditDouble");
        SetField(r.Component, "_busy", true);   // the first click's grant still running
        await r.Click(a => a.Add());
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<AppDbContext>().RoleMappings.Count(m => m.AdGroup == "AuditDouble").Should().Be(0);

        SetField(r.Component, "_busy", false);
        await r.Click(a => a.Add());
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<AppDbContext>().RoleMappings.Count(m => m.AdGroup == "AuditDouble").Should().Be(1);
    }

    [Fact]
    public async Task Present_TheDKey_OnADayWithNoBrief_DoesNothing()
    {
        const string day = "2001-01-02";
        await using var r = await RenderAsync<Present>(app, "/present/" + day, new() { ["Day"] = day });
        var pressing = () => r.Click(p => p.OnPresentKey("discussed"));
        await pressing.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AToastsUndoThatFails_SaysSo_InsteadOfEndingTheSession()
    {
        await using var r = await RenderAsync<ToastHost>(app, "/");
        var toasts = r.Scope.ServiceProvider.GetRequiredService<Tide.Web.Services.ToastService>();
        toasts.Show("Took it off.", () => throw new InvalidOperationException("That brief has been sent."));
        var id = toasts.Toasts.Single(t => t.Undo is not null).Id;

        var undoing = () => r.Click(h => h.Undo(id));
        await undoing.Should().NotThrowAsync();
        toasts.Toasts.Should().Contain(t => t.IsError && t.Message == "That brief has been sent.");
    }

    [Fact]
    public async Task TheSendDialog_ASecondSendWhileOneRuns_DoesNothing()
    {
        DateOnly day;
        await using (var briefs = SignedIn(app, SignIn.Analyst))
        {
            var svc = briefs.ServiceProvider.GetRequiredService<BriefService>();
            day = svc.CurrentDate;
            await svc.GetOrStartAsync(day);
        }
        await using var r = await RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = day });
        var plan = await SendFromTodayTests.SignedIn(r).PlanAsync(day, Edition.Team);
        SetField(r.Component, "_busy", true);   // the first click's send still running
        Task<bool>? sending = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { sending = r.Pressed(d => d.SendNow(plan)) as Task<bool>; });
        (await sending!).Should().BeFalse("the first send is still going");
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().BriefSends.Count(x => x.BriefDate == day && !x.IsTest).Should().Be(0);
    }

    // ---- Round 3 ----

    [Fact]
    public async Task SourcesAdmin_TheListHidesAKeyOrPasswordInAFeedAddress()
    {
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Add(new Source { Name = "Audit keyed feed", Url = "https://feeduser:hunter2pw@keyed.example/rss?apikey=SECRETKEY123&lang=en" });
            await db.SaveChangesAsync();
        }
        await using var r = await RenderAsync<Tide.Web.Components.Pages.Admin.SourcesAdmin>(app, "/admin/sources");
        var html = await r.HtmlOnce("Audit keyed feed");
        html.Should().Contain("keyed.example/rss").And.Contain("lang=en")
            .And.NotContain("SECRETKEY123").And.NotContain("hunter2pw").And.NotContain("feeduser");
    }

    // ---- Round 4 ----

    [Fact]
    public async Task TheBoard_ADropOfACardSomeoneElseMoved_SaysSoAndMovesNothing()
    {
        var a = await NewAction("Audit stale drop");
        ActionBoard board;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            board = await scope.ServiceProvider.GetRequiredService<ActionService>().BoardAsync(new ActionFilter(ActionView.Open));
        await using var r = await RenderAsync<ActionKanban>(app, "/actions?layout=board", new()
        {
            ["Board"] = board, ["Today"] = new DateOnly(2026, 10, 30),
        });
        // The tab held the card in In progress (its board as it was); it's Open now.
        await r.Click(k => k.OnCardDropped(a.Id.ToString(), nameof(ActionStatus.InProgress), nameof(ActionStatus.Blocked)));

        r.Scope.ServiceProvider.GetRequiredService<Tide.Web.Services.ToastService>().Toasts
            .Should().Contain(t => !t.IsError && t.Message == $"A-{a.Number} was moved by someone else; the board is up to date.");
        using var check = app.Services.CreateScope();
        check.ServiceProvider.GetRequiredService<AppDbContext>().Actions.Single(x => x.Id == a.Id).Status.Should().Be(ActionStatus.Open);
    }

    [Fact]
    public async Task TheActionsPage_ALinkThatChangesOnlyTheView_ShowsThatView()
    {
        await using var r = await RenderAsync<ActionsBoard>(app, "/actions?view=closed");
        (await r.Html()).Should().Contain("Nothing closed in the last 90 days.");

        // The sidebar's Actions (no view: Mine), then a link to the overdue view: the page stays open, the address changes
        // (as the router would hand it the new query value, then draw it).
        async Task Query(string? view) => await r.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            typeof(ActionsBoard).GetProperty(nameof(ActionsBoard.ViewParam))!.SetValue(r.Component, view);
            await (Task)typeof(ComponentBase).GetMethod("OnParametersSetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(r.Component, null)!;
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(r.Component, null);
        });
        await Query(null);
        (await r.Html()).Should().Contain("Nothing assigned to you.");
        await Query("overdue");
        (await r.Html()).Should().Contain("Nothing is overdue.");
    }

    [Fact]
    public async Task AnotherAction_ClosesTheLastOnesOutcomePrompt_SoItCantMoveThisOne()
    {
        ActionItem a = await NewAction("Audit prompt one"), b = await NewAction("Audit prompt two");
        await using var r = await RenderAsync<ActionPage>(app, "/actions/" + a.Number, new() { ["Number"] = (int?)a.Number });
        var control = (ActionStatusControl)GetField(r.Component, "_statusControl")!;
        await r.Click(_ => control.Pick(ActionStatus.Cancelled));
        (await r.Html()).Should().Contain("tide-status-prompt");

        await Open(r, "Number", (int?)b.Number);
        (await r.Html()).Should().NotContain("tide-status-prompt", "the prompt was action one's");
        var now = (ActionStatusControl)GetField(r.Component, "_statusControl")!;
        now.IsPrompting.Should().BeFalse();
        await r.Click(_ => now.Confirm());
        using var check = app.Services.CreateScope();
        check.ServiceProvider.GetRequiredService<AppDbContext>().Actions.Single(x => x.Id == b.Id).Status.Should().Be(ActionStatus.Open);
    }

    [Fact]
    public async Task TheBundlePreview_MarksAChangeThatGivesAdmin_AndSaysSoAboveTheTable()
    {
        await using var r = await RenderAsync<Tide.Web.Components.Pages.Admin.ConfigBundleAdmin>(app, "/admin/system/bundle");
        var bundle = new Tide.Application.Operations.ConfigBundle(Tide.Application.Operations.ConfigBundle.FormatName, 1, DateTimeOffset.UtcNow, "Tester", "1.0",
            [], [new("AUDIT-Admins", "Admin")], [], [], []);
        var preview = await r.Renderer.Dispatcher.InvokeAsync(() =>
        {
            r.Scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = r.Signedin;
            return r.Scope.ServiceProvider.GetRequiredService<Tide.Application.Operations.ConfigBundleService>().PreviewAsync(bundle);
        });
        preview.Warnings.Should().Be(1);
        SetField(r.Component, "_preview", preview);
        SetField(r.Component, "_fileName", "audit.json");
        await r.Click(_ => Task.CompletedTask);
        await r.Renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)r.Component).HandleEventAsync(EventCallbackWorkItem.Empty, null));
        var html = await r.Html();
        html.Should().Contain("alert alert-warning small py-2\" role=\"note\"").And.Contain("1 change needs a careful look before you import")
            .And.Contain("<tr class=\"tide-bundle-warn\">").And.Contain("tide-bundle-warn-text")
            .And.Contain("bi-exclamation-triangle-fill me-1\" aria-hidden=\"true\"").And.Contain("would administer this TIDE");
    }

    // ---- Round 5 ----

    [Fact]
    public void DayLinks_NameTheGregorianDay_UnderAThaiCulture()
    {
        var was = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");   // Buddhist calendar: 2026 is 2569
            var day = new DateOnly(2026, 10, 30);
            Tide.Web.Components.Shared.Ui.BriefUrl(day).Should().Be("briefs/2026-10-30");
            Tide.Web.Components.Shared.Ui.PresentUrl(day).Should().Be("present/2026-10-30");
            Tide.Web.Components.Shared.Ui.IsoDay(day).Should().Be("2026-10-30");
            using var scope = SignedIn(app, SignIn.Analyst);
            var user = scope.ServiceProvider.GetRequiredService<Tide.Application.Abstractions.ICurrentUser>();
            Tide.Web.Components.Layout.PaletteEntries.For(user, day).Should().Contain(e => e.Href == "present/2026-10-30");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = was; }
    }
}
