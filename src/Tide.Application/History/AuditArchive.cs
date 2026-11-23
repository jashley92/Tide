using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;

namespace Tide.Application.History;

/// <summary><c>Audit:RetentionDays</c>: how long the audit log is kept in the database. Unset (the default) keeps it
/// forever; a value under <see cref="AuditArchive.MinimumDays"/> is held to it. Older whole months are archived to files,
/// then removed from the database.</summary>
public sealed class AuditOptions
{
    public int? RetentionDays { get; set; }

    /// <summary>Where the archives go; unset: <c>audit-archive</c> under <c>Files:RootPath</c>, which the backups carry.</summary>
    public string? ArchivePath { get; set; }
}

/// <summary>One month archived: its file, rows, the file's SHA-256, and when it was written.</summary>
public sealed record ArchivedMonth(string File, int Rows, string Sha256, DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset WrittenAtUtc);

/// <summary>
/// Archive-then-prune for the audit log, off unless <c>Audit:RetentionDays</c> is set (decided 2026-11-21: a setting, off
/// by default, at least a year). Each whole calendar month (UTC) older than the retention is written to
/// <c>audit-YYYY-MM-&lt;stamp&gt;.jsonl.gz</c>, one JSON object per row; its SHA-256 and row count go in
/// <c>manifest.jsonl</c> beside it; the file is read back and checked; only then are that month's rows deleted, in batches
/// of <see cref="DeleteBatch"/>. A run that stops part-way leaves the rows it hadn't deleted, which the next run archives
/// in a file of their own (the stamp keeps names apart), so nothing is lost or overwritten.
/// </summary>
public static class AuditArchive
{
    /// <summary>The shortest retention: a year of the audit log always stays in the database.</summary>
    public const int MinimumDays = 365;

    /// <summary>Rows deleted at a time, by id: inside SQL Server's 2,100 parameters a statement.</summary>
    public const int DeleteBatch = 1000;

    public const string ManifestName = "manifest.jsonl";

    /// <summary>The archive folder: <see cref="AuditOptions.ArchivePath"/>, or <c>audit-archive</c> under the file store.</summary>
    public static string Folder(AuditOptions options, string fileStoreRoot) =>
        string.IsNullOrWhiteSpace(options.ArchivePath) ? Path.Combine(fileStoreRoot, "audit-archive") : Path.GetFullPath(options.ArchivePath);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The retention in force: null (keep forever) when unset or not positive, else at least <see cref="MinimumDays"/>.</summary>
    public static int? Days(AuditOptions options) =>
        options.RetentionDays is { } d and > 0 ? Math.Max(d, MinimumDays) : null;

    /// <summary>
    /// Archives and removes every whole month older than the retention; returns what it archived. Nothing when the
    /// retention is unset.
    /// </summary>
    public static async Task<IReadOnlyList<ArchivedMonth>> RunAsync(IAppDbContext db, AuditOptions options, string folder, DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (Days(options) is not { } days) return [];
        var cutoff = now.AddDays(-days).UtcDateTime;
        // The first moment of the month the cutoff falls in: everything before it is in a whole month past the retention.
        var before = new DateTimeOffset(cutoff.Year, cutoff.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var done = new List<ArchivedMonth>();
        Directory.CreateDirectory(folder);
        while (await db.AuditLog.AsNoTracking().Where(e => e.AtUtc < before).OrderBy(e => e.AtUtc).Select(e => (DateTimeOffset?)e.AtUtc).FirstOrDefaultAsync(ct) is { } oldest)
        {
            var from = new DateTimeOffset(oldest.UtcDateTime.Year, oldest.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var to = from.AddMonths(1);
            done.Add(await ArchiveMonthAsync(db, folder, from, to, now, ct));
        }
        return done;
    }

    private static async Task<ArchivedMonth> ArchiveMonthAsync(IAppDbContext db, string folder, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now,
        CancellationToken ct)
    {
        var name = $"audit-{from:yyyy-MM}-{now:yyyyMMddHHmmss}.jsonl.gz";
        var path = Path.Combine(folder, name);
        var month = db.AuditLog.AsNoTracking().Where(e => e.AtUtc >= from && e.AtUtc < to);
        var ids = new List<Guid>();
        await using (var file = new FileStream(path + ".partial", FileMode.CreateNew, FileAccess.Write))
        await using (var gz = new GZipStream(file, CompressionLevel.Optimal))
        await using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
        {
            await foreach (var e in month.OrderBy(e => e.AtUtc).ThenBy(e => e.Id).AsAsyncEnumerable().WithCancellation(ct))
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(e, Json));
                ids.Add(e.Id);
            }
        }
        File.Move(path + ".partial", path);

        // Read back what was written before anything is removed: the rows, in order, and the file's hash.
        var (rows, sha) = await CheckAsync(path, ct);
        if (rows != ids.Count) throw new InvalidOperationException($"The audit archive {name} reads back {rows} rows, not {ids.Count}; nothing was removed.");
        var archived = new ArchivedMonth(name, rows, sha, from, to, now);
        await File.AppendAllTextAsync(Path.Combine(folder, ManifestName), JsonSerializer.Serialize(archived, Json) + "\n", ct);

        // Only the rows written to the file, a batch at a time: a row of that month saved since (a clock set back) stays.
        foreach (var batch in ids.Chunk(DeleteBatch))
            await db.AuditLog.Where(e => batch.Contains(e.Id)).ExecuteDeleteAsync(ct);
        return archived;
    }

    /// <summary>The rows an archive holds and its SHA-256, as <see cref="ManifestName"/> records them.</summary>
    public static async Task<(int Rows, string Sha256)> CheckAsync(string path, CancellationToken ct = default)
    {
        string sha;
        await using (var raw = File.OpenRead(path)) sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(raw, ct));
        var rows = 0;
        await using var file = File.OpenRead(path);
        await using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
            if (line.Length > 0) rows++;
        return (rows, sha);
    }
}
