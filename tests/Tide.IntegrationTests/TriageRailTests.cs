using System.Net;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Adversaries;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Triage's context rail: what TIDE already knows about the selected item, beside the list and the reading pane. Its
/// CVEs' facts and the priority they suggest; the article that already covers it, with that article's open actions; the
/// other reports of the same event, with Attach all and Not the same; the story the covering article is in; and what
/// it names of Our stack and the watched adversaries, or that it names neither, with Add to what we run. Each section
/// is there only for an item it applies to, and its buttons take the page's own paths.
/// </summary>
public class TriageRailTests(TideApp app) : IClassFixture<TideApp>
{
    private const string RailStart = "<aside id=\"tide-rail\"";

    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    private async Task<T> Db<T>(Func<AppDbContext, Task<T>> what)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await what(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>A threat article (a draft, so a report can still be attached), P1, naming <paramref name="cve"/>, in a
    /// story when one is named; then an open action and a closed one raised from it. The article's id.</summary>
    private async Task<Guid> CoveringArticle(string key, string cve, string? story = null)
    {
        var article = await Db(async db =>
        {
            var a = new Article { Title = $"Covering {key} article", Bluf = "Earlier coverage.", Status = ArticleStatus.Draft, AuthorName = "Robin",
                Classification = Classification.Threat, Priority = Priority.P1 };
            a.Cves.Add(new ArticleCve { CveId = cve });
            if (story is not null)
            {
                var s = new Story { Name = story };
                db.Add(s);
                a.StoryId = s.Id;
            }
            db.Articles.Add(a);
            await db.SaveChangesAsync();
            return a.Id;
        });
        await As(async sp =>
        {
            var actions = sp.GetRequiredService<ActionService>();
            var team = await sp.GetRequiredService<TeamService>().AddAsync($"Rail team {key}", null, null);
            await actions.CreateAsync(new ActionForm { Title = $"Sweep for {key}", TeamId = team, Kind = ActionKind.IocSweep, ArticleId = article });
            var closed = await actions.CreateAsync(new ActionForm { Title = $"Closed {key} block", TeamId = team, Kind = ActionKind.Block, ArticleId = article });
            await actions.SetStatusAsync((await actions.GetAsync(closed))!.Action.Id, ActionStatus.Done, ActionOutcome.Blocked, null);
            return true;
        });
        return article;
    }

    private Task<bool> Cve(string id, bool kev, decimal cvss, decimal epss) => Db(async db =>
    {
        db.CveRecords.Add(new CveRecord { CveId = id, IsKev = kev, CvssScore = cvss, CvssSeverity = "CRITICAL", EpssScore = epss,
            Description = $"Sample: {id} lets anyone in." });
        await db.SaveChangesAsync();
        return true;
    });

    /// <summary>New items, each from a feed of its own, newest first as given; their ids.</summary>
    private Task<List<Guid>> Items(string key, params (string Title, string? Cves)[] items) => Db(async db =>
    {
        var now = DateTimeOffset.UtcNow;
        var rows = items.Select((x, n) => new FeedItem
        {
            Source = new Source { Name = $"Rail feed {key} {n}", Url = $"https://rail.example/{key}/feed{n}" },
            Title = x.Title, Url = $"https://rail.example/{key}/{n}", UrlHash = $"rail-{key}-{n}", Cves = x.Cves, Excerpt = $"Excerpt {n}.",
            PublishedAtUtc = now.AddMinutes(-n), FetchedAtUtc = now.AddMinutes(-n),
        }).ToList();
        db.AddRange(rows);
        await db.SaveChangesAsync();
        return rows.Select(r => r.Id).ToList();
    });

    private static string Key() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>The page's markup and its rail's, split where MainLayout draws the rail (after the content).</summary>
    private static (string Page, string Rail) Split(string html)
    {
        html = WebUtility.HtmlDecode(html);
        var at = html.IndexOf(RailStart, StringComparison.Ordinal);
        at.Should().BePositive("Triage has a context rail");
        return (html[..at], html[at..html.IndexOf("</aside>", at, StringComparison.Ordinal)]);
    }

    /// <summary>A section of the rail, by its heading's id; null when there's none.</summary>
    private static string? Section(string rail, string id)
    {
        var at = rail.IndexOf($"aria-labelledby=\"{id}\"", StringComparison.Ordinal);
        return at < 0 ? null : rail[at..rail.IndexOf("</section>", at, StringComparison.Ordinal)];
    }

    private static readonly string[] Sections = ["rail-cve-h", "rail-covered-h", "rail-same-h", "rail-story-h", "rail-named-h"];

    private static IEnumerable<string> Shown(string rail) => Sections.Where(s => Section(rail, s) is not null);

    [Fact]
    public async Task ACoveredKevItem_ShowsItsCveFacts_TheArticleThatCoversIt_ItsOpenActions_AndItsStory()
    {
        var key = Key();
        var cve = $"CVE-2026-{Random.Shared.Next(70000, 79999)}";
        await Cve(cve, kev: true, cvss: 9.8m, epss: .94m);
        await CoveringArticle(key, cve, story: $"Rail story {key}");
        await Items(key, ($"Gateway {key} exploited in the wild", cve));

        await using var r = await RenderAsync<Triage>(app, $"/triage?q={key}");
        var (page, rail) = Split(await r.HtmlOnce($"Gateway {key}"));

        rail.Should().Contain("aria-label=\"What TIDE knows\"");
        Shown(rail).Should().Equal("rail-cve-h", "rail-covered-h", "rail-story-h", "rail-named-h");
        Section(rail, "rail-cve-h").Should().Contain(cve).And.Contain("<dt>CVSS</dt>").And.Contain("9.8").And.Contain("<dt>EPSS</dt>").And.Contain("94%")
            .And.Contain("<dt>On KEV</dt>").And.Contain("Yes").And.Contain($"Sample: {cve} lets anyone in.").And.Contain("P1").And.Contain("on KEV");
        Section(rail, "rail-covered-h").Should().Contain($">Covering {key} article</a>").And.Contain("Threat").And.Contain("title=\"Priority P1\"")
            .And.Contain($"Sweep for {key}").And.NotContain($"Closed {key} block", "only the actions still open");
        Section(rail, "rail-story-h").Should().Contain($">Rail story {key}</a>").And.Contain("Active").And.Contain("1 article");
        Section(rail, "rail-named-h").Should().Contain("Not found").And.Contain("We run it").And.Contain("Watched adversary").And.Contain("Add to what we run…");
        page.Should().NotContain("rail-covered-h").And.NotContain("Add to what we run…", "it's in the rail now, not the decision bar");
    }

    [Fact]
    public async Task AnItemNothingCovers_HasOnlyWhatItNames_AndNoCveFactsWithoutACve()
    {
        var key = Key();
        await Items(key, ($"Phishing kit {key} adds QR codes", null));

        await using var r = await RenderAsync<Triage>(app, $"/triage?q={key}");
        var (_, rail) = Split(await r.HtmlOnce($"Phishing kit {key}"));

        Shown(rail).Should().Equal("rail-named-h");
        Section(rail, "rail-named-h").Should().Contain("Not found").And.Contain("Add to what we run…");
    }

    [Fact]
    public async Task ACveWithNoRecord_SaysSo_AndAnArticleThatCannotTakeAReport_StillShowsAsCovering()
    {
        var key = Key();
        var cve = $"CVE-2026-{Random.Shared.Next(80000, 89999)}";
        var article = await CoveringArticle(key, cve);
        await Db(async db => await db.Articles.Where(a => a.Id == article).ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, ArticleStatus.Ready)));
        await Items(key, ($"Unenriched {key} flaw", cve));

        await using var r = await RenderAsync<Triage>(app, $"/triage?q={key}");
        var (_, rail) = Split(await r.HtmlOnce($"Unenriched {key}"));

        Section(rail, "rail-cve-h").Should().Contain(cve).And.Contain("Not enriched yet");
        Section(rail, "rail-covered-h").Should().Contain($"Covering {key} article");
        Section(rail, "rail-story-h").Should().BeNull("the covering article is in no story");
    }

    [Fact]
    public async Task ASameEventGroup_ListsItsReports_AttachesThemAllInOneStep_AndOneThatIsNotTheSame_Splits()
    {
        var key = Key();
        var name = "Quorvex" + key;
        var cve = $"CVE-2026-{Random.Shared.Next(60000, 69999)}";
        var article = await CoveringArticle(key, cve);
        var ids = await Items(key, ($"{name} links $6.3 billion theft to Zentra{key} laptop", null), ($"{name} hit by $6.3 billion hack", cve));

        await using var r = await RenderAsync<Triage>(app, $"/triage?q={name}");
        var (_, rail) = Split(await r.HtmlOnce("Looks like one event"));
        var same = Section(rail, "rail-same-h");
        same.Should().NotBeNull("the group's line is selected");
        same.Should().Contain("2 reports").And.Contain($"{name} links $6.3 billion theft").And.Contain($"{name} hit by $6.3 billion hack")
            .And.Contain(">Attach both<").And.Contain("Not the same");

        // From a report under the open group: the other one is listed.
        await r.Click(t => { t.Toggle(0); return Task.CompletedTask; });
        await r.Drawn();
        await r.Click(t => t.OnTriageKey("next"));
        await r.Drawn();
        (_, rail) = Split(await r.Html());
        Section(rail, "rail-same-h").Should().Contain("1 other report").And.Contain($"{name} hit by $6.3 billion hack").And.Contain(">Attach both<");

        // Attach both: the group's own all-or-none path, both on the article.
        await r.Click(t => t.AttachSameEvent());
        await r.Drawn();
        var stored = await Db(db => db.FeedItems.AsNoTracking().Where(i => ids.Contains(i.Id)).Select(i => i.ArticleId).ToListAsync());
        stored.Should().AllSatisfy(a => a.Should().Be(article));
    }

    [Fact]
    public async Task NotTheSame_FromTheRail_SplitsTheGroup_ForEveryone()
    {
        var key = Key();
        var name = "Brelmont" + key;
        var ids = await Items(key, ($"{name} confirms $4.1 million theft by Varn{key} crew", null), ($"{name} $4.1 million heist traced to Varn{key} keys", null));

        await using var r = await RenderAsync<Triage>(app, $"/triage?q={name}");
        var (_, rail) = Split(await r.HtmlOnce("Looks like one event"));
        Section(rail, "rail-same-h").Should().Contain("Not the same").And.NotContain("Attach", "nothing covers them");

        await r.Click(t => t.SplitSameEvent());
        await r.Drawn();
        (await Db(db => db.TriageSplits.CountAsync(s => ids.Contains(s.ItemId)))).Should().BePositive();
        var (_, after) = Split(await r.Html());
        Section(after, "rail-same-h").Should().BeNull("they're rows of their own now");
    }

    [Fact]
    public async Task WhatWeRunAndAWatchedAdversary_ShowAsFound_AndAddToWhatWeRun_AddsTheProduct()
    {
        var key = Key();
        var product = "Zorbix" + key;
        var actor = "Ember" + key;
        await As(async sp =>
        {
            await sp.GetRequiredService<StackService>().AddByNameAsync(product);
            await sp.GetRequiredService<AdversaryService>().SaveAsync(new AdversaryForm { Name = actor, Watched = true });
            return true;
        });
        await Items(key, ($"{actor} exploits {product} appliances", null), ($"Unrelated {key} webinar", null));

        await using var r = await RenderAsync<Triage>(app, $"/triage?q={key}");
        var (_, rail) = Split(await r.HtmlOnce(product));
        var named = Section(rail, "rail-named-h");
        named.Should().Contain($"We run: {product}").And.Contain($"Watching: {actor}").And.NotContain("Not found").And.NotContain("Add to what we run…");

        await r.Click(t => t.OnTriageKey("next"));
        await r.Drawn();
        (_, rail) = Split(await r.Html());
        Section(rail, "rail-named-h").Should().Contain("Not found").And.Contain("Add to what we run…");

        // The rail's add takes the page's path: Our stack has the product, and the list is read again.
        var added = "Qwexa" + key;
        await r.Click(t => t.AddToStack(added));
        await r.Drawn();
        (await Db(db => db.StackProducts.AnyAsync(p => p.Name == added))).Should().BeTrue();
    }

    [Fact]
    public async Task NothingSelected_SaysSo_AndTheSourcesTab_HasNoRail()
    {
        var key = Key();
        await using var r = await RenderAsync<Triage>(app, $"/triage?q=nothing-matches-{key}");
        var (_, rail) = Split(await r.HtmlOnce("tide-rail"));
        Shown(rail).Should().BeEmpty();
        rail.Should().Contain("Choose an item");

        await using var sources = await RenderAsync<Triage>(app, "/triage?tab=sources");
        (await sources.Html()).Should().NotContain(RailStart, "the Sources tab lists feeds, not items");
    }

    [Fact]
    public async Task OnlyWhoeverMayTriage_GetsTheRail_AndAnAnalystWithoutAdmin_SeesItAll()
    {
        var key = Key();
        var cve = $"CVE-2026-{Random.Shared.Next(50000, 59999)}";
        await Cve(cve, kev: false, cvss: 7.5m, epss: .2m);
        await CoveringArticle(key, cve, story: $"Analyst story {key}");
        await Items(key, ($"Analyst-only {key} item", cve));

        var analyst = new SignIn("rail:" + key, "Rail analyst", "Analyst");
        await using var r = await RenderAsync<Triage>(app, $"/triage?q={key}", @as: analyst);
        var (_, rail) = Split(await r.HtmlOnce($"Analyst-only {key}"));
        Shown(rail).Should().Equal("rail-cve-h", "rail-covered-h", "rail-story-h", "rail-named-h");
        Section(rail, "rail-cve-h").Should().Contain("P2", "an EPSS of 20% suggests P2");

        // A viewer and an action owner can't open Triage, so they get no rail: the page refuses them.
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        foreach (var role in new[] { "Viewer", "ActionOwner" })
        {
            var response = await client.GetAsync($"/triage?q={key}&roles={role}");
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain(RailStart, role).And.NotContain($"Analyst-only {key}", role);
        }
        await client.GetAsync("/triage?roles=default");
    }
}
