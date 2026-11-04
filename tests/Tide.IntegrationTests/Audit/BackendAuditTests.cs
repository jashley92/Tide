using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Intake;
using Tide.Application.Trends;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests.Audit;

/// <summary>Regression tests from the backend audit: each pins a defect found in the services or the data layer.</summary>
public class BackendAuditTests : IDisposable
{
    private readonly TestDb _db = new();
    private const string FeedUrl = "https://news.example.com/feed.xml";

    public BackendAuditTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private static string Rss(params (string Title, string Link, string? Description)[] items) =>
        "<rss version=\"2.0\"><channel><title>News</title>" +
        string.Concat(items.Select(i => $"<item><title>{i.Title}</title><link>{i.Link}</link><description>{i.Description}</description><pubDate>Mon, 26 Oct 2026 09:00:00 GMT</pubDate></item>")) +
        "</channel></rss>";

    private async Task<Guid> Source() =>
        await _db.Intake().SaveSourceAsync(new SourceForm { Name = "News", Kind = SourceKind.Feed, Url = FeedUrl, PollMinutes = 30 });

    private async Task<Guid> Article(string title = "An article")
    {
        var id = await _db.Articles().CreateAsync(title);
        var form = ArticleForm.From((await _db.Articles().GetAsync(id))!);
        form.Bluf = "Bottom line.";
        await _db.Articles().SaveAsync(form);
        return id;
    }

    // --- A2-1: the inbox search ran as a case-sensitive Contains on SQLite (instr), unlike every other search ------------

    [Fact]
    public async Task Inbox_Search_IgnoresCase_OnEveryProvider()
    {
        var source = await Source();
        _db.Http.Set(FeedUrl, Rss(("Ransomware gang hits a hospital", "https://example.com/1", null), ("Phishing kit update", "https://example.com/2", null)));
        await _db.Intake().PollNowAsync(source);

        var rows = await _db.Intake().InboxAsync(new InboxFilter(Search: "RANSOMWARE"));

        rows.Should().ContainSingle().Which.Item.Title.Should().StartWith("Ransomware");
    }

    [Fact]
    public async Task Inbox_Search_TakesWildcardsLiterally()
    {
        var source = await Source();
        _db.Http.Set(FeedUrl, Rss(("100% of hosts patched", "https://example.com/1", null), ("Nothing to see", "https://example.com/2", null)));
        await _db.Intake().PollNowAsync(source);

        (await _db.Intake().InboxAsync(new InboxFilter(Search: "100%"))).Should().ContainSingle();
        (await _db.Intake().InboxAsync(new InboxFilter(Search: "%"))).Should().ContainSingle("a percent sign is a character to find, not a wildcard")
            .Which.Item.Title.Should().StartWith("100%");
        (await _db.Intake().InboxAsync(new InboxFilter(Search: "_"))).Should().BeEmpty("an underscore matches only an underscore");
    }

    // --- A2-2: the inbox loaded every row of a status and then filtered and cut the list in memory ----------------------

    [Fact]
    public async Task Inbox_SinceAndTake_StillApply_AndTheNewestComeFirst()
    {
        var source = await Source();
        var items = Enumerable.Range(1, 6).Select(i => ($"Item {i}", $"https://example.com/{i}", (string?)null)).ToArray();
        _db.Http.Set(FeedUrl, Rss(items));
        await _db.Intake().PollNowAsync(source);
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = _db.Context())
        {
            // Item n was published n days ago (Item 1 the newest); Item 6 is undated and was fetched long ago.
            foreach (var row in await ctx.FeedItems.ToListAsync())
            {
                var n = int.Parse(row.Title[5..], System.Globalization.CultureInfo.InvariantCulture);
                row.PublishedAtUtc = n == 6 ? null : now.AddDays(-n);
                if (n == 6) row.FetchedAtUtc = now.AddDays(-30);
            }
            await ctx.SaveChangesAsync();
        }

        var newest = await _db.Intake().InboxAsync(new InboxFilter(Take: 3));
        newest.Select(r => r.Item.Title).Should().Equal("Item 1", "Item 2", "Item 3");

        var recent = await _db.Intake().InboxAsync(new InboxFilter(Since: now.AddDays(-3.5)));
        recent.Select(r => r.Item.Title).Should().Equal("Item 1", "Item 2", "Item 3");
        var all = await _db.Intake().InboxAsync(new InboxFilter(Since: now.AddDays(-20)));
        all.Select(r => r.Item.Title).Should().Equal("Item 1", "Item 2", "Item 3", "Item 4", "Item 5");
        all.Should().NotContain(r => r.Item.Title == "Item 6", "an undated item counts by when it was fetched, which was before the cutoff");

        // Other tabs page the same way: the newest of the status, cut to the page.
        foreach (var title in new[] { "Item 2", "Item 5" })
            await _db.Intake().ShortlistAsync(all.Select(r => r.Item).First(i => i.Title == title).Id);
        var shortlisted = await _db.Intake().InboxAsync(new InboxFilter(FeedItemStatus.Shortlisted, Take: 1));
        shortlisted.Select(r => r.Item.Title).Should().Equal("Item 2");
    }

    // --- A2-3: deleting a draft that actions were raised from failed with a bare database error -----------------------

    [Fact]
    public async Task DeleteArticle_WithActionsRaisedFromIt_IsRefusedWithAReason()
    {
        var article = await Article();
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = article });

        var act = () => _db.Articles().DeleteAsync(article);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*action*");
        (await _db.Articles().GetAsync(article)).Should().NotBeNull("nothing was deleted");
    }

    // --- A2-4: deleting a draft promoted from triage left the item "Promoted" with no article: a zombie nobody could triage ---

    [Fact]
    public async Task DeleteArticle_PromotedFromTriage_PutsTheItemBackInTheInbox()
    {
        var source = await Source();
        _db.Http.Set(FeedUrl, Rss(("Vendor fixes exploited bug", "https://example.com/fix", "Fixes CVE-2026-0002.")));
        await _db.Intake().PollNowAsync(source);
        var item = (await _db.Intake().InboxAsync(new InboxFilter())).Single().Item;
        var article = await _db.Intake().PromoteAsync(item.Id);

        await _db.Articles().DeleteAsync(article);

        await using var ctx = _db.Context();
        var row = await ctx.FeedItems.SingleAsync();
        row.Status.Should().Be(FeedItemStatus.New, "the draft is gone, so the item is back to be triaged again");
        row.ArticleId.Should().BeNull();
        row.StatusByName.Should().BeNull();
        (await _db.Intake().InboxAsync(new InboxFilter())).Should().ContainSingle(r => r.Item.Id == item.Id);
        var again = await _db.Intake().PromoteAsync(item.Id);
        again.Should().NotBe(article, "it can be promoted afresh at once, not after a claim timeout");
    }

    // --- A2-5: an article save or status change took any integer as a classification, priority or status ---------------

    [Fact]
    public async Task SaveArticle_RefusesAClassificationOrPriorityOutsideTheEnum()
    {
        var id = await Article();
        var form = ArticleForm.From((await _db.Articles().GetAsync(id))!);
        form.Classification = (Classification)99;

        var bad = () => _db.Articles().SaveAsync(form);
        await bad.Should().ThrowAsync<ArgumentException>();

        form.Classification = Classification.Threat;
        form.Priority = (Priority)99;
        await bad.Should().ThrowAsync<ArgumentException>();

        (await _db.Articles().GetAsync(id))!.Classification.Should().Be(Classification.Informational, "nothing was stored");
    }

    [Fact]
    public async Task SetArticleStatus_RefusesAStatusOutsideTheEnum()
    {
        var id = await Article();

        var bad = () => _db.Articles().SetStatusAsync(id, (ArticleStatus)99);

        await bad.Should().ThrowAsync<ArgumentException>();
        (await _db.Articles().GetAsync(id))!.Status.Should().Be(ArticleStatus.Draft);
    }

    // --- A2-6: the manager list was joined into a 4,000-character setting with no length check ---------------------------

    [Fact]
    public async Task SaveManagerEmails_RefusesAListLongerThanTheSettingHolds()
    {
        var many = string.Join("; ", Enumerable.Range(0, 200).Select(i => $"manager{i:D3}@a-long-example-domain.example.org"));

        var act = () => _db.Rotation().SaveManagerEmailsAsync(many);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*too many*");
        (await _db.Rotation().ManagerEmailsAsync()).Should().BeEmpty();
    }

    // --- A2-7: an action save that lost the race to another change surfaced as a database exception, not a stale edit ---

    [Fact]
    public async Task SaveAction_ThatLosesTheRaceToAnotherChange_IsAStaleEdit()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var svc = _db.Actions();
        var number = await svc.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team });
        var action = (await svc.GetAsync(number))!.Action;
        var form = ActionForm.From(action);
        form.Title = "Sweep the proxy logs";
        // Between the stale-copy check and the save, someone else takes the action on.
        _db.BeforeSave = ctx =>
        {
            if (!ctx.ChangeTracker.Entries<ActionItem>().Any(e => e.State == EntityState.Modified)) return;
            _db.BeforeSave = null;
            using var other = _db.Context();
            var row = other.Actions.Single(a => a.Id == action.Id);
            row.AssigneeId = "S-1-5-21-OTHER";
            row.AssigneeName = "Someone Else";
            row.EditVersion = Guid.NewGuid();
            other.SaveChanges();
        };

        var save = () => svc.SaveAsync(form);

        await save.Should().ThrowAsync<StaleEditException>();
        var after = (await svc.GetAsync(number))!.Action;
        after.Title.Should().Be("Sweep", "the stale save was refused whole");
        after.AssigneeName.Should().Be("Someone Else");
    }

    [Fact]
    public async Task MoveAction_ThatLosesTheRaceToAnotherChange_IsAStaleEdit()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var svc = _db.Actions();
        var number = await svc.CreateAsync(new ActionForm { Title = "Sweep", Kind = ActionKind.IocSweep, TeamId = team });
        var id = (await svc.GetAsync(number))!.Action.Id;
        _db.BeforeSave = ctx =>
        {
            if (!ctx.ChangeTracker.Entries<ActionItem>().Any(e => e.State == EntityState.Modified)) return;
            _db.BeforeSave = null;
            using var other = _db.Context();
            var row = other.Actions.Single(a => a.Id == id);
            row.EditVersion = Guid.NewGuid();
            other.SaveChanges();
        };

        var move = () => svc.MoveAsync(id, ActionStatus.Open, ActionStatus.InProgress);

        await move.Should().ThrowAsync<StaleEditException>();
    }

    // --- A2-11: name uniqueness compared case-sensitively on SQLite, so SQL Server's collation refused "news" raw ------

    [Fact]
    public async Task SaveSource_ANameThatDiffersOnlyInCase_IsRefusedWithTheFriendlyMessage()
    {
        var id = await Source();

        var clash = () => _db.Intake().SaveSourceAsync(new SourceForm { Name = "NEWS", Kind = SourceKind.Feed, Url = "https://other.example/feed", PollMinutes = 30 });
        await clash.Should().ThrowAsync<ArgumentException>().WithMessage("*already a source*");

        // Renaming a source to its own name, in any case, is fine.
        var self = await _db.Intake().SaveSourceAsync(new SourceForm { Id = id, Name = "news", Kind = SourceKind.Feed, Url = FeedUrl, PollMinutes = 30 });
        self.Should().Be(id);
    }

    [Fact]
    public async Task SavePlaybook_ANameThatDiffersOnlyInCase_IsRefused()
    {
        await _db.Playbooks().SaveAsync(new PlaybookForm { Name = "Sweep", Steps = ["Check the proxy"] });

        var clash = () => _db.Playbooks().SaveAsync(new PlaybookForm { Name = "sweep", Steps = ["Check the proxy"] });

        await clash.Should().ThrowAsync<ArgumentException>().WithMessage("*already a playbook*");
    }

    // --- A2-12: the presenter and opening note of a Sent brief could still be changed by the service -------------------

    [Fact]
    public async Task SetDetails_OnASentBrief_IsRefused()
    {
        var brief = await _db.Briefs().GetOrStartAsync(_db.Clock.CurrentBriefDate);
        await using (var ctx = _db.Context())
        {
            var row = await ctx.Briefs.SingleAsync(b => b.Id == brief.Id);
            row.Status = BriefStatus.Sent;
            await ctx.SaveChangesAsync();
        }

        var act = () => _db.Briefs().SetDetailsAsync(brief.Id, "S-1-5-21-OTHER", "Someone Else", "A new note");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*sent*");
        (await _db.Briefs().GetAsync(brief.BriefDate))!.AnalystName.Should().Be("Test Analyst");
    }

    // --- A2-16: the article list's date filter scanned every brief for every article (2.4 s at 10k articles / 2k briefs);
    // it now starts from the article's brief items. It must still count exactly what a Trends line counts. ---------------

    [Fact]
    public async Task ArticleList_DateFilter_CountsWhatTheTrendLineCounts_AndRunsThreeQueries()
    {
        // In-range brief: one article first briefed there, one first briefed before the range (counted earlier), one never
        // briefed and written in range, one draft in range (never counts), one never briefed and written before the range.
        var trends = _db.Trends();
        var period = trends.DefaultEnd(PeriodKind.Month);
        var inRange = NextBusinessDayFrom(period.Start.AddDays(3));
        var earlier = NextBusinessDayFrom(period.Previous.Start.AddDays(3));
        var firstHere = await Article("First briefed in range");
        var firstBefore = await Article("First briefed before the range");
        var written = await Article("Written in range, never briefed");
        var draft = await _db.Articles().CreateAsync("Draft in range");
        var old = await Article("Written before the range");
        await using (var ctx = _db.Context())
        {
            foreach (var (id, day) in new[] { (firstHere, inRange), (written, inRange), (draft, inRange), (old, earlier), (firstBefore, earlier) })
            {
                var a = await ctx.Articles.SingleAsync(x => x.Id == id);
                a.CreatedAtUtc = _db.Clock.StartOfDay(day).AddHours(10);
                if (id != draft) a.Status = ArticleStatus.Ready;
            }
            var before = new Brief { BriefDate = earlier, AnalystId = "x", AnalystName = "x" };
            before.Items.Add(new BriefItem { ArticleId = firstBefore, SortOrder = 0 });
            var now = new Brief { BriefDate = inRange, AnalystId = "x", AnalystName = "x" };
            now.Items.Add(new BriefItem { ArticleId = firstHere, SortOrder = 0 });
            now.Items.Add(new BriefItem { ArticleId = firstBefore, SortOrder = 1 });
            ctx.Briefs.AddRange(before, now);
            await ctx.SaveChangesAsync();
        }
        var report = await trends.ReportAsync(period);
        var row = report.Find("articles:all")!;
        _db.Commands.Reset();
        var page = await _db.Articles().PageAsync(report.ArticlesFilter(row, period)!, ArticleSort.Written, true, 50);

        row.Count(report.Periods.Count - 1).Should().Be(2);
        page.Total.Should().Be(2);
        page.Rows.Select(r => r.Id).Should().BeEquivalentTo([firstHere, written]);
        _db.Commands.Count.Should().Be(3, "a count, a page and the brief dates: nothing per article");
        (await _db.Articles().PageAsync(report.ArticlesFilter(row, period.Previous)!, ArticleSort.Written, true, 50)).Rows.Select(r => r.Id)
            .Should().BeEquivalentTo([firstBefore, old]);
    }

    private static DateOnly NextBusinessDayFrom(DateOnly d) => Tide.Application.Org.WorkingDays.Weekdays.Next(d, includeToday: true);

    // --- A5-3: one note cell over 300 characters threw and aborted the whole metrics import (no record; a drop-folder file
    // read again every pass). The row is skipped with a reason, as every other bad cell is. ---------------------------------

    [Fact]
    public async Task AMetricsCsv_WithAnOverlongNote_SkipsThatRow_NotTheWholeFile()
    {
        var group = await _db.Metrics().AddGroupAsync("SOC");
        await _db.Metrics().SaveMetricAsync(new Tide.Application.Metrics.MetricForm { GroupId = group, Name = "Open tickets" });
        var day = Tide.Application.Org.WorkingDays.Weekdays.Previous(_db.Clock.Today).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var csv = $"date,metric_key,value,note\n{day},open-tickets,7,{new string('n', 301)}\n";

        var import = await _db.MetricImports().UploadAsync("long-note.csv", System.Text.Encoding.UTF8.GetBytes(csv));

        import.Rejected.Should().BeFalse();
        (import.Applied + import.Skipped).Should().Be(1);
        import.Skipped.Should().Be(1);
        import.Problems.Should().Contain("Row 2").And.Contain("longer than 300");
    }

    [Fact]
    public async Task AMetricsCsv_WithAnOverlongNote_StillAppliesTheOtherRows_FromTheDropFolderToo()
    {
        var group = await _db.Metrics().AddGroupAsync("SOC");
        await _db.Metrics().SaveMetricAsync(new Tide.Application.Metrics.MetricForm { GroupId = group, Name = "Open tickets" });
        var day = Tide.Application.Org.WorkingDays.Weekdays.Previous(_db.Clock.Today);
        var earlier = Tide.Application.Org.WorkingDays.Weekdays.Previous(day);
        var csv = $"date,metric_key,value,note\n{day:yyyy-MM-dd},open-tickets,7,{new string('n', 301)}\n{earlier:yyyy-MM-dd},open-tickets,5,fine\n";
        _db.User.IsAuthenticated = false;   // the drop folder is read by the service itself, with nobody signed in

        var import = await _db.MetricImports().ImportDroppedAsync("drop.csv", System.Text.Encoding.UTF8.GetBytes(csv));

        import.Rejected.Should().BeFalse();
        import.Applied.Should().Be(1);
        import.Skipped.Should().Be(1);
        await using var ctx = _db.Context();
        (await ctx.MetricImports.CountAsync()).Should().Be(1, "the read is recorded, so the file is moved on and not read again");
        (await ctx.MetricValues.SingleAsync()).Value.Should().Be(5);
    }

    // --- A2-19: a drop-folder file whose import threw stayed in the inbox and was read again every pass, and the files
    // behind it waited. A fault the database may not repeat is retried; any other is set aside in "failed". --------------

    private (Tide.Infrastructure.Storage.MetricDropFolder Folder, string Root) DropFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "tide-audit-drop-" + Guid.NewGuid().ToString("N"));
        var folder = new Tide.Infrastructure.Storage.MetricDropFolder(
            Microsoft.Extensions.Options.Options.Create(new Tide.Application.Files.FileStoreOptions { RootPath = root }), new TestHostEnvironment(),
            new StaticOptionsMonitor<Tide.Application.Metrics.MetricImportOptions>(new Tide.Application.Metrics.MetricImportOptions { DropFolderEnabled = true }),
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<Tide.Infrastructure.Storage.MetricDropFolder>.Instance);
        Directory.CreateDirectory(folder.Inbox);
        return (folder, root);
    }

    private static void Drop(Tide.Infrastructure.Storage.MetricDropFolder folder, string name, int minutesAgo)
    {
        var path = Path.Combine(folder.Inbox, name);
        File.WriteAllText(path, "date,metric_key,value\n2026-10-22,open,42\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
    }

    private static string[] Names(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).ToArray() : [];

    [Fact]
    public async Task DropFolder_AFileTheDatabaseCouldntTake_WaitsForTheNextPass_WithoutHoldingTheOthers()
    {
        var group = await _db.Metrics().AddGroupAsync("SOC");
        await _db.Metrics().SaveMetricAsync(new Tide.Application.Metrics.MetricForm { GroupId = group, Name = "Open" });
        var (folder, root) = DropFolder();
        try
        {
            Drop(folder, "first.csv", minutesAgo: 10);
            Drop(folder, "second.csv", minutesAgo: 5);
            _db.User.IsAuthenticated = false;
            var faults = 0;
            _db.BeforeSave = ctx =>
            {
                if (ctx.ChangeTracker.Entries<MetricImport>().Any() && faults++ == 0) throw new TimeoutException("the database timed out");
            };

            (await folder.ProcessAsync(_db.MetricImports())).Should().Be(1, "the second file still went");

            Names(folder.Inbox).Should().BeEquivalentTo("first.csv");
            Names(Path.Combine(folder.Inbox, "processed")).Should().ContainSingle(f => f.EndsWith("-second.csv"));
            Directory.Exists(Path.Combine(folder.Inbox, "failed")).Should().BeFalse();

            (await folder.ProcessAsync(_db.MetricImports())).Should().Be(1, "the next pass reads it");
            Names(folder.Inbox).Should().BeEmpty();
        }
        finally { _db.BeforeSave = null; Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DropFolder_AFileThatFailsForGood_IsSetAsideInFailed_AndTheRestStillGo()
    {
        var group = await _db.Metrics().AddGroupAsync("SOC");
        await _db.Metrics().SaveMetricAsync(new Tide.Application.Metrics.MetricForm { GroupId = group, Name = "Open" });
        var (folder, root) = DropFolder();
        try
        {
            Drop(folder, "first.csv", minutesAgo: 10);
            Drop(folder, "second.csv", minutesAgo: 5);
            _db.User.IsAuthenticated = false;
            var faults = 0;
            _db.BeforeSave = ctx =>
            {
                if (ctx.ChangeTracker.Entries<MetricImport>().Any() && faults++ == 0) throw new InvalidOperationException("a bug for this file");
            };

            (await folder.ProcessAsync(_db.MetricImports())).Should().Be(1);

            Names(folder.Inbox).Should().BeEmpty("nothing is read for ever");
            Names(Path.Combine(folder.Inbox, "failed")).Should().ContainSingle(f => f.EndsWith("-first.csv"));
            Names(Path.Combine(folder.Inbox, "processed")).Should().ContainSingle(f => f.EndsWith("-second.csv"));
            (await folder.ProcessAsync(_db.MetricImports())).Should().Be(0);
        }
        finally { _db.BeforeSave = null; Directory.Delete(root, recursive: true); }
    }

    // --- A2-20: change events and transactions: nothing is published for a save that failed, or for one inside a
    // transaction that rolled back; a save inside a transaction is heard when it commits; a throwing listener doesn't
    // fail the save. ---------------------------------------------------------------------------------------------------

    private static Article NewArticle(string title) => new() { Title = title, Bluf = "b", AuthorId = "x", AuthorName = "x" };

    [Fact]
    public async Task ChangeEvents_AreNotPublished_ForARolledBackTransaction_AndArePublishedOnCommit()
    {
        using var db = new TestDb(retrying: false);   // it opens transactions itself
        var heard = new List<Tide.Application.Abstractions.Change>();
        db.Feed.Changed += heard.Add;

        await using (var ctx = db.Context())
        await using (var tx = await ctx.Database.BeginTransactionAsync())
        {
            ctx.Articles.Add(NewArticle("rolled back"));
            await ctx.SaveChangesAsync();
            heard.Should().BeEmpty("the save isn't committed yet");
            await tx.RollbackAsync();
        }
        heard.Should().BeEmpty("a rolled-back save never happened as far as other pages are concerned");

        Guid committed;
        await using (var ctx = db.Context())
        await using (var tx = await ctx.Database.BeginTransactionAsync())
        {
            var a = NewArticle("committed");
            committed = a.Id;
            ctx.Articles.Add(a);
            await ctx.SaveChangesAsync();
            heard.Should().BeEmpty();
            await tx.CommitAsync();
        }
        heard.Select(c => c.Topic).Should().Equal(Tide.Application.Abstractions.ChangeFeed.Article(committed), Tide.Application.Abstractions.ChangeFeed.Articles);
    }

    [Fact]
    public async Task ChangeEvents_AreNotPublished_ForASaveThatFailed_AndAThrowingListenerDoesntFailASave()
    {
        var heard = new List<Tide.Application.Abstractions.Change>();
        _db.Feed.Changed += heard.Add;
        _db.BeforeSave = _ => throw new DbUpdateException("busy");
        var failed = () => _db.Articles().CreateAsync("fails");
        await failed.Should().ThrowAsync<DbUpdateException>();
        heard.Should().BeEmpty();

        _db.BeforeSave = null;
        _db.Feed.Changed += _ => throw new ObjectDisposedException("a page that closed");
        var id = await _db.Articles().CreateAsync("saved");

        (await _db.Articles().GetAsync(id)).Should().NotBeNull();
        heard.Select(c => c.Topic).Should().Equal(Tide.Application.Abstractions.ChangeFeed.Article(id), Tide.Application.Abstractions.ChangeFeed.Articles);
    }

    // --- A2-22: indicators stored before A2-21 keep their old spelling; a one-time, marked pass brings them in line and
    // merges the duplicates that makes, so the sighting lookup and exports meet them again. ------------------------------

    [Fact]
    public async Task CanonicalizeIndicators_RewritesOldSpellings_MergesCollisions_RunsOnce_AndTheLookupFindsThem()
    {
        var a = await Article("Old article");
        var b = await Article("Other article");
        await using (var ctx = _db.Context())
        {
            ctx.ArticleIndicators.AddRange(
                new ArticleIndicator { ArticleId = a, Type = IndicatorType.IpAddress, Value = "010.000.000.001", Note = "from the firewall log" },
                new ArticleIndicator { ArticleId = a, Type = IndicatorType.IpAddress, Value = "10.0.0.1" },
                new ArticleIndicator { ArticleId = a, Type = IndicatorType.Domain, Value = "EVIL.Example.COM" },
                new ArticleIndicator { ArticleId = a, Type = IndicatorType.Url, Value = "https://EVIL.Example.COM/Path" },
                new ArticleIndicator { ArticleId = b, Type = IndicatorType.IpAddress, Value = "010.000.000.001" });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.Context())
            (await Tide.Infrastructure.Persistence.DatabaseInitializer.CanonicalizeIndicatorsAsync(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance))
                .Should().Be(3, "two rows renamed, one duplicate merged");

        await using (var ctx = _db.Context())
        {
            var mine = await ctx.ArticleIndicators.Where(i => i.ArticleId == a).OrderBy(i => i.Type).ThenBy(i => i.Value).ToListAsync();
            mine.Select(i => (i.Type, i.Value, i.Note)).Should().BeEquivalentTo(
            [
                (IndicatorType.IpAddress, "10.0.0.1", "from the firewall log"),   // the canonical row kept, the merged row's note carried over
                (IndicatorType.Domain, "evil.example.com", (string?)null),
                (IndicatorType.Url, "https://EVIL.Example.COM/Path", (string?)null),   // a URL's path is case-sensitive: untouched
            ]);
            (await ctx.ArticleIndicators.SingleAsync(i => i.ArticleId == b)).Value.Should().Be("10.0.0.1");
            (await ctx.Settings.AnyAsync(s => s.Key == Tide.Infrastructure.Persistence.DatabaseInitializer.IndicatorsCanonicalKey)).Should().BeTrue();
            (await Tide.Infrastructure.Persistence.DatabaseInitializer.CanonicalizeIndicatorsAsync(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance))
                .Should().Be(0, "the marker says it's done");
        }

        var lookup = (await _db.Search().SearchAsync("10.0.0.1")).Indicator!;
        lookup.Sightings.Items.Select(s => s.ArticleId).Should().BeEquivalentTo([a, b]);
        (await _db.Search().SearchAsync("evil.example.com")).Indicator!.Sightings.Items.Should().Contain(s => s.ArticleId == a && !s.Related);
    }

    // --- A2-23: a role mapping for a group that differs only in case was added again on SQLite (and would hit the unique
    // index raw on SQL Server); an undefined role value was stored as its number ---------------------------------------

    [Fact]
    public async Task RoleMapping_AGroupThatDiffersOnlyInCase_IsTheSameMapping_AndAnUnknownRoleIsRefused()
    {
        var directory = new FakeRoleDirectory();
        var mappings = new Tide.Application.Admin.RoleMappingService(_db, _db.User, directory);
        await mappings.AddAsync("CORP\\CTI Analysts", AppRole.Analyst);

        await mappings.AddAsync("corp\\cti analysts", AppRole.Analyst);

        (await mappings.ListAsync()).Should().ContainSingle().Which.AdGroup.Should().Be("CORP\\CTI Analysts");
        directory.Invalidated.Should().Be(1, "the second add changed nothing");
        var bad = () => mappings.AddAsync("CORP\\Other", (AppRole)99);
        await bad.Should().ThrowAsync<ArgumentException>();
        await mappings.AddAsync("CORP\\CTI Analysts", AppRole.Admin);   // the same group with another role is a new mapping
        (await mappings.ListAsync()).Should().HaveCount(2);
    }

    private sealed class FakeRoleDirectory : Tide.Application.Abstractions.IRoleDirectory
    {
        public int Invalidated;
        public IReadOnlySet<Permission> PermissionsForRoles(IEnumerable<string> roleNames) => new HashSet<Permission>();
        public bool IsRole(string name) => Enum.TryParse<AppRole>(name, out _);
        public IReadOnlySet<string> RolesForGroups(IEnumerable<string> adGroups) => new HashSet<string>();
        public IReadOnlySet<string> RolesMappedByName => new HashSet<string>();
        public void Invalidate() => Invalidated++;
        public bool IsCurrent => true;
    }

    // --- A5-R-4: a save that matched an old row ignoring case kept its old spelling, which then flowed into hunts and STIX;
    // the matched row now takes the canonical value (Agent 5's acceptance test, with the old rows seeded here) ----------

    [Fact]
    public async Task SavingTheCanonicalSpelling_BesideAnOldRow_KeepsOne_AndReadsAreCanonical()
    {
        var article = await Article("Old spellings");
        await using (var seed = _db.Context())
        {
            seed.ArticleIndicators.AddRange(
                new ArticleIndicator { ArticleId = article, Type = IndicatorType.Domain, Value = "EVIL.Example", Note = "kept" },
                new ArticleIndicator { ArticleId = article, Type = IndicatorType.IpAddress, Value = "010.000.000.001" });
            await seed.SaveChangesAsync();
        }
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);
        form.Indicators.Add(new IndicatorForm { Type = IndicatorType.Domain, Value = "evil.example" });
        form.Indicators.Add(new IndicatorForm { Type = IndicatorType.IpAddress, Value = "10.0.0.1" });

        await _db.Articles().SaveAsync(form);

        await using var ctx = _db.Context();
        var rows = ctx.ArticleIndicators.Where(i => i.ArticleId == article).ToList();
        rows.Count(i => i.Type == IndicatorType.Domain && string.Equals(i.Value, "evil.example", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        rows.Count(i => i.Type == IndicatorType.IpAddress && (i.Value == "10.0.0.1" || i.Value == "010.000.000.001")).Should().Be(1);
        rows.Select(r => r.Value).Should().BeEquivalentTo(["evil.example", "10.0.0.1"], "the matched row took the canonical spelling");
        rows.Single(r => r.Type == IndicatorType.Domain).Note.Should().Be("kept", "the old row was kept and renamed, not replaced");
        var hunt = await _db.Queries().BuildAsync("kql", rows);
        hunt.Should().NotContain("EVIL.Example").And.NotContain("010.000.000.001");
        var set = await _db.Exports().GatherAsync(Tide.Application.Exports.ExportScope.Article, article.ToString(), TlpLevel.Red);
        var stix = System.Text.Encoding.UTF8.GetString(Tide.Application.Exports.Stix.Write(set!, "Team"));
        stix.Should().NotContain("EVIL.Example").And.NotContain("010.000.000.001");
    }

    // --- A2-24: the edition ceilings and recipient lists are read by the team edition's readers and by those who may
    // send, not by anyone signed in --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AppRole.Reader, false, false)]
    [InlineData(AppRole.ActionOwner, true, false)]
    [InlineData(AppRole.Analyst, true, true)]
    [InlineData(AppRole.Admin, true, true)]
    public async Task DistributionSettings_Reads_NeedTheTeamEdition_AndRecipientsNeedToPresent(AppRole role, bool ceilings, bool recipients)
    {
        _db.User.Roles = new HashSet<AppRole> { role };
        var settings = _db.Settings();

        var get = () => settings.GetAsync();
        var list = () => settings.GetRecipientsAsync(Edition.Public);

        if (ceilings) (await get()).Public.Should().Be(TlpLevel.Green);
        else await get.Should().ThrowAsync<Tide.Application.Security.ForbiddenException>();
        if (recipients) (await list()).Should().BeEmpty();
        else await list.Should().ThrowAsync<Tide.Application.Security.ForbiddenException>();
    }

    // --- Sanity: a provider-neutral check that the DateTimeOffset ticks conversion orders and filters on SQLite ---------

    [Fact]
    public async Task FeedItems_OrderAndRangeFilter_ByDateTimeOffset_WorkOnSqlite()
    {
        var source = await Source();
        _db.Http.Set(FeedUrl, Rss(("A", "https://example.com/a", null), ("B", "https://example.com/b", null)));
        await _db.Intake().PollNowAsync(source);
        var now = DateTimeOffset.UtcNow;
        await using var ctx = _db.Context();
        var a = await ctx.FeedItems.SingleAsync(i => i.Title == "A");
        var b = await ctx.FeedItems.SingleAsync(i => i.Title == "B");
        a.PublishedAtUtc = now.AddHours(-2);
        b.PublishedAtUtc = now.AddHours(-1);
        await ctx.SaveChangesAsync();

        var ordered = await ctx.FeedItems.AsNoTracking().OrderByDescending(i => i.PublishedAtUtc ?? i.FetchedAtUtc).Select(i => i.Title).ToListAsync();
        ordered.Should().Equal("B", "A");
        var since = now.AddMinutes(-90);
        (await ctx.FeedItems.AsNoTracking().Where(i => (i.PublishedAtUtc ?? i.FetchedAtUtc) >= since).Select(i => i.Title).ToListAsync()).Should().Equal("B");
    }
}
