using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Tide.Application.Abstractions;

namespace Tide.Infrastructure.Persistence;

/// <summary>
/// Design-time factory so EF tooling (migrations) can build the context without the Web host. Defaults to SQLite, the
/// development provider. Set <c>TIDE_MIGRATIONS_PROVIDER=SqlServer</c> to work on the production SQL Server set (in the
/// <c>Tide.Migrations.SqlServer</c> assembly). No database is contacted when adding a migration.
/// </summary>
/// <example>
///   # Add a migration to both sets (PowerShell):
///   dotnet ef migrations add &lt;Name&gt; --project src/Tide.Infrastructure --startup-project src/Tide.Web `
///     --context AppDbContext --output-dir Persistence/Migrations
///   $env:TIDE_MIGRATIONS_PROVIDER = 'SqlServer'
///   dotnet ef migrations add &lt;Name&gt; --project src/Tide.Migrations.SqlServer --startup-project src/Tide.Web `
///     --context AppDbContext --output-dir Migrations
/// </example>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public const string SqlServerMigrationsAssembly = "Tide.Migrations.SqlServer";

    public AppDbContext CreateDbContext(string[] args)
    {
        var provider = Environment.GetEnvironmentVariable("TIDE_MIGRATIONS_PROVIDER") ?? "Sqlite";
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
            builder.UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=Tide;Trusted_Connection=True;TrustServerCertificate=True;",
                sql => sql.MigrationsAssembly(SqlServerMigrationsAssembly));
        else
            builder.UseSqlite("Data Source=App_Data/tide-design.db");
        return new AppDbContext(builder.Options);
    }
}

/// <summary>Hands the application layer a short-lived context per operation (see <see cref="IAppDbContextFactory"/>).</summary>
public sealed class AppDbContextFactoryAdapter(IDbContextFactory<AppDbContext> inner) : IAppDbContextFactory
{
    public async Task<IAppDbContext> CreateAsync(CancellationToken ct = default) => await inner.CreateDbContextAsync(ct);
}
