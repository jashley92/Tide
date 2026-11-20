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
    public void Migrate_and_a_plain_script_run_a_migrations_SQL_as_it_is_written()
    {
        var commands = Generate(MigrationsSqlGenerationOptions.Default, "UPDATE s SET Outcome = N'it''s done' FROM BriefSends s;");

        commands.Should().ContainSingle().Which.Should().StartWith("UPDATE s SET Outcome = N'it''s done' FROM BriefSends s;");
    }
}
