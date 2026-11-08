using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>
/// Triage's suggested groups ("Looks like one event"): one line for the event, opening to the reports, each with all its
/// actions; "Promote as one article" and "Not the same", and no "dismiss all".
/// </summary>
public class TriageGroupTests(TideApp app) : IClassFixture<TideApp>
{
    // Three reports of one made-up event (names of the test's own, so no other test's items join), from two feeds, the
    // second at AMBER, the newest leading the inbox; their ids, newest first.
    private async Task<List<Guid>> Seed(string key, int minutesAhead)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        var clear = new Source { Name = "Clear feed " + key, Url = "https://group.example/feed/clear-" + key, DefaultTlp = TlpLevel.Clear };
        var amber = new Source { Name = "Amber feed " + key, Url = "https://group.example/feed/amber-" + key, DefaultTlp = TlpLevel.Amber };
        var at = DateTimeOffset.UtcNow.AddMinutes(minutesAhead);
        FeedItem Item(Source source, string title, int n, string? cves) => new()
        {
            Source = source, Title = title, Url = $"https://group.example/{key}/{n}", UrlHash = $"group-{key}-{n}", Cves = cves,
            Excerpt = $"Excerpt {n} of the {key} story.", PublishedAtUtc = at.AddMinutes(-n), FetchedAtUtc = at.AddMinutes(-n),
        };
        var name = "Quorvex" + key;
        var items = new[]
        {
            Item(clear, $"{name} links $6.3 billion theft to Zentra{key} laptop", 0, null),
            Item(amber, $"{name} hit by $6.3 billion hack", 1, "CVE-2026-6001"),
            Item(clear, $"{name} probes $5.8 billion theft", 2, null),
        };
        db.AddRange(clear, amber);
        db.AddRange(items);
        await db.SaveChangesAsync();
        return items.Select(i => i.Id).ToList();
    }

    // Set aside afterwards, so the next test's group leads the inbox whatever order they run in.
    private async Task SetAside(List<Guid> ids)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        await db.FeedItems.Where(i => ids.Contains(i.Id) && i.Status == FeedItemStatus.New)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, FeedItemStatus.Duplicate));
    }

    private static string Line(string html, string cls)
    {
        var at = html.IndexOf("<li class=\"" + cls, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"a line with {cls} is shown");
        return html[at..html.IndexOf("</li>", at, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task AGroup_IsOneLine_WithTheNewestHeadline_AndOpensToItsReports()
    {
        const string key = "a";
        var ids = await Seed(key, 100);
        try
        {
            await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage");
            var html = await r.HtmlOnce("Looks like one event");

            var head = Line(html, "tide-triage-row tide-triage-group selected");
            head.Should().Contain("aria-label=\"Looks like one event: 3 reports from 2 feeds. Newest: Quorvexa links $6.3 billion theft to Zentraa laptop\"")
                .And.Contain("Looks like one event: 3 reports · 2 feeds")
                .And.Contain("Newest: <span class=\"tide-triage-title\">“Quorvexa links $6.3 billion theft to Zentraa laptop”</span>")
                .And.Contain("Clear feed a")
                .And.Contain("In common: Quorvexa, $6.3 billion")
                .And.Contain("CVE-2026-6001", "every CVE any report names is on the group's line")
                .And.Contain("aria-expanded=\"false\"").And.Contain("aria-label=\"Show the 3 reports\"")
                .And.Contain("Promote as one article (3 sources)").And.Contain("Not the same: split")
                .And.Contain("Dismiss the 3").And.Contain("Shortlist the 3", "X and S act on the whole group, once confirmed");
            html.Should().NotContain("tide-triage-member", "the reports wait until it's opened")
                .And.NotContain("Quorvexa hit by $6.3 billion hack");

            await r.Click(t => t.OnTriageKey("expand"));
            html = await r.Html();
            Line(html, "tide-triage-row tide-triage-group").Should().Contain("aria-expanded=\"true\"").And.Contain("Hide the 3 reports");
            var members = html.Split("<li class=\"tide-triage-row tide-triage-member").Skip(1).ToList();
            members.Should().HaveCount(3);
            members[0].Should().Contain("Clear feed a").And.Contain("Excerpt 0 of the a story.");
            members[1].Should().Contain("Amber feed a").And.Contain("Excerpt 1 of the a story.").And.Contain("TLP:AMBER");
            members[2].Should().Contain("Excerpt 2 of the a story.", "every report's source and excerpt is shown, not hidden in the group");

            // J moves onto the first report, which keeps all its own actions; ← goes back to the group and closes it.
            await r.Click(t => t.OnTriageKey("next"));
            html = await r.Html();
            var first = Line(html, "tide-triage-row tide-triage-member selected");
            first.Should().Contain("Promote <span class=\"tide-kbd\">P</span>").And.Contain("Dismiss <span class=\"tide-kbd\">X</span>")
                .And.Contain("Shortlist <span class=\"tide-kbd\">S</span>").And.Contain("Attach to");
            await r.Click(t => t.OnTriageKey("collapse"));
            html = await r.Html();
            html.Should().NotContain("tide-triage-member");
            Line(html, "tide-triage-row tide-triage-group selected").Should().Contain("aria-expanded=\"false\"");

            await r.Click(t => t.OnTriageKey("toggle"));
            (await r.Html()).Should().Contain("tide-triage-member", "Enter opens it too");
        }
        finally { await SetAside(ids); }
    }

    [Fact]
    public async Task NotTheSame_SplitsTheGroup_ForEveryVisit()
    {
        const string key = "b";
        var ids = await Seed(key, 200);
        try
        {
            await using (var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage"))
            {
                await r.HtmlOnce("Looks like one event");
                await r.Click(async t =>
                {
                    await t.Split(0);
                    await t.OnTriageKey("prev");
                });
                var html = await r.Html();
                html.Should().NotContain("tide-triage-group").And.NotContain("tide-triage-member");
                Line(html, "tide-triage-row selected").Should().Contain("Quorvexb links $6.3 billion theft", "the newest report is selected");
                html.Should().Contain("Quorvexb hit by $6.3 billion hack").And.Contain("Quorvexb probes $5.8 billion theft");
            }

            // It's stored (EP-84): the next visit shows the reports apart too.
            await using var again = await ComponentHarness.RenderAsync<Triage>(app, "/triage");
            (await again.HtmlOnce("Quorvexb links")).Should().NotContain("Newest: <span class=\"tide-triage-title\">“Quorvexb")
                .And.Contain("Quorvexb hit by $6.3 billion hack");
        }
        finally { await SetAside(ids); }
    }

    [Fact]
    public async Task PromoteAsOne_OpensTheFormOnTheGroup_AndMakesOneArticleOfEveryReport()
    {
        const string key = "c";
        var ids = await Seed(key, 300);
        try
        {
            await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage");
            await r.HtmlOnce("Looks like one event");

            await r.Click(t => t.OnTriageKey("promote"));
            var html = await r.Html();
            var form = html[html.IndexOf("<form class=\"tide-promote-form", StringComparison.Ordinal)..];
            form = form[..form.IndexOf("</form>", StringComparison.Ordinal)];
            form.Should().Contain("value=\"Quorvexc links $6.3 billion theft to Zentrac laptop\"", "the newest headline")
                .And.Contain("<option value=\"Amber\" selected").And.Contain("From the sources: TLP:AMBER, the most restrictive")
                .And.Contain("id=\"promote-priority\"", "a report names a CVE, so it's a threat")
                .And.Contain("Promote 3 as one and write");

            await r.Click(t => t.SubmitPromote());

            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            var rows = await db.FeedItems.Where(i => ids.Contains(i.Id)).ToListAsync();
            rows.Should().OnlyContain(i => i.Status == FeedItemStatus.Promoted && i.ArticleId == rows[0].ArticleId && i.ArticleId != null);
            var article = await db.Articles.SingleAsync(a => a.Id == rows[0].ArticleId);
            article.Tlp.Should().Be(TlpLevel.Amber);
            (await db.ArticleLinks.CountAsync(l => l.ArticleId == article.Id)).Should().Be(2);
            (await db.ArticleCves.Where(c => c.ArticleId == article.Id).Select(c => c.CveId).ToListAsync()).Should().Equal("CVE-2026-6001");
        }
        finally { await SetAside(ids); }
    }

    [Fact]
    public async Task TheAddress_OpensTheGroupAtItsReport()
    {
        const string key = "d";
        var ids = await Seed(key, 400);
        try
        {
            await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?at={ids[1]}");
            var html = await r.HtmlOnce("tide-triage-member");
            Line(html, "tide-triage-row tide-triage-member selected").Should().Contain("Quorvexd hit by $6.3 billion hack");
        }
        finally { await SetAside(ids); }
    }
}
