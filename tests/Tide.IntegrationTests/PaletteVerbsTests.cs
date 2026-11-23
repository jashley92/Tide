using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The command palette's verbs, without a page: "&gt;" lists only the commands, "take A-12" and "hand A-12" name an
/// action's, present mode has none, and a page's verb is hidden from someone without its permission and gone once the
/// page is.
/// </summary>
public class PaletteVerbFilterTests
{
    private static readonly DateOnly Day = new(2026, 10, 29);

    private static PaletteCommand Verb(string label, Permission? permission = Permission.WriteArticles, Func<Task>? run = null) =>
        new(label, "", permission, "This article", run ?? (() => Task.CompletedTask));

    private static List<string> Labels(IEnumerable<PaletteEntry> entries) => entries.Select(e => e.Label).ToList();

    [Fact]
    public void AnArrowFirst_ListsOnlyTheCommands_ThePagesFirst()
    {
        var analyst = new FakeUser(AppRole.Analyst);
        var entries = PaletteEntries.For(analyst, Day);
        var page = PaletteEntries.FromVerbs([Verb("Save and mark ready")]);

        Labels(PaletteEntries.Filter(page, entries, ">", analyst)).Should()
            .Equal("Save and mark ready", "New article", "New action", "Present today's brief");
        Labels(PaletteEntries.Filter(page, entries, "> ready", analyst)).Should().Equal("Save and mark ready");
        PaletteEntries.Filter(page, entries, "> triage", analyst).Should().BeEmpty("Triage is a page, not a command");

        var all = PaletteEntries.Filter(page, entries, "", analyst);
        all[0].Label.Should().Be("Save and mark ready", "the page's own verbs come first");
        all.Should().Contain(e => e.Label == "Triage" && e.Kind == PaletteKind.Page, "without the arrow the pages are there too");
        all[0].Detail.Should().Be("This article");
    }

    [Fact]
    public void TakeOrHand_AndANumber_IsTheActionsOwnFlow_ForSomeoneWhoMayWorkActions()
    {
        var analyst = new FakeUser(AppRole.Analyst);
        PaletteEntries.Typed("take A-12", analyst).Select(e => (e.Label, e.Href, e.Kind)).Should()
            .Equal(("Take A-12", "actions/12?do=takeit", PaletteKind.Command));
        PaletteEntries.Typed("Hand a-7", analyst).Select(e => (e.Label, e.Href)).Should().Equal(("Hand A-7 to…", "actions/7?do=handoff"));
        PaletteEntries.Typed("hand off 7", new FakeUser(AppRole.ActionOwner)).Should().ContainSingle();
        PaletteEntries.Typed("take A-12", new FakeUser(AppRole.Viewer)).Should().BeEmpty("a viewer can't work actions");
        PaletteEntries.Typed("take", analyst).Should().BeEmpty();
        PaletteEntries.Typed("open A-12", analyst).Should().BeEmpty();

        var entries = PaletteEntries.For(analyst, Day);
        Labels(PaletteEntries.Filter([], entries, "> take A-12", analyst)).Should().Equal("Take A-12");
        // On A-12's page its own Take A-12 is listed, once.
        var page = PaletteEntries.FromVerbs([Verb("Take A-12", null)]);
        PaletteEntries.Filter(page, entries, "take A-12", analyst).Where(e => e.Kind == PaletteKind.Command).Should().ContainSingle()
            .Which.Run.Should().NotBeNull("the page's own");
    }

    [Fact]
    public void PresentMode_HasNoCommands()
    {
        var analyst = new FakeUser(AppRole.Analyst);
        var entries = PaletteEntries.For(analyst, Day);
        var page = PaletteEntries.FromVerbs([Verb("Save and mark ready")]);
        PaletteEntries.Filter(page, entries, "", analyst, verbs: false).Should().NotContain(e => e.Kind == PaletteKind.Command)
            .And.Contain(e => e.Label == "Triage");
        PaletteEntries.Filter(page, entries, ">", analyst, verbs: false).Should().BeEmpty();
        PaletteEntries.Filter(page, entries, "take A-12", analyst, verbs: false).Should().BeEmpty();
    }

    [Fact]
    public void APagesVerb_IsHiddenWithoutItsPermission_AndGoneWithThePage()
    {
        var verbs = new PaletteCommands();
        var page = verbs.Register(() => [Verb("Save and mark ready"), Verb("Copy A-3's hunt query", Permission.ReadActions)], () => { });
        Labels(PaletteEntries.FromVerbs(verbs.For(new FakeUser(AppRole.Analyst)))).Should().Equal("Save and mark ready", "Copy A-3's hunt query");
        Labels(PaletteEntries.FromVerbs(verbs.For(new FakeUser(AppRole.Viewer)))).Should().Equal("Copy A-3's hunt query");
        page.Dispose();
        verbs.For(new FakeUser(AppRole.Analyst)).Should().BeEmpty("the page has gone");
    }

    [Fact]
    public async Task APagesVerb_DrawsThePageAgain_AfterItRuns_EvenWhenItFails()
    {
        var verbs = new PaletteCommands();
        var drawn = 0;
        using var page = verbs.Register(() => [Verb("Fine"), Verb("Fails", run: () => throw new InvalidOperationException("No."))], () => drawn++);
        var analyst = new FakeUser(AppRole.Analyst);

        await verbs.For(analyst).Single(v => v.Label == "Fine").Run();
        drawn.Should().Be(1, "as a click on the page's own button would");
        var failing = () => verbs.For(analyst).Single(v => v.Label == "Fails").Run();
        await failing.Should().ThrowAsync<InvalidOperationException>("the palette says it in a toast");
        drawn.Should().Be(2);
    }
}

/// <summary>
/// The verbs pages offer the palette, rendered in the app: each only on its page and for someone allowed, each running
/// what the page's own button runs (its confirmation included), the service still refusing someone who isn't allowed,
/// and the palette running a verb once it has closed.
/// </summary>
public class PaletteVerbsTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Viewer = new("dev:pv-viewer", "Vic Viewer", "Viewer");
    private static readonly SignIn Owner = new("dev:pv-owner", "Olu Owner", "ActionOwner");

    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    // The verbs offered now, read as the palette reads them (signed in as the page was).
    internal static Task<List<PaletteCommand>> Verbs<T>(Rendered<T> r) => r.Renderer.Dispatcher.InvokeAsync(() =>
    {
        var sp = r.Scope.ServiceProvider;
        sp.GetRequiredService<IHttpContextAccessor>().HttpContext = r.Signedin;
        return sp.GetRequiredService<PaletteCommands>().For(sp.GetRequiredService<ICurrentUser>()).ToList();
    });

    private static async Task<PaletteCommand> VerbOf<T>(Rendered<T> r, string label) => (await Verbs(r)).Single(v => v.Label == label);

    private static IReadOnlyList<ToastService.Toast> Toasts<T>(Rendered<T> r) => r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts;

    // A draft with its bottom line and (unless asked otherwise) a bullet, so nothing blocks it being marked ready.
    private async Task<Guid> Draft(string title, bool bullet = true, ArticleStatus status = ArticleStatus.Draft)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var article = new Article { Title = title, AuthorId = SignIn.Analyst.UserId, AuthorName = "Tester", Tlp = TlpLevel.Clear, Bluf = "Exploited in the wild.", Status = status };
        if (bullet) article.Bullets.Add(new ArticleBullet { Text = "Patch it.", Tlp = TlpLevel.Clear });
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        return article.Id;
    }

    private Task<Rendered<ArticleEdit>> OpenArticle(Guid id, SignIn? who = null) =>
        RenderAsync<ArticleEdit>(app, $"/articles/{id}", new() { ["Id"] = id }, who);

    private Task<IReadOnlyList<DateOnly>> BriefDates(Guid id) => As(sp => sp.GetRequiredService<ArticleService>().BriefDatesAsync(id));

    [Fact]
    public async Task AnArticle_OffersAddToTodaysBrief_AndSaveAndMarkReady_WhichDoWhatItsButtonsDo()
    {
        var id = await Draft("Palette verbs on an article");
        await using var r = await OpenArticle(id);
        (await Verbs(r)).Select(v => (v.Label, v.Scope)).Should().Equal(
            ("Add this article to today's brief", "This article"), ("Save and mark ready", "This article"), ("Hand this draft to…", "This article"));

        var add = await VerbOf(r, "Add this article to today's brief");
        await r.Click(_ => add.Run());
        (await BriefDates(id)).Should().Contain(Today);
        Toasts(r).Should().Contain(t => t.Message == $"On the brief for {Tide.Web.Components.Shared.Ui.ShortDay(Today)}." && !t.IsError);
        (await r.Html()).Should().Contain("On today&#x27;s brief", "the page is drawn again, as after its button");

        var markReady = await VerbOf(r, "Save and mark ready");
        await r.Click(_ => markReady.Run());
        (await As(sp => sp.GetRequiredService<ArticleService>().GetAsync(id)))!.Status.Should().Be(ArticleStatus.Ready);
        Toasts(r).Should().Contain(t => t.Message == "Marked ready for a brief.");
        (await Verbs(r)).Select(v => v.Label).Should().Equal(["Hand this draft to…"], "it's on today's brief and ready: only Hand to… is still offered");
    }

    [Fact]
    public async Task ADraftThatIsntReady_IsNotOfferedSaveAndMarkReady_AsItsButtonIsDisabled_AndTheServiceRefusesIt()
    {
        var id = await Draft("Palette verbs on an unready draft", bullet: false);
        await using var r = await OpenArticle(id);
        System.Net.WebUtility.HtmlDecode(await r.Html()).Should().Contain("It isn't ready yet: Add at least one bullet.", "the editor's own button says why it's disabled");
        (await Verbs(r)).Select(v => v.Label).Should().Equal("Add this article to today's brief", "Hand this draft to…");

        var marking = () => As(sp => sp.GetRequiredService<ArticleService>().SetStatusAsync(id, ArticleStatus.Ready));
        (await marking.Should().ThrowAsync<InvalidOperationException>()).WithMessage("It isn't ready yet: Add at least one bullet.");
        (await As(sp => sp.GetRequiredService<ArticleService>().GetAsync(id)))!.Status.Should().Be(ArticleStatus.Draft);
    }

    [Fact]
    public async Task HandThisDraftTo_OpensTheHeadsHandToDialog_AndHandsNothingUntilItsAnswered()
    {
        var id = await Draft("Palette hand-off draft");
        await using var r = await OpenArticle(id);
        (await r.Html()).Should().Contain("bi-person-up", "the head offers Hand to…, so the palette does");
        var hand = await VerbOf(r, "Hand this draft to…");
        (hand.Permission, hand.Scope).Should().Be((Permission.WriteArticles, "This article"));

        await r.Press(_ => hand.Run());
        (await r.HtmlOnce("Hand this article to a colleague")).Should().Contain("Hand it to", "the head's own dialog");
        (await As(sp => sp.GetRequiredService<ArticleService>().GetAsync(id)))!.CurrentWriterId.Should().Be(SignIn.Analyst.UserId, "nothing moves until it's answered");
    }

    [Fact]
    public async Task AnArticleNoLongerBeingWritten_IsNotOfferedHandThisDraftTo_AsTheHeadHasNoHandTo()
    {
        var id = await Draft("Palette hand-off presented", status: ArticleStatus.Presented);
        await using var r = await OpenArticle(id);
        (await r.Html()).Should().NotContain("bi-person-up", "the head offers no Hand to…");
        (await Verbs(r)).Should().NotContain(v => v.Label == "Hand this draft to…");
    }

    [Fact]
    public async Task AnArticle_OffersNothing_ToSomeoneWhoCantEditIt()
    {
        var id = await Draft("Palette verbs for a viewer");
        await using var r = await OpenArticle(id, Viewer);
        (await Verbs(r)).Should().BeEmpty();
    }

    [Fact]
    public async Task AVerbRunForSomeoneWhoLostAccess_IsHidden_AndTheServiceStillRefusesIt()
    {
        var id = await Draft("Palette verb refused");
        await using var r = await OpenArticle(id);
        var add = await VerbOf(r, "Add this article to today's brief");

        // Their access goes while the page is open: the palette no longer lists it, and running it anyway is refused.
        await using (var viewer = SignedIn(app, Viewer))
            r.Signedin.User = viewer.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext!.User;
        (await Verbs(r)).Should().NotContain(v => v.Label == add.Label);
        await r.Click(_ => add.Run());
        (await BriefDates(id)).Should().NotContain(Today);
        Toasts(r).Should().Contain(t => t.IsError, "refused, and said so as the button would");
    }

    // A team the tester is on, with an action its other member, Olu, has taken.
    private async Task<int> HeldByOlu(string name)
    {
        var team = await As(async sp =>
        {
            var teams = sp.GetRequiredService<TeamService>();
            var id = await teams.AddAsync(name, null, null);
            await teams.AddMemberAsync(id, SignIn.Analyst.UserId, SignIn.Analyst.Name);
            await teams.AddMemberAsync(id, Owner.UserId, Owner.Name);
            return id;
        });
        var number = await As(sp => sp.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = name + " action", TeamId = team }));
        await As(async sp =>
        {
            var actions = sp.GetRequiredService<ActionService>();
            await actions.TakeAsync((await actions.GetAsync(number))!.Action.Id);
            return true;
        }, Owner);
        return number;
    }

    private Task<ActionDetail> Detail(int number) => As(async sp => (await sp.GetRequiredService<ActionService>().GetAsync(number))!);

    private Task<Rendered<ActionPage>> OpenAction(int number, SignIn? who = null) =>
        RenderAsync<ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number }, who);

    [Fact]
    public async Task AnAction_TakeAsksFirst_WhenAColleagueHasIt_AndHandOffOpensItsDialog()
    {
        var number = await HeldByOlu("Palette take team");
        await using var r = await OpenAction(number);
        var verbs = await Verbs(r);
        verbs.Select(v => v.Label).Should().Contain([$"Take A-{number}", $"Hand A-{number} to…"])
            .And.NotContain($"Copy A-{number}'s hunt query", "it has no hunt query");
        verbs.Should().OnlyContain(v => v.Scope == $"A-{number}");

        var take = await VerbOf(r, $"Take A-{number}");
        await r.Press(_ => take.Run());
        (await r.HtmlOnce("Take this action from Olu Owner?")).Should().Contain("Take this action from Olu Owner?", "the confirmation isn't skipped");
        (await Detail(number)).Action.AssigneeId.Should().Be(Owner.UserId, "nothing moves until it's answered");

        await using var again = await OpenAction(number);
        var hand = await VerbOf(again, $"Hand A-{number} to…");
        await again.Press(_ => hand.Run());
        (await again.HtmlOnce("Hand it to")).Should().Contain($"Hand A-{number} off");
        (await Detail(number)).Action.TeamId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task AnAction_OffersAViewerNoTakeOrHandOff()
    {
        var number = await HeldByOlu("Palette viewer team");
        await using var r = await OpenAction(number, Viewer);
        (await Verbs(r)).Should().NotContain(v => v.Label.StartsWith("Take", StringComparison.Ordinal) || v.Label.StartsWith("Hand", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnActionsHuntQuery_IsCopied_OrItSaysWhyNot()
    {
        var number = await HeldByOlu("Palette hunt team");
        var id = (await Detail(number)).Action.Id;
        await As(sp => sp.GetRequiredService<ActionService>().AddHandWrittenHuntAsync(id, "kql", "DeviceNetworkEvents | where RemoteIP == \"198.51.100.7\""));
        await using var r = await OpenAction(number, Viewer);
        var copy = await VerbOf(r, $"Copy A-{number}'s hunt query");
        // No browser here to take it: it says so where the page's own Copy would, rather than claiming it worked.
        await r.Click(_ => copy.Run());
        Toasts(r).Should().Contain(t => t.IsError && t.Message.Contains("couldn't be copied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Triage_PromoteTheSelectedItem_OpensItsPromoteForm_AsPDoes()
    {
        var key = Guid.NewGuid().ToString("N");
        Guid source;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var feed = new Source { Name = "Palette feed " + key[..6], Url = $"https://palette.example/{key}/feed" };
            db.Add(feed);
            db.Add(new FeedItem
            {
                Source = feed, Title = "Palette promote me", Url = $"https://palette.example/{key}/1", UrlHash = $"palette-{key}",
                PublishedAtUtc = DateTimeOffset.UtcNow, FetchedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            source = feed.Id;
        }
        await using var r = await RenderAsync<Triage>(app, $"/triage?source={source}", @as: new("palette:" + key, "Palette tester", "Analyst"));
        await r.HtmlOnce("Palette promote me");
        var promote = await VerbOf(r, "Promote the selected item");
        promote.Scope.Should().Be("Triage");

        await r.Click(_ => promote.Run());
        (await r.Html()).Should().Contain("aria-label=\"Promote to an article\"", "the row's own promote form, nothing promoted yet");
        (await Verbs(r)).Should().BeEmpty("the form is open");
    }

    // ---- The palette itself ----

    private static void SetField<T>(T target, string name, object? value) =>
        typeof(T).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static Task<List<PaletteEntry>> Shown(Rendered<CommandPalette> r, string query) => r.Renderer.Dispatcher.InvokeAsync(() =>
    {
        r.Scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = r.Signedin;
        SetField(r.Component, "_query", query);
        return r.Component.Shown.ToList();
    });

    [Fact]
    public async Task ThePalette_OffNoPage_HasOnlyTheCommandsForAnywhere()
    {
        await using var r = await RenderAsync<CommandPalette>(app, "/");
        (await Shown(r, ">")).Select(e => e.Label).Should().Equal("New article", "New action", "Present today's brief");
    }

    [Fact]
    public async Task ThePalette_RunsTheChosenVerb_OnceItHasClosed()
    {
        await using var r = await RenderAsync<CommandPalette>(app, "/");
        var ran = 0;
        using var page = r.Scope.ServiceProvider.GetRequiredService<PaletteCommands>()
            .Register(() => [new PaletteCommand("Mark the brief ready", "", Permission.WriteArticles, "Today", () => { ran++; return Task.CompletedTask; })], () => { });

        await r.Click(p => { p.Open(); return Task.CompletedTask; });
        (await Shown(r, "> mark")).Select(e => e.Label).Should().Equal("Mark the brief ready");
        await r.Click(p => { p.OnKey("enter"); return Task.CompletedTask; });
        ran.Should().Be(0, "it runs once the palette has closed and given focus back");
        (await r.Html()).Should().NotContain("data-palette-input", "closed");
        await r.Click(p => p.RunPending());
        ran.Should().Be(1);
        await r.Click(p => p.RunPending());
        ran.Should().Be(1, "once");
    }

    [Fact]
    public async Task ThePalette_InPresentMode_ListsNoVerbs()
    {
        await using var r = await RenderAsync<CommandPalette>(app, $"/present/{Today:yyyy-MM-dd}", new() { ["ShowButton"] = false, ["Verbs"] = false });
        using var page = r.Scope.ServiceProvider.GetRequiredService<PaletteCommands>()
            .Register(() => [new PaletteCommand("Mark the brief ready", "", null, "Today", () => Task.CompletedTask)], () => { });
        (await Shown(r, "")).Should().NotContain(e => e.Kind == PaletteKind.Command).And.NotBeEmpty();
        (await Shown(r, ">")).Should().BeEmpty();
        (await Shown(r, "take A-1")).Should().NotContain(e => e.Kind == PaletteKind.Command);
    }
}

/// <summary>Today's verb, in an app of its own so no other test's article is on today's brief.</summary>
public class PaletteTodayVerbTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task Today_MarkTheBriefReady_AsksWhatMarkReadyAsks_ThenMarksIt()
    {
        var today = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        Guid briefId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Nothing blocks it, but it has no scorecard tag: a warning, which Mark ready asks about first.
            var article = new Article { Title = "Palette today article", AuthorId = "x", AuthorName = "X", Tlp = TlpLevel.Clear, Bluf = "Patch now.", Status = ArticleStatus.Ready };
            article.Bullets.Add(new ArticleBullet { Text = "It's exploited.", Tlp = TlpLevel.Clear });
            db.Articles.Add(article);
            await db.SaveChangesAsync();
            await using var me = SignedIn(app, SignIn.Analyst);
            var briefs = me.ServiceProvider.GetRequiredService<BriefService>();
            briefId = (await briefs.GetOrStartAsync(today)).Id;
            await briefs.AddArticleAsync(briefId, article.Id);
        }

        var iso = Tide.Web.Components.Shared.Ui.IsoDay(today);
        await using var r = await RenderAsync<BriefPage>(app, "/", new() { ["Day"] = iso, ["AsToday"] = true });
        await r.HtmlOnce("Palette today article");
        var verbs = await r.Renderer.Dispatcher.InvokeAsync(() =>
        {
            r.Scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = r.Signedin;
            return r.Component.Verbs().ToList();
        });
        var ready = verbs.Should().ContainSingle().Which;
        (ready.Label, ready.Scope, ready.Permission).Should().Be(("Mark the brief ready", "Today", Permission.WriteArticles));

        Task? asking = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { asking = r.Pressed(_ => ready.Run()); });
        (await r.HtmlOnce("Ready anyway?")).Should().Contain("Ready anyway?", "the page's own confirmation");
        // Read straight from the database: signing in again here would end the sign-in the question was asked on.
        await using (var scope = app.Services.CreateAsyncScope())
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Briefs.Single(b => b.Id == briefId).Status.Should().Be(BriefStatus.Draft, "not until it's answered");

        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.ConfirmAsked());
        await r.Renderer.Dispatcher.InvokeAsync(() => asking!);
        await using (var scope = SignedIn(app, SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(today))!.Status.Should().Be(BriefStatus.Ready);
    }
}

/// <summary>The brief's Send both editions, in an app of its own so the day's sends are this test's.</summary>
public class PaletteSendBothVerbTests(TideApp app) : IClassFixture<TideApp>
{
    internal static Task<List<PaletteCommand>> Verbs(Rendered<BriefPage> r) => PaletteVerbsTests.Verbs(r);

    // The day's brief as it's opened: today's is Today, the start page.
    internal static Task<Rendered<BriefPage>> OpenBrief(TideApp app, DateOnly day) =>
        RenderAsync<BriefPage>(app, "/", new() { ["Day"] = Tide.Web.Components.Shared.Ui.IsoDay(day), ["AsToday"] = IsToday(app, day) });

    private static bool IsToday(TideApp app, DateOnly day) => day == app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    [Fact]
    public async Task SendBothEditions_OpensTheSendDialog_AndSendsNothing_AndGoesOnceTheyHaveGone()
    {
        var day = await SendBoth.DayAsync(app);
        await using var r = await OpenBrief(app, day);
        var send = (await Verbs(r)).Should().ContainSingle(v => v.Label == "Send both editions").Which;
        (send.Permission, send.Scope).Should().Be((Permission.PresentBriefs, IsToday(app, day) ? "Today" : "This brief"));

        await r.Press(_ => send.Run());
        SendBoth.Tag(await r.HtmlOnce("id=\"send-both-h\"")).Should().Contain("disabled", "the dialog's own Send both, waiting for its two ticks");
        (await SendBoth.RealSendsAsync(r, day)).Should().BeEmpty("the verb only opens the confirmation");

        // Both editions gone: the dialog no longer offers Send both, and neither does the palette.
        var distribution = SendFromTodayTests.SignedIn(r);
        foreach (var edition in new[] { Edition.Team, Edition.Public })
            await distribution.SendAsync(await distribution.PlanAsync(day, edition));
        await using var after = await OpenBrief(app, day);
        (await Verbs(after)).Should().NotContain(v => v.Label == "Send both editions");
    }
}

/// <summary>
/// Send both on a page the browser loaded: the interactive page adopts what the prerender read instead of reading it
/// again (the Today and brief pages), and Send both editions must come with it.
/// </summary>
public class PaletteSendBothPrerenderTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TheInteractivePage_AdoptingThePrerendersRead_StillOffersSendBothEditions()
    {
        var day = await SendBoth.DayAsync(app);
        await using var prerendered = await PaletteSendBothVerbTests.OpenBrief(app, day);
        (await PaletteSendBothVerbTests.Verbs(prerendered)).Should().Contain(v => v.Label == "Send both editions");

        var interactive = new BriefPage();
        await prerendered.Renderer.Dispatcher.InvokeAsync(() => interactive.Adopt(prerendered.Component));
        interactive.Verbs().Should().Contain(v => v.Label == "Send both editions", "nothing was read again, so it's what the prerender read");
    }
}

/// <summary>Send both held for a required review: its button is disabled, so the palette doesn't offer it.</summary>
public class PaletteSendBothHeldTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task AnEditionHeldForReview_HidesSendBothEditions_AsItsButtonIsDisabled()
    {
        var day = await SendBoth.DayAsync(app, Tide.Application.Reviews.ReviewRule.AmberAndAbove);
        await using var r = await PaletteSendBothVerbTests.OpenBrief(app, day);
        (await PaletteSendBothVerbTests.Verbs(r)).Should().NotContain(v => v.Label == "Send both editions");

        await r.Press(p => p.AskSend());
        SendBoth.Tag(await r.HtmlOnce("id=\"send-both-h\"")).Should().Contain("disabled");
    }
}

/// <summary>Send both before the meeting has been held: its button waits for the meeting, and so does the palette.</summary>
public class PaletteSendBothBeforeMeetingTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task BeforeTheMeeting_SendBothEditionsIsNotOffered()
    {
        var day = await SendBoth.DayAsync(app, held: false);
        await using (var r = await PaletteSendBothVerbTests.OpenBrief(app, day))
            (await PaletteSendBothVerbTests.Verbs(r)).Should().NotContain(v => v.Label == "Send both editions");

        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            await briefs.MarkPresentedAsync((await briefs.GetOrStartAsync(day)).Id);
        }
        await using var held = await PaletteSendBothVerbTests.OpenBrief(app, day);
        (await PaletteSendBothVerbTests.Verbs(held)).Should().Contain(v => v.Label == "Send both editions", "the meeting has been held");
    }
}
