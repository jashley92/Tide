using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Operations;

namespace Tide.Infrastructure.Persistence;

/// <summary>
/// How big the database and its tables are. On SQL Server: each table's rows and reserved bytes from
/// <c>sys.dm_db_partition_stats</c>, which needs VIEW DATABASE STATE; without it, rows only from <c>sys.partitions</c>
/// (visible to any login that can read the tables), with no bytes. The database's size from <c>sys.database_files</c>. On
/// SQLite (development): a count of each table's rows and the file's pages.
/// </summary>
public sealed class DataUsage(IAppDbContextFactory dbs) : IDataUsage
{
    public async Task<DatabaseSize> MeasureAsync(CancellationToken ct = default)
    {
        await using var db = (AppDbContext)await dbs.CreateAsync(ct);
        return db.Database.IsSqlServer() ? await SqlServerAsync(db, ct) : await SqliteAsync(db, ct);
    }

    private static async Task<DatabaseSize> SqlServerAsync(AppDbContext db, CancellationToken ct)
    {
        List<TableSize> tables;
        try
        {
            tables = await db.Database.SqlQueryRaw<TableSize>("""
                SELECT t.name AS Name, SUM(CASE WHEN p.index_id IN (0, 1) THEN p.row_count ELSE 0 END) AS Rows,
                       SUM(p.reserved_page_count) * 8192 AS Bytes
                FROM sys.dm_db_partition_stats p JOIN sys.tables t ON t.object_id = p.object_id
                WHERE t.is_ms_shipped = 0 GROUP BY t.name
                """).ToListAsync(ct);
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            // No VIEW DATABASE STATE: rows only.
            tables = await db.Database.SqlQueryRaw<TableSize>("""
                SELECT t.name AS Name, SUM(p.rows) AS Rows, CAST(NULL AS bigint) AS Bytes
                FROM sys.partitions p JOIN sys.tables t ON t.object_id = p.object_id
                WHERE t.is_ms_shipped = 0 AND p.index_id IN (0, 1) GROUP BY t.name
                """).ToListAsync(ct);
        }
        var bytes = await db.Database.SqlQueryRaw<long>("SELECT SUM(CAST(size AS bigint)) * 8192 AS Value FROM sys.database_files").SingleAsync(ct);
        return new DatabaseSize(bytes, [.. tables.Where(t => t.Name != "__EFMigrationsHistory").OrderByDescending(t => t.Bytes ?? 0).ThenByDescending(t => t.Rows)]);
    }

    // Each mapped table's rows, counted through the model (no table name is put into SQL), and the file's size.
    private static async Task<DatabaseSize> SqliteAsync(AppDbContext db, CancellationToken ct)
    {
        var count = typeof(EntityFrameworkQueryableExtensions).GetMethods()
            .Single(m => m.Name == nameof(EntityFrameworkQueryableExtensions.LongCountAsync) && m.GetParameters().Length == 2);
        var set = typeof(DbContext).GetMethods().Single(m => m.Name == nameof(DbContext.Set) && m.GetParameters().Length == 0);
        var tables = new List<TableSize>();
        foreach (var type in db.Model.GetEntityTypes().Where(t => t.GetTableName() is not null && !t.IsOwned() && t.BaseType is null))
        {
            var query = set.MakeGenericMethod(type.ClrType).Invoke(db, null)!;
            var rows = await (Task<long>)count.MakeGenericMethod(type.ClrType).Invoke(null, [IgnoreFilters(query, type.ClrType), ct])!;
            tables.Add(new TableSize(type.GetTableName()!, rows, null));
        }
        var pages = await db.Database.SqlQueryRaw<long>("SELECT page_count * page_size AS Value FROM pragma_page_count(), pragma_page_size()").SingleAsync(ct);
        return new DatabaseSize(pages, [.. tables.OrderByDescending(t => t.Rows)]);
    }

    // Every row, a soft-deleted action's too: the table's size, not what a page lists.
    private static object IgnoreFilters(object query, Type type) =>
        typeof(EntityFrameworkQueryableExtensions).GetMethods()
            .Single(m => m.Name == nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters) && m.GetParameters().Length == 1)
            .MakeGenericMethod(type).Invoke(null, [query])!;
}
