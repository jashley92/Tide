using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Tide.Application.Operations;

namespace Tide.Infrastructure.Persistence;

/// <summary>
/// Times every database command for Diagnostics' Performance panel (<see cref="PerformanceMonitor"/>), and logs one that
/// takes longer than <see cref="ThresholdMs"/> (<c>Diagnostics:SlowSqlMs</c>, 500 by default; 0 turns the warning off) as
/// a Warning with event id <see cref="EventId"/>: its duration and its SQL text, which holds the parameters' names but never
/// their values (a search term or an indicator is TLP data). The panel keeps the last few.
/// </summary>
public sealed class SlowCommandInterceptor(PerformanceMonitor monitor, ILogger<SlowCommandInterceptor> log, int thresholdMs) : DbCommandInterceptor
{
    public const int EventId = 7201;

    /// <summary>The longest SQL text logged; a longer one is cut.</summary>
    public const int MaxSql = 4000;

    public int ThresholdMs { get; } = thresholdMs;

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Timed(command, eventData);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Timed(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Timed(command, eventData);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        Timed(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Timed(command, eventData);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
    {
        Timed(command, eventData);
        return ValueTask.FromResult(result);
    }

    private void Timed(DbCommand command, CommandExecutedEventData e)
    {
        var ms = e.Duration.TotalMilliseconds;
        monitor.Record(TimedKind.DbCommand, null, ms);
        if (ThresholdMs <= 0 || ms < ThresholdMs) return;
        var sql = command.CommandText.Length > MaxSql ? command.CommandText[..MaxSql] + "…" : command.CommandText;
        monitor.SlowCommand(ms, sql);
        log.LogWarning(new EventId(EventId, "SlowSql"), "A database command took {Ms:N0} ms (over {Threshold:N0} ms): {Sql}", ms, ThresholdMs, sql);
    }
}
