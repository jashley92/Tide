namespace Tide.IntegrationTests;

/// <summary>
/// A test that needs a real SQL Server (production's database). It runs when <c>TIDE_TEST_SQL</c> holds a connection
/// string to a server with no database in it (see docs/development.md for a local Docker container) and is skipped
/// otherwise, so the default SQLite run needs nothing installed. The SQL Server rows of <see cref="TestDb.Providers"/>
/// theories need the same variable.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public const string Variable = "TIDE_TEST_SQL";

    /// <summary>The server connection string, or null when none is configured.</summary>
    public static string? Server =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } s ? s : null;

    public SqlServerFactAttribute()
    {
        if (Server is null) Skip = $"Set {Variable} to a SQL Server connection string to run (docs/development.md).";
    }

    /// <summary>A connection string for a new, uniquely named database on <see cref="Server"/>, for a test to drop.</summary>
    public static string NewDatabase() =>
        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(Server ?? throw new InvalidOperationException($"Set {Variable}."))
            { InitialCatalog = $"tide_test_{Guid.NewGuid():N}" }.ConnectionString;
}
