using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Application.Api;
using Tide.Application.Intake;
using Tide.Application.Trends;
using Tide.Domain.Enums;
using Tide.TestSupport;
using Tide.Web.Components.Shared;
using Xunit.Abstractions;

namespace Tide.IntegrationTests.Audit;

/// <summary>
/// The query budget: each hot page or pass, run against the scale harness's data (<see cref="ScaleSeed"/>, at
/// <see cref="Factor"/> of its size) on a fixed clock, must make no more reads and saves, send no more parameters and read
/// no more rows than <c>Golden/query-budget.json</c> allows. Unlike a timing these are the same on every run and every
/// machine, so a PR that adds a query or a save per row, an IN list that grows with the data, or a read of a whole table to
/// show a page of it fails here, with the number that grew named. The commands that write are held more loosely: SQLite
/// sends a save a row at a time and SQL Server batches it, so they count rows on one and batches on the other. To accept a deliberate change, run the test with
/// <c>TIDE_UPDATE_BUDGET=1</c> (it writes what it measured into the source folder) and commit the budget with the change.
/// </summary>
public class QueryBudgetTests(ITestOutputHelper output)
{
    /// <summary>The share of the scale harness's data seeded: big enough that a read growing with the data shows, small
    /// enough to seed in seconds.</summary>
    public const double Factor = 0.2;

    /// <summary>11:00 in New York on a Wednesday: a working day with working days either side, after the meeting.</summary>
    public static readonly DateTimeOffset At = new(2026, 6, 17, 15, 0, 0, TimeSpan.Zero);

    private const string File = "query-budget.json";

    /// <summary>What an operation cost: round trips that read, SaveChanges calls, commands that wrote (about the rows
    /// written, on SQLite), parameters sent, the most in one command, and rows read back.</summary>
    public sealed record Usage(int Reads, int Saves, int RowsWritten, int Parameters, int MaxParameters, int RowsRead);

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task HotPaths_StayWithinTheirQueryBudget(string provider)
    {
        var clock = new TestClock(At);
        using var db = new TestDb(provider: provider, time: clock);
        db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        await ScaleSeed.SeedAsync(db, Factor);
        // A receiver added two days ago, so the webhook pass has the actions raised since to post.
        await db.Webhooks(new TestClock(At.AddDays(-2))).AddAsync("https://soar.example.org/hooks/tide", WebhookService.Events);

        var measured = await MeasureAsync(db, clock);

        var table = new StringBuilder("| Operation | " + string.Join(" | ", Metrics.Select(m => m.Key)) + " |\n|---|"
            + string.Concat(Metrics.Select(_ => "---:|")) + "\n");
        foreach (var (name, u) in measured)
            table.Append(CultureInfo.InvariantCulture, $"| {name} | {string.Join(" | ", Metrics.Select(m => m.Of(u)))} |\n");
        output.WriteLine(table.ToString());

        if (Environment.GetEnvironmentVariable("TIDE_UPDATE_BUDGET") == "1")
        {
            // The source folder's file, read and written in turn by each provider's row, so both rows of one run land.
            var source = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Golden", File);
            var update = JsonNode.Parse(System.IO.File.ReadAllText(source))!.AsObject();
            update[provider] = Write(measured);
            System.IO.File.WriteAllText(source, update.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n");
            return;
        }
        var budget = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", File)))!.AsObject();
        var (failures, notes) = Compare(provider, Read(budget[provider]?.AsObject()), measured);
        foreach (var note in notes) output.WriteLine(note);
        if (failures.Count > 0) Assert.Fail(Explain(provider, failures));
    }

    // Each operation as its page or pass runs it, in a fixed order (the passes write, and what they write is read after).
    private static async Task<SortedDictionary<string, Usage>> MeasureAsync(TestDb db, TestClock clock)
    {
        var measured = new SortedDictionary<string, Usage>(StringComparer.Ordinal);
        async Task Measure(string name, Func<Task> call)
        {
            db.Commands.Reset();
            await call();
            var c = db.Commands;
            measured[name] = new Usage(c.Reads, c.Saves, c.Writes, c.Parameters, c.MaxParameters, c.Rows);
        }
        var day = db.Clock.CurrentBriefDate;
        var intake = db.Intake(time: clock);

        // Today: what the start page reads for an analyst, but for the candidates (Brief.Candidates).
        await Measure("Today", async () =>
        {
            await db.Settings().GetAsync();
            await db.Rotation().UpcomingAsync();
            var brief = await db.Briefs().GetAsync(day) ?? throw new InvalidOperationException("The seed starts today's brief.");
            await db.Rotation().CalendarAsync(day, day);
            await db.Actions().ListAsync(new ActionFilter(ActionView.Mine));
            await db.Actions().TeamQueueAsync();
            await intake.InboxAsync(new InboxFilter(FeedItemStatus.Shortlisted, Take: AddArticlesDialog.MaxShortlist));
            await db.Briefs().EditionActionsAsync(brief);
            await db.Meetings(clock).GetAsync(brief.Id);
            if (await intake.NewCountAsync() > 0) await intake.NewKevCountAsync();
            await db.Distribution().SendsAsync(day);
        });
        await Measure("Brief.Candidates", async () => await db.Briefs().CandidatesAsync((await db.Briefs().GetAsync(day))!.Id));

        // Triage: each tab as the page reads it (the page of items, the missed reports on New, the tab counts, the sources).
        foreach (var status in Enum.GetValues<FeedItemStatus>())
            await Measure($"Triage.Inbox({status})", async () =>
            {
                await intake.InboxPageAsync(new InboxFilter(status));
                if (status == FeedItemStatus.New) await intake.MissedAsync();
                await intake.TallyAsync();
                await intake.PolledSourcesAsync();
            });

        await Measure("Actions.Board", () => db.Actions().BoardAsync(new ActionFilter()));
        await Measure("Actions.List(Open)", () => db.Actions().ListAsync(new ActionFilter()));
        await Measure("Actions.List(Mine)", () => db.Actions().ListAsync(new ActionFilter(ActionView.Mine)));
        await Measure("Actions.List(Closed)", () => db.Actions().ListAsync(new ActionFilter(ActionView.Closed)));

        await Measure("Search(text)", () => db.Search().SearchAsync("ransomware vendor"));
        await Measure("Search(CVE)", () => db.Search().SearchAsync("CVE-2025-0042"));
        await Measure("Search(indicator)", () => db.Search().SearchAsync("10.0.7.9"));

        var trends = db.Trends();
        await Measure("Trends(6 months)", () => trends.ReportAsync(trends.DefaultEnd(PeriodKind.Month)));
        await Measure("Trends(24 months)", () => trends.ReportAsync(trends.DefaultEnd(PeriodKind.Month), span: 24));
        await Measure("Reports(month)", () => new ReportService(db.Trends(), db.Metrics(), db.Clock, db, db.User).BuildAsync(trends.DefaultEnd(PeriodKind.Month)));

        var reminders = db.Reminders();
        foreach (var kind in Enum.GetValues<ReminderKind>())
            await Measure($"Reminders({kind})", () => reminders.RunAsync(kind));
        await Measure("Webhooks.Deliver", () => db.Webhooks(clock).DeliverDueAsync());
        return measured;
    }

    /// <summary>How far a number may grow past its budget before the test fails: a tenth, and at least a read or save, a
    /// few parameters or rows, so a small deliberate change needn't touch the budget and anything that scales with the data
    /// does. Rows written get half again and at least 20: it's the one number that differs by provider (rows on SQLite,
    /// batches on SQL Server), and a save per row already fails on saves.</summary>
    public static int Allowance(string metric, int budget) => metric switch
    {
        "rowsWritten" => Math.Max(20, (int)Math.Ceiling(budget * 0.5)),
        _ => Math.Max(metric is "reads" or "saves" ? 1 : metric == "rowsRead" ? 10 : 4, (int)Math.Ceiling(budget * 0.1)),
    };

    private static readonly (string Key, string Words, Func<Usage, int> Of)[] Metrics =
    [
        ("reads", "reads (queries)", u => u.Reads),
        ("saves", "saves (SaveChanges calls)", u => u.Saves),
        ("rowsWritten", "rows written (write commands)", u => u.RowsWritten),
        ("parameters", "parameters sent", u => u.Parameters),
        ("maxParameters", "parameters in one command", u => u.MaxParameters),
        ("rowsRead", "rows read", u => u.RowsRead),
    ];

    /// <summary>What grew past its budget (failures), and what fell well below it (notes: the budget could be tightened).</summary>
    public static (List<string> Failures, List<string> Notes) Compare(string provider, IReadOnlyDictionary<string, Usage>? budget,
        IReadOnlyDictionary<string, Usage> measured)
    {
        List<string> failures = [], notes = [];
        if (budget is null)
        {
            failures.Add($"There's no {provider} budget yet.");
            return (failures, notes);
        }
        foreach (var (name, now) in measured)
        {
            if (!budget.TryGetValue(name, out var was))
            {
                failures.Add($"{name} is new: it has no budget.");
                continue;
            }
            foreach (var (key, words, of) in Metrics)
            {
                var (b, n) = (of(was), of(now));
                if (n > b + Allowance(key, b))
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{name}: {words} grew from {b:N0} to {n:N0} (allowed up to {b + Allowance(key, b):N0})."));
                else if (n < b - Allowance(key, b))
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: {words} fell from {b:N0} to {n:N0}; the budget can be tightened."));
            }
        }
        failures.AddRange(budget.Keys.Except(measured.Keys).Select(name => $"{name} has a budget but is no longer measured."));
        return (failures, notes);
    }

    private static string Explain(string provider, List<string> failures) =>
        $"""
         The hot paths went over their query budget (Golden/{File}, {provider}):
           {string.Join("\n   ", failures)}
         If the change is deliberate (a new query that earns its place, data the page now needs), accept it with
           TIDE_UPDATE_BUDGET=1 dotnet test tests/Tide.IntegrationTests -c Release --filter "FullyQualifiedName~QueryBudgetTests"
         (and with TIDE_TEST_SQL set for the SqlServer budget), then commit the budget with the change and say why in the PR.
         """;

    private static Dictionary<string, Usage>? Read(JsonObject? section) =>
        section?.ToDictionary(p => p.Key, p =>
        {
            int Of(string key) => (int)p.Value![key]!;
            return new Usage(Of("reads"), Of("saves"), Of("rowsWritten"), Of("parameters"), Of("maxParameters"), Of("rowsRead"));
        });

    private static JsonObject Write(SortedDictionary<string, Usage> measured)
    {
        var section = new JsonObject();
        foreach (var (name, u) in measured)
        {
            var row = new JsonObject();
            foreach (var (key, _, of) in Metrics) row[key] = of(u);
            section[name] = row;
        }
        return section;
    }

    [Fact]
    public void ABudgetFailure_NamesTheNumberThatGrew_AndLetsASmallChangeThrough_AndRowsWrittenMore()
    {
        var budget = new Dictionary<string, Usage> { ["Actions.Board"] = new(5, 1, 40, 20, 6, 300), ["Gone"] = new(1, 0, 0, 0, 0, 1) };
        var measured = new Dictionary<string, Usage> { ["Actions.Board"] = new(6, 3, 59, 22, 6, 900), ["Fresh"] = new(1, 0, 0, 0, 0, 1) };

        var (failures, _) = Compare(TestDb.Sqlite, budget, measured);

        failures.Should().BeEquivalentTo(
            "Actions.Board: saves (SaveChanges calls) grew from 1 to 3 (allowed up to 2).",
            "Actions.Board: rows read grew from 300 to 900 (allowed up to 330).",
            "Fresh is new: it has no budget.",
            "Gone has a budget but is no longer measured.");
        Compare(TestDb.SqlServer, null, measured).Failures.Should().ContainSingle().Which.Should().Contain("no SqlServer budget");
    }
}
