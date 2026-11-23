using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Tide.Infrastructure.Persistence;

/// <summary>
/// <c>Tide.Web.dll --verify-database "&lt;connection string&gt;" [--provider SqlServer|Sqlite]</c>: whether this build can run
/// on a database, without starting the site. It opens the database, checks this build knows every migration the database
/// records and that none of its own are missing, and reads a page of each table a page opens with (so a schema the model
/// no longer matches fails here, not in front of an analyst). The restore drill runs it against the scratch copy; a DBA
/// can run it before pointing the site at a restored database. It changes nothing. Exit code 0 when it can run, 1 when it
/// can't, 2 for a usage error.
/// </summary>
public static class DatabaseVerifier
{
    public const string Flag = "--verify-database";

    /// <summary>Runs the check when <paramref name="args"/> ask for it; null when they don't (the site starts as usual).</summary>
    public static async Task<int?> RunIfAskedAsync(string[] args, TextWriter output)
    {
        var at = Array.IndexOf(args, Flag);
        if (at < 0) return null;
        if (at + 1 >= args.Length || string.IsNullOrWhiteSpace(args[at + 1]))
        {
            await output.WriteLineAsync($"Usage: Tide.Web.dll {Flag} \"<connection string>\" [--provider SqlServer|Sqlite]");
            return 2;
        }
        var p = Array.IndexOf(args, "--provider");
        string provider;
        try { provider = DependencyInjection.DatabaseProvider(p >= 0 && p + 1 < args.Length ? args[p + 1] : DependencyInjection.SqlServer); }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return 2;
        }
        return await RunAsync(args[at + 1], provider, output);
    }

    public static async Task<int> RunAsync(string connectionString, string provider, TextWriter output, CancellationToken ct = default)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        if (provider == DependencyInjection.Sqlite) builder.UseSqlite(connectionString);
        else builder.UseSqlServer(connectionString, sql => sql.MigrationsAssembly(AppDbContextFactory.SqlServerMigrationsAssembly));
        var failed = new List<string>();
        async Task Say(bool passed, string what)
        {
            if (!passed) failed.Add(what);
            await output.WriteLineAsync($"{(passed ? "OK  " : "FAIL")} {what}");
        }
        try
        {
            await using var db = new AppDbContext(builder.Options);
            if (!await db.Database.CanConnectAsync(ct))
            {
                await Say(false, "The database doesn't answer, or doesn't exist.");
                return 1;
            }
            await Say(true, "The database answers.");

            var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            var unknown = applied.Where(m => !known.Contains(m)).ToList();
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            await Say(unknown.Count == 0, unknown.Count == 0 ? $"This build knows all {applied.Count} migrations the database records."
                : $"The database records {unknown.Count} migration(s) this build doesn't know (a newer release's): {string.Join(", ", unknown.Take(5))}.");
            await Say(pending.Count == 0, pending.Count == 0 ? "No migration of this build is missing."
                : $"{pending.Count} migration(s) of this build aren't applied yet: {string.Join(", ", pending.Take(5))}. The site would apply them at start (or a DBA, with Database:ApplyMigrations off).");

            // A page of each table a page opens with: the model and the schema agree, and the rows read.
            foreach (var (name, read) in Pages(db))
            {
                try
                {
                    var rows = await read(ct);
                    await Say(true, $"{name}: a page reads ({rows} row(s)).");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await Say(false, $"{name}: couldn't be read: {ex.GetBaseException().Message}");
                }
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or SqliteException or InvalidOperationException)
        {
            await Say(false, $"The database couldn't be checked: {ex.GetBaseException().Message}");
        }
        await output.WriteLineAsync(failed.Count == 0 ? "This build can run on the database." : "This build can't run on the database as it is.");
        return failed.Count == 0 ? 0 : 1;
    }

    private static IEnumerable<(string Name, Func<CancellationToken, Task<int>> Read)> Pages(AppDbContext db) =>
    [
        ("Articles", ct => Rows(db.Articles.AsNoTracking().OrderByDescending(a => a.CreatedAtUtc).Take(20).ToListAsync(ct))),
        ("Briefs", ct => Rows(db.Briefs.AsNoTracking().OrderByDescending(b => b.BriefDate).Take(20).ToListAsync(ct))),
        ("Actions", ct => Rows(db.Actions.AsNoTracking().IgnoreQueryFilters().OrderByDescending(a => a.Number).Take(20).ToListAsync(ct))),
        ("Feed items", ct => Rows(db.FeedItems.AsNoTracking().OrderByDescending(f => f.Id).Take(20).ToListAsync(ct))),
        ("Stored files", ct => Rows(db.Files.AsNoTracking().IgnoreQueryFilters().Take(20).ToListAsync(ct))),
        ("Audit log", ct => Rows(db.AuditLog.AsNoTracking().OrderByDescending(e => e.AtUtc).Take(20).ToListAsync(ct))),
        ("Settings", ct => Rows(db.Settings.AsNoTracking().Take(20).ToListAsync(ct))),
    ];

    // The rows a page read, loaded as entities (so every mapped column is read), counted.
    private static async Task<int> Rows<T>(Task<List<T>> page) => (await page).Count;
}
