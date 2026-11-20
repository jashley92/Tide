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

    /// <summary>The non-blank batches of <paramref name="sql"/>, split on GO lines as sqlcmd (and EF) split them.</summary>
    internal static IEnumerable<string> Batches(string sql)
    {
        var batch = new StringBuilder();
        foreach (var line in sql.ReplaceLineEndings("\n").Split('\n'))
        {
            var t = line.Trim();
            if (t.Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(batch.ToString())) yield return batch.ToString().Trim('\n');
                batch.Clear();
            }
            else
            {
                batch.Append(line).Append('\n');
            }
        }
        if (!string.IsNullOrWhiteSpace(batch.ToString())) yield return batch.ToString().Trim('\n');
    }
}
