using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Preferences;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>
/// Triage's default action follows what TIDE knows: a report covered by an article it can still go to has "Attach to"
/// as its main button (P still promotes), a group covered by one has "Attach all N", one step for every report. The
/// flags sit at the end of a row's line in one order, the keys are one line with the rest behind "?", and the reading
/// pane opens by itself on a wide window until the person closes it, which is remembered.
/// </summary>
public class TriageAttachTests(TideApp app) : IClassFixture<TideApp>
{
    // An article (a draft unless said) naming the CVE, as earlier coverage of it.
    private async Task<Guid> Article(string title, string cve, ArticleStatus status = ArticleStatus.Draft)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var article = new Article { Title = title, Bluf = "Earlier coverage.", Status = status, AuthorName = "Robin" };
        article.Cves.Add(new ArticleCve { CveId = cve });
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        return article.Id;
    }

    // New items of a feed of their own, newest first as given, each naming the CVE given for it (or none).
    private async Task<(Guid Source, List<Guid> Ids)> Items(params (string Title, string? Cve)[] items)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = Guid.NewGuid().ToString("N");
        var source = new Source { Name = "Attach feed " + key[..6], Url = $"https://attach.example/{key}/feed" };
        var now = DateTimeOffset.UtcNow;
        var rows = items.Select((x, n) => new FeedItem
        {
            Source = source, Title = x.Title, Url = $"https://attach.example/{key}/{n}", UrlHash = $"attach-{key}-{n}", Cves = x.Cve,
            PublishedAtUtc = now.AddMinutes(-n), FetchedAtUtc = now.AddMinutes(-n),
        }).ToList();
        db.Add(source);
        db.AddRange(rows);
        await db.SaveChangesAsync();
        return (source.Id, rows.Select(r => r.Id).ToList());
    }

    private async Task<List<FeedItem>> Stored(IEnumerable<Guid> ids)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wanted = ids.ToList();
        return await db.FeedItems.AsNoTracking().Where(i => wanted.Contains(i.Id)).ToListAsync();
    }

    // The selected line's markup.
    private static string Selected(string html)
    {
        var at = Regex.Match(html, "<li class=\"tide-triage-row[^\"]*selected").Index;
        at.Should().BePositive("a line is selected");
        return html[at..html.IndexOf("</li>", at, StringComparison.Ordinal)];
    }

    // Someone of their own, so a preference one test keeps isn't another's.
    private static ComponentHarness.SignIn Someone() => new("attach:" + Guid.NewGuid().ToString("N"), "Attach tester", "Analyst");

    [Fact]
    public async Task ACoveredItem_HasAttachAsItsMainButton_PStillPromotes_AndTheButtonAttachesIt()
    {
        var article = await Article("Lantern VPN flaw exploited in the wild", "CVE-2026-7201");
        var (source, ids) = await Items(("Lantern VPN bug now used by two gangs", "CVE-2026-7201"));
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: Someone());

        var row = Selected(await r.HtmlOnce("Lantern VPN bug now used by two gangs"));
        row.Should().Contain("<button class=\"btn btn-primary btn-sm\"").And.Contain("Attach to “Lantern VPN flaw exploited in the wild”")
            .And.Contain("<button class=\"btn btn-outline-secondary btn-sm\"");
        Regex.IsMatch(row, "class=\"btn btn-primary btn-sm\"[^>]*>(<span[^>]*></span>)? Attach to").Should().BeTrue("Attach is the main (primary) button");
        Regex.IsMatch(row, "class=\"btn btn-outline-secondary btn-sm\"[^>]*>(<span[^>]*></span>)? Promote <span class=\"tide-kbd\">P</span>")
            .Should().BeTrue("Promote is still there, with its key");
        row.Should().NotContain("Add as a source of “Lantern VPN flaw", "the main button says it once");

        // P still promotes: its form opens, and Esc closes it.
        await r.Click(t => t.OnTriageKey("promote"));
        (await r.Html()).Should().Contain("aria-label=\"Promote to an article\"");
        await r.Click(t => t.OnTriageKey("cancel"));

        await r.Click(t => t.AttachCovered(0));
        (await Stored(ids)).Single().Should().Match<FeedItem>(i => i.Status == FeedItemStatus.Promoted && i.ArticleId == article);
    }

    [Fact]
    public async Task AnItemCoveredOnlyByAPresentedArticle_KeepsPromoteAsItsMainButton()
    {
        await Article("Kestrel router flaw (presented)", "CVE-2026-7202", ArticleStatus.Presented);
        var (source, _) = await Items(("Kestrel routers still unpatched", "CVE-2026-7202"));
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: Someone());

        var row = Selected(await r.HtmlOnce("Kestrel routers still unpatched"));
        row.Should().NotContain("Attach to “").And.Contain("covered");
        Regex.IsMatch(row, "class=\"btn btn-primary btn-sm\"[^>]*>(<span[^>]*></span>)? Promote <span class=\"tide-kbd\">P</span>").Should().BeTrue();
    }

    [Fact]
    public async Task AGroupCoveredByAnArticle_IsAttachedWhole_InOneStep()
    {
        var article = await Article("Quorvexz theft, first report", "CVE-2026-7203");
        // Three reports of one event from two feeds, published ahead of every other test's items so the group leads.
        List<Guid> ids;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var clear = new Source { Name = "Group clear feed", Url = "https://attach.example/group/clear" };
            var other = new Source { Name = "Group other feed", Url = "https://attach.example/group/other" };
            var at = DateTimeOffset.UtcNow.AddMinutes(500);
            FeedItem Item(Source s, string title, int n, string? cve) => new()
            {
                Source = s, Title = title, Url = $"https://attach.example/group/{n}", UrlHash = $"attach-group-{n}", Cves = cve,
                PublishedAtUtc = at.AddMinutes(-n), FetchedAtUtc = at.AddMinutes(-n),
            };
            var items = new[]
            {
                Item(clear, "Quorvexz links $7.1 billion theft to Zentraz laptop", 0, null),
                Item(other, "Quorvexz hit by $7.1 billion hack", 1, "CVE-2026-7203"),
                Item(clear, "Quorvexz probes $7.1 billion theft", 2, null),
            };
            db.AddRange(clear, other);
            db.AddRange(items);
            await db.SaveChangesAsync();
            ids = items.Select(i => i.Id).ToList();
        }
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage", @as: Someone());

        var line = Selected(await r.HtmlOnce("Looks like one event"));
        line.Should().Contain("tide-triage-group");
        Regex.IsMatch(line, "class=\"btn btn-primary btn-sm\"[^>]*>(<span[^>]*></span>)? Attach all 3 to “Quorvexz theft, first report”").Should().BeTrue();
        Regex.IsMatch(line, "class=\"btn btn-outline-secondary btn-sm\"[^>]*>(<span[^>]*></span>)? Promote as one article \\(3 sources\\) <span class=\"tide-kbd\">P</span>")
            .Should().BeTrue("promoting the group is still there, and P");

        await r.Click(t => t.AttachCovered(0));
        (await Stored(ids)).Should().HaveCount(3).And.AllSatisfy(i =>
        {
            i.Status.Should().Be(FeedItemStatus.Promoted);
            i.ArticleId.Should().Be(article);
        });
    }

    [Fact]
    public async Task TheFlags_AreAtTheEndOfTheLine_InOneOrder()
    {
        await Article("Osprey mail server flaw", "CVE-2026-7204");
        var (source, _) = await Items(("Osprey mail servers attacked", "CVE-2026-7204"), ("Osprey flaw: more victims", "CVE-2026-7204"));
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: Someone());

        var row = Selected(await r.HtmlOnce("Osprey mail servers attacked"));
        var meta = row[row.IndexOf("<div class=\"tide-triage-meta\">", StringComparison.Ordinal)..];
        var flags = meta.IndexOf("<span class=\"tide-triage-flags\">", StringComparison.Ordinal);
        flags.Should().BePositive("the flags are together");
        meta.IndexOf("CVE-2026-7204 covered", StringComparison.Ordinal).Should().BeGreaterThan(flags);
        meta.IndexOf("Same CVE as 1 other new item", StringComparison.Ordinal).Should().BeGreaterThan(meta.IndexOf("CVE-2026-7204 covered", StringComparison.Ordinal),
            "covered comes before same CVE, as the order says");
        meta.IndexOf("tide-reliability", StringComparison.Ordinal).Should().BeLessThan(flags, "what the item is comes first, its flags after");
    }

    [Fact]
    public async Task TheKeys_AreOneLine_AndTheRestAreBehindQuestionMark()
    {
        var (source, _) = await Items(("Keys legend item", null));
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: Someone());
        var html = await r.HtmlOnce("Keys legend item");
        var at = html.IndexOf("<div class=\"tide-sub tide-triage-keys\">", StringComparison.Ordinal);
        at.Should().BePositive();
        var keys = html[at..html.IndexOf("</div>", at, StringComparison.Ordinal)];
        keys.Should().Contain("<span class=\"tide-kbd\">J</span>").And.Contain("<span class=\"tide-kbd\">P</span> promote")
            .And.Contain("<span class=\"tide-kbd\">V</span> read it here").And.Contain("<span class=\"tide-kbd\">?</span> every key")
            .And.NotContain("<span class=\"tide-kbd\">R</span>", "the rest are in the shortcuts list");
        var listed = Tide.Web.Components.Layout.Shortcuts.For("triage").SelectMany(g => g.Keys).SelectMany(k => k.Keys).ToList();
        listed.Should().Contain(["J", "K", "S", "X", "D", "P", "O", "V", "R", "Esc", "Enter", "→", "←", "↓", "↑", "?"], "every key still works and is listed");
    }

    [Fact]
    public async Task TheReadingPane_OpensByItselfOnAWideWindow_UntilClosed_WhichIsRemembered()
    {
        var (source, _) = await Items(("Reading default item", null));
        var who = Someone();
        string html;
        await using (var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: who))
        {
            html = await r.HtmlOnce("Reading default item");
            html.Should().Contain("tide-triage-layout reading reading-auto", "open by itself, where the window is wide enough (app.css)")
                .And.Contain("<h2 class=\"tide-reading-title\">Reading default item</h2>");

            // V reads it here and keeps it open at every width; Esc (close-reading) closes it.
            await r.Click(t => t.OnTriageKey("read"));
            (await r.Html()).Should().Contain("tide-triage-layout reading\"");
            await r.Click(t => t.OnTriageKey("close-reading"));
            (await r.Html()).Should().NotContain("<aside class=\"tide-card tide-reading\"");
        }
        await using (var again = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: who))
        {
            (await again.HtmlOnce("Reading default item")).Should().NotContain("<aside class=\"tide-card tide-reading\"", "closed is remembered");
            await again.Click(t => t.OnTriageKey("read"));
        }
        await using (var third = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: who))
        {
            (await third.HtmlOnce("Reading default item")).Should().Contain("tide-triage-layout reading\"", "open is remembered too, at every width");
        }
        await using (var scope = ComponentHarness.SignedIn(app, who))
        {
            (await scope.ServiceProvider.GetRequiredService<UserPreferenceService>().GetAsync(UserPreferences.TriageReading)).Should().Be(UserPreferences.ReadingOpen);
        }
    }
}
