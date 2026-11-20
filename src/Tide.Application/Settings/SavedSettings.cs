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
    /// <paramref name="confirmOutside"/>; the organization's domains saved with it are the ones it's checked against.
    /// <para>A page passes what it showed when it loaded as <paramref name="loaded"/>. Then only the values changed on it
    /// are saved, so another administrator's change to a setting this page left alone stands; and a setting changed here
    /// that someone else has saved since the page loaded refuses the save (<see cref="Articles.StaleEditException"/>)
    /// rather than putting theirs back.</para></summary>
    public async Task SaveAsync(IReadOnlyDictionary<string, string?> values, bool confirmOutside = false,
        IReadOnlyDictionary<string, string?>? loaded = null, CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can change these settings.");
        var checkedValues = values.ToDictionary(v => v.Key, v => OperationalSettings.Normalize(v.Key, v.Value), StringComparer.OrdinalIgnoreCase);
        var changed = loaded is null ? checkedValues
            : checkedValues.Where(v => !loaded.TryGetValue(v.Key, out var was) || AsStored(v.Key, was) != v.Value)
                .ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
        if (changed.Count == 0) return;
        // The check and the save it guards in one serializable transaction: two administrators saving the same setting at
        // the same moment can't both pass it, the second reads what the first saved.
        try { await dbs.SerializedAsync(db => Write(db, changed, loaded, confirmOutside, ct), ct); }
        catch (DbUpdateException e) when (KeyTaken(e)) { throw Stale(); }
        await saved.RefreshAsync(dbs, ct);
    }

    private async Task Write(IAppDbContext db, Dictionary<string, string> changed, IReadOnlyDictionary<string, string?>? loaded,
        bool confirmOutside, CancellationToken ct)
    {
        var keys = changed.Keys.ToList();
        var rows = await db.Settings.Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, ct);
        if (loaded is not null) NotChangedSince(loaded, changed, rows);
        if (changed.TryGetValue(OperationalSettings.BriefNotReadyRecipients, out var told))
        {
            var inside = changed.TryGetValue(OperationalSettings.OrganizationDomains, out var domains) && boundary is not null
                ? boundary.With(OperationalSettings.SplitAddresses(domains))
                : boundary.CurrentOrUnknown();
            inside.CheckAdded(OperationalSettings.SplitAddresses(told),
                OperationalSettings.SplitAddresses(rows.GetValueOrDefault(OperationalSettings.BriefNotReadyRecipients)?.Value), confirmOutside,
                "They're told each morning there's no brief yet.");
        }
        foreach (var (key, value) in changed)
        {
            if (rows.TryGetValue(key, out var row) && row.Value == value) continue;
            if (row is null) db.Settings.Add(row = new AppSetting { Key = key });
            row.Value = value;
            row.UpdatedAtUtc = time.GetUtcNow();
            row.UpdatedBy = user.UserId;
        }
    }

    // Another save added the same setting's row first (SQLite names the column, SQL Server the key): as stale as a
    // change seen before saving.
    private static bool KeyTaken(DbUpdateException e) => e is not DbUpdateConcurrencyException && e.InnerException?.Message is { } m
        && (m.Contains("Settings.Key", StringComparison.Ordinal) || m.Contains("PK_Settings", StringComparison.Ordinal));

    private static Articles.StaleEditException Stale() => new("Someone else changed a setting you changed here since you " +
        "opened this page, so nothing was saved. Reload to see their change, then make yours again.");

    // A value as it would be stored, to compare what a page showed with what it sends and with what's saved now. One
    // that doesn't pass its check (a server's Windows time zone id, say) is compared as it is.
    private static string AsStored(string key, string? value) =>
        OperationalSettings.Problem(key, value) is null ? OperationalSettings.Normalize(key, value) : (value ?? "").Trim();

    // A setting changed on the page that someone else has saved since it loaded: saving would put back what they set, so
    // nothing is saved. A saved row that no longer passes its check isn't in force (the page showed the server's value),
    // so it doesn't count; nor does no row, as no one has saved one; nor does someone else having saved just what this
    // page sends, as nothing of theirs would be lost.
    private static void NotChangedSince(IReadOnlyDictionary<string, string?> loaded, Dictionary<string, string> changed, Dictionary<string, AppSetting> rows)
    {
        if (changed.Any(c => loaded.TryGetValue(c.Key, out var was) && rows.TryGetValue(c.Key, out var row)
                && OperationalSettings.Problem(c.Key, row.Value) is null && OperationalSettings.Normalize(c.Key, row.Value) is var now
                && now != AsStored(c.Key, was) && now != c.Value))
            throw Stale();
    }
}
