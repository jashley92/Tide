using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Preferences;

/// <summary>
/// The preferences a person can keep, each with the values it may hold (the first is its default) and the permission
/// that opens the page it belongs to. A new preference is one more entry here; its rows go in the same table.
/// </summary>
public static class UserPreferences
{
    /// <summary>The Actions page as a list or a board.</summary>
    public const string ActionsLayout = "actions.layout";

    public const string List = "list", Board = "board";

    public sealed record Definition(string Name, IReadOnlyList<string> Values, Permission Needs)
    {
        public string Default => Values[0];
    }

    private static readonly Dictionary<string, Definition> Known = new(StringComparer.Ordinal)
    {
        [ActionsLayout] = new(ActionsLayout, [List, Board], Permission.ReadActions),
    };

    /// <summary>The preference named <paramref name="name"/>; throws <see cref="ArgumentException"/> for any other.</summary>
    public static Definition For(string name) =>
        Known.TryGetValue(name, out var d) ? d : throw new ArgumentException($"There's no preference named \"{name}\".", nameof(name));
}

/// <summary>
/// The signed-in person's own preferences (<see cref="UserPreferences"/>), kept on the server so they follow them from
/// computer to computer. A preference only says how a page is shown to the person who chose it, so anyone who can open
/// that page may keep one, Viewers too: it's not a write to shared data, and no one can read or change another's.
/// </summary>
public sealed class UserPreferenceService(IAppDbContextFactory dbs, ICurrentUser user, TimeProvider time)
{
    /// <summary>The person's value for <paramref name="name"/>, or the preference's default when they haven't chosen
    /// (or what's stored is no longer one of its values).</summary>
    public async Task<string> GetAsync(string name, CancellationToken ct = default)
    {
        var d = Require(name);
        await using var db = await dbs.CreateAsync(ct);
        var stored = await db.UserPreferences.AsNoTracking()
            .Where(p => p.UserId == user.UserId && p.Name == d.Name).Select(p => p.Value).FirstOrDefaultAsync(ct);
        return stored is not null && d.Values.Contains(stored) ? stored : d.Default;
    }

    /// <summary>Keeps <paramref name="value"/> as the person's <paramref name="name"/>. Throws
    /// <see cref="ArgumentException"/> for a value the preference can't hold.</summary>
    public async Task SetAsync(string name, string value, CancellationToken ct = default)
    {
        var d = Require(name);
        if (!d.Values.Contains(value)) throw new ArgumentException($"\"{value}\" isn't one of {string.Join(", ", d.Values)}.", nameof(value));
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbs.CreateAsync(ct);
            var row = await db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == user.UserId && p.Name == d.Name, ct);
            if (row is null) db.UserPreferences.Add(row = new UserPreference { UserId = user.UserId, Name = d.Name });
            row.Value = value;
            row.UpdatedAtUtc = time.GetUtcNow();
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            // The same person's other tab added the row first: change that one instead.
            catch (DbUpdateException) when (attempt == 0) { }
        }
    }

    private UserPreferences.Definition Require(string name)
    {
        var d = UserPreferences.For(name);
        if (!user.IsAuthenticated) throw new ForbiddenException("Sign in to keep a preference.");
        if (!user.Has(d.Needs)) throw new ForbiddenException($"You don't have permission to {Policies.Describe(d.Needs)}.");
        return d;
    }
}
