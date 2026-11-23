using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tide.Application.Operations;

namespace Tide.Infrastructure.Persistence;

/// <summary>
/// Times every database command for Diagnostics' Performance panel (<see cref="PerformanceMonitor"/>), and logs one that
/// takes longer than <see cref="DiagnosticsOptions.SlowSqlMs"/> (500 by default; 0 turns the warning off) as a Warning
/// with event id <see cref="PerformanceMonitor.SlowSqlEventId"/>: its duration and its SQL text, which holds the
/// parameters' names but never their values (a search term or an indicator is TLP data). The panel keeps the last few.
/// </summary>
public sealed class SlowCommandInterceptor(PerformanceMonitor monitor, IOptionsMonitor<DiagnosticsOptions> options,
    ILogger<SlowCommandInterceptor> log) : DbCommandInterceptor
{
    /// <summary>The longest SQL text logged; a longer one is cut.</summary>
    public const int MaxSql = 4000;

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result) =>
        Timed(command, eventData, result);

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(Timed(command, eventData, result));

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result) =>
        Timed(command, eventData, result);

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(Timed(command, eventData, result));

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result) =>
        Timed(command, eventData, result);

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(Timed(command, eventData, result));

    private T Timed<T>(DbCommand command, CommandExecutedEventData e, T result)
    {
        var ms = e.Duration.TotalMilliseconds;
        monitor.Record(TimedKind.DbCommand, null, ms);
        var threshold = options.CurrentValue.SlowSqlMs;
        if (threshold <= 0 || ms < threshold) return result;
        var sql = command.CommandText.Length > MaxSql ? command.CommandText[..MaxSql] + "…" : command.CommandText;
        monitor.SlowCommand(ms, sql);
        log.LogWarning(new EventId(PerformanceMonitor.SlowSqlEventId, "SlowSql"), "A database command took {Ms:N0} ms (over {Threshold:N0} ms): {Sql}",
            ms, threshold, sql);
        return result;
    }
}
