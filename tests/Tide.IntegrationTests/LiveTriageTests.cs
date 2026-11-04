using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Triage hearing a colleague: the rows that arrive, change and leave are marked (a ghost says who sent the one that
/// left where), the selection moves on from a row taken away, one toast names the colleague, and nothing moves under an
/// open promote form until the "Updates waiting" pill is chosen. Its own app, so its items lead its inbox.
/// </summary>
public class LiveTriageTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Robin = new("dev:robin", "Robin", "Analyst", "Admin");

    // Items of the test's own, ahead of the demo's (newest first), with titles too unalike to be grouped.
    private async Task<List<FeedItem>> Seed(string key, int minutesAhead, params string[] titles)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = new Source { Name = "Live feed " + key, Url = $"https://live.example/feed/{key}" };
        var at = DateTimeOffset.UtcNow.AddMinutes(minutesAhead);
        var items = titles.Select((t, n) => new FeedItem
        {
            Source = source, Title = t, Url = $"https://live.example/{key}/{n}", UrlHash = $"live-{key}-{n}",
            PublishedAtUtc = at.AddMinutes(-n), FetchedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        }).ToList();
        db.Add(source);
        db.AddRange(items);
        await db.SaveChangesAsync();
        return items;
    }

    private async Task Change(Func<AppDbContext, Task> what)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await what(db);
        await db.SaveChangesAsync();
    }

    // What Robin's Triage tells the others once Robin has triaged (it does so from another circuit).
    private void RobinAnnounces() =>
        app.Services.GetRequiredService<ChangeFeed>().Publish(new Change(ChangeFeed.Triage, Robin.UserId, Robin.Name, Guid.NewGuid()));

    // The markup's quotes, as the renderer encodes them.
    private static string Quoted(string s) => s.Replace("“", "&#x201C;", StringComparison.Ordinal).Replace("”", "&#x201D;", StringComparison.Ordinal);

    private static string Line(string html, string marker)
    {
        var at = html.IndexOf(marker, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"{marker} is shown");
        var start = html.LastIndexOf("<li", at, StringComparison.Ordinal);
        return html[start..html.IndexOf("</li>", at, StringComparison.Ordinal)];
    }

    private async Task SetAside(IEnumerable<FeedItem> items)
    {
        var ids = items.Select(i => i.Id).ToList();
        await Change(db => db.FeedItems.Where(i => ids.Contains(i.Id)).ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, FeedItemStatus.Duplicate)));
    }

    [Fact]
    public async Task AColleaguesTriage_MarksWhatArrivedChangedAndLeft_AndMovesTheSelectionOn()
    {
        var items = await Seed("a", 500, "Zarnox ransomware hits port operator", "Quillfeather phishing kit resurfaces", "Mirelake botnet grows");
        var (gone, changed, kept) = (items[0], items[1], items[2]);
        var arrived = new List<FeedItem>();
        try
        {
            await using var r = await RenderAsync<Triage>(app, "/triage");
            Line(await r.HtmlOnce("Zarnox ransomware"), "Zarnox ransomware").Should().Contain("selected", "the newest is selected");

            await using (var scope = SignedIn(app, Robin))
                await scope.ServiceProvider.GetRequiredService<Tide.Application.Intake.IntakeService>().DismissAsync(gone.Id, "Not relevant");
            await Change(db => db.FeedItems.Where(i => i.Id == changed.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.Title, "Quillfeather phishing kit resurfaces in Europe")));
            arrived.AddRange(await Seed("a2", 450, "Brightwater wiper found"));
            await Change(db => db.FeedItems.Where(i => i.Id == arrived[0].Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.FetchedAtUtc, DateTimeOffset.UtcNow.AddMinutes(1))));
            RobinAnnounces();

            var html = await r.HtmlOnce("tide-live-ghost");
            Line(html, Quoted("Robin dismissed “Zarnox ransomware hits port operator”")).Should().Contain("class=\"tide-live-ghost\"").And.Contain("aria-hidden=\"true\"");
            Line(html, "Quillfeather phishing kit resurfaces in Europe").Should().Contain("tide-live-changed").And.Contain("selected",
                "the row after the one taken away is selected");
            Line(html, "Brightwater wiper found").Should().Contain("tide-live-arrived").And.Contain("tide-live-new").And.Contain("Brightwater wiper found (new)");
            Line(html, "Mirelake botnet grows").Should().NotContain("tide-live-");
            html.Should().Contain(Quoted("Robin dismissed “Zarnox ransomware hits port operator”. The next item is selected."), "the status line says so")
                .And.Contain("tide-tabbar-count tide-live-tick", "the Dismissed count ticks");
            r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Select(t => t.Message)
                .Should().Contain("Robin dismissed 1 item · 1 new from the feeds");

            (await GhostGone(r)).Should().BeTrue("the ghost goes once it has faded");
        }
        finally { await SetAside(items.Concat(arrived)); }
    }

    private static async Task<bool> GhostGone(Rendered<Triage> r)
    {
        for (var i = 0; i < 50; i++)
        {
            if (!(await r.Html()).Contains("tide-live-ghost", StringComparison.Ordinal)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    [Fact]
    public async Task SameCve_IsAButton_ThatSearchesTheInboxForTheSharedCve()
    {
        var items = await Seed("c", 700, "Halvorn kernel flaw patched", "Distro ships fix for Halvorn bug", "Pellmark phishing wave");
        await Change(db => db.FeedItems.Where(i => i.Id == items[0].Id || i.Id == items[1].Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.Cves, "CVE-2026-77001")));
        try
        {
            await using var r = await RenderAsync<Triage>(app, "/triage");
            var html = await r.HtmlOnce("Halvorn kernel flaw");
            Line(html, "Halvorn kernel flaw patched").Should().Contain("<button type=\"button\" class=\"tide-badge tide-badge-warn tide-badge-wrap tide-chip-button\"")
                .And.Contain("Show the items with CVE-2026-77001");

            Tide.Application.Intake.InboxRow row;
            await using (var scope = SignedIn(app, Robin))
                row = (await scope.ServiceProvider.GetRequiredService<Tide.Application.Intake.IntakeService>().InboxAsync(new(Search: "Halvorn")))
                    .Single(x => x.Item.Id == items[0].Id);
            await r.Click(t => t.ShowSameCve(row));

            html = await r.Html();
            html.Should().Contain("Halvorn kernel flaw patched").And.Contain("Distro ships fix for Halvorn bug").And.NotContain("Pellmark phishing wave");
        }
        finally { await SetAside(items); }
    }

    [Fact]
    public async Task UnderAnOpenPromoteForm_ChangesWait_UntilThePillShowsThem()
    {
        var items = await Seed("b", 600, "Ostravia spyware campaign named", "Velkin supply chain compromise");
        var gone = items[1];
        try
        {
            await using var r = await RenderAsync<Triage>(app, "/triage");
            await r.HtmlOnce("Ostravia spyware");
            await r.Click(t => t.OnTriageKey("promote"));
            (await r.Html()).Should().Contain("tide-promote-form");

            await Change(db => db.FeedItems.Where(i => i.Id == gone.Id).ExecuteUpdateAsync(u => u
                .SetProperty(i => i.Status, FeedItemStatus.Dismissed).SetProperty(i => i.StatusByName, "Robin")));
            RobinAnnounces();

            var waiting = await r.HtmlOnce("Updates waiting");
            waiting.Should().Contain("class=\"tide-live-pill\"").And.Contain("Velkin supply chain compromise", "nothing moved under the form")
                .And.NotContain("tide-live-ghost").And.Contain("tide-promote-form");

            await r.Click(t => t.ShowWaiting());

            var html = await r.Html();
            html.Should().NotContain("tide-live-pill").And.Contain(Quoted("Robin dismissed “Velkin supply chain compromise”"))
                .And.Contain("tide-promote-form", "the form's item is still there to promote");
            Line(html, "Ostravia spyware campaign named").Should().Contain("selected");
        }
        finally { await SetAside(items); }
    }
}
