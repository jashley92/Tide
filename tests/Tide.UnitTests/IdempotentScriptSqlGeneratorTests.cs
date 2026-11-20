using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Tide.Infrastructure.Persistence;

namespace Tide.UnitTests;

/// <summary>
/// The DBA schema script is EF's idempotent script, where SQL Server compiles each migration's batch before running it:
/// a migration's own SQL that reads a column the migration adds must run through EXEC. Nothing here connects anywhere.
/// </summary>
public class IdempotentScriptSqlGeneratorTests
{
    private static string[] Generate(MigrationsSqlGenerationOptions options, string sql)
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=nowhere;Database=none", s => s.MigrationsAssembly(AppDbContextFactory.SqlServerMigrationsAssembly))
            .ReplaceService<IMigrationsSqlGenerator, IdempotentScriptSqlGenerator>()
            .Options);
        var generator = db.GetService<IMigrationsSqlGenerator>();
        return generator.Generate([new SqlOperation { Sql = sql }], model: null, options)
            .Select(c => c.CommandText.Trim())
            .ToArray();
    }

    [Fact]
    public void A_migrations_own_SQL_runs_through_EXEC_in_the_idempotent_script_one_batch_each_with_its_quotes_doubled()
    {
        var commands = Generate(MigrationsSqlGenerationOptions.Idempotent,
            "UPDATE s SET Outcome = N'it''s done' FROM BriefSends s;\nGO\n\nUPDATE a SET B = 1 FROM Articles a;\n");

        commands.Should().HaveCount(2);
        commands[0].Should().StartWith("EXEC(N'UPDATE s SET Outcome = N''it''''s done'' FROM BriefSends s;')");
        commands[1].Should().StartWith("EXEC(N'UPDATE a SET B = 1 FROM Articles a;')");
    }

    [Fact]
    public void GO_with_a_count_runs_the_batch_that_many_times_each_through_EXEC()
    {
        var commands = Generate(MigrationsSqlGenerationOptions.Idempotent, "INSERT INTO T DEFAULT VALUES;\nGO 3\nSELECT 1;");

        commands.Should().Equal(
            "EXEC(N'INSERT INTO T DEFAULT VALUES;')", "EXEC(N'INSERT INTO T DEFAULT VALUES;')", "EXEC(N'INSERT INTO T DEFAULT VALUES;')",
            "EXEC(N'SELECT 1;')");
    }

    // The script must run exactly the batches Migrate() runs: EF's own splitter, for the plain script, is the reference.
    [Theory]
    [InlineData("UPDATE A SET X = 1;\nGO 2\nUPDATE B SET Y = 2;")]
    [InlineData("UPDATE A SET X = 1;\n   go\nUPDATE B SET Y = 'a';")]
    [InlineData("UPDATE A SET X = 1;\nGO -- not a count\nUPDATE B SET Y = 2;")]
    [InlineData("UPDATE A SET X = 1;\r\nGO\r\nGO\r\nUPDATE B SET Y = 2;\r\nGO")]
    [InlineData("UPDATE A SET X = 1;\nGOTO_LABEL:\nUPDATE B SET Y = 2;")]
    [InlineData("UPDATE A SET X = \\\n1;\nGO\t4\nSELECT 1;")]
    public void The_idempotent_script_runs_the_same_batches_as_Migrate_each_through_EXEC(string sql)
    {
        var plain = Generate(MigrationsSqlGenerationOptions.Default, sql).Where(c => c.Length > 0).ToArray();
        var idempotent = Generate(MigrationsSqlGenerationOptions.Idempotent, sql);

        plain.Should().NotBeEmpty();
        idempotent.Should().Equal(plain.Select(p => "EXEC(N'" + p.Replace("'", "''", StringComparison.Ordinal) + "')"));
    }

    [Fact]
    public void Migrate_and_a_plain_script_run_a_migrations_SQL_as_it_is_written()
    {
        var commands = Generate(MigrationsSqlGenerationOptions.Default, "UPDATE s SET Outcome = N'it''s done' FROM BriefSends s;");

        commands.Should().ContainSingle().Which.Should().StartWith("UPDATE s SET Outcome = N'it''s done' FROM BriefSends s;");
    }
}
