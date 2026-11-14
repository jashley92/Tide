using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Content;
using Tide.Domain.Entities;
using Tide.Infrastructure.Persistence;

namespace Tide.Infrastructure.Security;

/// <summary>
/// Mirrors signed-in users into <see cref="AppUser"/>. A user is written at most once per <see cref="TouchEvery"/>
/// (unless their name, email or roles changed), so calling this on every request is cheap.
/// </summary>
public sealed class UserDirectory(IServiceScopeFactory scopes, TimeProvider time, Application.Org.OrgClock clock) : IUserDirectory
{
    public static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Fingerprint)> _recent = new(StringComparer.OrdinalIgnoreCase);

    // The name each user's row holds, as last written or read: RecordedName answers from here, so a stand-in name asks the
    // database at most once per user (once per TouchEvery for someone with no row yet).
    private readonly ConcurrentDictionary<string, (string? Name, DateTimeOffset At)> _names = new(StringComparer.OrdinalIgnoreCase);

    public async Task TouchAsync(string userId, string displayName, string? upn, string? email, string rolesCsv, bool nameIsStandIn = false, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var fingerprint = string.Join('\u001f', displayName, upn, email, rolesCsv);
        if (_recent.TryGetValue(userId, out var seen) && seen.Fingerprint == fingerprint && now - seen.At < TouchEvery) return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Users.FirstOrDefaultAsync(u => u.Sid == userId, ct);
        if (row is null)
        {
            db.Users.Add(row = new AppUser { Sid = userId });
            // A first sign-in: the tour is due (someone already recorded, who used TIDE before, gets What's new instead),
            // and the pages' Show me around links show for a while (GuideLinkService). A preference already kept stays.
            const string tour = Application.Preferences.UserPreferences.TourState, since = Application.Operations.GuideLinkService.Since;
            var kept = await db.UserPreferences.Where(p => p.UserId == userId && (p.Name == tour || p.Name == since)).Select(p => p.Name).ToListAsync(ct);
            void Start(string name, string value)
            {
                if (!kept.Contains(name)) db.UserPreferences.Add(new UserPreference { UserId = userId, Name = name, Value = value, UpdatedAtUtc = now });
            }
            Start(tour, Application.Preferences.UserPreferences.TourDue);
            // The organization's day, as the window is counted in (not UTC's, a day ahead in a US evening).
            Start(since, Application.Operations.GuideLinkService.Day(clock.Today));
        }
        // A stand-in name (the bare username: AD didn't answer in time) keeps the real one recorded, as for the address.
        if (!nameIsStandIn || string.IsNullOrWhiteSpace(row.DisplayName)) row.DisplayName = Cut(displayName);
        row.UserPrincipalName = Cut(upn ?? "");
        // No address this time (AD didn't answer in time, or has none) keeps the one recorded: it's never blanked.
        if (!string.IsNullOrWhiteSpace(email)) row.Email = Cut(email.Trim());
        row.RolesCsv = rolesCsv.Length > 500 ? rolesCsv[..500] : rolesCsv;
        row.LastSeenUtc = now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return; }   // a first sign-in racing itself on two requests: the other one wrote it
        _recent[userId] = (now, fingerprint);
        _names[userId] = (Named(row.DisplayName), now);
    }

    public string? RecordedName(string userId)
    {
        var now = time.GetUtcNow();
        if (_names.TryGetValue(userId, out var known) && (known.Name is not null || now - known.At < TouchEvery)) return known.Name;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var name = Named(db.Users.AsNoTracking().Where(u => u.Sid == userId).Select(u => u.DisplayName).FirstOrDefault());
            _names[userId] = (name, now);
            return name;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            return null;   // the stand-in is shown, as before; asked again next time
        }
    }

    private static string? Named(string? name) => string.IsNullOrWhiteSpace(name) ? null : name;

    public async Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AsNoTracking().OrderByName(u => u.DisplayName).ToListAsync(ct);
    }

    private static string Cut(string s) => s.Length > 256 ? s[..256] : s;
}
