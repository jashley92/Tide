using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;

namespace Tide.Application.Preferences;

/// <summary>People (by user id) a notification may go to.</summary>
/// <param name="Addresses">The address of each who has one, opted out or not, in the order found.</param>
/// <param name="OptedOut">Those who turned the kind off in My notifications.</param>
internal sealed record Recipients(IReadOnlyDictionary<string, string> Addresses, IReadOnlySet<string> OptedOut)
{
    /// <summary>The addresses of those who haven't opted out, in the order found.</summary>
    public List<string> Reachable() => [.. Addresses.Where(a => !OptedOut.Contains(a.Key)).Select(a => a.Value)];
}

/// <summary>
/// Looks up where people's notifications of a kind go, and who opted out of it. The senders run as the system, so this
/// reads others' choices; it says only who not to email. A team mailbox and fallback addresses are addresses, not people:
/// they never come through here, so they never opt out.
/// </summary>
internal static class RecipientResolver
{
    public static async Task<Recipients> ResolveAsync(IAppDbContext db, NotificationKind kind, IEnumerable<string> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        var addresses = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Sid) && u.Email != null && u.Email != "")
            .ToDictionaryAsync(u => u.Sid, u => u.Email!, ct);
        if (ids.Count == 0) return new Recipients(addresses, new HashSet<string>());
        var off = await db.UserPreferences.AsNoTracking()
            .Where(p => p.Name == kind.Name && p.Value == UserPreferences.Off && ids.Contains(p.UserId))
            .Select(p => p.UserId).ToListAsync(ct);
        return new Recipients(addresses, off.ToHashSet());
    }
}
