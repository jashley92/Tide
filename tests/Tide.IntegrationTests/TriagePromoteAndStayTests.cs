using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Briefs;
using Tide.Application.Intake;
using Tide.Application.Preferences;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>The app with every outbound request recorded (none should be made) and page navigations recorded, so a test
/// can say whether the page went on to the editor or stayed.</summary>
public sealed class StayApp : TideApp
{
    public FakeHttp Http { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<IOutboundHttp>(Http);
            s.AddScoped<NavigationManager, RecordingNavigation>();
        });
    }

    /// <summary>Where the page asked to go, in order.</summary>
    public sealed class RecordingNavigation : NavigationManager, IHostEnvironmentNavigationManager
    {
        public List<string> Went { get; } = [];

        void IHostEnvironmentNavigationManager.Initialize(string baseUri, string uri) => Initialize(baseUri, uri);

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            Went.Add(uri);
            Uri = ToAbsoluteUri(uri).ToString();
        }
    }
}

/// <summary>Promote and keep triaging (Shift+Enter in the promote form): the draft is made, the inbox stays with the next
/// item selected, and a toast offers Write it and Undo. "Add to today's brief" puts the draft on the brief.</summary>
public class TriagePromoteAndStayTests(StayApp app) : IClassFixture<StayApp>
{
    // Each test that ticks "Add to today's brief" signs in as someone of its own: the tick is remembered per person.
    private static SignIn Analyst(string key) => new("dev:stay-" + key, "Stay " + key, "Analyst");

    private static List<string> Went(Rendered<Triage> r) => ((StayApp.RecordingNavigation)r.Scope.ServiceProvider.GetRequiredService<NavigationManager>()).Went;

    // A render after a call that, in the browser, an event handler would re-render for: "toggle" does nothing on a row
    // that isn't a group's, and renders.
    private static Task Rendered(Triage t) => t.OnTriageKey("toggle");

    private static Task Stay(Rendered<Triage> r) => r.Click(async t =>
    {
        await t.SubmitPromote(stay: true);
        await Rendered(t);
    });

    private static Task Tick(Rendered<Triage> r) => r.Click(async t =>
    {
        await t.SetAddToBrief(new ChangeEventArgs { Value = true });
        await Rendered(t);
    });
    private static string TitleOf(string key, int n) => $"stay-{key} report {n}: a vendor fixes an exploited gateway bug";

    // Items of their own, newest first, each with a CVE of its own (so they don't group). Opened with ?q=stay-<key>, the
    // inbox lists only these, whatever the other tests left in it.
    private async Task<Guid[]> Seed(string key, int count, FeedItemStatus status = FeedItemStatus.New, TlpLevel tlp = TlpLevel.Amber)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        var source = new Source { Name = "Stay source " + key, Url = "https://stay.example/feed/" + key, DefaultTlp = tlp };
        db.Add(source);
        var at = DateTimeOffset.UtcNow;
        var items = Enumerable.Range(1, count).Select(n => new FeedItem
        {
            Source = source, Title = TitleOf(key, n), Url = $"https://stay.example/{key}/{n}", UrlHash = $"stay-{key}-{n}",
            Cves = $"CVE-2026-{Math.Abs((key + n).GetHashCode()) % 90000 + 10000}", PublishedAtUtc = at.AddMinutes(-n), FetchedAtUtc = at.AddMinutes(-n),
            Status = status, StatusByName = status == FeedItemStatus.New ? null : "Robin", StatusAtUtc = status == FeedItemStatus.New ? null : at.AddHours(-1),
        }).ToList();
        db.AddRange(items);
        await db.SaveChangesAsync();
        return [.. items.Select(i => i.Id)];
    }

    private static string Selected(string html)
    {
        var at = html.IndexOf("<li class=\"tide-triage-row selected", StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, "a row is selected");
        return html[at..html.IndexOf("</li>", at, StringComparison.Ordinal)];
    }

    private async Task<FeedItem> Item(Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>().FeedItems.AsNoTracking().SingleAsync(i => i.Id == id);
    }

    private async Task<bool> OnBrief(Guid articleId, DateOnly day)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        return await db.BriefItems.AnyAsync(i => i.ArticleId == articleId && db.Briefs.Any(b => b.Id == i.BriefId && b.BriefDate == day));
    }

    private static ToastService.Toast LastToast(Rendered<Triage> r) => r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts[^1];

    [Fact]
    public async Task PromoteAndKeepTriaging_MakesTheDraft_StaysOnTheInbox_AndSelectsTheNextItem()
    {
        var ids = await Seed("next", 3);
        await using var r = await RenderAsync<Triage>(app, "/triage?q=stay-next");
        Selected(await r.HtmlOnce(TitleOf("next", 1))).Should().Contain(TitleOf("next", 1));

        await r.Click(t => t.OnTriageKey("promote"));
        await Stay(r);
        var html = await r.Html();

        var made = await Item(ids[0]);
        made.Status.Should().Be(FeedItemStatus.Promoted);
        made.ArticleId.Should().NotBeNull();
        Went(r).Should().BeEmpty("the inbox stays: the editor isn't opened");
        html.Should().NotContain("tide-promote-form", "the form closed with the promote");
        Selected(html).Should().Contain(TitleOf("next", 2), "the selection moved on to the next item, as after S or X");
        html.Should().NotContain(TitleOf("next", 1), "the promoted item left New");
        r.Component.PendingFocus.Should().Be("tideTriage.focusRow");

        var toast = LastToast(r);
        toast.Message.Should().Be($"Promoted “{TitleOf("next", 1)}” to a draft.");
        toast.Link.Should().Be(new ToastService.ToastLink($"articles/{made.ArticleId}", "Write it"));
        toast.Undo.Should().NotBeNull();

        // Again on the next: two promotes, no page change.
        await r.Click(t => t.OnTriageKey("promote"));
        await Stay(r);
        (await Item(ids[1])).Status.Should().Be(FeedItemStatus.Promoted);
        Selected(await r.Html()).Should().Contain(TitleOf("next", 3));
        Went(r).Should().BeEmpty();
    }

    [Fact]
    public async Task PromoteAndWrite_StillOpensTheDraft()
    {
        var ids = await Seed("write", 1);
        await using var r = await RenderAsync<Triage>(app, "/triage?q=stay-write");
        await r.HtmlOnce(TitleOf("write", 1));
        await r.Click(t => t.OnTriageKey("promote"));
        await r.Click(t => t.SubmitPromote());

        var item = await Item(ids[0]);
        item.Status.Should().Be(FeedItemStatus.Promoted);
        Went(r).Should().Equal($"articles/{item.ArticleId}");
    }

    [Fact]
    public async Task TheForm_OffersBothWaysOut_WithTheirKeys_AndTheBriefTick()
    {
        await Seed("form", 1);
        await using var r = await RenderAsync<Triage>(app, "/triage?q=stay-form");
        await r.HtmlOnce(TitleOf("form", 1));
        await r.Click(t => t.OnTriageKey("promote"));
        var html = await r.Html();
        var form = html[html.IndexOf("<form class=\"tide-promote-form", StringComparison.Ordinal)..];
        form = form[..form.IndexOf("</form>", StringComparison.Ordinal)];

        // Enter submits the form (Promote and write, as before); Shift+Enter is triage.js's, pressing the stay button.
        form.Should().MatchRegex("<button type=\"submit\" class=\"btn btn-primary btn-sm\" aria-keyshortcuts=\"Enter\"><span class=\"bi bi-pencil-square\" aria-hidden=\"true\"></span> Promote and write</button>")
            .And.MatchRegex("<button type=\"button\" class=\"btn btn-outline-secondary btn-sm tide-promote-stay\" data-promote-stay aria-keyshortcuts=\"Shift&#x2B;Enter\"><span class=\"bi bi-inbox\" aria-hidden=\"true\"></span> Promote and keep triaging</button>")
            .And.Contain("tide-promote-cancel\">Cancel</button>")
            .And.Contain("Enter writes it, Shift+Enter keeps triaging, Esc cancels")
            .And.Contain("id=\"promote-brief\"").And.NotContain("id=\"promote-brief\" checked", "off until the person ticks it");
        form.IndexOf("Promote and write", StringComparison.Ordinal).Should().BeLessThan(form.IndexOf("Promote and keep triaging", StringComparison.Ordinal),
            "Promote and write stays the main button");

        var keys = Shortcuts.For("triage").SelectMany(g => g.Keys).ToList();
        keys.Should().ContainSingle(k => k.Keys.SequenceEqual(new[] { "Shift Enter" }) && k.What.Contains("keep triaging"));
        keys.Should().ContainSingle(k => k.Keys.SequenceEqual(new[] { "P" }) && k.Letter);
    }

    [Fact]
    public async Task TheBriefTick_IsRemembered_ForTheNextPromote()
    {
        await Seed("tick", 2);
        var who = Analyst("tick");
        await using (var r = await RenderAsync<Triage>(app, "/triage?q=stay-tick", @as: who))
        {
            await r.HtmlOnce(TitleOf("tick", 1));
            await r.Click(t => t.OnTriageKey("promote"));
            await Tick(r);
            (await r.Html()).Should().Contain("id=\"promote-brief\" checked");
        }
        await using (var scope = SignedIn(app, who))
            (await scope.ServiceProvider.GetRequiredService<UserPreferenceService>().GetAsync(UserPreferences.TriagePromoteToBrief)).Should().Be(UserPreferences.On);
        await using var again = await RenderAsync<Triage>(app, "/triage?q=stay-tick", @as: who);
        await again.HtmlOnce(TitleOf("tick", 1));
        await again.Click(t => t.OnTriageKey("promote"));
        (await again.Html()).Should().Contain("id=\"promote-brief\" checked", "the next form starts as the person left the tick");
    }

    [Fact]
    public async Task AddToBrief_PutsTheDraftOnTodaysBrief_AtTheEnd_AndTheToastSaysSo()
    {
        var ids = await Seed("brief", 2);
        await using var r = await RenderAsync<Triage>(app, "/triage?q=stay-brief", @as: Analyst("brief"));
        await r.HtmlOnce(TitleOf("brief", 1));
        await r.Click(t => t.OnTriageKey("promote"));
        await Tick(r);
        await Stay(r);

        var item = await Item(ids[0]);
        DateOnly day;
        IReadOnlyList<BriefStop> order;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            day = briefs.CurrentDate;
            order = await briefs.RunningOrderAsync(day);
        }
        order[^1].ArticleId.Should().Be(item.ArticleId!.Value, "it goes on the end of the running order");
        LastToast(r).Message.Should().MatchRegex("^Promoted “.+” to a draft\\. It's on (today's brief|the brief for .+)\\.$");
        LastToast(r).IsWarning.Should().BeFalse();
    }

    [Fact]
    public async Task AddToBrief_StartsTheBrief_WhenThereIsNone()
    {
        var ids = await Seed("start", 1);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var briefs = sp.GetRequiredService<BriefService>();
        var day = briefs.CurrentDate;
        var db = sp.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        // No brief for the day yet (another test may have started it): its items and it go first.
        if (await db.Briefs.Include(b => b.Items).FirstOrDefaultAsync(b => b.BriefDate == day) is { } had)
        {
            db.BriefItems.RemoveRange(had.Items);
            db.Briefs.Remove(had);
            await db.SaveChangesAsync();
        }

        var done = await sp.GetRequiredService<TriagePromoter>().PromoteAsync([ids[0]], new PromoteForm { AddToBrief = true });

        done.BriefDate.Should().Be(day);
        done.NotOnBrief.Should().BeNull();
        (await OnBrief(done.Outcome.ArticleId, day)).Should().BeTrue("the brief was started with it");
        done.Undo!.BriefId.Should().NotBeNull();
    }

    [Fact]
    public async Task AddToBrief_OnASentBrief_PromotesAllTheSame_AndSaysWhyItIsntOnIt()
    {
        var ids = await Seed("sent", 1);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var briefs = sp.GetRequiredService<BriefService>();
        var brief = await briefs.GetOrStartAsync(briefs.CurrentDate);
        var db = sp.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        await db.Briefs.Where(b => b.Id == brief.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BriefStatus.Sent));
        try
        {
            var done = await sp.GetRequiredService<TriagePromoter>().PromoteAsync([ids[0]], new PromoteForm { AddToBrief = true });

            done.Outcome.Made.Should().BeTrue("the promote stands on its own");
            (await Item(ids[0])).Status.Should().Be(FeedItemStatus.Promoted);
            done.BriefDate.Should().BeNull();
            done.NotOnBrief.Should().Be("This brief has been sent; its articles are fixed.");
            (await OnBrief(done.Outcome.ArticleId, brief.BriefDate)).Should().BeFalse();
        }
        finally
        {
            await db.Briefs.Where(b => b.Id == brief.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BriefStatus.Draft));
        }
    }

    [Fact]
    public async Task WithoutTheTick_NothingGoesOnTheBrief()
    {
        var ids = await Seed("notick", 1);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var done = await sp.GetRequiredService<TriagePromoter>().PromoteAsync([ids[0]], new PromoteForm());
        done.BriefDate.Should().BeNull();
        (await OnBrief(done.Outcome.ArticleId, sp.GetRequiredService<BriefService>().CurrentDate)).Should().BeFalse();
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("ActionOwner")]
    [InlineData("Reader")]
    public async Task OnlyAnalysts_PromoteOrAddToTheBrief_AndNothingIsClaimedForThoseWhoCant(string role)
    {
        var ids = await Seed("perm-" + role, 1);
        await using var scope = SignedIn(app, new SignIn("dev:stay-" + role, role, role));
        var promoter = scope.ServiceProvider.GetRequiredService<TriagePromoter>();

        await promoter.Invoking(p => p.PromoteAsync([ids[0]], new PromoteForm { AddToBrief = true })).Should().ThrowAsync<ForbiddenException>();
        await promoter.Invoking(p => p.PromoteAsync([ids[0]], new PromoteForm())).Should().ThrowAsync<ForbiddenException>();
        await promoter.Invoking(p => p.UndoAsync(new PromoteUndo(Guid.NewGuid(), Guid.NewGuid(), []))).Should().ThrowAsync<ForbiddenException>();
        (await Item(ids[0])).Status.Should().Be(FeedItemStatus.New, "nothing was claimed");
    }

    [Fact]
    public async Task ThePromotedDraft_KeepsTheTlpChosen()
    {
        var ids = await Seed("tlp", 1, tlp: TlpLevel.Green);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var done = await sp.GetRequiredService<TriagePromoter>().PromoteAsync([ids[0]], new PromoteForm { Tlp = TlpLevel.Red, AddToBrief = true });
        var db = sp.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        (await db.Articles.AsNoTracking().SingleAsync(a => a.Id == done.Outcome.ArticleId)).Tlp.Should().Be(TlpLevel.Red);
    }

    [Fact]
    public async Task Undo_DeletesTheDraft_TakesItOffTheBrief_AndPutsTheItemBackAsItWas()
    {
        var ids = await Seed("undo", 2, FeedItemStatus.Shortlisted);
        await using var r = await RenderAsync<Triage>(app, "/triage?tab=shortlisted&q=stay-undo", @as: Analyst("undo"));
        await r.HtmlOnce(TitleOf("undo", 1));
        var was = await Item(ids[0]);
        await r.Click(t => t.OnTriageKey("promote"));
        await Tick(r);
        await Stay(r);
        var articleId = (await Item(ids[0])).ArticleId!.Value;
        DateOnly day;
        await using (var scope = SignedIn(app, SignIn.Analyst)) day = scope.ServiceProvider.GetRequiredService<BriefService>().CurrentDate;
        (await OnBrief(articleId, day)).Should().BeTrue();

        // The toast's Undo, as its button (or Alt+Z) runs it.
        var toasts = r.Scope.ServiceProvider.GetRequiredService<ToastService>();
        await r.Click(_ => toasts.UndoAsync(LastToast(r).Id));

        var back = await Item(ids[0]);
        back.Status.Should().Be(FeedItemStatus.Shortlisted, "it goes back where it was, not to New");
        (back.StatusByName, back.StatusAtUtc, back.ArticleId).Should().Be((was.StatusByName, was.StatusAtUtc, (Guid?)null));
        (await OnBrief(articleId, day)).Should().BeFalse();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            (await db.Articles.AnyAsync(a => a.Id == articleId)).Should().BeFalse("the draft is deleted");
            (await db.AuditLog.AnyAsync(e => e.EntityType == nameof(Article) && e.EntityKey == articleId.ToString() && e.Action == "Deleted")).Should().BeTrue("the deletion is audited, as a draft's is");
        }
        LastToast(r).Message.Should().StartWith("Promote undone");
        Selected(await r.Html()).Should().Contain(TitleOf("undo", 1), "the item is selected again");
    }

    [Fact]
    public async Task Undo_IsRefused_OnceSomeoneHasSavedTheDraft()
    {
        var ids = await Seed("undo-saved", 1);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var promoter = sp.GetRequiredService<TriagePromoter>();
        var done = await promoter.PromoteAsync([ids[0]], new PromoteForm());
        var db = sp.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        // A colleague's save in the editor moves the edit version.
        await db.Articles.Where(a => a.Id == done.Outcome.ArticleId).ExecuteUpdateAsync(u => u.SetProperty(a => a.EditVersion, Guid.NewGuid()));

        (await promoter.UndoAsync(done.Undo!)).Should().BeFalse();
        (await db.Articles.AnyAsync(a => a.Id == done.Outcome.ArticleId)).Should().BeTrue("their work stands");
        (await Item(ids[0])).Status.Should().Be(FeedItemStatus.Promoted);
    }

    [Fact]
    public async Task Undo_IsRefused_WhenAColleagueAttachedAnotherReport()
    {
        var ids = await Seed("undo-attached", 2);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var promoter = sp.GetRequiredService<TriagePromoter>();
        var done = await promoter.PromoteAsync([ids[0]], new PromoteForm());
        await sp.GetRequiredService<IntakeService>().AttachAllAsync([ids[1]], done.Outcome.ArticleId);

        (await promoter.UndoAsync(done.Undo!)).Should().BeFalse();
        (await Item(ids[0])).Status.Should().Be(FeedItemStatus.Promoted);
        (await Item(ids[1])).ArticleId.Should().Be(done.Outcome.ArticleId);
    }

    [Fact]
    public async Task AColleaguesPromoteFirst_IsSaid_WithNoUndo_AndNothingGoesOnTheBrief()
    {
        var ids = await Seed("first", 1);
        PromoteOutcome theirs;
        await using (var colleague = SignedIn(app, new SignIn("dev:stay-sam", "Sam", "Analyst")))
            theirs = await colleague.ServiceProvider.GetRequiredService<IntakeService>().PromoteOrJoinAsync([ids[0]], new PromoteForm());

        await using var scope = SignedIn(app, SignIn.Analyst);
        var done = await scope.ServiceProvider.GetRequiredService<TriagePromoter>().PromoteAsync([ids[0]], new PromoteForm { AddToBrief = true });
        done.Outcome.Made.Should().BeFalse();
        done.Outcome.ArticleId.Should().Be(theirs.ArticleId);
        done.Undo.Should().BeNull("there's nothing of this person's to take back");
        done.NotOnBrief.Should().Be("Sam made this article, so it wasn't added to the brief.");
    }

    [Fact]
    public async Task PromoteAndKeepTriaging_FetchesNothing()
    {
        await Seed("nofetch", 2);
        int Outbound() { lock (app.Http.Requests) return app.Http.Requests.Count; }
        await using var r = await RenderAsync<Triage>(app, "/triage?q=stay-nofetch");
        await r.HtmlOnce(TitleOf("nofetch", 1));
        var before = Outbound();

        await r.Click(t => t.OnTriageKey("promote"));
        await Stay(r);

        Outbound().Should().Be(before, "nothing is asked of a source: Triage never fetches an article by itself");
        Selected(await r.Html()).Should().Contain(TitleOf("nofetch", 2));
    }
}
