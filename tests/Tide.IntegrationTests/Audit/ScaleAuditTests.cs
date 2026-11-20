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
using Tide.Domain.Common;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Xunit.Abstractions;

namespace Tide.IntegrationTests.Audit;

/// <summary>
/// The backend audit's scale harness: seeds a few years of data into the in-memory database and times the hot service
/// calls, counting the SQL each one runs. Off unless <c>TIDE_SCALE</c> is set (it takes a minute or two); <c>TIDE_SCALE_F</c>
/// scales the data (0.5 for half, 2 for double; 1 by default). It writes a timings table and every command's text to the
/// directory named by <c>TIDE_SCALE_OUT</c> (or the temp directory). Its deterministic sibling, run on every PR, is
/// <see cref="QueryBudgetTests"/>.
/// </summary>
public class ScaleAuditTests(ITestOutputHelper output) : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private Guid _storyId;

    private sealed record Timing(string Call, long Ms, int Queries, string Note);

    [Fact]
    public async Task HotCalls_AtScale()
    {
        if (Environment.GetEnvironmentVariable("TIDE_SCALE") is not { Length: > 0 }) return;
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var outDir = Environment.GetEnvironmentVariable("TIDE_SCALE_OUT") is { Length: > 0 } d ? d : Path.GetTempPath();
        Directory.CreateDirectory(outDir);

        var seed = Stopwatch.StartNew();
        var factor = Environment.GetEnvironmentVariable("TIDE_SCALE_F") is { Length: > 0 } f ? double.Parse(f, CultureInfo.InvariantCulture) : 1;
        _storyId = (await ScaleSeed.SeedAsync(_db, factor)).StoryId;
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
        await Time("Search(\"ransomware vendor\")", async () => (await _db.Search().SearchAsync("ransomware vendor")).Articles is var hits && hits.AtLeast ? $"{hits.Total}+ articles" : $"{hits.Total} articles");
        // A letter beyond ASCII takes SQLite's search through .NET (SqliteLike), so it is timed apart.
        await Time("Search(\"rançongiciel vendor\")", async () => (await _db.Search().SearchAsync("rançongiciel vendor")).Articles is var hits && hits.AtLeast ? $"{hits.Total}+ articles" : $"{hits.Total} articles");
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
}
