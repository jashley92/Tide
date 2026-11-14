using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;

namespace Tide.Application.Operations;

/// <summary>
/// The release this server ran before the one running: what What's new shows someone who has never seen it from
/// (<see cref="ReleaseNotesCatalog.Due"/>). Two settings rows hold it: <see cref="VersionKey"/>, the release last run, and
/// <see cref="UpgradedFromKey"/>, the one before that. At startup, before anything else reads them, <see cref="RecordAsync"/>
/// reads them and moves them on when the release has changed; this process keeps what it read.
/// </summary>
public sealed class ReleaseHistory
{
    public const string VersionKey = "App:Version", UpgradedFromKey = "App:UpgradedFrom";

    /// <summary>A release from before TIDE recorded the one it ran (v1.13.0 or earlier): earlier than any.</summary>
    public static readonly ReleaseVersion BeforeRecorded = new(0, 0, 0);

    /// <summary>The release this server ran before the one running; null when that isn't known (a new install).</summary>
    public ReleaseVersion? UpgradedFrom { get; private set; }

    /// <summary>
    /// What it was upgraded from, given what's recorded (<paramref name="version"/>, <paramref name="from"/>: null when
    /// there's no row), and whether the record needs writing. Nothing recorded is a new install when nobody has signed in
    /// (<paramref name="hadUsers"/>), else one from before the record; a restart on the same release keeps what it read.
    /// </summary>
    public static (ReleaseVersion? From, bool Write) Decide(ReleaseVersion running, string? version, string? from, bool hadUsers)
    {
        if (version is null) return (hadUsers ? BeforeRecorded : null, true);
        if (version == running.ToString()) return (ReleaseVersion.Parse(from), false);
        return (ReleaseVersion.Parse(version), true);
    }

    /// <summary>Reads the record and, when the release running isn't the one recorded, records it and the one before.
    /// A development build records the release it runs as (<see cref="ReleaseNotesCatalog.Running"/>).</summary>
    public async Task RecordAsync(IAppDbContext db, ReleaseVersion running, CancellationToken ct = default)
    {
        var rows = await db.Settings.Where(s => s.Key == VersionKey || s.Key == UpgradedFromKey).ToDictionaryAsync(s => s.Key, ct);
        var hadUsers = !rows.ContainsKey(VersionKey) && await db.Users.AnyAsync(ct);
        var (from, write) = Decide(running, rows.GetValueOrDefault(VersionKey)?.Value, rows.GetValueOrDefault(UpgradedFromKey)?.Value, hadUsers);
        UpgradedFrom = from;
        if (!write) return;
        var now = DateTimeOffset.UtcNow;
        void Set(string key, string value)
        {
            if (!rows.TryGetValue(key, out var row)) db.Settings.Add(row = new AppSetting { Key = key });
            (row.Value, row.UpdatedAtUtc, row.UpdatedBy) = (value, now, "system");
        }
        Set(VersionKey, running.ToString());
        Set(UpgradedFromKey, from?.ToString() ?? "");
        try { await db.SaveChangesAsync(ct); }
        // Another process starting on the same database wrote it first, from the same record.
        catch (DbUpdateException) { db.ChangeTracker.Clear(); }
    }
}
