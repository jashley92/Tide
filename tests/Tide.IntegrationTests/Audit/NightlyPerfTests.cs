using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Tide.Application.Actions;
using Tide.Application.Intake;
using Tide.Application.Trends;
using Tide.Domain.Enums;
using Tide.TestSupport;
using Xunit.Abstractions;

namespace Tide.IntegrationTests.Audit;

/// <summary>
/// The nightly performance check (<c>.github/workflows/perf.yml</c>), off unless <c>TIDE_PERF</c> is set: each read-only
/// hot page, on the scale harness's data at <see cref="Factor"/> its size.
/// <list type="bullet">
/// <item><b>Tier 2, timings (SQLite):</b> the median of <see cref="Runs"/> warm runs against <c>Golden/timing-budget.json</c>.
/// Over its budget is a warning; over twice it fails (decided 2026-11-21: report, then gate at 2×). A timing is noisy (the
/// same call varied 1.8× between runs), so the gate leaves room and the per-PR query budget catches the deterministic
/// regressions.</item>
/// <item><b>Tier 3, logical reads (SQL Server, with <c>TIDE_TEST_SQL</c>):</b> the pages SQL Server read for each, from
/// <c>sys.dm_exec_query_stats</c> after clearing the plan cache, against <c>Golden/logical-reads.json</c> plus
/// <see cref="ReadsAllowance"/>. Stable where milliseconds aren't, and it shows whether a plan uses its index.</item>
/// </list>
/// Each tier writes a markdown table to <c>TIDE_PERF_OUT</c> (the workflow's summary). <c>TIDE_UPDATE_BUDGET=1</c> writes
/// what it measured as the new budget, as the query budget does.
/// </summary>
public class NightlyPerfTests(ITestOutputHelper output)
{
    public const double Factor = 2;
    public const int Runs = 5;

    /// <summary>How far logical reads may grow past their baseline: a quarter.</summary>
    public const double ReadsAllowance = 0.25;

    private static bool Updating => Environment.GetEnvironmentVariable("TIDE_UPDATE_BUDGET") == "1";

    [PerfFact]
    public async Task Timings_StayWithinTwiceTheirBudget()
    {
        using var db = await SeededAsync(TestDb.Sqlite);
        var measured = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, call) in Pages(db))
        {
            await call();   // warm: the plan, the caches, the JIT
            var times = new List<double>();
            for (var i = 0; i < Runs; i++)
            {
                var sw = Stopwatch.StartNew();
                await call();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            measured[name] = times.Order().ElementAt(Runs / 2);
        }
        if (Updating) { Write("timing-budget.json", measured.Select(m => KeyValuePair.Create(m.Key, Math.Ceiling(m.Value * 1.5 / 10) * 10))); return; }

        var budget = Read("timing-budget.json");
        var rows = new StringBuilder("| Page | Median ms | Budget ms | |\n|---|---:|---:|---|\n");
        var failures = new List<string>();
        foreach (var (name, ms) in measured)
        {
            var limit = budget.GetValueOrDefault(name);
            var verdict = limit <= 0 ? "no budget" : ms > 2 * limit ? "**over 2×: fails**" : ms > limit ? "over budget (warning)" : "ok";
            if (limit > 0 && ms > 2 * limit) failures.Add($"{name}: {ms:N0} ms, over twice its {limit:N0} ms budget");
            rows.Append(CultureInfo.InvariantCulture, $"| {name} | {ms:N0} | {(limit > 0 ? limit.ToString("N0", CultureInfo.InvariantCulture) : "—")} | {verdict} |\n");
        }
        Report("Timings (SQLite, " + Factor + "× data, median of " + Runs + " warm runs)", rows.ToString());
        if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
    }

    [PerfFact(SqlServer = true)]
    public async Task LogicalReads_StayWithinAQuarterOfTheirBaseline()
    {
        using var db = await SeededAsync(TestDb.SqlServer);
        var measured = new SortedDictionary<string, double>(StringComparer.Ordinal);
        await using var admin = new SqlConnection(SqlServerFactAttribute.Server);
        await admin.OpenAsync();
        foreach (var (name, call) in Pages(db))
        {
            await call();   // warm, so the reads are the plan's, not the first compile's
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = "DBCC FREEPROCCACHE WITH NO_INFOMSGS;";
            await cmd.ExecuteNonQueryAsync();
            await call();
            // Every statement run since the cache was cleared: this test's database is the only one at work.
            cmd.CommandText = "SELECT ISNULL(SUM(total_logical_reads), 0) FROM sys.dm_exec_query_stats";
            measured[name] = Convert.ToDouble(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
        if (Updating) { Write("logical-reads.json", measured); return; }

        var baseline = Read("logical-reads.json");
        var rows = new StringBuilder("| Page | Logical reads | Baseline | |\n|---|---:|---:|---|\n");
        var failures = new List<string>();
        foreach (var (name, reads) in measured)
        {
            var was = baseline.GetValueOrDefault(name);
            var limit = Math.Max(was * (1 + ReadsAllowance), was + 20);
            var verdict = was <= 0 ? "no baseline" : reads > limit ? "**over: fails**" : "ok";
            if (was > 0 && reads > limit) failures.Add($"{name}: {reads:N0} logical reads, over its {was:N0} baseline by more than a quarter");
            rows.Append(CultureInfo.InvariantCulture, $"| {name} | {reads:N0} | {(was > 0 ? was.ToString("N0", CultureInfo.InvariantCulture) : "—")} | {verdict} |\n");
        }
        Report("Logical reads (SQL Server, " + Factor + "× data)", rows.ToString());
        if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
    }

    private static async Task<TestDb> SeededAsync(string provider)
    {
        var db = new TestDb(provider: provider, time: new TestClock(QueryBudgetTests.At));
        db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        await ScaleSeed.SeedAsync(db, Factor);
        // The search text built, as the Search index job keeps it: the steady state, not the minutes after an upgrade.
        while (true)
        {
            await using var ctx = db.Context();
            if (await Tide.Application.Search.ArticleSearchIndex.RebuildAsync(ctx, 1000) < 1000) break;
        }
        return db;
    }

    // The query budget's read-only pages (Today and the passes that write are left out: a second run would differ), each
    // read as its page reads it. A page added to the budget belongs here too.
    private static IEnumerable<(string Name, Func<Task> Call)> Pages(TestDb db)
    {
        var day = db.Clock.CurrentBriefDate;
        var intake = db.Intake(time: new TestClock(QueryBudgetTests.At));
        var trends = db.Trends();
        yield return ("Brief.Candidates", async () => await db.Briefs().CandidatesAsync((await db.Briefs().GetAsync(day))!.Id));
        foreach (var status in Enum.GetValues<FeedItemStatus>())
            yield return ($"Triage.Inbox({status})", async () =>
            {
                await intake.InboxPageAsync(new InboxFilter(status));
                if (status == FeedItemStatus.New) await intake.MissedAsync();
                await intake.TallyAsync();
                await intake.PolledSourcesAsync();
            });
        yield return ("Actions.Board", () => db.Actions().BoardAsync(new ActionFilter()));
        yield return ("Actions.List(Open)", () => db.Actions().ListAsync(new ActionFilter()));
        yield return ("Actions.List(Mine)", () => db.Actions().ListAsync(new ActionFilter(ActionView.Mine)));
        yield return ("Actions.List(Closed)", () => db.Actions().ListAsync(new ActionFilter(ActionView.Closed)));
        yield return ("Search(text)", () => db.Search().SearchAsync("ransomware vendor"));
        yield return ("Search(CVE)", () => db.Search().SearchAsync("CVE-2025-0042"));
        yield return ("Search(indicator)", () => db.Search().SearchAsync("10.0.7.9"));
        yield return ("Trends(6 months)", () => trends.ReportAsync(trends.DefaultEnd(PeriodKind.Month)));
        yield return ("Trends(24 months)", () => trends.ReportAsync(trends.DefaultEnd(PeriodKind.Month), span: 24));
        yield return ("Reports(month)", () => new ReportService(db.Trends(), db.Metrics(), db.Clock, db, db.User).BuildAsync(trends.DefaultEnd(PeriodKind.Month)));
    }

    private static Dictionary<string, double> Read(string file) =>
        JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", file))) ?? [];

    // Into the source folder, as TIDE_UPDATE_GOLDEN does.
    private static void Write(string file, IEnumerable<KeyValuePair<string, double>> values) =>
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Golden", file),
            new JsonObject(values.Select(v => KeyValuePair.Create(v.Key, (JsonNode?)v.Value)))
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n");

    private void Report(string title, string table)
    {
        output.WriteLine(title + "\n" + table);
        if (Environment.GetEnvironmentVariable("TIDE_PERF_OUT") is { Length: > 0 } path)
            File.AppendAllText(path, $"### {title}\n\n{table}\n");
    }
}

/// <summary>A nightly performance test: skipped unless <c>TIDE_PERF</c> is set (and, for SQL Server, <c>TIDE_TEST_SQL</c>).</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TIDE_PERF") is not { Length: > 0 })
            Skip = "Set TIDE_PERF to run the nightly performance check (.github/workflows/perf.yml).";
    }

    /// <summary>Needs SQL Server too.</summary>
    public bool SqlServer
    {
        get => false;
        init { if (value && Skip is null && SqlServerFactAttribute.Server is null) Skip = $"Set {SqlServerFactAttribute.Variable} too."; }
    }
}
