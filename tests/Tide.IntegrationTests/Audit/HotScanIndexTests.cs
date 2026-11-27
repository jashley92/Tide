using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests.Audit;

/// <summary>
/// The hot reads that must be served by an index, checked on SQLite's plan for the very SQL the services send: the
/// sidebar's new-item count (every navigation) and Triage's tab tally read the (Status, FetchedAtUtc, SourceId) index
/// alone, never the wide feed-item rows; Workload's "open or closed this week" seeks both sides of its OR rather than
/// scanning every action ever raised. A timing would be too noisy to fail on; a plan that loses its index isn't.
/// </summary>
public partial class HotScanIndexTests : IDisposable
{
    private readonly TestDb _db = new();

    public HotScanIndexTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task TheNewItemCount_ReadsTheStatusIndexAlone()
    {
        var plan = await PlanOfAsync(() => _db.Intake().NewCountAsync(), sql => sql.Contains("COUNT(*)", StringComparison.Ordinal) && !sql.Contains("GROUP BY", StringComparison.Ordinal));

        plan.Should().Contain("USING COVERING INDEX IX_FeedItems_Status_FetchedAtUtc_SourceId");
    }

    [Fact]
    public async Task TriagesTabTally_ReadsTheStatusIndexAlone()
    {
        var plan = await PlanOfAsync(() => _db.Intake().TallyAsync(), sql => sql.Contains("GROUP BY \"f\".\"Status\"", StringComparison.Ordinal));

        plan.Should().Contain("USING COVERING INDEX IX_FeedItems_Status_FetchedAtUtc_SourceId");
    }

    [Fact]
    public async Task Workload_SeeksTheOpenAndTheRecentlyClosed_RatherThanScanningEveryAction()
    {
        var plan = await PlanOfAsync(() => _db.Actions().WorkloadAsync(), sql => sql.Contains("\"ClosedDate\" >=", StringComparison.Ordinal));

        plan.Should().Contain("IX_Actions_Status_DueDate").And.Contain("IX_Actions_ClosedDate");
        plan.Should().NotContain("SCAN a\n");
    }

    // Runs the call, finds the one command it sent that matches, and returns SQLite's plan for it, a step a line (the
    // parameters bound to null: SQLite plans without looking at them).
    private async Task<string> PlanOfAsync(Func<Task> call, Func<string, bool> matches)
    {
        _db.Commands.Record = true;
        _db.Commands.Reset();
        await call();
        var sql = _db.Commands.Sql.Where(matches).Should().ContainSingle().Subject;
        await using var ctx = _db.PlainContext();
        var connection = (SqliteConnection)ctx.Database.GetDbConnection();
        await ctx.Database.OpenConnectionAsync();
        await using var explain = connection.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (System.Text.RegularExpressions.Match p in Parameter().Matches(sql))
            if (!explain.Parameters.Contains(p.Value)) explain.Parameters.AddWithValue(p.Value, DBNull.Value);
        var steps = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
            while (await reader.ReadAsync()) steps.Add(reader.GetString(3));
        return string.Join('\n', steps) + "\n";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"@\w+")]
    private static partial System.Text.RegularExpressions.Regex Parameter();
}
