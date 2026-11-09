using System.Globalization;
using System.Text.Json;
using Tide.Application.Abstractions;
using Tide.Application.Content;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.History;

/// <summary>One download, as it's recorded: the address's route ("/files/{id}/download"), the record it names (the
/// file's id, "brief/2026-11-09"), the name it was sent under, the TLP ceiling it was held to (an export's), and its size
/// in bytes when known.</summary>
public sealed record DownloadRecord(string Route, string Key, string? FileName, TlpLevel? Ceiling, long? Bytes);

/// <summary>
/// Writes a download to the audit log, as the person who asked for it: a row of kind <see cref="EntityType"/> whose
/// action is <see cref="Action"/>, keyed by the record it names, so the audit log's kind, action and "only this record"
/// filters find every download of one file or one export. The web layer decides which downloads are written here (an
/// export above TLP:GREEN, every file, the configuration bundle and the audit log's own CSV); every download is in the
/// access log either way.
/// </summary>
public sealed class DownloadAudit(IAppDbContextFactory dbs, ICurrentUser user)
{
    /// <summary>The audit log's kind for a download (not an entity: nothing is changed).</summary>
    public const string EntityType = "Download";

    /// <summary>The audit log's action for a download, as it's stored.</summary>
    public const string Action = "Downloaded";

    /// <summary>The field a download's row holds its TLP ceiling in (the audit log labels it).</summary>
    public const string CeilingField = "CeilingTlp";

    private const string NameField = "Name", RouteField = "Route", BytesField = "Bytes";

    // The audit log's key column is this long.
    private const int MaxKey = 100;

    public async Task RecordAsync(DownloadRecord download, CancellationToken ct = default)
    {
        var changes = new Dictionary<string, string?[]>
        {
            [NameField] = [null, download.FileName],
            [RouteField] = [null, download.Route],
        };
        if (download.Ceiling is { } tlp) changes[CeilingField] = [null, tlp.ToString()];
        if (download.Bytes is { } bytes) changes[BytesField] = [null, bytes.ToString(CultureInfo.InvariantCulture)];
        await using var db = await dbs.CreateAsync(ct);
        db.AuditLog.Add(new AuditLogEntry
        {
            AtUtc = DateTimeOffset.UtcNow, UserId = user.UserId, UserName = user.DisplayName, Action = Action, EntityType = EntityType,
            EntityKey = Words.Clip(download.Key, MaxKey),
            ChangesJson = JsonSerializer.Serialize(changes),
        });
        await db.SaveChangesAsync(ct);
    }
}
