using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tide.Infrastructure.Persistence;

namespace Tide.IntegrationTests;

/// <summary>
/// <c>--verify-database</c> says whether this build can run on a database: it answers, its migrations are all this build's
/// and none of this build's is missing, and a page of each core table reads. It changes nothing.
/// </summary>
public class DatabaseVerifierTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "tide-verify-" + Guid.NewGuid().ToString("N") + ".db");
    private string Connection => $"Data Source={_file};Pooling=False";

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
        GC.SuppressFinalize(this);
    }

    private async Task<(int Code, string Text)> VerifyAsync(params string[] args)
    {
        var output = new StringWriter();
        var code = await DatabaseVerifier.RunIfAskedAsync(args, output);
        return (code ?? -1, output.ToString());
    }

    [Fact]
    public async Task A_database_this_build_migrated_passes_and_a_newer_releases_does_not()
    {
        (await DatabaseVerifier.RunIfAskedAsync(["--urls", "http://x"], TextWriter.Null)).Should().BeNull("the site starts as usual");
        (await VerifyAsync("--verify-database")).Code.Should().Be(2);

        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(Connection).Options))
            await db.Database.MigrateAsync();
        var (code, text) = await VerifyAsync("--verify-database", Connection, "--provider", "Sqlite");
        text.Should().Contain("This build knows all").And.Contain("Articles: a page reads").And.EndWith("This build can run on the database." + Environment.NewLine);
        code.Should().Be(0);

        await using (var c = new SqliteConnection(Connection))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('29991231000000_FromTheFuture', '10.0.0')""";
            await cmd.ExecuteNonQueryAsync();
        }
        (code, text) = await VerifyAsync("--verify-database", Connection, "--provider", "Sqlite");
        (code, text).Should().Match<(int, string)>(r => r.Item1 == 1 && r.Item2.Contains("29991231000000_FromTheFuture") && r.Item2.Contains("can't run"));
    }

    [Fact]
    public async Task A_database_that_isnt_there_fails_without_being_made()
    {
        var (code, text) = await VerifyAsync("--verify-database", $"Data Source={_file};Mode=ReadOnly;Pooling=False", "--provider", "Sqlite");
        code.Should().Be(1);
        text.Should().Contain("FAIL");
        File.Exists(_file).Should().BeFalse("it changes nothing");
    }
}
