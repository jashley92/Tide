using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>Triage's single Dismiss and Shortlist (X, S): the row leaves the list at one keypress, so a toast offers to
/// put it back.</summary>
public class TriageUndoTests(TideApp app) : IClassFixture<TideApp>
{
    // The newest new item, so it leads the inbox and is selected; a CVE of its own, so nothing groups with it.
    private async Task<Guid> Seed(string key, int minutesAhead)
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
        db.AddRange(source, item);
        await db.SaveChangesAsync();
        return item.Id;
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
        var id = await Seed(key, minutesAhead);
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage");
        await r.HtmlOnce(TitleOf(key));

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
        var html = await r.Html();
        var at = html.IndexOf("<li class=\"tide-triage-row selected", StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0);
        html[at..html.IndexOf("</li>", at, StringComparison.Ordinal)].Should().Contain(TitleOf(key), "it's back, and selected");

        // Set aside, so the other case's item leads the inbox whatever order they run in.
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>().FeedItems.Where(i => i.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, FeedItemStatus.Duplicate));
    }

    [Fact]
    public async Task Undo_LeavesAColleaguesDecisionSince()
    {
        const string key = "colleague";
        var id = await Seed(key, 300);
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage");
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
    }

    [Fact]
    public async Task DismissingFromShortlisted_OffersNoUndo_AsPuttingBackWouldLandInNew()
    {
        const string key = "shortlisted";
        var id = await Seed(key, 400);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            await db.FeedItems.Where(i => i.Id == id).ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, FeedItemStatus.Shortlisted));
        }
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage?tab=shortlisted");
        await r.HtmlOnce(TitleOf(key));
        await r.Click(t => t.OnTriageKey("dismiss"));

        (await Item(id)).Status.Should().Be(FeedItemStatus.Dismissed);
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().NotContain(t => t.Message.Contains(TitleOf(key), StringComparison.Ordinal));
    }
}
