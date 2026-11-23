using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;

namespace Tide.Application.Files;

/// <summary>What the weekly check of stored files found.</summary>
/// <param name="Rows">Stored-file rows read.</param>
/// <param name="OnDisk">Files the store holds.</param>
/// <param name="Orphans">Files with no row (older than <see cref="FileSweep.Settle"/>: a newer one may be an upload whose
/// row isn't saved yet); a few ids named.</param>
/// <param name="Missing">Rows whose bytes aren't there; a few ids named.</param>
/// <param name="Checked">Files whose SHA-256 was checked against their row.</param>
/// <param name="Mismatched">Of those, the ones whose bytes no longer match; ids named.</param>
public sealed record SweepResult(int Rows, int OnDisk, int Orphans, int Missing, int Checked, int Mismatched, IReadOnlyList<string> Examples)
{
    public bool Clean => Orphans == 0 && Missing == 0 && Mismatched == 0;

    public string Summary() =>
        $"{Rows:N0} rows, {OnDisk:N0} files: {Missing:N0} missing, {Orphans:N0} without a row, {Mismatched:N0} of {Checked:N0} sampled don't match their hash"
        + (Examples.Count > 0 ? $" ({string.Join("; ", Examples)})" : "");
}

/// <summary>
/// The weekly check of stored files, which only reports (decided 2026-11-21: nothing is deleted without a setting): rows
/// whose bytes are gone (a restore without the files, a disk fault), files with no row (left by a delete that couldn't
/// remove them, or a restore of an older database), and the SHA-256 of a sample of files against what their row recorded
/// (the restore drill checks backups; this checks the live store). A removed file's bytes are kept for the record, so its
/// row counts too.
/// </summary>
public static class FileSweep
{
    /// <summary>Files sampled for their hash each week.</summary>
    public const int Sample = 50;

    /// <summary>How old a file with no row must be to count: an upload writes its bytes before its row.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromHours(1);

    private const int MaxExamples = 5;

    public static async Task<SweepResult> RunAsync(IAppDbContext db, IFileStore store, DateTimeOffset now, Random? random = null, CancellationToken ct = default)
    {
        var all = await db.Files.AsNoTracking().IgnoreQueryFilters().Select(f => new { f.Id, Purged = f.BytesPurgedAtUtc != null }).ToListAsync(ct);
        var rows = all.Select(f => f.Id).ToHashSet();
        var disk = store.List().ToDictionary(f => f.Id, f => f.WrittenAtUtc);
        var orphans = disk.Where(f => !rows.Contains(f.Key) && now - f.Value > Settle).Select(f => f.Key).ToList();
        // A row whose bytes were purged on purpose (Files:PurgeRemovedAfterDays) isn't missing them.
        var missing = all.Where(f => !f.Purged && !disk.ContainsKey(f.Id)).Select(f => f.Id).ToList();

        // A different sample each week, each file at most once.
        var present = rows.Where(disk.ContainsKey).ToArray();
        (random ?? Random.Shared).Shuffle(present);
        var sample = present.Take(Sample).ToList();
        var hashes = await db.Files.AsNoTracking().IgnoreQueryFilters().Where(f => sample.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Sha256, ct);
        var mismatched = new List<Guid>();
        foreach (var id in sample)
        {
            await using var bytes = store.OpenRead(id);
            if (bytes is null) continue;   // removed since it was listed
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(bytes, ct));
            // Uploads store lower case; a row written by hand or by an older tool might not.
            if (!string.Equals(hash, hashes[id], StringComparison.OrdinalIgnoreCase)) mismatched.Add(id);
        }

        var examples = missing.Take(MaxExamples).Select(id => $"missing {id}")
            .Concat(orphans.Take(MaxExamples).Select(id => $"no row {id}"))
            .Concat(mismatched.Take(MaxExamples).Select(id => $"hash differs {id}")).ToList();
        return new SweepResult(rows.Count, disk.Count, orphans.Count, missing.Count, sample.Count, mismatched.Count, examples);
    }
}
