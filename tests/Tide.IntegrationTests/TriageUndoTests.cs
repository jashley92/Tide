using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Intake;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>Triage's single Dismiss and Shortlist (X, S): the row leaves the list at one keypress, so a toast offers to
/// put it back.</summary>
public class TriageUndoTests(TideApp app) : IClassFixture<TideApp>
{
    // A new item, and a newer one of its own that leads the inbox (so "selected again" means this one, not the first
    // row); each with a CVE of its own and nothing in common, so they don't group. The item's id, then the newer one's.
    private async Task<(Guid Id, Guid Lead)> Seed(string key, int minutesAhead)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        var source = new Source { Name = "Undo source " + key, Url = "https://undo.example/feed/" + key, DefaultTlp = TlpLevel.Clear };
        var at = DateTimeOffset.UtcNow.AddMinutes(minutesAhead);
        var item = new FeedItem
        {
            Source = source, Title = TitleOf(key), Url = "https://undo.example/" + key, UrlHash = "triage-undo-" + key,
            Cves = $"CVE-2026-{7100 + minutesAhead}", PublishedAtUtc = at, FetchedAtUtc = at,
        };
        var lead = new FeedItem
        {
            Source = source, Title = $"harbour cranes idle {key} after a scheduling outage", Url = "https://undo.example/lead-" + key,
            UrlHash = "triage-undo-lead-" + key, Cves = $"CVE-2026-{8100 + minutesAhead}", PublishedAtUtc = at.AddMinutes(5), FetchedAtUtc = at.AddMinutes(5),
        };
        db.AddRange(source, item, lead);
        await db.SaveChangesAsync();
        return (item.Id, lead.Id);
    }

    // Set aside afterwards, so the next test's items lead the inbox whatever order they run in.
    private async Task SetAside(params Guid[] ids)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>().FeedItems
            .Where(i => ids.Contains(i.Id)).ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, FeedItemStatus.Duplicate));
    }

    // Opens Triage on the item (not the newest), as a link to it does.
    private Task<ComponentHarness.Rendered<Triage>> Open(Guid id, string tab = "") =>
        ComponentHarness.RenderAsync<Triage>(app, $"/triage?{tab}at={id}");

    private static string Selected(string html)
    {
        var at = html.IndexOf("<li class=\"tide-triage-row selected", StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, "a row is selected");
        return html[at..html.IndexOf("</li>", at, StringComparison.Ordinal)];
    }

    // A colleague's decision, through the service as they'd make it.
    private async Task<TriageDecision> AsColleague(Func<IntakeService, Task<TriageDecision>> decide)
    {
        await using var scope = ComponentHarness.SignedIn(app, new ComponentHarness.SignIn("dev:colleague", "Colleague", "Analyst"));
        return await decide(scope.ServiceProvider.GetRequiredService<IntakeService>());
    }

    private static string TitleOf(string key) => "triage-undo " + key + ": a vendor patches a router flaw";

    private async Task<FeedItem> Item(Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        return await db.FeedItems.AsNoTracking().SingleAsync(i => i.Id == id);
    }

    [Theory]
    [InlineData("dismiss", "Dismissed", FeedItemStatus.Dismissed, 100)]
    [InlineData("shortlist", "Shortlisted", FeedItemStatus.Shortlisted, 200)]
    public async Task ASingleDecision_ByItsKey_OffersUndo_WhichPutsItBackInNew(string key, string said, FeedItemStatus decided, int minutesAhead)
    {
        var (id, lead) = await Seed(key, minutesAhead);
        await using var r = await Open(id);
        Selected(await r.HtmlOnce(TitleOf(key))).Should().Contain(TitleOf(key));

        await r.Click(t => t.OnTriageKey(key));
        (await Item(id)).Status.Should().Be(decided);
        (await r.Html()).Should().NotContain(TitleOf(key), "the row has left the New list");
        var toasts = r.Scope.ServiceProvider.GetRequiredService<ToastService>();
        var toast = toasts.Toasts.Single(t => t.Message == $"{said}: {TitleOf(key)}");
        toast.Undo.Should().NotBeNull();
        toasts.Undoable.Should().Be(toast, "Alt+Z takes it back too");

        await r.Click(_ => toasts.UndoAsync(toast.Id));
        var back = await Item(id);
        back.Status.Should().Be(FeedItemStatus.New);
        back.StatusByName.Should().BeNull();
        back.DismissReason.Should().BeNull();
        toasts.Toasts.Should().Contain(t => t.Message == $"Put back in New: {TitleOf(key)}");
        Selected(await r.Html()).Should().Contain(TitleOf(key), "it's back, and selected, though a newer item leads the list");
        await SetAside(id, lead);
    }

    [Fact]
    public async Task Undo_LeavesAColleaguesSameDecisionMadeAfter()
    {
        // Both pressed X; the colleague's dismissal landed second, and is theirs to undo, not ours.
        const string key = "redismiss";
        var (id, lead) = await Seed(key, 250);
        await using var r = await Open(id);
        await r.HtmlOnce(TitleOf(key));
        await r.Click(t => t.OnTriageKey("dismiss"));
        var toasts = r.Scope.ServiceProvider.GetRequiredService<ToastService>();
        var toast = toasts.Toasts.Single(t => t.Message == $"Dismissed: {TitleOf(key)}");

        var theirs = await AsColleague(intake => intake.DismissAsync(id, "Out of scope"));
        theirs.FromNew.Should().BeFalse("it was already dismissed");
        await r.Click(_ => toasts.UndoAsync(toast.Id));

        var item = await Item(id);
        item.Status.Should().Be(FeedItemStatus.Dismissed);
        item.StatusByName.Should().Be("Colleague");
        item.DismissReason.Should().Be("Out of scope");
        toasts.Toasts.Should().Contain(t => t.Message == "Nothing to undo: a colleague already changed it.");
        await SetAside(id, lead);
    }

    [Fact]
    public async Task AStaleRow_ThatReplacesAColleaguesShortlist_OffersNoUndo()
    {
        // The page read the item as New; a colleague shortlisted it since (unheard here), and X replaced that. Undo would
        // put it in New, not back on the shortlist, so there's none.
        const string key = "stale";
        var (id, lead) = await Seed(key, 350);
        await using var r = await Open(id);
        await r.HtmlOnce(TitleOf(key));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            await db.FeedItems.Where(i => i.Id == id).ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, FeedItemStatus.Shortlisted)
                .SetProperty(i => i.StatusByName, "Colleague").SetProperty(i => i.StatusAtUtc, DateTimeOffset.UtcNow));
        }

        await r.Click(t => t.OnTriageKey("dismiss"));

        (await Item(id)).Status.Should().Be(FeedItemStatus.Dismissed);
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts
            .Should().NotContain(t => t.Undo != null && t.Message.Contains(TitleOf(key), StringComparison.Ordinal));
        await SetAside(id, lead);
    }

    [Fact]
    public async Task Undo_LeavesAColleaguesDecisionSince()
    {
        const string key = "colleague";
        var (id, lead) = await Seed(key, 300);
        await using var r = await Open(id);
        await r.HtmlOnce(TitleOf(key));
        await r.Click(t => t.OnTriageKey("dismiss"));
        var toasts = r.Scope.ServiceProvider.GetRequiredService<ToastService>();
        var toast = toasts.Toasts.Single(t => t.Message == $"Dismissed: {TitleOf(key)}");

        // A colleague puts it back and shortlists it before the Undo.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            await db.FeedItems.Where(i => i.Id == id).ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, FeedItemStatus.Shortlisted));
        }
        await r.Click(_ => toasts.UndoAsync(toast.Id));

        (await Item(id)).Status.Should().Be(FeedItemStatus.Shortlisted);
        toasts.Toasts.Should().Contain(t => t.Message == "Nothing to undo: a colleague already changed it.");
        await SetAside(id, lead);
    }

    [Fact]
    public async Task DismissingFromShortlisted_OffersNoUndo_AsPuttingBackWouldLandInNew()
    {
        const string key = "shortlisted";
        var (id, lead) = await Seed(key, 400);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            await db.FeedItems.Where(i => i.Id == id).ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, FeedItemStatus.Shortlisted));
        }
        await using var r = await Open(id, "tab=shortlisted&");
        await r.HtmlOnce(TitleOf(key));
        await r.Click(t => t.OnTriageKey("dismiss"));

        (await Item(id)).Status.Should().Be(FeedItemStatus.Dismissed);
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().NotContain(t => t.Message.Contains(TitleOf(key), StringComparison.Ordinal));
        await SetAside(id, lead);
    }
}
