using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.Application.Preferences;

/// <summary>Who a reminder may still go to: someone whose roles, as last seen at sign-in, grant <paramref name="Needed"/>,
/// and who signed in at or after <paramref name="SeenSince"/>. Anyone else has lost access, or left, as far as TIDE can
/// tell.</summary>
internal sealed record ActiveRule(Permission Needed, DateTimeOffset SeenSince)
{
    public bool Allows(string rolesCsv, DateTimeOffset lastSeenUtc) => lastSeenUtc >= SeenSince && StoredRoles.Has(rolesCsv, Needed);
}

/// <summary>People (by user id) a notification may go to.</summary>
/// <param name="Addresses">The address of each who has one, opted out or not, in the order found; never one of the
/// inactive.</param>
/// <param name="OptedOut">Those who turned the kind off in My notifications.</param>
/// <param name="Inactive">Those with an address the <see cref="ActiveRule"/> left out: their roles no longer grant what the
/// email shows, or they haven't signed in lately. They're treated as having no address.</param>
internal sealed record Recipients(IReadOnlyDictionary<string, string> Addresses, IReadOnlySet<string> OptedOut, IReadOnlySet<string> Inactive)
{
    /// <summary>The addresses of those who haven't opted out, in the order found.</summary>
    public List<string> Reachable() => [.. Addresses.Where(a => !OptedOut.Contains(a.Key)).Select(a => a.Value)];
}

/// <summary>
/// Looks up where people's notifications of a kind go, and who opted out of it. The senders run as the system, so this
/// reads others' choices; it says only who not to email. A reminder also passes an <see cref="ActiveRule"/>: someone whose
/// roles no longer grant what it shows, or who hasn't signed in within <c>Notifications:ActiveWithinDays</c>, is left out
/// (<see cref="Recipients.Inactive"/>). A team mailbox and fallback addresses are addresses, not people: they never come
/// through here, so they never opt out and are never inactive.
/// </summary>
internal static class RecipientResolver
{
    /// <summary>The people's addresses and who opted out of <paramref name="kind"/> (none can, for a null kind: a scan nobody
    /// chooses about); with <paramref name="active"/>, without those it leaves out.</summary>
    public static async Task<Recipients> ResolveAsync(IAppDbContext db, NotificationKind? kind, IEnumerable<string> userIds, CancellationToken ct,
        ActiveRule? active = null)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return new Recipients(new Dictionary<string, string>(), new HashSet<string>(), new HashSet<string>());
        var people = await WithAddress(db, ids).Select(u => new { u.Sid, u.Email, u.RolesCsv, u.LastSeenUtc }).ToListAsync(ct);
        var inactive = active is null ? [] : people.Where(p => !active.Allows(p.RolesCsv, p.LastSeenUtc)).Select(p => p.Sid).ToHashSet();
        var addresses = people.Where(p => !inactive.Contains(p.Sid)).ToDictionary(p => p.Sid, p => p.Email!);
        if (kind is null) return new Recipients(addresses, new HashSet<string>(), inactive);
        var off = await db.UserPreferences.AsNoTracking()
            .Where(p => p.Name == kind.Name && p.Value == UserPreferences.Off && ids.Contains(p.UserId))
            .Select(p => p.UserId).ToListAsync(ct);
        return new Recipients(addresses, off.ToHashSet(), inactive);
    }

    /// <summary>The address of each of these people who has one (the user mirror, as signed in from the directory).</summary>
    public static Task<Dictionary<string, string>> AddressesAsync(IAppDbContext db, IEnumerable<string> userIds, CancellationToken ct) =>
        WithAddress(db, userIds.Distinct().ToList()).ToDictionaryAsync(u => u.Sid, u => u.Email!, ct);

    // These people, those of them with an address.
    private static IQueryable<Domain.Entities.AppUser> WithAddress(IAppDbContext db, List<string> ids) =>
        db.Users.AsNoTracking().Where(u => ids.Contains(u.Sid) && u.Email != null && u.Email != "");
}
