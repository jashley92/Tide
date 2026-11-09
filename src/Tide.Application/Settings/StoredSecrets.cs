using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Tide.Application.Abstractions;
using Tide.Application.Api;
using Tide.Application.Assist;
using Tide.Application.Intake;
using Tide.Application.Operations;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Settings;

/// <summary>
/// A secret an administrator may save in the app in place of the server's configuration. <see cref="Purpose"/> is its own
/// Data Protection purpose, so a value protected for one secret never opens as another (nor as a webhook's).
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

/// <summary>A saved secret as the page shows it: never the value. Whether one is saved, when and by whom, whether it opens
/// on this server (<see cref="Unreadable"/>: a database restored without its keys), and whether the server's configuration
/// sets the secret, which wins.</summary>
public sealed record StoredSecretStatus(StoredSecretDefinition Definition, bool Saved, DateTimeOffset? SavedAtUtc, string? SavedBy,
    bool Unreadable, bool ServerSets);

/// <summary>
/// The secrets saved in the app, decrypted and kept in memory: read at start-up and again whenever one is saved or cleared.
/// A saved secret fills in its option only where the server's configuration leaves it blank (<see cref="StoredSecretOverlay{T}"/>),
/// so the server's value always wins, and a change reloads the options at once, with no restart. One that won't decrypt
/// (the database restored without the Data Protection keys) is unreadable: left out, and logged. Singleton.
/// </summary>
public sealed class StoredSecrets(ISecretProtector protector, ILogger<StoredSecrets> log) : IDisposable
{
    private sealed record Saved(string? Value, DateTimeOffset SavedAtUtc, string SavedBy);

    private IReadOnlyDictionary<string, Saved> _saved = new Dictionary<string, Saved>();
    private ConfigurationReloadToken _changed = new();
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    /// <summary>Fires when a saved secret changes.</summary>
    public IChangeToken GetChangeToken() => _changed;

    /// <summary>The saved secret, decrypted, or false when none is saved or it won't open here.</summary>
    public bool TryGet(StoredSecretDefinition secret, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        value = _saved.GetValueOrDefault(secret.Key)?.Value;
        return value is not null;
    }

    internal (bool Saved, DateTimeOffset? At, string? By, bool Unreadable) Status(StoredSecretDefinition secret) =>
        _saved.GetValueOrDefault(secret.Key) is { } s ? (true, s.SavedAtUtc, s.SavedBy, s.Value is null) : (false, null, null, false);

    /// <summary>Reads the saved secrets again, one refresh at a time (as <see cref="SavedSettings"/> does).</summary>
    public async Task RefreshAsync(IAppDbContextFactory dbs, CancellationToken ct = default)
    {
        await _refreshing.WaitAsync(ct);
        try
        {
            await using var db = await dbs.CreateAsync(ct);
            var rows = await db.StoredSecrets.AsNoTracking().ToListAsync(ct);
            var saved = new Dictionary<string, Saved>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (StoredSecretDefinition.Find(row.Key) is not { } secret) continue;
                var value = protector.Unprotect(secret.Purpose, row.Protected);
                if (value is null) log.LogWarning("The saved {Secret} won't decrypt on this server: the database may have been restored without its Data Protection keys. Save it again in Administration › Secrets.", secret.Key);
                saved[secret.Key] = new(value, row.ModifiedAtUtc ?? row.CreatedAtUtc, row.SavedByName);
            }
            if (saved.Count == _saved.Count && saved.All(s => _saved.TryGetValue(s.Key, out var had) && had == s.Value)) return;
            _saved = saved;
            Interlocked.Exchange(ref _changed, new ConfigurationReloadToken()).OnReload();
        }
        finally { _refreshing.Release(); }
    }

    public void Dispose() => _refreshing.Dispose();

    /// <summary>The NVD key, when the server's configuration has none.</summary>
    public static void Apply(StoredSecrets s, CveOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.NvdApiKey) && s.TryGet(StoredSecretDefinition.NvdApiKey, out var key)) o.NvdApiKey = key;
    }

    /// <summary>The Azure OpenAI key, when the server's configuration has none.</summary>
    public static void Apply(StoredSecrets s, AiOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.ApiKey) && s.TryGet(StoredSecretDefinition.AiApiKey, out var key)) o.ApiKey = key;
    }
}

/// <summary>Fills in <typeparamref name="T"/>'s secret from the saved ones, and reloads it when they change.</summary>
public sealed class StoredSecretOverlay<T>(StoredSecrets secrets, Action<StoredSecrets, T> apply) : IPostConfigureOptions<T>, IOptionsChangeTokenSource<T>
    where T : class
{
    public string? Name => Options.DefaultName;

    public IChangeToken GetChangeToken() => secrets.GetChangeToken();

    public void PostConfigure(string? name, T options)
    {
        if (name == Options.DefaultName) apply(secrets, options);
    }
}

/// <summary>Saves and clears the secrets an administrator may keep in the app: administrators only, the value encrypted
/// before it's stored, audited (as set, never the value) and in force at once.</summary>
public sealed class StoredSecretService(IAppDbContextFactory dbs, ICurrentUser user, StoredSecrets secrets, ISecretProtector protector, IConfiguration config)
{
    /// <summary>Each secret as the page shows it.</summary>
    public IReadOnlyList<StoredSecretStatus> List()
    {
        RequireAdmin();
        return StoredSecretDefinition.All.Select(d =>
        {
            var (saved, at, by, unreadable) = secrets.Status(d);
            return new StoredSecretStatus(d, saved, at, by, unreadable, !string.IsNullOrWhiteSpace(config[d.Key]));
        }).ToList();
    }

    /// <summary>The keys of the secrets saved here that open on this server: where the server's configuration has none,
    /// these are in force (Server configuration and Setting sources say so).</summary>
    public IReadOnlySet<string> InForceKeys() =>
        List().Where(s => s.Saved && !s.Unreadable).Select(s => s.Definition.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Saves <paramref name="value"/> as <paramref name="key"/>'s secret, encrypted, in place of any saved before.</summary>
    public async Task SaveAsync(string key, string? value, CancellationToken ct = default)
    {
        RequireAdmin();
        var secret = StoredSecretDefinition.Find(key) ?? throw new ArgumentException("That isn't a secret that can be saved here.");
        var text = value?.Trim() ?? "";
        if (text.Length == 0) throw new ArgumentException("Enter the secret, or use Clear to remove the saved one.");
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
        RequireAdmin();
        var secret = StoredSecretDefinition.Find(key) ?? throw new ArgumentException("That isn't a secret that can be saved here.");
        await using (var db = await dbs.CreateAsync(ct))
        {
            if (await db.StoredSecrets.FirstOrDefaultAsync(s => s.Key == secret.Key, ct) is not { } row) return;
            db.StoredSecrets.Remove(row);
            await db.SaveChangesAsync(ct);
        }
        await secrets.RefreshAsync(dbs, ct);
    }

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can see or change the saved secrets.");
    }
}
