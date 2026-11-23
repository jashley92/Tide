using System.Data.Common;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Tide.Application.Operations;
using Tide.Infrastructure.Persistence;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>
/// Diagnostics' Performance panel reads <see cref="PerformanceMonitor"/>: percentiles over the last hour and day from
/// hourly histograms, names kept apart up to a limit, the last slow commands. A slow command is logged with its text and
/// never a parameter's value; a page is named without its query string or ids.
/// </summary>
public class PerformanceMonitorTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Percentiles_are_read_over_the_last_hour_or_day_and_an_hour_a_day_old_is_dropped()
    {
        var clock = new Clock(Start);
        var monitor = new PerformanceMonitor(clock);
        for (var i = 0; i < 90; i++) monitor.Record(TimedKind.Request, "/today", 10);
        for (var i = 0; i < 10; i++) monitor.Record(TimedKind.Request, "/today", 1000);
        monitor.Record(TimedKind.Request, "/search", 200);

        var hour = monitor.Summary(TimedKind.Request, 1);
        hour.Select(s => s.Name).Should().Equal("*", "/today", "/search");
        var today = hour[1];
        today.Count.Should().Be(100);
        today.P50Ms.Should().BeInRange(10, 10 * Math.Sqrt(2), "the bin's upper edge, within a factor of √2");
        today.P95Ms.Should().BeInRange(1000 / Math.Sqrt(2), 1000);
        today.MaxMs.Should().Be(1000);

        clock.Now = Start.AddHours(2);
        monitor.Record(TimedKind.Request, "/today", 5);
        monitor.Summary(TimedKind.Request, 1)[1].Count.Should().Be(1, "only this hour's");
        monitor.Summary(TimedKind.Request, PerformanceMonitor.Hours).Single(s => s.Name == "/today").Count.Should().Be(101);

        clock.Now = Start.AddHours(PerformanceMonitor.Hours + 1);
        monitor.Summary(TimedKind.Request, PerformanceMonitor.Hours).Single(s => s.Name == "/today").Count.Should().Be(1, "the first hour has gone");
        monitor.Summary(TimedKind.DbCommand, 1).Should().BeEmpty();
    }

    [Fact]
    public void Names_past_the_limit_go_under_other_and_the_slow_lists_keep_the_newest()
    {
        var monitor = new PerformanceMonitor(new Clock(Start));
        for (var i = 0; i < PerformanceMonitor.MaxNames + 50; i++) monitor.Record(TimedKind.PageEvent, $"/page{i}", 1);
        monitor.Summary(TimedKind.PageEvent, 1, take: 1000).Count.Should().BeLessThanOrEqualTo(PerformanceMonitor.MaxNames + 3);
        monitor.Summary(TimedKind.PageEvent, 1, take: 1000).Should().Contain(s => s.Name == PerformanceMonitor.Other);

        for (var i = 0; i < PerformanceMonitor.MaxSlow + 5; i++) monitor.SlowCommand(i, $"SELECT {i}");
        monitor.SlowCommands().Should().HaveCount(PerformanceMonitor.MaxSlow);
        monitor.SlowCommands()[0].Sql.Should().Be($"SELECT {PerformanceMonitor.MaxSlow + 4}");
    }

    [Fact]
    public void Circuits_are_counted_with_the_most_open_at_once()
    {
        var monitor = new PerformanceMonitor(new Clock(Start));
        monitor.CircuitOpened();
        monitor.CircuitOpened();
        monitor.CircuitClosed();
        monitor.CircuitOpened();
        monitor.CircuitClosed();
        (monitor.CircuitsOpen, monitor.PeakCircuits()).Should().Be((1, 2));
    }

    [Fact]
    public void A_slow_command_is_logged_with_its_text_and_never_a_value()
    {
        var monitor = new PerformanceMonitor(new Clock(Start));
        var log = new ListLogger<SlowCommandInterceptor>();
        var interceptor = new SlowCommandInterceptor(monitor, log, thresholdMs: 500);
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Id\" FROM \"Articles\" WHERE \"Title\" LIKE @p0";
        command.Parameters.AddWithValue("@p0", "%secret-indicator.example%");

        interceptor.NonQueryExecuted(command, Executed(connection, command, TimeSpan.FromMilliseconds(80)), 0);
        log.Warnings.Should().BeEmpty("under the threshold");
        interceptor.NonQueryExecuted(command, Executed(connection, command, TimeSpan.FromMilliseconds(750)), 0);

        log.Warnings.Should().ContainSingle().Which.Should().Contain("750 ms").And.Contain("LIKE @p0").And.NotContain("secret-indicator");
        monitor.SlowCommands().Should().ContainSingle().Which.Sql.Should().NotContain("secret-indicator");
        monitor.Summary(TimedKind.DbCommand, 1).Single().Count.Should().Be(2, "every command is timed");
    }

    [Theory]
    [InlineData("search?q=evil.example.net", "/search")]
    [InlineData("articles/6f1c2a8e-1d7b-4a43-9a7e-0b7f0d1c2e3f/edit", "/articles/{id}/edit")]
    [InlineData("actions/A-42#comments", "/actions/{id}")]
    [InlineData("", "/")]
    public void A_page_is_named_without_its_query_or_ids(string relative, string page) =>
        PerformanceCircuitHandler.PageOf(relative).Should().Be(page);

    private static CommandExecutedEventData Executed(DbConnection connection, DbCommand command, TimeSpan duration) =>
        new(null!, (_, _) => "", connection, command, command.CommandText, null, DbCommandMethod.ExecuteNonQuery, Guid.NewGuid(), Guid.NewGuid(), 0,
            false, false, Start, duration, CommandSource.Unknown);
}
