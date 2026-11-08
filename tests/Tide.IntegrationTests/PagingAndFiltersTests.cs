using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Intake;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>
/// EP-85 in the services: Triage and the actions list page in the database (a count, then one page of a stable order),
/// the board's closed columns reach a week (or a month when asked), and the new filters: the actions' words, priority,
/// due date and status, and Triage's CVEs, KEV and what we run.
/// </summary>
public class PagingAndFiltersServiceTests : IDisposable
{
    private TestDb _db = new();

    public PagingAndFiltersServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<Guid> Team() => await _db.Teams().AddAsync("Cyber Defense", null, null);

    // Actions written straight in, numbered from 1, so a test can make a hundred of them quickly.
    private async Task Actions(Guid team, IEnumerable<Action<ActionItem>> each)
    {
        await using var ctx = _db.Context();
        var n = await ctx.Actions.MaxAsync(a => (int?)a.Number) ?? 0;
        foreach (var set in each)
        {
            var a = new ActionItem { Number = ++n, Title = $"Action {n}", TeamId = team };
            set(a);
            ctx.Actions.Add(a);
        }
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task TheActionsList_PagesInTheDatabase_CountingEveryMatch_InAStableOrder()
    {
        var team = await Team();
        var today = _db.Clock.Today;
        await Actions(team, Enumerable.Range(0, 120).Select(i => (Action<ActionItem>)(a => a.DueDate = today.AddDays(i % 10))));
        var svc = _db.Actions();

        _db.Commands.Record = true;
        _db.Commands.Reset();
        var first = await svc.PageAsync(new ActionFilter(ActionView.Open));
        _db.Commands.Sql.Should().Contain(sql => sql.Contains("LIMIT", StringComparison.Ordinal) && sql.Contains("OFFSET", StringComparison.Ordinal),
            "the page is cut in the database, not after reading every action");
        first.Total.Should().Be(120);
        first.Rows.Should().HaveCount(ActionService.ListPageSize);
        first.Rows.Select(r => r.DueDate).Should().BeInAscendingOrder("soonest due first");

        var second = await svc.PageAsync(new ActionFilter(ActionView.Open), page: 2);
        var third = await svc.PageAsync(new ActionFilter(ActionView.Open), page: 3);
        third.Rows.Should().HaveCount(20);
        first.Rows.Concat(second.Rows).Concat(third.Rows).Select(r => r.Id).Should().OnlyHaveUniqueItems().And.HaveCount(120, "each action is on one page");

        var past = await svc.PageAsync(new ActionFilter(ActionView.Open), page: 9);
        past.Page.Should().Be(3, "a page past the end is the last");
        past.Rows.Select(r => r.Id).Should().Equal(third.Rows.Select(r => r.Id));

        var byNumber = await svc.PageAsync(new ActionFilter(ActionView.Open), ActionSort.Number, descending: true);
        byNumber.Rows[0].Number.Should().Be(120);
    }

    [Fact]
    public async Task TheActionsList_GroupedByKind_KeepsEachKindTogetherAcrossPages_AndCountsEveryOne()
    {
        var team = await Team();
        await Actions(team, Enumerable.Range(0, 70).Select(i => (Action<ActionItem>)(a => a.Kind = i % 2 == 0 ? ActionKind.Block : ActionKind.IocSweep)));
        var svc = _db.Actions();

        var first = await svc.PageAsync(new ActionFilter(ActionView.Open), group: ActionGrouping.Kind);
        first.Groups.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [ActionService.KindName(ActionKind.IocSweep)] = 35, [ActionService.KindName(ActionKind.Block)] = 35,
        });
        first.Rows.Take(35).Should().OnlyContain(r => r.Kind == ActionKind.IocSweep, "a kind's rows come together, in the kinds' order");
        first.Rows.Skip(35).Should().OnlyContain(r => r.Kind == ActionKind.Block);
        (await svc.PageAsync(new ActionFilter(ActionView.Open), group: ActionGrouping.Kind, page: 2)).Rows.Should().HaveCount(20).And.OnlyContain(r => r.Kind == ActionKind.Block);
    }

    [Fact]
    public async Task TheActionsSearch_FindsTheTitle_TheNumber_AndTheArticlesTitle()
    {
        var team = await Team();
        var article = await _db.Articles().CreateAsync("Zentra breach write-up", "Zentra", null, TlpLevel.Clear);
        var svc = _db.Actions();
        var fromArticle = await svc.CreateAsync(new ActionForm { Title = "Block the hashes", TeamId = team, ArticleId = article });
        await svc.CreateAsync(new ActionForm { Title = "Rotate VPN 50% of keys", TeamId = team });
        await svc.CreateAsync(new ActionForm { Title = "Something else", TeamId = team });
        var number = fromArticle;

        async Task<IEnumerable<string>> Find(string words) =>
            (await svc.PageAsync(new ActionFilter(ActionView.Open) { Search = words })).Rows.Select(r => r.Title);

        (await Find("hashes")).Should().Equal("Block the hashes");
        (await Find("zentra BREACH")).Should().Equal(["Block the hashes"], "the article's title counts, in any case");
        (await Find($"A-{number}")).Should().Equal("Block the hashes");
        (await Find($"{number}")).Should().Equal("Block the hashes");
        (await Find("50%")).Should().Equal(["Rotate VPN 50% of keys"], "a percent sign is a character, not a wildcard");
        (await Find("nothing like it")).Should().BeEmpty();
    }

    [Fact]
    public async Task ThePriorityStatusAndDueFilters_NarrowTheListAndTheBoard_AndCombine()
    {
        var team = await Team();
        var svc = _db.Actions();
        var today = svc.Today;
        var weekEnd = ActionSla.EndOfBusinessWeek(today, svc.Days);
        await Actions(team,
        [
            a => (a.Title, a.Priority, a.DueDate) = ("late P1", Priority.P1, today.AddDays(-2)),
            a => (a.Title, a.Priority, a.DueDate, a.Status) = ("today P2", Priority.P2, today, ActionStatus.InProgress),
            a => (a.Title, a.Priority, a.DueDate, a.Status) = ("week end P3", Priority.P3, weekEnd, ActionStatus.Blocked),
            a => (a.Title, a.Priority) = ("undated P4", Priority.P4),
            a => (a.Title, a.Priority, a.DueDate) = ("next month P2", Priority.P2, weekEnd.AddDays(30)),
            a => (a.Title, a.Priority, a.DueDate, a.Status, a.ClosedAtUtc, a.ClosedDate) = ("closed late", Priority.P1, today.AddDays(-5), ActionStatus.Done, DateTimeOffset.UtcNow.AddDays(-1), today.AddDays(-1)),
        ]);

        async Task<IEnumerable<string>> List(ActionFilter f) => (await svc.PageAsync(f)).Rows.Select(r => r.Title);
        async Task<IEnumerable<string>> Board(ActionFilter f) => (await svc.BoardAsync(f)).Cards.Select(r => r.Title);
        var open = new ActionFilter(ActionView.Open);

        (await List(open with { Priorities = [Priority.P2] })).Should().BeEquivalentTo("today P2", "next month P2");
        (await List(open with { Status = ActionStatus.Blocked })).Should().Equal("week end P3");
        (await List(open with { Due = ActionDue.Overdue })).Should().Equal("late P1");
        (await List(open with { Due = ActionDue.ThisWeek })).Should().BeEquivalentTo(["today P2", "week end P3"], "today to the business week's last working day");
        (await List(open with { Due = ActionDue.None })).Should().Equal("undated P4");
        (await List(open with { Priorities = [Priority.P2], Due = ActionDue.ThisWeek })).Should().Equal("today P2");
        (await List(open with { Priorities = [Priority.P2], Status = ActionStatus.Blocked })).Should().BeEmpty();

        (await Board(open with { Priorities = [Priority.P1] })).Should().BeEquivalentTo(["late P1", "closed late"], "the board's Done column is filtered too");
        (await Board(open with { Due = ActionDue.Overdue })).Should().Equal(["late P1"], "a closed action is never overdue");
        (await Board(open with { Search = "week end" })).Should().Equal("week end P3");
    }

    [Fact]
    public async Task ARuleChosenAsAFilter_NarrowsTheListInTheDatabase_AndEachRuleIsCounted()
    {
        var team = await Team();
        var today = _db.Clock.Today;
        await Actions(team,
        [
            a => (a.Priority, a.DueDate) = (Priority.P1, today.AddDays(-1)),
            a => (a.Priority, a.DueDate) = (Priority.P1, today.AddDays(20)),
            a => (a.Priority, a.AssigneeId) = (Priority.P3, null),
            a => (a.Priority, a.AssigneeId, a.AssigneeName) = (Priority.P3, "someone", "Someone"),
        ]);
        var urgent = new ActionCardRule { Name = "Urgent", PriorityAtLeast = Priority.P1 };
        var late = new ActionCardRule { Name = "Late", Sla = CardRuleSla.Overdue };
        var nobodys = new ActionCardRule { Name = "Nobody's", Unassigned = true, PriorityAtLeast = Priority.P3 };
        await using (var ctx = _db.Context())
        {
            ctx.ActionCardRules.AddRange(urgent, late, nobodys);
            await ctx.SaveChangesAsync();
        }
        var svc = _db.Actions();
        var open = new ActionFilter(ActionView.Open);

        (await svc.PageAsync(open with { RuleId = urgent.Id })).Total.Should().Be(2);
        (await svc.PageAsync(open with { RuleId = late.Id })).Total.Should().Be(1);
        (await svc.PageAsync(open with { RuleId = nobodys.Id })).Total.Should().Be(3, "the P1s are higher than P3, and nobody holds them either");
        var counts = await svc.RuleCountsAsync(open with { RuleId = urgent.Id }, [urgent, late, nobodys]);
        counts.Should().BeEquivalentTo(new Dictionary<Guid, int> { [urgent.Id] = 2, [late.Id] = 1, [nobodys.Id] = 3 }, "each chip counts its own, whichever is chosen");

        // Each matches what the board's own matching says.
        var cards = (await svc.BoardAsync(open)).Cards;
        foreach (var rule in new[] { urgent, late, nobodys })
            cards.Count(c => ActionRules.Matches(rule, c, svc.Today, svc.Days)).Should().Be(counts[rule.Id]);
    }

    [Fact]
    public async Task TheBoardsClosedColumns_ReachAWeek_OrAMonthWhenAsked()
    {
        var team = await Team();
        var now = DateTimeOffset.UtcNow;
        Action<ActionItem> Closed(string title, int daysAgo, ActionStatus status = ActionStatus.Done) => a =>
            (a.Title, a.Status, a.ClosedAtUtc, a.ClosedDate) = (title, status, now.AddDays(-daysAgo), DateOnly.FromDateTime(now.AddDays(-daysAgo).UtcDateTime));
        await Actions(team, [Closed("3 days", 3), Closed("cancelled 5 days", 5, ActionStatus.Cancelled), Closed("10 days", 10), Closed("20 days", 20), Closed("40 days", 40), a => a.Title = "open"]);
        var svc = _db.Actions();

        var week = await svc.BoardAsync(new ActionFilter());
        week.ClosedDays.Should().Be(7);
        week.Cards.Select(c => c.Title).Should().BeEquivalentTo("open", "3 days", "cancelled 5 days");

        var month = await svc.BoardAsync(new ActionFilter(), ActionService.BoardOlderDays);
        month.ClosedDays.Should().Be(30);
        month.Cards.Select(c => c.Title).Should().BeEquivalentTo("open", "3 days", "cancelled 5 days", "10 days", "20 days");
    }

    // --- Triage ----------------------------------------------------------------------------------------------------------

    private async Task<Guid> Source(string name = "News") =>
        await _db.Intake().SaveSourceAsync(new SourceForm { Name = name, Url = $"https://{name.ToLowerInvariant()}.example.com/feed.xml", PollMinutes = 30 });

    // Items in a state, newest first by n (n = 0 the newest), with titles that share no words, so none group.
    private async Task<List<Guid>> Items(Guid source, int count, FeedItemStatus status, int daysAgo = 0, Func<int, string>? cves = null, Func<int, string>? title = null)
    {
        var now = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        await using var ctx = _db.Context();
        var items = Enumerable.Range(0, count).Select(n => new FeedItem
        {
            SourceId = source, Status = status, Title = title?.Invoke(n) ?? Guid.NewGuid().ToString("N"), Url = $"https://x.example/{Guid.NewGuid():N}", UrlHash = Guid.NewGuid().ToString("N"),
            Cves = cves?.Invoke(n), PublishedAtUtc = now.AddMinutes(-n), FetchedAtUtc = now.AddMinutes(-n),
        }).ToList();
        ctx.FeedItems.AddRange(items);
        await ctx.SaveChangesAsync();
        return items.Select(i => i.Id).ToList();
    }

    [Fact]
    public async Task TheInbox_PagesInTheDatabase_SayingHowManyInAll_NeverCutSilently()
    {
        var source = await Source();
        var ids = await Items(source, 230, FeedItemStatus.Dismissed);
        var intake = _db.Intake();

        _db.Commands.Record = true;
        _db.Commands.Reset();
        var first = await intake.InboxPageAsync(new InboxFilter(FeedItemStatus.Dismissed));
        _db.Commands.Sql.Should().Contain(sql => sql.Contains("LIMIT", StringComparison.Ordinal) && sql.Contains("OFFSET", StringComparison.Ordinal));
        first.Total.Should().Be(230, "every item counts: there's no cap at 200 any more");
        first.Rows.Select(r => r.Item.Id).Should().Equal(ids.Take(50), "newest first");
        (await intake.InboxPageAsync(new InboxFilter(FeedItemStatus.Dismissed, Page: 2))).Rows.Select(r => r.Item.Id).Should().Equal(ids.Skip(50).Take(50));
        var last = await intake.InboxPageAsync(new InboxFilter(FeedItemStatus.Dismissed, Page: 5));
        last.Rows.Select(r => r.Item.Id).Should().Equal(ids.Skip(200));
        var past = await intake.InboxPageAsync(new InboxFilter(FeedItemStatus.Dismissed, Page: 40));
        past.Page.Should().Be(5, "a page past the end is the last");
        (await intake.CountsAsync())[FeedItemStatus.Dismissed].Should().Be(230, "the tab counts stay totals");
    }

    [Fact]
    public async Task NewAndOlder_PageToo_TheirCountsStayTotals()
    {
        var source = await Source();
        await Items(source, 60, FeedItemStatus.New);
        await Items(source, 55, FeedItemStatus.New, daysAgo: 20);
        var intake = _db.Intake(new IntakeOptions { Enabled = true, MinPollMinutes = 5, AgeDays = 7 });

        var inNew = await intake.InboxPageAsync(new InboxFilter(Page: 2));
        (inNew.Total, inNew.Rows.Count).Should().Be((60, 10));
        var older = await intake.InboxPageAsync(new InboxFilter(Older: true, Page: 2));
        (older.Total, older.Rows.Count).Should().Be((55, 5));
        var (counts, olderCount) = await intake.TallyAsync();
        (counts[FeedItemStatus.New], olderCount).Should().Be((60, 55));
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheCveKevAndWhatWeRunFilters_KeepOnlyTheirItems_AndCombine(string provider)
    {
        _db = _db.On(provider);
        var db = _db;
        {
            db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
            await db.Stack().SaveAsync(new StackForm { Name = "GitLab" });
            var source = await db.Intake().SaveSourceAsync(new SourceForm { Name = "News", Url = "https://news.example.com/feed.xml", PollMinutes = 30 });
            var now = DateTimeOffset.UtcNow;
            await using (var ctx = db.Context())
            {
                FeedItem Item(string t, int n, string? cves, FeedItemStatus status = FeedItemStatus.New) => new()
                {
                    SourceId = source, Title = t, Url = "https://x.example/" + n, UrlHash = "h" + n, Cves = cves, Status = status,
                    PublishedAtUtc = now.AddMinutes(-n), FetchedAtUtc = now.AddMinutes(-n),
                };
                ctx.FeedItems.AddRange(Item("kev story", 1, "CVE-2026-7001"), Item("plain cve story", 2, "CVE-2026-7002"), Item("no cve story", 3, null),
                    Item("GitLab runner fix", 4, null), Item("GitLab exploited", 5, "CVE-2026-7001"),
                    Item("dismissed kev", 6, "CVE-2026-7001", FeedItemStatus.Dismissed), Item("dismissed plain", 7, null, FeedItemStatus.Dismissed));
                ctx.CveRecords.AddRange(new CveRecord { CveId = "CVE-2026-7001", IsKev = true }, new CveRecord { CveId = "CVE-2026-7002", EpssScore = 0.01m });
                await ctx.SaveChangesAsync();
            }
            var intake = db.Intake();
            async Task<IEnumerable<string>> Titles(InboxFilter f) => (await intake.InboxPageAsync(f)).Rows.Select(r => r.Item.Title);

            (await Titles(new InboxFilter(Cves: InboxCves.Any))).Should().BeEquivalentTo("kev story", "plain cve story", "GitLab exploited");
            (await Titles(new InboxFilter(Cves: InboxCves.Kev))).Should().BeEquivalentTo("kev story", "GitLab exploited");
            (await Titles(new InboxFilter(Named: true))).Should().BeEquivalentTo("GitLab runner fix", "GitLab exploited");
            (await Titles(new InboxFilter(Named: true, Cves: InboxCves.Kev))).Should().Equal("GitLab exploited");
            (await Titles(new InboxFilter(Cves: InboxCves.Kev, Search: "kev"))).Should().Equal("kev story");
            (await intake.InboxPageAsync(new InboxFilter(Cves: InboxCves.Kev))).Total.Should().Be(2);

            // Elsewhere than New too, where the database pages.
            (await Titles(new InboxFilter(FeedItemStatus.Dismissed, Cves: InboxCves.Any))).Should().Equal("dismissed kev");
            (await Titles(new InboxFilter(FeedItemStatus.Dismissed, Cves: InboxCves.Kev))).Should().Equal("dismissed kev");
            (await Titles(new InboxFilter(FeedItemStatus.Dismissed, Named: true))).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Groups_AreWorkedOutOnThePageShown()
    {
        var source = await Source("Clearfeed");
        var other = await Source("Amberfeed");
        // Three reports of one event, older than the 50 unrelated items in front of them: they're on page 2, together.
        await Items(source, 50, FeedItemStatus.Shortlisted);
        var at = DateTimeOffset.UtcNow.AddHours(-3);
        await using (var ctx = _db.Context())
        {
            FeedItem Item(Guid s, string title, int n, string? cves) => new()
            {
                SourceId = s, Status = FeedItemStatus.Shortlisted, Title = title, Url = $"https://group.example/{n}", UrlHash = $"group-{n}", Cves = cves,
                PublishedAtUtc = at.AddMinutes(-n), FetchedAtUtc = at.AddMinutes(-n),
            };
            ctx.FeedItems.AddRange(Item(source, "Quorvexp links $6.3 billion theft to Zentrap laptop", 0, null), Item(other, "Quorvexp hit by $6.3 billion hack", 1, "CVE-2026-6001"),
                Item(source, "Quorvexp probes $5.8 billion theft", 2, null));
            await ctx.SaveChangesAsync();
        }
        var intake = _db.Intake();

        (await intake.InboxPageAsync(new InboxFilter(FeedItemStatus.Shortlisted))).Rows.Should().OnlyContain(r => r.Group == null);
        var second = await intake.InboxPageAsync(new InboxFilter(FeedItemStatus.Shortlisted, Page: 2));
        second.Rows.Should().HaveCount(3).And.OnlyContain(r => r.Group != null && r.Group.Ids.Count == 3, "the group is found on its page");
    }
}

/// <summary>EP-85 on the pages: the shared pager, Triage's pages and new filters in the address, J and K at a page's ends,
/// the board's capped columns, and the actions list paged and filtered from its address.</summary>
public partial class PagingAndFiltersPageTests(TideApp app) : IClassFixture<TideApp>
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task ThePager_IsANavOfLinks_SayingWhichPageThisIs()
    {
        await using var r = await ComponentHarness.RenderAsync<Pager>(app, "/triage", new()
        {
            ["Page"] = 6, ["Pages"] = 12, ["Many"] = "items", ["Href"] = (Func<int, string>)(p => $"triage?tab=new&page={p}"),
        });
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("<nav class=\"tide-pager\" aria-label=\"Pages of items\">");
        html.Should().Contain("<a class=\"btn btn-primary btn-sm\" href=\"triage?tab=new&page=6\" aria-current=\"page\" aria-label=\"Page 6, this page\">6</a>");
        html.Should().Contain("href=\"triage?tab=new&page=5\" aria-label=\"Page 5\"").And.Contain("href=\"triage?tab=new&page=12\" aria-label=\"Page 12\"");
        html.Should().Contain("href=\"triage?tab=new&page=5\" rel=\"prev\"").And.Contain("href=\"triage?tab=new&page=7\" rel=\"next\"");
        Regex.Count(html, "tide-pager-gap").Should().Be(2, "1 … 5 6 7 … 12");
        Regex.Count(html, "aria-current=").Should().Be(1);
        html.Should().NotContain("<button", "links, so a middle-click opens a page in a new tab");

        await using var first = await ComponentHarness.RenderAsync<Pager>(app, "/triage", new()
        {
            ["Page"] = 1, ["Pages"] = 3, ["Many"] = "items", ["Href"] = (Func<int, string>)(p => $"triage?page={p}"),
        });
        var one = WebUtility.HtmlDecode(await first.Html());
        one.Should().Contain("<a class=\"btn btn-outline-secondary btn-sm disabled\" role=\"link\" aria-disabled=\"true\">", "Previous goes nowhere on the first page");
        one.Should().NotContain("tide-pager-gap");

        await using var single = await ComponentHarness.RenderAsync<Pager>(app, "/triage", new()
        {
            ["Page"] = 1, ["Pages"] = 1, ["Many"] = "items", ["Href"] = (Func<int, string>)(p => $"triage?page={p}"),
        });
        (await single.Html()).Should().NotContain("tide-pager", "one page needs no pager");
    }

    // 60 dismissed items from a source of the test's own, newest first by n, so the source filter isolates them.
    private async Task<(Guid Source, List<Guid> Ids)> Seed(string key, int count = 60, Func<int, string?>? cves = null, Func<int, string>? title = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = new Source { Name = "Paging feed " + key, Url = $"https://paging.example/{key}/feed" };
        var now = DateTimeOffset.UtcNow;
        var items = Enumerable.Range(0, count).Select(n => new FeedItem
        {
            Source = source, Status = FeedItemStatus.Dismissed, Title = title?.Invoke(n) ?? $"Paging {key} {Guid.NewGuid():N}", Url = $"https://paging.example/{key}/{n}",
            UrlHash = $"paging-{key}-{n}", Cves = cves?.Invoke(n), PublishedAtUtc = now.AddMinutes(-n), FetchedAtUtc = now.AddMinutes(-n),
        }).ToList();
        db.Add(source);
        db.AddRange(items);
        await db.SaveChangesAsync();
        return (source.Id, items.Select(i => i.Id).ToList());
    }

    private static int Rows(string html) => Regex.Count(html, "<li class=\"tide-triage-row");

    [Fact]
    public async Task Triage_ReadsItsPageFromTheAddress_AndItsLinksKeepTheFilters()
    {
        var (source, _) = await Seed("a");
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?tab=dismissed&source={source}&page=2");
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("51–60 of 60 items"));
        html.Should().Contain("51–60 of 60 items");
        Rows(html).Should().Be(10);
        html.Should().Contain($"<a class=\"btn btn-outline-secondary btn-sm\" href=\"triage?tab=dismissed&source={source}\" aria-label=\"Page 1\">1</a>",
            "the first page's address has no page in it");
        html.Should().Contain($"href=\"triage?tab=dismissed&source={source}&page=2\" aria-current=\"page\"");
        html.Should().NotContain("Showing the newest", "there's no cap to apologise for");

        // A filter change starts again from the first page.
        await r.Click(t => t.Refilter());
        ((int)typeof(Triage).GetField("_page", Private)!.GetValue(r.Component)!).Should().Be(1);
        ((IReadOnlyList<InboxRow>)typeof(Triage).GetField("_rows", Private)!.GetValue(r.Component)!).Should().HaveCount(50);
    }

    [Fact]
    public async Task Triage_J_OnThePagesLastRow_GoesOnToTheNextPage_AndK_ComesBack()
    {
        var (source, ids) = await Seed("b");
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?tab=dismissed&source={source}");
        await r.HtmlOnce("1–50 of 60 items");
        var selected = typeof(Triage).GetField("_selected", Private)!;
        for (var i = 0; i < 49; i++) await r.Click(t => t.OnTriageKey("next"));
        ((int)selected.GetValue(r.Component)!).Should().Be(49);

        await r.Click(t => t.OnTriageKey("next"));
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("51–60 of 60 items").And.Contain("Page 2 of 2.", "the status line says where J went");
        ((int)selected.GetValue(r.Component)!).Should().Be(0);
        Selected(html).Should().Contain(Title(r, ids[50]));

        await r.Click(t => t.OnTriageKey("next"));   // the second page's second row: within the page as before
        ((int)selected.GetValue(r.Component)!).Should().Be(1);
        await r.Click(t => t.OnTriageKey("prev"));
        await r.Click(t => t.OnTriageKey("prev"));
        html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("1–50 of 60 items");
        ((int)selected.GetValue(r.Component)!).Should().Be(49, "K on the first row goes back to the previous page's last");
        Selected(html).Should().Contain(Title(r, ids[49]));

        for (var i = 0; i < 20; i++) await r.Click(t => t.OnTriageKey("next"));
        await r.Click(t => t.OnTriageKey("next"));
        ((int)selected.GetValue(r.Component)!).Should().Be(9, "on the last page J stops at its last row");
    }

    private static string Title(ComponentHarness.Rendered<Triage> r, Guid id)
    {
        using var scope = r.Scope.ServiceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().FeedItems.Single(i => i.Id == id).Title;
    }

    private static string Selected(string html)
    {
        var at = html.IndexOf("aria-current=\"true\"", StringComparison.Ordinal);
        at.Should().BePositive("a row is selected");
        return html[at..html.IndexOf("</li>", at, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task Triage_TheKevAndWhatWeRunFilters_AreInTheAddress_AndCombine()
    {
        await using (var setup = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            await setup.ServiceProvider.GetRequiredService<StackService>().SaveAsync(new StackForm { Name = "Zorbulon" });
            var db = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.CveRecords.AnyAsync(c => c.CveId == "CVE-2099-8501"))
            {
                db.CveRecords.AddRange(new CveRecord { CveId = "CVE-2099-8501", IsKev = true }, new CveRecord { CveId = "CVE-2099-8502" });
                await db.SaveChangesAsync();
            }
        }
        string[] titles = ["Kev story c", "Plain cve story c", "No cve story c", "Zorbulon flaw c", "Zorbulon exploited c"];
        string?[] cves = ["CVE-2099-8501", "CVE-2099-8502", null, null, "CVE-2099-8501"];
        var (source, _) = await Seed("c", 5, n => cves[n], n => titles[n]);
        async Task<string> Page(string query)
        {
            await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?tab=dismissed&source={source}{query}");
            return WebUtility.HtmlDecode(await r.HtmlOnce("items"));
        }

        var kev = await Page("&cve=kev");
        kev.Should().Contain("Kev story c").And.Contain("Zorbulon exploited c").And.NotContain("Plain cve story c").And.Contain("2 items");
        FilterBarHtml.Pressed(kev, "KEV").Should().BeTrue("the pill shows the filter the address holds");
        FilterBarHtml.Pressed(kev, "Names a CVE").Should().BeFalse("KEV and Names a CVE are one filter, so they take turns");
        var any = await Page("&cve=any");
        any.Should().Contain("Plain cve story c").And.NotContain("No cve story c").And.Contain("3 items");
        var named = await Page("&named=true");
        named.Should().Contain("Zorbulon flaw c").And.Contain("Zorbulon exploited c").And.NotContain("Kev story c");
        FilterBarHtml.Pressed(named, "We run it").Should().BeTrue();
        var both = await Page("&cve=kev&named=true");
        both.Should().Contain("Zorbulon exploited c").And.NotContain("Zorbulon flaw c").And.Contain("1 item");
        Triage.Address(FeedItemStatus.New, null, null, false, null, 0, page: 3, cves: InboxCves.Kev, named: true, older: true)
            .Should().Be("triage?tab=new&cve=kev&named=true&older=true&page=3");
    }

    // A board of 45 open cards, 25 in progress and 3 done, none of them this person's to move.
    private static ActionBoard Board(int closedDays = ActionService.BoardClosedDays)
    {
        var n = 0;
        ActionRow Card(ActionStatus status) => new(Guid.NewGuid(), ++n, $"Card {n}", ActionKind.Other, Priority.P3, Guid.Empty, "SOC", null, null,
            null, status, ActionOutcome.None, status == ActionStatus.Done ? DateOnly.FromDateTime(DateTime.UtcNow) : null, null, null, null, 0, DateTimeOffset.UtcNow);
        var cards = Enumerable.Range(0, 45).Select(_ => Card(ActionStatus.Open))
            .Concat(Enumerable.Range(0, 25).Select(_ => Card(ActionStatus.InProgress)))
            .Concat(Enumerable.Range(0, 3).Select(_ => Card(ActionStatus.Done))).ToList();
        return new ActionBoard(cards, false, new HashSet<Guid>()) { ClosedDays = closedDays };
    }

    private static string Column(string html, ActionStatus status)
    {
        var at = html.IndexOf($"data-status=\"{status}\" aria-labelledby", StringComparison.Ordinal);
        at.Should().BePositive();
        var end = html.IndexOf("<section", at, StringComparison.Ordinal);
        return end < 0 ? html[at..] : html[at..end];
    }

    private static int Cards(string column) => Regex.Count(column, "<article ");

    [Fact]
    public async Task TheBoard_ShowsEachColumnsFirst20_ThenTwentyMoreAtATime()
    {
        await using var r = await ComponentHarness.RenderAsync<ActionKanban>(app, "/actions?layout=board", new()
        {
            ["Board"] = Board(), ["Today"] = DateOnly.FromDateTime(DateTime.UtcNow),
        });
        var html = WebUtility.HtmlDecode(await r.Html());
        var open = Column(html, ActionStatus.Open);
        Cards(open).Should().Be(20);
        open.Should().Contain("<span class=\"tide-count tide-muted fw-normal\">45</span>", "the heading counts the whole column");
        open.Should().Contain("aria-label=\"Show 20 more in Open\">Show 20 more</button>");
        Column(html, ActionStatus.InProgress).Should().Contain("Show 5 more");
        Cards(Column(html, ActionStatus.Done)).Should().Be(3);

        await r.Click(k => { k.ShowMore(ActionStatus.Open); return Task.CompletedTask; });
        html = await r.Html();
        Cards(Column(html, ActionStatus.Open)).Should().Be(40);
        Column(html, ActionStatus.Open).Should().Contain("Show 5 more");
        Cards(Column(html, ActionStatus.InProgress)).Should().Be(20, "each column is shown more of on its own");

        await r.Click(k => { k.ShowMore(ActionStatus.Open); return Task.CompletedTask; });
        Column(await r.Html(), ActionStatus.Open).Should().NotContain("more</button>");
        Cards(Column(await r.Html(), ActionStatus.Open)).Should().Be(45);
    }

    [Fact]
    public async Task TheBoardsDone_ReachesAWeek_AndShowOlderTakesItToAMonth()
    {
        var older = 0;
        await using var r = await ComponentHarness.RenderAsync<ActionKanban>(app, "/actions?layout=board", new()
        {
            ["Board"] = Board(), ["Today"] = DateOnly.FromDateTime(DateTime.UtcNow),
            ["OnShowOlder"] = EventCallback.Factory.Create(new object(), () => older++),
        });
        var done = Column(WebUtility.HtmlDecode(await r.Html()), ActionStatus.Done);
        done.Should().Contain("Last 7 days").And.Contain("Show older: the last 30 days");
        Column(await r.Html(), ActionStatus.Open).Should().NotContain("Show older", "only under Done");
        await r.Click(k => (Task)typeof(ActionKanban).GetMethod("ShowOlder", Private)!.Invoke(k, null)!);
        older.Should().Be(1, "the page reads the board again, further back");

        await using var month = await ComponentHarness.RenderAsync<ActionKanban>(app, "/actions?layout=board", new()
        {
            ["Board"] = Board(ActionService.BoardOlderDays), ["Today"] = DateOnly.FromDateTime(DateTime.UtcNow),
        });
        Column(WebUtility.HtmlDecode(await month.Html()), ActionStatus.Done).Should().Contain("Last 30 days").And.NotContain("Show older");
    }

    // 60 open actions of the test's own, P1 every third, blocked every fifth, undated every fourth; found by their words.
    private async Task SeedActions()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Actions.AnyAsync(a => a.Title.StartsWith("Pagerprobe"))) return;
        var team = await db.Teams.FirstAsync();
        var n = await db.Actions.MaxAsync(a => (int?)a.Number) ?? 0;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        for (var i = 0; i < 60; i++)
            db.Actions.Add(new ActionItem
            {
                Number = ++n, Title = $"Pagerprobe {i:00}", TeamId = team.Id, Priority = i % 3 == 0 ? Priority.P1 : Priority.P3,
                Status = i % 5 == 0 ? ActionStatus.Blocked : ActionStatus.Open, DueDate = i % 4 == 0 ? null : today.AddDays(30 + i),
            });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task TheActionsList_ReadsItsPageAndFiltersFromTheAddress_AndAFilterChangeGoesBackToPage1()
    {
        await SeedActions();
        await using var r = await ComponentHarness.RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list&q=Pagerprobe&page=2");
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("51–60"));
        html.Should().Contain("51–60 of 60 matching actions");
        html.Should().Contain("aria-current=\"page\" aria-label=\"Page 2, this page\"");
        html.Should().MatchRegex("<a class=\"btn btn-outline-secondary btn-sm\" href=\"[^\"]*actions\\?view=open&layout=list&q=Pagerprobe\" aria-label=\"Page 1\">",
            "a page's link keeps the filters, and the first page's has no page in it");
        html.Should().Contain("value=\"Pagerprobe\"", "the words are in the search box");
        html.Should().NotContain("Filter actions", "one search box serves the list and the board");

        (WebUtility.HtmlDecode(await (await ComponentHarness.RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list&q=Pagerprobe&priority=p1")).HtmlOnce("matching")))
            .Should().Contain("20 matching actions").And.Contain("Priority: P1", "a priority no pill says is a chip");
        (WebUtility.HtmlDecode(await (await ComponentHarness.RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list&q=Pagerprobe&status=blocked")).HtmlOnce("matching")))
            .Should().Contain("12 matching actions");
        (WebUtility.HtmlDecode(await (await ComponentHarness.RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list&q=Pagerprobe&due=none&priority=p1")).HtmlOnce("matching")))
            .Should().Contain("5 matching actions", "every twelfth is both undated and P1");
        (WebUtility.HtmlDecode(await (await ComponentHarness.RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list&q=Pagerprobe&due=week")).HtmlOnce("actions")))
            .Should().Contain("No actions match");

        // Another priority: the first page again (the address is written too, which a page outside a circuit can't do).
        typeof(ActionsBoard).GetField("_priorities", Private)!.SetValue(r.Component, new[] { Priority.P3 });
        try { await r.Click(c => (Task)typeof(ActionsBoard).GetMethod("Refilter", Private)!.Invoke(c, null)!); }
        catch (NavigationException) { }
        ((int)typeof(ActionsBoard).GetField("_page", Private)!.GetValue(r.Component)!).Should().Be(1);
        ((int)typeof(ActionsBoard).GetField("_total", Private)!.GetValue(r.Component)!).Should().Be(40);
        ((IReadOnlyList<ActionRow>)typeof(ActionsBoard).GetField("_rows", Private)!.GetValue(r.Component)!).Should().HaveCount(40);
    }

    [Fact]
    public async Task TheBoard_TakesTheSameFilters_FromTheAddress()
    {
        await SeedActions();
        await using var r = await ComponentHarness.RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=board&q=Pagerprobe&status=blocked&priority=p1");
        var html = WebUtility.HtmlDecode(await r.HtmlOnce("action"));
        html.Should().Contain("4 actions", "blocked and P1: every fifteenth");
        FilterBarHtml.Pressed(html, "Blocked").Should().BeTrue();
        FilterBarHtml.Pressed(html, "Tracking").Should().BeFalse();
    }
}
