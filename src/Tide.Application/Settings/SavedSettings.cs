using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Tide.Application.Abstractions;
using Tide.Application.Distribution;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Settings;

/// <summary>
/// The <see cref="OperationalSettings"/> saved in Administration, kept in memory: read from the database at start-up and
/// again whenever one is saved or imported. A saved value is laid over the server's in the options it belongs to
/// (<see cref="OptionsOverlay{TSource, T}"/>), and a change reloads those options, so whatever reads them through
/// <see cref="IOptionsMonitor{T}"/> (the background services on each pass, <see cref="Org.OrgClock"/>, the pages) has
/// the new value at once, with no restart. Singleton.
/// </summary>
public sealed class SavedSettings : IOverlaySource, IDisposable
{
    private IReadOnlyDictionary<string, string> _values = new Dictionary<string, string>();
    private ConfigurationReloadToken _changed = new();

    /// <summary>Fires when a saved value changes.</summary>
    public IChangeToken GetChangeToken() => _changed;

    /// <summary>The saved value, checked and as it's stored, or false when none is saved (the server's value holds).</summary>
    public bool TryGet(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) => _values.TryGetValue(key, out value);

    internal void Use(string key, Action<string> apply)
    {
        if (TryGet(key, out var value)) apply(value);
    }

    /// <summary>Reads the saved values again. A row that no longer passes its check is ignored, so the server's value holds.</summary>
    public async Task RefreshAsync(IAppDbContextFactory dbs, CancellationToken ct = default)
    {
        // One at a time, each reading after the one before it has set: two administrators saving at once each refreshed,
        // and the refresh that read first could set last, putting back the value the other had just saved (in force,
        // and shown on its page, until the next save or a restart).
        await _refreshing.WaitAsync(ct);
        try
        {
            await using var db = await dbs.CreateAsync(ct);
            var keys = OperationalSettings.Keys.ToList();
            var rows = await db.Settings.AsNoTracking().Where(s => keys.Contains(s.Key)).ToListAsync(ct);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.Where(r => OperationalSettings.Problem(r.Key, r.Value) is null))
                values[row.Key] = OperationalSettings.Normalize(row.Key, row.Value);
            Set(values);
        }
        finally { _refreshing.Release(); }
    }

    private readonly SemaphoreSlim _refreshing = new(1, 1);

    public void Dispose() => _refreshing.Dispose();

    internal void Set(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == _values.Count && values.All(v => _values.TryGetValue(v.Key, out var had) && had == v.Value)) return;
        _values = values;
        Interlocked.Exchange(ref _changed, new ConfigurationReloadToken()).OnReload();
    }
}

/// <summary>What's saved in Administration and laid over the server's options (<see cref="SavedSettings"/>,
/// <see cref="StoredSecrets"/>): its change reloads them.</summary>
public interface IOverlaySource
{
    IChangeToken GetChangeToken();
}

/// <summary>Lays what <typeparamref name="TSource"/> holds over the server's values in <typeparamref name="T"/>, and
/// reloads it when that changes.</summary>
public sealed class OptionsOverlay<TSource, T>(TSource source, Action<TSource, T> apply) : IPostConfigureOptions<T>, IOptionsChangeTokenSource<T>
    where TSource : IOverlaySource
    where T : class
{
    public string? Name => Options.DefaultName;

    public IChangeToken GetChangeToken() => source.GetChangeToken();

    public void PostConfigure(string? name, T options)
    {
        if (name == Options.DefaultName) apply(source, options);
    }
}

/// <summary>Saves the <see cref="OperationalSettings"/>: administrators only, every value checked. Each is a row in the
/// <c>Settings</c> table, audited like any change, and takes effect at once.</summary>
public sealed class OperationalSettingsService(IAppDbContextFactory dbs, ICurrentUser user, SavedSettings saved, TimeProvider time,
    Distribution.EmailBoundary? boundary = null)
{
    /// <summary>Saves the values given (key to value), all or none: one that doesn't pass its check refuses the lot,
    /// saying why. A value the same as the one saved is left alone. An address outside the organization added to who's
    /// told there's no brief is refused (<see cref="Distribution.OutsideAddressesException"/>, naming it) unless
    /// <paramref name="confirmOutside"/>; the organization's domains saved with it are the ones it's checked against.</summary>
    public async Task SaveAsync(IReadOnlyDictionary<string, string?> values, bool confirmOutside = false, CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can change these settings.");
        var checkedValues = values.ToDictionary(v => v.Key, v => OperationalSettings.Normalize(v.Key, v.Value));
        await using (var db = await dbs.CreateAsync(ct))
        {
            var keys = checkedValues.Keys.ToList();
            var rows = await db.Settings.Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, ct);
            if (checkedValues.TryGetValue(OperationalSettings.BriefNotReadyRecipients, out var told))
            {
                var inside = checkedValues.TryGetValue(OperationalSettings.OrganizationDomains, out var domains) && boundary is not null
                    ? boundary.With(OperationalSettings.SplitAddresses(domains))
                    : boundary.CurrentOrUnknown();
                inside.CheckAdded(OperationalSettings.SplitAddresses(told),
                    OperationalSettings.SplitAddresses(rows.GetValueOrDefault(OperationalSettings.BriefNotReadyRecipients)?.Value), confirmOutside,
                    "They're told each morning there's no brief yet.");
            }
            foreach (var (key, value) in checkedValues)
            {
                if (rows.TryGetValue(key, out var row) && row.Value == value) continue;
                if (row is null) db.Settings.Add(row = new AppSetting { Key = key });
                row.Value = value;
                row.UpdatedAtUtc = time.GetUtcNow();
                row.UpdatedBy = user.UserId;
            }
            await db.SaveChangesAsync(ct);
        }
        await saved.RefreshAsync(dbs, ct);
    }
}
