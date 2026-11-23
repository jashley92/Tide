using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Files;
using Tide.Application.Org;
using Tide.Domain.Entities;

namespace Tide.Application.Operations;

/// <summary>One table's rows, and its bytes when the database says (SQL Server; null on SQLite).</summary>
public sealed record TableSize(string Name, long Rows, long? Bytes);

/// <summary>A folder's bytes.</summary>
public sealed record FolderSize(string Name, long Bytes);

/// <summary>The database's size and its tables', as the provider reports them.</summary>
public sealed record DatabaseSize(long? Bytes, IReadOnlyList<TableSize> Tables);

/// <summary>Reads how big the database and its tables are, in the provider's own way (Infrastructure).</summary>
public interface IDataUsage
{
    Task<DatabaseSize> MeasureAsync(CancellationToken ct = default);
}

/// <summary><c>Backup:StaleAfterHours</c>: how old the newest successful full backup may be before <c>/health/jobs</c>
/// says Degraded. Unset (or 0) turns the check off, the default: only an administrator who runs <c>Backup-Tide.ps1</c>,
/// which records each backup, sets it.</summary>
public sealed class BackupOptions
{
    public int? StaleAfterHours { get; set; }
}

/// <summary>What the Data &amp; retention panel shows: the latest snapshot, the growth since one 30 and 90 days before, the
/// newest backups the backup script recorded, and the retention in force.</summary>
public sealed record DataReport(SystemStat? Latest, IReadOnlyList<TableSize> Tables, IReadOnlyList<FolderSize> Folders,
    SystemStat? Month, SystemStat? Quarter, BackupRun? LastFull, BackupRun? LastLog, IReadOnlyList<(string What, string Kept)> Retention);

/// <summary>
/// The daily snapshot of how big TIDE is (<see cref="SystemStat"/>) and what Diagnostics reads from it, plus the disk and
/// backup checks <c>/health/jobs</c> and the statistics job make. The disk below <see cref="DiskFloor"/> free is Degraded
/// (decided 2026-11-21); a stale backup only when <see cref="BackupOptions.StaleAfterHours"/> is set.
/// </summary>
public sealed class SystemStats(IAppDbContextFactory dbs, IDataUsage usage, IOptionsMonitor<FileStoreOptions> files,
    IOptionsMonitor<BackupOptions> backups, OrgClock clock, TimeProvider time)
{
    /// <summary>The share of the data root's disk that must stay free; below it, <c>/health/jobs</c> says Degraded.</summary>
    public const double DiskFloor = 0.10;

    /// <summary>The event ids the statistics job logs a low disk and a stale backup under.</summary>
    public const int DiskLowEventId = 7203, BackupStaleEventId = 7205;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The data root, as the app resolves it.</summary>
    public string Root => Path.GetFullPath(files.CurrentValue.RootPath);

    /// <summary>Takes today's snapshot, unless there is one. Returns whether it took one.</summary>
    public async Task<bool> TakeAsync(CancellationToken ct = default)
    {
        var day = clock.Today;
        await using var db = await dbs.CreateAsync(ct);
        if (await db.SystemStats.AnyAsync(s => s.Day == day, ct)) return false;
        var size = await usage.MeasureAsync(ct);
        var folders = Folders(Root);
        var (free, total) = Disk(Root);
        db.SystemStats.Add(new SystemStat
        {
            Day = day, TakenAtUtc = time.GetUtcNow(), DatabaseBytes = size.Bytes, DiskFreeBytes = free, DiskTotalBytes = total,
            FilesBytes = folders?.Sum(f => f.Bytes), Tables = JsonSerializer.Serialize(size.Tables, Json),
            Folders = JsonSerializer.Serialize(folders ?? [], Json),
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return false; }   // another process took today's meanwhile
        return true;
    }

    /// <summary>What the Data &amp; retention panel shows.</summary>
    public async Task<DataReport> ReportAsync(IReadOnlyList<(string What, string Kept)> retention, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var latest = await db.SystemStats.AsNoTracking().OrderByDescending(s => s.Day).FirstOrDefaultAsync(ct);
        async Task<SystemStat?> Before(int days) => latest is null ? null
            : await db.SystemStats.AsNoTracking().Where(s => s.Day <= latest.Day.AddDays(-days)).OrderByDescending(s => s.Day).FirstOrDefaultAsync(ct);
        var (month, quarter) = (await Before(30), await Before(90));
        var lastFull = await LastBackupAsync(db, "full", ct);
        var lastLog = await LastBackupAsync(db, "log", ct);
        return new DataReport(latest,
            latest is null ? [] : JsonSerializer.Deserialize<List<TableSize>>(latest.Tables, Json) ?? [],
            latest is null ? [] : JsonSerializer.Deserialize<List<FolderSize>>(latest.Folders, Json) ?? [],
            month, quarter, lastFull, lastLog, retention);
    }

    /// <summary>When the newest successful full backup the backup script recorded finished; null for none.</summary>
    public async Task<DateTimeOffset?> LastFullBackupAsync(CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        return (await LastBackupAsync(db, "full", ct))?.FinishedAtUtc;
    }

    private static Task<BackupRun?> LastBackupAsync(IAppDbContext db, string kind, CancellationToken ct) =>
        db.BackupRuns.AsNoTracking().Where(b => b.Kind == kind && b.Succeeded).OrderByDescending(b => b.FinishedAtUtc).FirstOrDefaultAsync(ct);

    /// <summary>A sentence when the data root's disk is below <see cref="DiskFloor"/> free, else null. Asks the disk only.</summary>
    public string? DiskProblem()
    {
        var (free, total) = Disk(Root);
        return free is { } f && total is > 0 && (double)f / total.Value < DiskFloor
            ? $"The data root's disk has {f * 100.0 / total.Value:0.#}% free, under {DiskFloor:P0}." : null;
    }

    /// <summary>A sentence when <c>Backup:StaleAfterHours</c> is set and the newest successful full backup (as last read)
    /// is older, or there's none; else null.</summary>
    public string? BackupProblem(DateTimeOffset? lastFull)
    {
        if (backups.CurrentValue.StaleAfterHours is not ({ } hours and > 0)) return null;
        if (lastFull is not { } at) return $"No full backup has been recorded, and Backup:StaleAfterHours is {hours}.";
        var age = time.GetUtcNow() - at;
        return age.TotalHours > hours ? $"The newest full backup finished {age.TotalHours:0} hours ago, over Backup:StaleAfterHours ({hours})." : null;
    }

    /// <summary>The disk's free and total bytes under <paramref name="path"/>; nulls when it can't be read.</summary>
    public static (long? Free, long? Total) Disk(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return (null, null); }
    }

    /// <summary>The data root's top-level folders with their bytes (files directly in it as "(top level)"); null when it
    /// can't be read.</summary>
    public static List<FolderSize>? Folders(string root)
    {
        try
        {
            var dir = new DirectoryInfo(root);
            if (!dir.Exists) return null;
            var each = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var list = dir.EnumerateDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Select(d => new FolderSize(d.Name, d.EnumerateFiles("*", each).Sum(f => f.Length))).ToList();
            var top = dir.EnumerateFiles().Sum(f => f.Length);
            if (top > 0) list.Add(new FolderSize("(top level)", top));
            return [.. list.OrderByDescending(f => f.Bytes)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// When the newest successful full backup finished, as the statistics job last read it (every few minutes), so
/// <c>/health/jobs</c> can judge a stale backup without asking the database. Null until read, or when none is recorded.
/// </summary>
public sealed class BackupWatch
{
    private DateTimeOffset? _lastFull;
    private int _read;

    public DateTimeOffset? LastFull => _lastFull;

    /// <summary>Whether it has been read at least once in this process.</summary>
    public bool Read => Volatile.Read(ref _read) == 1;

    public void Set(DateTimeOffset? lastFull)
    {
        _lastFull = lastFull;
        Volatile.Write(ref _read, 1);
    }
}

