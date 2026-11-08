using System.Diagnostics;
using System.Globalization;
using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Exports;
using Tide.Application.Intake;
using Tide.Application.Trends;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Xunit.Abstractions;

namespace Tide.IntegrationTests.Audit;

/// <summary>
/// The backend audit's scale harness: seeds a few years of data into the in-memory database and times the hot service
/// calls, counting the SQL each one runs. Off unless <c>TIDE_SCALE</c> is set (it takes a minute or two); it writes a
/// timings table and every command's text to the directory named by <c>TIDE_SCALE_OUT</c> (or the temp directory).
/// </summary>
public class ScaleAuditTests(ITestOutputHelper output) : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed record Timing(string Call, long Ms, int Queries, string Note);

    [Fact]
    public async Task HotCalls_AtScale()
    {
        if (Environment.GetEnvironmentVariable("TIDE_SCALE") is not { Length: > 0 }) return;
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var outDir = Environment.GetEnvironmentVariable("TIDE_SCALE_OUT") is { Length: > 0 } d ? d : Path.GetTempPath();
        Directory.CreateDirectory(outDir);

        var seed = Stopwatch.StartNew();
        await SeedAsync();
        output.WriteLine($"Seeded in {seed.ElapsedMilliseconds} ms");

        var today = _db.Clock.Today;
        var timings = new List<Timing>();
        var sql = new StringBuilder();
        async Task Time(string name, Func<Task<string>> call)
        {
            _db.Commands.Record = true;
            _db.Commands.Reset();
            var sw = Stopwatch.StartNew();
            var note = await call();
            sw.Stop();
            var queries = _db.Commands.Count;
            timings.Add(new Timing(name, sw.ElapsedMilliseconds, queries, note));
            sql.Append("=== ").Append(name).Append(" (").Append(queries).Append(" commands)\n");
            foreach (var s in _db.Commands.Sql) sql.Append(s).Append("\n---\n");
            _db.Commands.Record = false;
        }

        // Actions
        await Time("Actions.BoardAsync(Open)", async () => $"{(await _db.Actions().BoardAsync(new ActionFilter())).Cards.Count} cards");
        await Time("Actions.ListAsync(Open)", async () => $"{(await _db.Actions().ListAsync(new ActionFilter())).Count} rows");
        await Time("Actions.ListAsync(Closed)", async () => $"{(await _db.Actions().ListAsync(new ActionFilter(ActionView.Closed))).Count} rows");
        await Time("Actions.ListAsync(Mine)", async () => $"{(await _db.Actions().ListAsync(new ActionFilter(ActionView.Mine))).Count} rows");
        await Time("Actions.WorkloadAsync", async () => $"{(await _db.Actions().WorkloadAsync()).Count} teams");
        await Time("Actions.GetAsync(1)", async () => $"{(await _db.Actions().GetAsync(1))?.Action.Comments.Count} comments");

        // Articles
        await Time("Articles.PageAsync(all, 50)", async () => $"{(await _db.Articles().PageAsync(new ArticleFilter(), ArticleSort.Written, true, 50)).Total} total");
        await Time("Articles.PageAsync(search)", async () => $"{(await _db.Articles().PageAsync(new ArticleFilter { Search = "ransomware vendor" }, ArticleSort.Written, true, 50)).Total} total");
        await Time("Articles.PageAsync(tag+dates)", async () => $"{(await _db.Articles().PageAsync(new ArticleFilter { Tag = "ransomware", From = today.AddMonths(-6), To = today }, ArticleSort.Written, true, 50)).Total} total");
        await Time("Articles.PageAsync(tactic)", async () => $"{(await _db.Articles().PageAsync(new ArticleFilter { Tactic = MitreTactic.InitialAccess }, ArticleSort.Title, false, 50)).Total} total");
        await Time("Articles.ListAsync(200)", async () => $"{(await _db.Articles().ListAsync()).Count} rows");

        // Search
        await Time("Search(\"ransomware vendor\")", async () => $"{(await _db.Search().SearchAsync("ransomware vendor")).Articles.Total} articles");
        await Time("Search(indicator)", async () => $"{(await _db.Search().SearchAsync("10.0.7.9")).Indicator?.Sightings.Total} sightings");
        await Time("Search(CVE)", async () => $"{(await _db.Search().SearchAsync("CVE-2025-0042")).Cve?.Articles} articles");

        // Trends
        var trends = _db.Trends();
        await Time("Trends.ReportAsync(6 months)", async () => $"{(await trends.ReportAsync(trends.DefaultEnd(PeriodKind.Month))).Articles[0].Current} in last month");
        await Time("Trends.ReportAsync(24 months)", async () => $"{(await trends.ReportAsync(trends.DefaultEnd(PeriodKind.Month), span: 24)).Tags.Count} tag rows");
        await Time("Trends.ReportAsync(5 quarters)", async () => $"{(await trends.ReportAsync(trends.DefaultEnd(PeriodKind.Quarter))).Attack.Count} attack rows");
        await Time("Trends.ReportAsync(12 quarters)", async () => $"{(await trends.ReportAsync(trends.DefaultEnd(PeriodKind.Quarter), span: 12)).Actions.Count} action rows");

        // The article list's date filter must count exactly what a Trends line counted (the contract behind "open a count").
        var report = await trends.ReportAsync(trends.DefaultEnd(PeriodKind.Month), span: 24);
        foreach (var row in report.Articles.Concat(report.Tags.Take(4)).Concat(report.Attack.Take(3)))
            foreach (var at in new[] { 0, report.Periods.Count - 2, report.Periods.Count - 1 })
                if (report.ArticlesFilter(row, report.Periods[at]) is { } filter)
                    (await _db.Articles().PageAsync(filter, ArticleSort.Written, true, 1)).Total.Should().Be(row.Count(at),
                        $"the list's filter for {row.Key} in {report.Periods[at]} must count what the trend line counted");

        // Intake
        foreach (var status in Enum.GetValues<FeedItemStatus>())
            await Time($"Intake.InboxAsync({status})", async () => $"{(await _db.Intake().InboxAsync(new InboxFilter(status))).Count} rows");
        await Time("Intake.InboxAsync(New, search)", async () => $"{(await _db.Intake().InboxAsync(new InboxFilter(Search: "CVE-2025"))).Count} rows");
        await Time("Intake.CountsAsync", async () => $"{(await _db.Intake().CountsAsync()).Count} statuses");

        // Briefs
        await Time("Briefs.GetAsync(today)", async () => $"{(await _db.Briefs().GetAsync(today))?.Items.Count} items");
        await Time("Briefs.ListAsync(60)", async () => $"{(await _db.Briefs().ListAsync()).Count} rows");
        await Time("Briefs.CandidatesAsync", async () => $"{(await _db.Briefs().CandidatesAsync((await _db.Briefs().GetAsync(today))!.Id)).Count} candidates");

        // Metrics
        await Time("Metrics.HistoryAsync(1 year)", async () => $"{(await _db.Metrics().HistoryAsync(today.AddYears(-1), today)).Values.Count} values");
        await Time("Metrics.GetDayAsync(today)", async () => $"{(await _db.Metrics().GetDayAsync(today)).Groups.Count} groups");

        // Rotation
        await Time("Rotation.CalendarAsync(1 year)", async () => $"{(await _db.Rotation().CalendarAsync(today, today.AddDays(365))).Count} days");
        await Time("Rotation.UpcomingAsync", async () => $"{(await _db.Rotation().UpcomingAsync()).MyRuns.Count} runs");

        // Reminders
        var reminders = _db.Reminders();
        await Time("Reminders.DueSoon", async () => $"{await reminders.RunAsync(ReminderKind.DueSoon)} emails");
        await Time("Reminders.Overdue", async () => $"{await reminders.RunAsync(ReminderKind.Overdue)} emails");
        await Time("Reminders.Digest", async () => $"{await reminders.RunAsync(ReminderKind.Digest)} emails");

        // Exports
        await Time("Export.GatherAsync(brief)", async () => $"{(await _db.Exports().GatherAsync(ExportScope.Brief, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), TlpLevel.AmberStrict))?.Articles.Count} articles");
        await Time("Export.GatherAsync(story)", async () => $"{(await _db.Exports().GatherAsync(ExportScope.Story, _storyId.ToString(), TlpLevel.AmberStrict))?.Articles.Count} articles");

        var table = new StringBuilder("| Call | ms | SQL commands | Note |\n|---|---:|---:|---|\n");
        foreach (var t in timings) table.Append(CultureInfo.InvariantCulture, $"| {t.Call} | {t.Ms} | {t.Queries} | {t.Note} |\n");
        output.WriteLine(table.ToString());
        await File.WriteAllTextAsync(Path.Combine(outDir, "scale-timings.md"), table.ToString());
        await File.WriteAllTextAsync(Path.Combine(outDir, "scale-sql.txt"), sql.ToString());
    }

    private Guid _storyId;

    // About two years of a busy team: 10k articles (half briefed), 2k briefs, 5k actions with comments, events and
    // checklists, 50k feed items in every state, 2k metric values, a 3-person rotation.
    private async Task SeedAsync()
    {
        await _db.Scorecard().SeedStarterSetAsync();
        var today = _db.Clock.Today;
        var rnd = new Random(42);
        var now = DateTimeOffset.UtcNow;

        List<Guid> tagIds;
        await using (var ctx = _db.PlainContext())
        {
            tagIds = await ctx.ScorecardTags.OrderBy(t => t.Key).Select(t => t.Id).ToListAsync();
            var story = new Story { Name = "Edge VPN exploitation", CreatedAtUtc = now };
            _storyId = story.Id;
            ctx.Stories.Add(story);
            await ctx.SaveChangesAsync();
        }
        var techniques = new[] { "T1190", "T1566", "T1566.002", "T1059", "T1059.001", "T1133", "T1486", "T1078", "T1021", "T1041" };

        // Briefs: one per business day going back ~8 years (2,000 of them), each carrying five articles.
        var briefDays = new List<DateOnly>();
        for (var d = today; briefDays.Count < 2000; d = d.AddDays(-1))
            if (Org.IsBusinessDay(d)) briefDays.Add(d);

        var articleIds = new List<Guid>();
        for (var batch = 0; batch < 10; batch++)
        {
            await using var ctx = _db.PlainContext();
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var i = 0; i < 1000; i++)
            {
                var n = batch * 1000 + i;
                var briefed = n < 10000 / 2;
                var day = briefed ? briefDays[n / 5 % briefDays.Count] : today.AddDays(-rnd.Next(0, 730));
                var a = new Article
                {
                    Title = $"Article {n}: {(n % 3 == 0 ? "ransomware gang hits vendor" : n % 3 == 1 ? "phishing wave" : "zero-day exploited")}",
                    Bluf = "Bottom line " + n + (n % 7 == 0 ? " ransomware vendor" : ""), SourceName = "Vendor " + n % 50, SourceUrl = $"https://e.com/{n}",
                    Tlp = (TlpLevel)(n % 4), Classification = n % 2 == 0 ? Classification.Threat : Classification.Informational,
                    Priority = n % 2 == 0 ? (Priority)(n % 4) : null, Status = briefed ? ArticleStatus.Presented : n % 10 == 0 ? ArticleStatus.Draft : ArticleStatus.Ready,
                    AuthorId = "S-1-5-21-TEST-1", AuthorName = "Test Analyst",
                    CreatedAtUtc = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero), StoryId = n % 500 == 0 ? _storyId : null,
                };
                a.Bullets.Add(new ArticleBullet { Text = "Bullet one of " + n, SortOrder = 0 });
                a.Bullets.Add(new ArticleBullet { Text = "Bullet two **bold** of " + n, SortOrder = 1 });
                a.Indicators.Add(new ArticleIndicator { Type = IndicatorType.IpAddress, Value = $"10.{n / 256 % 256}.{n % 256}.9" });
                a.Indicators.Add(new ArticleIndicator { Type = IndicatorType.Domain, Value = $"c2-{n}.example.net" });
                a.Cves.Add(new ArticleCve { CveId = $"CVE-2025-{n % 300:0000}" });
                a.Tags.Add(new ArticleTag { TagId = tagIds[n % tagIds.Count] });
                if (n % 2 == 0) a.Tags.Add(new ArticleTag { TagId = tagIds[(n + 3) % tagIds.Count] });
                a.Techniques.Add(new ArticleTechnique { TechniqueId = techniques[n % techniques.Length] });
                if (n % 3 == 0) a.Techniques.Add(new ArticleTechnique { TechniqueId = techniques[(n + 1) % techniques.Length] });
                ctx.Articles.Add(a);
                articleIds.Add(a.Id);
            }
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.PlainContext())
        {
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var b = 0; b < briefDays.Count; b++)
            {
                var brief = new Brief
                {
                    BriefDate = briefDays[b], AnalystId = "S-1-5-21-TEST-1", AnalystName = "Test Analyst", Status = b == 0 ? BriefStatus.Draft : BriefStatus.Sent,
                    CreatedAtUtc = now, Intro = "Opening note",
                };
                for (var k = 0; k < 5; k++) brief.Items.Add(new BriefItem { ArticleId = articleIds[b * 5 + k], SortOrder = k });
                ctx.Briefs.Add(brief);
            }
            await ctx.SaveChangesAsync();
        }

        Guid teamA, teamB;
        await using (var ctx = _db.PlainContext())
        {
            var a = new Team { Key = "cyber-defense", Name = "Cyber Defense", CreatedAtUtc = now };
            var b = new Team { Key = "vuln-mgmt", Name = "Vulnerability Management", CreatedAtUtc = now, Email = "vm@example.org" };
            a.Members.Add(new TeamMember { UserId = "S-1-5-21-TEST-1", DisplayName = "Test Analyst", AddedAtUtc = now });
            ctx.Teams.AddRange(a, b);
            ctx.Users.Add(new AppUser { Sid = "S-1-5-21-TEST-1", DisplayName = "Test Analyst", Email = "test@example.com", LastSeenUtc = now });
            await ctx.SaveChangesAsync();
            (teamA, teamB) = (a.Id, b.Id);
        }

        for (var batch = 0; batch < 5; batch++)
        {
            await using var ctx = _db.PlainContext();
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var i = 0; i < 1000; i++)
            {
                var n = batch * 1000 + i;
                var raised = today.AddDays(-rnd.Next(0, 730));
                var open = n % 5 == 0;
                var act = new ActionItem
                {
                    Number = n + 1, Title = "Action " + n, Kind = (ActionKind)(n % 5), Priority = (Priority)(n % 4), TeamId = n % 2 == 0 ? teamA : teamB,
                    ArticleId = articleIds[n % articleIds.Count], Status = open ? (ActionStatus)(n % 3) : ActionStatus.Done,
                    Outcome = open ? ActionOutcome.None : ActionOutcome.NoHits, DueDate = raised.AddDays(n % 9), AssigneeId = n % 4 == 0 ? "S-1-5-21-TEST-1" : null,
                    AssigneeName = n % 4 == 0 ? "Test Analyst" : null, ClosedDate = open ? null : raised.AddDays(5), Description = "Do the thing " + n,
                    CreatedAtUtc = new DateTimeOffset(raised.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero),
                    ClosedAtUtc = open ? null : new DateTimeOffset(raised.AddDays(5).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero),
                };
                act.Comments.Add(new ActionComment { Text = "Looked at it", AuthorId = "S-1-5-21-TEST-1", AuthorName = "Test Analyst", AtUtc = now });
                act.Comments.Add(new ActionComment { Text = "Ransomware vendor note " + n, AuthorId = "S-1-5-21-TEST-1", AuthorName = "Test Analyst", AtUtc = now });
                for (var e = 0; e < 3; e++) act.Events.Add(new ActionEvent { Text = "Event " + e, ByName = "Test Analyst", AtUtc = now.AddMinutes(e) });
                for (var c = 0; c < 3; c++) act.Checklist.Add(new ActionChecklistItem { Text = "Step " + c, SortOrder = c, IsDone = c == 0 });
                if (n % 10 == 0) act.Hunts.Add(new HuntQuery { Dialect = "kql", Text = "x", GeneratedText = "x", Result = HuntResult.Hits, RunAtUtc = now, CreatedAtUtc = now, CreatedByName = "T" });
                ctx.Actions.Add(act);
            }
            await ctx.SaveChangesAsync();
        }

        Guid sourceId;
        await using (var ctx = _db.PlainContext())
        {
            var s = new Source { Name = "Big feed", Url = "https://feed.example/rss", CreatedAtUtc = now };
            ctx.Sources.Add(s);
            await ctx.SaveChangesAsync();
            sourceId = s.Id;
        }
        for (var batch = 0; batch < 10; batch++)
        {
            await using var ctx = _db.PlainContext();
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var i = 0; i < 5000; i++)
            {
                var n = batch * 5000 + i;
                var status = (n % 10) switch { 0 => FeedItemStatus.New, 1 => FeedItemStatus.Shortlisted, 2 or 3 => FeedItemStatus.Promoted, 4 => FeedItemStatus.Duplicate, _ => FeedItemStatus.Dismissed };
                var url = $"https://news.example/{n}";
                ctx.FeedItems.Add(new FeedItem
                {
                    SourceId = sourceId, Url = url, UrlHash = UrlCanonicalizer.Hash(url), Title = $"Feed item {n} about CVE-2025-{n % 300:0000}",
                    TitleKey = TitleMatch.Key($"Feed item {n} about CVE-2025-{n % 300:0000}"), Excerpt = new string('x', 1500), Cves = $"CVE-2025-{n % 300:0000}",
                    PublishedAtUtc = now.AddHours(-n), FetchedAtUtc = now.AddHours(-n), Status = status,
                    ArticleId = status == FeedItemStatus.Promoted ? articleIds[n % articleIds.Count] : null,
                    StatusAtUtc = status == FeedItemStatus.New ? null : now, StatusByName = status == FeedItemStatus.New ? null : "Test Analyst",
                });
            }
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.PlainContext())
        {
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            var group = new MetricGroup { Key = "queue", Name = "Alert queue", CreatedAtUtc = now };
            var metrics = Enumerable.Range(0, 4).Select(i => new MetricDefinition { Key = "m" + i, Name = "Metric " + i, GroupId = group.Id, SortOrder = i, CreatedAtUtc = now }).ToList();
            ctx.MetricGroups.Add(group);
            ctx.MetricDefinitions.AddRange(metrics);
            foreach (var m in metrics)
                for (var k = 0; k < 500; k++)
                    ctx.MetricValues.Add(new MetricValue { DefinitionId = m.Id, BriefDate = briefDays[k], Value = k % 37, EnteredById = "x", EnteredByName = "x", CreatedAtUtc = now });
            var roster = new[] { ("S-1-5-21-TEST-1", "Test Analyst"), ("dev:robin", "Robin"), ("dev:sam", "Sam") };
            for (var i = 0; i < roster.Length; i++) ctx.RotationMembers.Add(new RotationMember { UserId = roster[i].Item1, DisplayName = roster[i].Item2, SortOrder = i });
            var monday = Tide.Application.Rotation.RotationService.WeekStart(today.AddDays(-365));
            for (var w = 0; w < 110; w++)
                ctx.RotationWeeks.Add(new RotationWeek { WeekStart = monday.AddDays(7 * w), AnalystId = roster[w % 3].Item1, AnalystName = roster[w % 3].Item2, UpdatedAtUtc = now, UpdatedBy = "x" });
            await ctx.SaveChangesAsync();
        }
    }

    private static class Org
    {
        public static bool IsBusinessDay(DateOnly d) => Tide.Application.Org.WorkingDays.Weekdays.IsWorkingDay(d);
    }
}
