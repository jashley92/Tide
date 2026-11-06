using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Intake;
using Tide.Application.Operations;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Pages.Admin;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

internal static class Drawing
{
    private static readonly MethodInfo Render = typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Draws the page again, as the app does after a click's handler (a test calls the handler itself).</summary>
    public static Task Drawn<T>(this Rendered<T> r) where T : ComponentBase => r.Renderer.Dispatcher.InvokeAsync(() => Render.Invoke(r.Component, null));
}

/// <summary>
/// EP-84's "Not the same": a split is kept for the whole team, as a row per report of the group, so it holds on the
/// next visit and in a colleague's inbox, notes who split it, and Undo (on the toast or the note) groups them again.
/// </summary>
public class TriageSplitPageTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Robin = new("dev:robin", "Robin", "Analyst");

    // Three reports of one made-up event from two feeds, the newest leading the inbox; their ids, newest first.
    private async Task<List<Guid>> Seed(string key, int minutesAhead)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        var one = new Source { Name = "Split feed one " + key, Url = "https://split.example/feed/one-" + key };
        var two = new Source { Name = "Split feed two " + key, Url = "https://split.example/feed/two-" + key };
        var at = DateTimeOffset.UtcNow.AddMinutes(minutesAhead);
        FeedItem Item(Source source, string title, int n) => new()
        {
            Source = source, Title = title, Url = $"https://split.example/{key}/{n}", UrlHash = $"split-{key}-{n}",
            PublishedAtUtc = at.AddMinutes(-n), FetchedAtUtc = at.AddMinutes(-n),
        };
        var name = "Vexmora" + key;
        var items = new[]
        {
            Item(one, $"{name} links $4.1 billion theft to Draxel{key} wallet", 0),
            Item(two, $"{name} hit by $4.1 billion hack", 1),
            Item(one, $"{name} probes $3.9 billion theft", 2),
        };
        db.AddRange(one, two);
        db.AddRange(items);
        await db.SaveChangesAsync();
        return items.Select(i => i.Id).ToList();
    }

    private async Task SetAside(List<Guid> ids)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        await db.FeedItems.Where(i => ids.Contains(i.Id) && i.Status == FeedItemStatus.New)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, FeedItemStatus.Duplicate));
    }

    private async Task<List<TriageSplit>> Members(List<Guid> ids)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        return await db.TriageSplits.AsNoTracking().Where(s => ids.Contains(s.ItemId)).ToListAsync();
    }

    private static string Row(string html, string text)
    {
        var at = html.IndexOf(text, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"{text} is shown");
        var start = html.LastIndexOf("<li", at, StringComparison.Ordinal);
        return html[start..html.IndexOf("</li>", at, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task ASplit_IsKept_ForTheNextVisit_AndForAColleague_WithWhoSplitIt()
    {
        const string key = "a";
        var ids = await Seed(key, 1000);
        try
        {
            await using (var r = await RenderAsync<Triage>(app, "/triage"))
            {
                await r.HtmlOnce("Looks like one event");
                await r.Click(t => t.Split(0));
                await r.Drawn();
                r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().NotContain(t => t.IsError);
                var html = await r.Html();
                html.Should().NotContain("tide-triage-group");
                Row(html, "Vexmoraa links $4.1 billion theft").Should().Contain("selected", "the newest report is selected")
                    .And.Contain("Split by Tester, ").And.Contain("aria-label=\"Undo the split of 3 reports\"");
                Row(html, "Vexmoraa hit by $4.1 billion hack").Should().NotContain("Split by", "the note is on the newest report only");
                r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts
                    .Should().Contain(t => t.Message == "Split into 3 rows, for everyone." && t.Undo != null);
            }

            var members = await Members(ids);
            members.Should().HaveCount(3, "a row per report").And.OnlyContain(m => m.SplitBy == "dev:tester" && m.SplitByName == "Tester");
            members.Select(m => m.ItemId).Should().BeEquivalentTo(ids);
            members.Select(m => m.Batch).Distinct().Should().ContainSingle();

            // Another visit, by a colleague: still split, and the note says who did it.
            await using var robin = await RenderAsync<Triage>(app, "/triage", @as: Robin);
            var again = await robin.HtmlOnce("Vexmoraa links");
            again.Should().NotContain("Looks like one event: 3 reports");
            Row(again, "Vexmoraa links $4.1 billion theft").Should().Contain("Split by Tester, ");
        }
        finally { await SetAside(ids); }
    }

    [Fact]
    public async Task Undo_OnTheToast_OrOnTheNote_GroupsThemAgain()
    {
        const string key = "b";
        var ids = await Seed(key, 2000);
        try
        {
            await using var r = await RenderAsync<Triage>(app, "/triage");
            await r.HtmlOnce("Looks like one event");
            await r.Click(t => t.Split(0));
            var toasts = r.Scope.ServiceProvider.GetRequiredService<ToastService>();
            var toast = toasts.Toasts.Single(t => t.Undo != null);
            await r.Click(_ => toasts.UndoAsync(toast.Id));   // the toast's button, as the signed-in analyst
            var html = await r.HtmlOnce("Looks like one event: 3 reports");
            Row(html, "Looks like one event: 3 reports").Should().Contain("Vexmorab links $4.1 billion theft");
            html.Should().NotContain("Split by");
            (await Members(ids)).Should().BeEmpty();

            // Split again, then undone by a colleague from the note.
            await r.Click(t => t.Split(0));
            await using var robin = await RenderAsync<Triage>(app, "/triage", @as: Robin);
            await robin.HtmlOnce("Split by Tester");
            var batch = (await Members(ids)).Select(p => p.Batch).Distinct().Single();
            await robin.Click(t => t.UndoSplit(batch));
            await robin.Drawn();
            (await robin.Html()).Should().Contain("Looks like one event: 3 reports", "anyone who triages can undo a split");
            (await Members(ids)).Should().BeEmpty();
            (await r.HtmlOnce("Looks like one event: 3 reports")).Should().NotContain("Split by", "the first inbox heard the undo");
        }
        finally { await SetAside(ids); }
    }

    [Fact]
    public async Task AColleaguesOpenInbox_HearsTheSplit()
    {
        const string key = "c";
        var ids = await Seed(key, 3000);
        try
        {
            await using var robin = await RenderAsync<Triage>(app, "/triage", @as: Robin);
            (await robin.HtmlOnce("Looks like one event")).Should().Contain("Vexmorac links");
            await using var r = await RenderAsync<Triage>(app, "/triage");
            await r.HtmlOnce("Looks like one event");
            await r.Click(t => t.Split(0));

            var html = await robin.HtmlOnce("Split by Tester");
            html.Should().Contain("Split by Tester").And.NotContain("Looks like one event: 3 reports");
        }
        finally { await SetAside(ids); }
    }
}

/// <summary>Splits as the service keeps them: who may, what's audited and heard, and what goes with pruned items.</summary>
public class TriageSplitServiceTests : IDisposable
{
    private TestDb _db = new();

    public TriageSplitServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<List<FeedItem>> Seed(DateTimeOffset? fetched = null, int count = 3)
    {
        var source = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "Feed", Url = "https://feed.example.com/rss", PollMinutes = 30 });
        var other = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "Other", Url = "https://other.example.com/rss", PollMinutes = 30 });
        var at = fetched ?? DateTimeOffset.UtcNow;
        var items = Enumerable.Range(0, count).Select(n => new FeedItem
        {
            SourceId = n % 2 == 0 ? source : other, Title = $"Report {n}", Url = $"https://feed.example.com/{n}", UrlHash = $"split-{n}",
            FetchedAtUtc = at, PublishedAtUtc = at.AddMinutes(-n),
        }).ToList();
        await using var ctx = _db.Context();
        ctx.FeedItems.AddRange(items);
        await ctx.SaveChangesAsync();
        return items;
    }

    [Fact]
    public async Task ASplit_IsARowPerReport_Audited_AndHeard_AndUndoTakesThemAllBack()
    {
        var items = await Seed(count: 4);
        var heard = new List<Change>();
        _db.Feed.Changed += heard.Add;

        var batch = await _db.Intake().SplitAsync(items.Select(i => i.Id).ToList());

        batch.Should().NotBeNull();
        heard.Should().Contain(c => c.Topic == ChangeFeed.Triage && c.ActorId == _db.User.UserId, "colleagues' inboxes regroup");
        await using (var ctx = _db.Context())
        {
            (await ctx.TriageSplits.CountAsync(s => s.Batch == batch)).Should().Be(4);
            (await ctx.AuditLog.CountAsync(a => a.EntityType == nameof(TriageSplit) && a.Action == "Added")).Should().Be(4, "one entry per report");
        }
        (await _db.Intake().SplitAsync([items[0].Id, items[1].Id, items[3].Id])).Should().BeNull("every two of those share a split already");

        heard.Clear();
        (await _db.Intake().UndoSplitAsync(batch!.Value)).Should().Be(4);
        (await _db.Intake().UndoSplitAsync(batch.Value)).Should().Be(0, "nothing's left to undo");
        heard.Should().Contain(c => c.Topic == ChangeFeed.Triage);
        await using var check = _db.Context();
        (await check.TriageSplits.AnyAsync()).Should().BeFalse();
        (await check.AuditLog.CountAsync(a => a.EntityType == nameof(TriageSplit) && a.Action == "Deleted")).Should().Be(4, "the undo is audited");
    }

    [Fact]
    public async Task ASplitItem_StillGroupsWithAnotherReport_InTheInbox()
    {
        // Three reports of one event from two feeds, the oldest split from the newest only.
        var source = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "One", Url = "https://one.example.com/rss", PollMinutes = 30 });
        var other = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "Two", Url = "https://two.example.com/rss", PollMinutes = 30 });
        var now = DateTimeOffset.UtcNow;
        FeedItem Item(Guid feed, string title, int n) => new()
        {
            SourceId = feed, Title = title, Url = $"https://x.example.com/{n}", UrlHash = $"g-{n}", FetchedAtUtc = now, PublishedAtUtc = now.AddMinutes(-n),
        };
        var a = Item(source, "Quarnex links $2.2 billion theft to Pellow laptop", 0);
        var c = Item(other, "Quarnex probes $2.2 billion theft", 1);
        var b = Item(other, "Quarnex hit by $2.2 billion hack", 2);
        await using (var ctx = _db.Context())
        {
            ctx.FeedItems.AddRange(a, b, c);
            await ctx.SaveChangesAsync();
        }
        await _db.Intake().SplitAsync([a.Id, b.Id]);

        var rows = await _db.Intake().InboxAsync(new InboxFilter(FeedItemStatus.New));
        rows.Single(r => r.Item.Id == a.Id).Group!.Ids.Should().BeEquivalentTo([a.Id, c.Id], "a still groups with c");
        rows.Single(r => r.Item.Id == b.Id).Group.Should().BeNull("b can't join a, and alone with c it's one feed");
        rows.Single(r => r.Item.Id == a.Id).Split.Should().Match<SplitNote>(n => n.ByName == "Test Analyst" && n.Reports == 2);
    }

    [Fact]
    public async Task OnlyThoseWhoTriage_SplitOrUndo()
    {
        var items = await Seed();
        var batch = await _db.Intake().SplitAsync([items[0].Id, items[1].Id]);
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };

        await _db.Intake().Invoking(s => s.SplitAsync([items[1].Id, items[2].Id])).Should().ThrowAsync<ForbiddenException>();
        await _db.Intake().Invoking(s => s.UndoSplitAsync(batch!.Value)).Should().ThrowAsync<ForbiddenException>();
        await using var ctx = _db.Context();
        (await ctx.TriageSplits.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ASplit_TakesTwoToEightReportsThatAreThere()
    {
        var items = await Seed();
        await _db.Intake().Invoking(s => s.SplitAsync([items[0].Id, items[0].Id])).Should().ThrowAsync<ArgumentException>();
        await _db.Intake().Invoking(s => s.SplitAsync([.. Enumerable.Range(0, 9).Select(_ => Guid.NewGuid())])).Should().ThrowAsync<ArgumentException>();
        await _db.Intake().Invoking(s => s.SplitAsync([items[0].Id, Guid.NewGuid()])).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task PrunedItems_TakeTheirSplitsWithThem(string provider)
    {
        _db = _db.On(provider);
        var old = await Seed(DateTimeOffset.UtcNow.AddDays(-200));
        await using (var ctx = _db.Context())
        {
            // A recent report the second old one was split from: the old one's row goes with it, the recent one's stays.
            ctx.FeedItems.Add(new FeedItem { SourceId = old[0].SourceId, Title = "Recent", Url = "https://feed.example.com/new", UrlHash = "split-new", FetchedAtUtc = DateTimeOffset.UtcNow });
            await ctx.SaveChangesAsync();
        }
        Guid recent;
        await using (var ctx = _db.Context()) recent = (await ctx.FeedItems.SingleAsync(i => i.UrlHash == "split-new")).Id;
        await _db.Intake().SplitAsync(old.Select(i => i.Id).ToList());
        await _db.Intake().SplitAsync([old[1].Id, recent]);

        (await _db.Intake().PruneAsync()).Should().Be(3);

        await using var check = _db.Context();
        (await check.TriageSplits.Select(s => s.ItemId).ToListAsync()).Should().Equal([recent], "each pruned item's rows went with it (cascade)");
        (await check.FeedItems.Select(i => i.Id).ToListAsync()).Should().Equal(recent);
    }
}

/// <summary>EP-84's grouping settings: kept like the other intake settings, checked, audited and carried in the bundle.</summary>
public class TriageGroupingSettingsTests
{
    [Fact]
    public async Task TheWindowAndExtraWords_AreSaved_Checked_AndTakeEffect()
    {
        using var rig = new SettingsRig();
        rig.Now<IntakeOptions>().Grouping().Should().BeEquivalentTo(TriageGroupOptions.Default, "unsaved, the defaults hold");

        await rig.Get<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?>
        {
            [OperationalSettings.GroupWindowHours] = "36",
            [OperationalSettings.GroupIgnoredWords] = " Webinar\nZero-Click; hackers ",
        });

        var grouping = rig.Now<IntakeOptions>().Grouping();
        grouping.Window.Should().Be(TimeSpan.FromHours(36));
        grouping.Ignored.Should().BeEquivalentTo("webinar", "zero", "click");
        await using var ctx = rig.Db.Context();
        ctx.Settings.Single(s => s.Key == OperationalSettings.GroupIgnoredWords).Value.Should().Be("webinar; zero-click", "a built-in word is left out");
        ctx.AuditLog.Should().Contain(a => a.EntityType == nameof(AppSetting) && a.EntityKey == OperationalSettings.GroupIgnoredWords);
    }

    [Theory]
    [InlineData(OperationalSettings.GroupWindowHours, "11")]
    [InlineData(OperationalSettings.GroupWindowHours, "97")]
    [InlineData(OperationalSettings.GroupWindowHours, "two days")]
    [InlineData(OperationalSettings.GroupIgnoredWords, "supply chain")]
    [InlineData(OperationalSettings.GroupIgnoredWords, "c#")]
    public async Task ABadValue_IsRefused(string key, string value)
    {
        using var rig = new SettingsRig();
        OperationalSettings.Problem(key, value).Should().NotBeNull();
        await rig.Invoking(r => r.Save(key, value)).Should().ThrowAsync<ArgumentException>();
        await using var ctx = rig.Db.Context();
        ctx.Settings.Should().NotContain(s => s.Key == key);
    }

    [Fact]
    public void TheCatalog_ListsThemOnSources()
    {
        SettingsCatalog.Find(OperationalSettings.GroupWindowHours)!.EditedIn.Should().Be("admin/sources");
        SettingsCatalog.Find(OperationalSettings.GroupIgnoredWords)!.EditedIn.Should().Be("admin/sources");
    }

    [Fact]
    public async Task TheConfigurationBundle_CarriesThem()
    {
        using var source = new SettingsRig();
        await source.Save(OperationalSettings.GroupWindowHours, "72");
        await source.Save(OperationalSettings.GroupIgnoredWords, "webinar\nroundtable");
        var bundle = await source.Get<ConfigBundleService>().ExportAsync();
        bundle.Settings.Should().Contain(new BundleSetting(OperationalSettings.GroupWindowHours, "72"))
            .And.Contain(new BundleSetting(OperationalSettings.GroupIgnoredWords, "webinar; roundtable"));

        using var target = new SettingsRig();
        await target.Get<ConfigBundleService>().ImportAsync(bundle);
        var grouping = target.Now<IntakeOptions>().Grouping();
        grouping.Window.Should().Be(TimeSpan.FromHours(72));
        grouping.Ignored.Should().BeEquivalentTo("webinar", "roundtable");
    }
}

/// <summary>Administration › Sources' Triage grouping section, rendered in the real app.</summary>
public class TriageGroupingAdminTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<string?> Saved(string key)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        return db.Settings.Where(s => s.Key == key).Select(s => s.Value).FirstOrDefault();
    }

    [Fact]
    public async Task Sources_ShowsTheBuiltInWords_AndSavesTheWindowAndExtraWords()
    {
        await using var r = await RenderAsync<SourcesAdmin>(app, "/admin/sources");
        var html = await r.Html();
        html.Should().Contain("Triage grouping").And.Contain("id=\"group-window\"").And.Contain("value=\"48\"")
            .And.Contain("How far apart two reports of one event can be (hours)").And.Contain("id=\"group-words\"")
            .And.Contain($"Always ignored: {TriageGroups.StopWords.Count + TriageGroups.GenericWords.Count} built-in words")
            .And.Contain("aria-labelledby=\"stop-words-title\"").And.Contain("<li class=\"list-inline-item tide-data\">hack</li>")
            .And.NotContain("Intake:GroupWindowHours");

        r.Component.IgnoredWords = "Webinar\nransomware";
        await r.Drawn();
        (await r.Html()).Should().Contain("Always ignored already, so left out: ransomware", "a built-in word typed is pointed out before saving");
        await r.Click(c => c.SaveGrouping());

        (await Saved(OperationalSettings.GroupIgnoredWords)).Should().Be("webinar");
        (await Saved(OperationalSettings.GroupWindowHours)).Should().Be("48");
        r.Component.IgnoredWords.Should().Be("webinar", "the form shows what was saved");
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.Message.Contains("leaving out ransomware", StringComparison.Ordinal));
    }
}
