using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Update;

namespace Tide.Infrastructure.Persistence;

/// <summary>
/// SQL Server's migration SQL, with one change for the idempotent script a DBA applies (<c>dotnet ef migrations script
/// --idempotent</c>, which the release bundle carries): a migration's own SQL (<c>migrationBuilder.Sql</c>) runs through
/// <c>EXEC</c>. In that script every statement sits in an IF block, in one batch per migration, and SQL Server compiles a
/// batch before it runs any of it, so SQL that reads a column the same migration adds (a backfill) fails on a database
/// without the column: "Invalid column name". Through EXEC it's compiled when it runs, after the column exists. EF already
/// does this for the statements it writes itself (filtered indexes, say), not for a migration's own SQL.
/// </summary>
/// <remarks>
/// Only the idempotent script changes. Migrate() and a plain script run the SQL as it's written, and released migrations
/// stay as they shipped (they're immutable). Used at design time only, by <see cref="AppDbContextFactory"/>.
/// </remarks>
public sealed class IdempotentScriptSqlGenerator(MigrationsSqlGeneratorDependencies dependencies, ICommandBatchPreparer commandBatchPreparer)
    : SqlServerMigrationsSqlGenerator(dependencies, commandBatchPreparer)
{
    protected override void Generate(SqlOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        if (!Options.HasFlag(MigrationsSqlGenerationOptions.Idempotent))
        {
            base.Generate(operation, model, builder);
            return;
        }
        // Each GO-separated batch becomes its own EXEC; the base still splits on the GO lines that separate them.
        var wrapped = new StringBuilder();
        foreach (var batch in Batches(operation.Sql))
        {
            if (wrapped.Length > 0) wrapped.AppendLine().AppendLine("GO");
            wrapped.Append("EXEC(N'").Append(batch.Replace("'", "''", StringComparison.Ordinal)).Append("')");
        }
        if (wrapped.Length == 0)
            return;
        base.Generate(new SqlOperation { Sql = wrapped.ToString(), SuppressTransaction = operation.SuppressTransaction }, model, builder);
    }

    /// <summary>
    /// The batches of <paramref name="sql"/>, split as SQL Server's generator splits a migration's SQL for Migrate(), so
    /// the script runs the same batches: a line starting with GO (any case, after any indent, then the end of the line or
    /// whitespace) ends one, and <c>GO n</c> runs it n times (a count that isn't a number means once, as for EF). A
    /// backslash at a line's end joins it to the next. Blank batches are dropped: EXEC of nothing does nothing.
    /// </summary>
    internal static IEnumerable<string> Batches(string sql)
    {
        var batch = new StringBuilder();
        var lines = sql.Replace("\\\r\n", "", StringComparison.Ordinal).Replace("\\\n", "", StringComparison.Ordinal)
            .ReplaceLineEndings("\n").Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("GO", StringComparison.OrdinalIgnoreCase) && (trimmed.Length == 2 || char.IsWhiteSpace(trimmed[2])))
            {
                var text = batch.ToString().Trim('\n');
                batch.Clear();
                var count = trimmed.Length >= 4 && int.TryParse(trimmed.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                    ? n
                    : 1;
                if (string.IsNullOrWhiteSpace(text)) continue;
                for (var i = 0; i < count; i++) yield return text;
            }
            else
            {
                batch.Append(line).Append('\n');
            }
        }
        var last = batch.ToString().Trim('\n');
        if (!string.IsNullOrWhiteSpace(last)) yield return last;
    }
}
