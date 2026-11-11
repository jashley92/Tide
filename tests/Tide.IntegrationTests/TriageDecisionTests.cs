using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>
/// Triage's decision bar: while the reading pane is shown, the selected item's buttons sit in a bar at the foot of the
/// pane, not in the row. The bar opens with a line saying why its main button is the main one, from what the item's
/// coverage says: attach it when an article that can still take a report covers it, otherwise promote it. With the pane
/// closed (or hidden on a narrow window) the buttons stay in the selected row, as they always have.
/// </summary>
public class TriageDecisionTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task Article(string title, string cve, ArticleStatus status = ArticleStatus.Draft)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var article = new Article { Title = title, Bluf = "Earlier coverage.", Status = status, AuthorName = "Robin" };
        article.Cves.Add(new ArticleCve { CveId = cve });
        db.Articles.Add(article);
        await db.SaveChangesAsync();
    }

    // One new item, of a feed of its own (the page is opened on that feed), naming the CVE given (or none).
    private async Task<Guid> Item(string title, string? cve)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = Guid.NewGuid().ToString("N");
        var source = new Source { Name = "Decide feed " + key[..6], Url = $"https://decide.example/{key}/feed" };
        db.Add(source);
        db.Add(new FeedItem
        {
            Source = source, Title = title, Url = $"https://decide.example/{key}/0", UrlHash = $"decide-{key}", Cves = cve,
            PublishedAtUtc = DateTimeOffset.UtcNow, FetchedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return source.Id;
    }

    private static ComponentHarness.SignIn Someone() => new("decide:" + Guid.NewGuid().ToString("N"), "Decide tester", "Analyst");

    private static string Between(string html, string start, string end)
    {
        var at = html.IndexOf(start, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"{start} is drawn");
        return html[at..(html.IndexOf(end, at, StringComparison.Ordinal) + end.Length)];
    }

    private static string Pane(string html) => Between(html, "<aside class=\"tide-card tide-reading\"", "</aside>");

    private static string Bar(string html) => Between(html, "<section class=\"tide-triage-decide\"", "</section>");

    // The reason line's words, as read.
    private static string Why(string html) => System.Net.WebUtility.HtmlDecode(Between(html, "<p class=\"tide-triage-why\">", "</p>"));

    private static string Selected(string html)
    {
        var at = Regex.Match(html, "<li class=\"tide-triage-row[^\"]*selected").Index;
        at.Should().BePositive("a line is selected");
        return html[at..html.IndexOf("</li>", at, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task ACoveredItem_HasItsDecisionBarUnderTheReadingPane_SayingWhyAttachIsTheMainButton()
    {
        await Article("Plover VPN flaw exploited in the wild", "CVE-2026-7301");
        var source = await Item("Plover VPN bug now used by two gangs", "CVE-2026-7301");
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: Someone());
        var html = await r.HtmlOnce("Plover VPN bug now used by two gangs");

        var pane = Pane(html);
        pane.Should().Contain("<section class=\"tide-triage-decide\"", "the bar is in the reading pane");
        pane.IndexOf("<section class=\"tide-triage-decide\"", StringComparison.Ordinal)
            .Should().BeGreaterThan(pane.IndexOf("tide-reading-title", StringComparison.Ordinal), "under what the pane shows");

        var bar = Bar(html);
        bar.Should().Contain("aria-label=\"Decide on this item\"");
        var why = Why(bar);
        why.Should().Contain("Already covered by “Plover VPN flaw exploited in the wild”")
            .And.Contain("so the suggested step is to attach it");
        Regex.IsMatch(bar, "class=\"btn btn-primary btn-sm tide-triage-attach\"[^>]*>(<span[^>]*></span>)? Attach to").Should().BeTrue("Attach is the main button");
        Regex.IsMatch(bar, "class=\"btn btn-outline-secondary btn-sm tide-triage-promote\"[^>]*>(<span[^>]*></span>)? Promote <span class=\"tide-kbd\">P</span>")
            .Should().BeTrue("Promote is beside it");
        bar.IndexOf("tide-triage-why", StringComparison.Ordinal).Should().BeLessThan(bar.IndexOf("tide-triage-attach", StringComparison.Ordinal), "the reason comes first");

        var row = Selected(html);
        row.Should().NotContain("tide-triage-decide").And.NotContain("tide-triage-promote").And.NotContain("Shortlist <span", "the row is only the item now");
    }

    [Fact]
    public async Task AnItemNothingCovers_HasPromoteAsTheMainButton_AndSaysSo()
    {
        var source = await Item("Sandpiper phishing kit adds QR codes", null);
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: Someone());
        var bar = Bar(await r.HtmlOnce("Sandpiper phishing kit adds QR codes"));

        Why(bar).Should().Contain("Nothing TIDE has covers it yet, so the suggested step is to promote it");
        bar.Should().NotContain("tide-triage-attach\"");
        Regex.IsMatch(bar, "class=\"btn btn-primary btn-sm tide-triage-promote\"[^>]*>(<span[^>]*></span>)? Promote <span class=\"tide-kbd\">P</span>").Should().BeTrue();
    }

    [Fact]
    public async Task AnItemCoveredOnlyByAnArticleThatCantTakeIt_SaysWhyItsPromoted()
    {
        await Article("Godwit router flaw (presented)", "CVE-2026-7302", ArticleStatus.Presented);
        var source = await Item("Godwit routers still unpatched", "CVE-2026-7302");
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: Someone());
        var bar = Bar(await r.HtmlOnce("Godwit routers still unpatched"));

        Why(bar).Should().Contain("Covered by “Godwit router flaw (presented)”")
            .And.Contain("which cannot take another report now, so the suggested step is to promote it");
        Regex.IsMatch(bar, "class=\"btn btn-primary btn-sm tide-triage-promote\"").Should().BeTrue();
    }

    [Fact]
    public async Task TheBarsButtons_RunMainFirst_ThenShortlist_AndDismissWithItsReasonLast()
    {
        var source = await Item("Curlew ransomware note changes", null);
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: Someone());
        var bar = Bar(await r.HtmlOnce("Curlew ransomware note changes"));
        var acts = Between(bar, "<div class=\"tide-triage-acts\">", "</select>");
        int At(string s) => acts.IndexOf(s, StringComparison.Ordinal);
        At("tide-triage-promote").Should().BePositive();
        At("Shortlist <span").Should().BeGreaterThan(At("tide-triage-promote"));
        At("aria-label=\"Reason for dismissing it\"").Should().BeGreaterThan(At("Shortlist <span"), "Dismiss and its reason close the line");
        bar.IndexOf("Dismiss <span class=\"tide-kbd\">X</span>", StringComparison.Ordinal).Should().BeGreaterThan(bar.IndexOf("Reason for dismissing it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithThePaneClosed_TheDecisionStaysInTheSelectedRow()
    {
        await Article("Turnstone mail flaw", "CVE-2026-7303");
        var source = await Item("Turnstone mail servers attacked", "CVE-2026-7303");
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: Someone());
        await r.HtmlOnce("Turnstone mail servers attacked");
        await r.Click(t => t.OnTriageKey("close-reading"));
        var html = await r.Html();

        html.Should().NotContain("<aside class=\"tide-card tide-reading\"");
        var row = Selected(html);
        row.Should().Contain("<section class=\"tide-triage-decide\"", "the row holds the decision, reason and all");
        Why(row).Should().Contain("so the suggested step is to attach it");
        Regex.IsMatch(row, "class=\"btn btn-primary btn-sm tide-triage-attach\"").Should().BeTrue();
        row.Should().Contain("Shortlist <span class=\"tide-kbd\">S</span>").And.Contain("Dismiss <span class=\"tide-kbd\">X</span>");
    }

    [Fact]
    public async Task OnAWindowTooNarrowForThePane_TheDecisionIsInTheRowOnly()
    {
        var source = await Item("Dunlin botnet adds a new loader", null);
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?source={source}", @as: Someone());
        await r.HtmlOnce("Dunlin botnet adds a new loader");
        // triage.js says the window has no room for the pane that opened by itself: the stylesheet hides it.
        await r.Click(t => t.OnReadingRoom(false));
        var html = await r.Html();

        Selected(html).Should().Contain("class=\"tide-triage-decide\"", "the buttons are in the row");
        Regex.Matches(html, "class=\"tide-triage-decide\"").Should().HaveCount(1, "and only there: the hidden pane holds no second copy");

        await r.Click(t => t.OnReadingRoom(true));
        Pane(await r.Html()).Should().Contain("class=\"tide-triage-decide\"", "with room again, the bar is back under the pane");
    }

    [Fact]
    public void TheReason_FollowsTheCoverage()
    {
        var briefed = Covered(attachable: true, brief: new DateOnly(2026, 11, 9));
        Triage.Suggestion(briefed, briefed, 1)
            .Should().Be("Already covered by “Flaw” (briefed Mon Nov 9), so the suggested step is to attach it as another report.");
        var draft = Covered(attachable: true, brief: null);
        Triage.Suggestion(draft, draft, 3)
            .Should().Be("Already covered by “Flaw”, so the suggested step is to attach all 3 as more reports of it.");
        Triage.Suggestion(null, Covered(attachable: false, brief: null), 1)
            .Should().Be("Covered by “Flaw”, which cannot take another report now, so the suggested step is to promote it as a new article.");
        Triage.Suggestion(null, null, 1).Should().Be("Nothing TIDE has covers it yet, so the suggested step is to promote it.");
        Triage.Suggestion(null, null, 2).Should().Be("Nothing TIDE has covers them yet, so the suggested step is to promote the 2 as one article.");
    }

    private static Tide.Application.Intake.Coverage Covered(bool attachable, DateOnly? brief) =>
        new("CVE-2026-0001", Guid.NewGuid(), "Flaw", brief) { Attachable = attachable };
}
