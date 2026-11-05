using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;
using Tide.Infrastructure.Persistence;

namespace Tide.Infrastructure.Security;

/// <summary>
/// Mirrors signed-in users into <see cref="AppUser"/>. A user is written at most once per <see cref="TouchEvery"/>
/// (unless their name, email or roles changed), so calling this on every request is cheap.
/// </summary>
public sealed class UserDirectory(IServiceScopeFactory scopes, TimeProvider time) : IUserDirectory
{
    public static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Fingerprint)> _recent = new(StringComparer.OrdinalIgnoreCase);

    public async Task TouchAsync(string userId, string displayName, string? upn, string? email, string rolesCsv, bool nameIsStandIn = false, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var fingerprint = string.Join('\u001f', displayName, upn, email, rolesCsv);
        if (_recent.TryGetValue(userId, out var seen) && seen.Fingerprint == fingerprint && now - seen.At < TouchEvery) return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Users.FirstOrDefaultAsync(u => u.Sid == userId, ct);
        if (row is null) db.Users.Add(row = new AppUser { Sid = userId });
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
    }

    public async Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct);
    }

    private static string Cut(string s) => s.Length > 256 ? s[..256] : s;
}
