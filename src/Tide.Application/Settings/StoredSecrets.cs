using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Tide.Application.Abstractions;
using Tide.Application.Operations;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Settings;

/// <summary>
/// A secret an administrator may save in the app in place of the server's configuration. <see cref="Purpose"/> is its own
/// Data Protection purpose, fixed even if the key were renamed, so a value protected for one secret never opens as another
/// (nor as a webhook's).
/// </summary>
public sealed record StoredSecretDefinition(string Key, string Purpose, int MaxLength)
{
    public static readonly StoredSecretDefinition SmtpPassword = new("Email:SmtpPassword", "Tide.Secrets.SmtpPassword.v1", 512);
    public static readonly StoredSecretDefinition NvdApiKey = new("Cve:NvdApiKey", "Tide.Secrets.NvdApiKey.v1", 200);
    public static readonly StoredSecretDefinition AiApiKey = new("Ai:ApiKey", "Tide.Secrets.AiApiKey.v1", 512);

    public static IReadOnlyList<StoredSecretDefinition> All { get; } = [SmtpPassword, NvdApiKey, AiApiKey];

    public static StoredSecretDefinition? Find(string key) => All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>How the page names it ("Email · Relay password"), from the settings catalogue.</summary>
    public string Label => SettingsCatalog.Find(Key) is { } info ? $"{info.Group} · {info.Label}" : Key;
}

/// <summary>A secret as the page shows it: never the value. When and by whom it was saved (null: none is), whether it
/// opens on this server (<see cref="Unreadable"/>: a database restored without its keys), and whether the server's
/// configuration sets it, which wins.</summary>
public sealed record StoredSecretStatus(StoredSecretDefinition Definition, DateTimeOffset? SavedAtUtc, string? SavedBy, bool Unreadable, bool ServerSets)
{
    public bool Saved => SavedAtUtc is not null;
}

/// <summary>
/// The secrets saved in the app, decrypted and kept in memory: read at start-up and again whenever one is saved or cleared.
/// A saved secret fills in its option only where the server's configuration leaves it blank
/// (<see cref="DependencyInjection.SecretOverlay{T}"/>), so the server's value always wins, and a change reloads the options
/// at once, with no restart. One that won't decrypt (the database restored without the Data Protection keys) is left out,
/// and logged. Singleton.
/// </summary>
public sealed class StoredSecrets(ISecretProtector protector, ILogger<StoredSecrets> log) : IOverlaySource, IDisposable
{
    /// <summary>A saved secret: its value, or null when it won't decrypt here.</summary>
    internal sealed record Entry(string? Value, DateTimeOffset SavedAtUtc, string SavedBy);

    private IReadOnlyDictionary<string, Entry> _saved = new Dictionary<string, Entry>();
    private ConfigurationReloadToken _changed = new();
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    /// <summary>Fires when the saved secrets are read again.</summary>
    public IChangeToken GetChangeToken() => _changed;

    /// <summary>The saved secret, decrypted, or false when none is saved or it won't open here.</summary>
    public bool TryGet(StoredSecretDefinition secret, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        value = Saved(secret)?.Value;
        return value is not null;
    }

    internal Entry? Saved(StoredSecretDefinition secret) => _saved.GetValueOrDefault(secret.Key);

    /// <summary>Reads the saved secrets again, one refresh at a time (as <see cref="SavedSettings"/> does, for the same
    /// reason: two saves at once mustn't let the earlier read land last).</summary>
    public async Task RefreshAsync(IAppDbContextFactory dbs, CancellationToken ct = default)
    {
        await _refreshing.WaitAsync(ct);
        try
        {
            await using var db = await dbs.CreateAsync(ct);
            var saved = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in await db.StoredSecrets.AsNoTracking().ToListAsync(ct))
            {
                if (StoredSecretDefinition.Find(row.Key) is not { } secret) continue;
                var value = protector.Unprotect(secret.Purpose, row.Protected);
                if (value is null) log.LogWarning("The saved {Secret} won't decrypt on this server: the database may have been restored without its Data Protection keys. Save it again in Administration › Secrets.", secret.Key);
                saved[secret.Key] = new(value, row.ModifiedAtUtc ?? row.CreatedAtUtc, row.SavedByName);
            }
            _saved = saved;
            Interlocked.Exchange(ref _changed, new ConfigurationReloadToken()).OnReload();
        }
        finally { _refreshing.Release(); }
    }

    public void Dispose() => _refreshing.Dispose();
}

/// <summary>Saves and clears the secrets an administrator may keep in the app: administrators only, the value encrypted
/// before it's stored, audited (as set, never the value) and in force at once.</summary>
public sealed class StoredSecretService(IAppDbContextFactory dbs, ICurrentUser user, StoredSecrets secrets, ISecretProtector protector, IConfiguration config)
{
    /// <summary>Each secret as the page shows it.</summary>
    public IReadOnlyList<StoredSecretStatus> List()
    {
        RequireAdmin();
        return [.. StoredSecretDefinition.All.Select(d => secrets.Saved(d) is { } s
            ? new StoredSecretStatus(d, s.SavedAtUtc, s.SavedBy, s.Value is null, ServerSets(d))
            : new StoredSecretStatus(d, null, null, false, ServerSets(d)))];
    }

    private bool ServerSets(StoredSecretDefinition d) => !string.IsNullOrWhiteSpace(config[d.Key]);

    /// <summary>Saves <paramref name="value"/> as <paramref name="key"/>'s secret, encrypted, in place of any saved before.</summary>
    public async Task SaveAsync(string key, string? value, CancellationToken ct = default)
    {
        var secret = Known(key);
        var text = value?.Trim() ?? "";
        if (text.Length == 0) throw new ArgumentException("Enter the secret, or use Remove to take the saved one away.");
        if (text.Length > secret.MaxLength) throw new ArgumentException($"That's longer than {secret.MaxLength} characters.");
        await using (var db = await dbs.CreateAsync(ct))
        {
            var row = await db.StoredSecrets.FirstOrDefaultAsync(s => s.Key == secret.Key, ct);
            if (row is null) db.StoredSecrets.Add(row = new StoredSecret { Key = secret.Key });
            row.Protected = protector.Protect(secret.Purpose, text);
            row.SavedByName = user.DisplayName;
            await db.SaveChangesAsync(ct);
        }
        await secrets.RefreshAsync(dbs, ct);
    }

    /// <summary>Removes <paramref name="key"/>'s saved secret: the server's configuration, if any, is used again.</summary>
    public async Task ClearAsync(string key, CancellationToken ct = default)
    {
        var secret = Known(key);
        await using (var db = await dbs.CreateAsync(ct))
        {
            if (await db.StoredSecrets.FirstOrDefaultAsync(s => s.Key == secret.Key, ct) is not { } row) return;
            db.StoredSecrets.Remove(row);
            await db.SaveChangesAsync(ct);
        }
        await secrets.RefreshAsync(dbs, ct);
    }

    // An administrator, and a secret that can be saved here.
    private StoredSecretDefinition Known(string key)
    {
        RequireAdmin();
        return StoredSecretDefinition.Find(key) ?? throw new ArgumentException("That isn't a secret that can be saved here.");
    }

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can see or change the saved secrets.");
    }
}
