using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tide.Application.Files;
using Tide.Application.Metrics;

namespace Tide.Infrastructure.Storage;

/// <summary>
/// The metrics drop folder (ADR 0014): <c>metrics-inbox</c> under <c>Files:RootPath</c>. A job elsewhere (a scheduled
/// export from a ticketing or mail system) leaves CSV files there; when <c>Metrics:Import:DropFolderEnabled</c> is set,
/// each is imported once and moved to <c>processed</c> (or <c>rejected</c>, when the whole file was refused; or
/// <c>failed</c>, when importing it threw for a reason that wouldn't pass), named with the time it was read. A file the
/// database couldn't take (a connection fault, a timeout) waits for the next pass without holding up the others. A file
/// is left alone while it may still be being written: until it has been unchanged for
/// <see cref="Settle"/>, and while another process holds it open. Only <c>*.csv</c> files directly in the folder are read;
/// links, hidden files and temporary files are skipped. Moved files are deleted after <see cref="KeepFor"/>.
/// </summary>
public sealed class MetricDropFolder
{
    public const string FolderName = "metrics-inbox";
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(90);

    private readonly IOptionsMonitor<MetricImportOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MetricDropFolder> _log;

    public MetricDropFolder(IOptions<FileStoreOptions> files, IHostEnvironment env, IOptionsMonitor<MetricImportOptions> options,
        TimeProvider time, ILogger<MetricDropFolder> log)
    {
        var root = files.Value.RootPath;
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Files:RootPath isn't set.");
        Inbox = Path.Combine(Path.GetFullPath(Path.IsPathRooted(root) ? root : Path.Combine(env.ContentRootPath, root)), FolderName);
        _options = options;
        _time = time;
        _log = log;
    }

    /// <summary>The folder files are left in.</summary>
    public string Inbox { get; }

    public bool Enabled => _options.CurrentValue.DropFolderEnabled;

    /// <summary>Imports the files waiting in the folder, oldest first. Returns how many were read.</summary>
    public async Task<int> ProcessAsync(MetricImportService imports, CancellationToken ct = default)
    {
        if (!Enabled) return 0;
        Directory.CreateDirectory(Inbox);
        var now = _time.GetUtcNow();
        var read = 0;
        var waiting = new DirectoryInfo(Inbox).EnumerateFiles("*.csv", SearchOption.TopDirectoryOnly)
            .Where(f => (f.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden)) == 0
                        && f.Name[0] is not ('.' or '~')
                        && now - f.LastWriteTimeUtc >= Settle)
            .OrderBy(f => f.LastWriteTimeUtc).ToList();
        foreach (var file in waiting)
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes;
            try { bytes = await ReadAsync(file.FullName, ct); }
            catch (IOException) { continue; }   // still held open by whoever is writing it; next pass
            string folder;
            try
            {
                folder = (await imports.ImportDroppedAsync(file.Name, bytes, ct)).Rejected ? "rejected" : "processed";
                read++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (IsTransient(ex))
            {
                // The database couldn't be reached or timed out: the file waits for the next pass; the others still go.
                _log.LogWarning(ex, "The database wasn't available for {File}; it's tried again at the next pass.", file.Name);
                continue;
            }
            catch (Exception ex)
            {
                // Anything else would fail the same way every pass, so the file is set aside rather than read for ever.
                _log.LogError(ex, "Importing {File} failed; it's moved to the failed folder.", file.Name);
                folder = "failed";
            }
            try { Move(file.FullName, folder, now); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Reading it again is harmless (an import changes nothing the second time) but would be recorded again.
                _log.LogError(ex, "Couldn't move {File} out of the metrics drop folder after importing it.", file.Name);
            }
        }
        Prune(now);
        return read;
    }

    // A fault the next pass may not see: the database connection or a timeout, however EF wrapped it.
    private static bool IsTransient(Exception ex) =>
        ex is System.Data.Common.DbException or TimeoutException
        || (ex is Microsoft.EntityFrameworkCore.DbUpdateException && ex.InnerException is { } inner && IsTransient(inner));

    // At most a byte more than the limit, so an enormous file is refused without being read whole. FileShare.None: a
    // writer that still has it open makes this throw.
    private static async Task<byte[]> ReadAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[(int)Math.Min(stream.Length, MetricCsv.MaxBytes + 1L)];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    private void Move(string path, string folder, DateTimeOffset now)
    {
        var target = Path.Combine(Inbox, folder);
        Directory.CreateDirectory(target);
        var name = now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Path.GetFileName(path);
        var to = Path.Combine(target, name);
        if (File.Exists(to)) to = Path.Combine(target, Path.GetFileNameWithoutExtension(name) + "-" + Guid.NewGuid().ToString("N")[..8] + ".csv");
        File.Move(path, to);
        // A move keeps the file's own time, which a copy that preserves it (robocopy, an archive) sets months back: the
        // prune counts from when it was read, so it's kept its KeepFor rather than deleted on this same pass.
        File.SetLastWriteTimeUtc(to, now.UtcDateTime);
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var folder in new[] { "processed", "rejected", "failed" })
        {
            var dir = new DirectoryInfo(Path.Combine(Inbox, folder));
            if (!dir.Exists) continue;
            foreach (var old in dir.EnumerateFiles().Where(f => now - f.LastWriteTimeUtc > KeepFor && (f.Attributes & FileAttributes.ReparsePoint) == 0))
            {
                try { old.Delete(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning(ex, "Couldn't delete {File}.", old.Name); }
            }
        }
    }
}
