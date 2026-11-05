using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;

namespace Tide.Application.Distribution;

/// <summary>
/// Where a test email to the signed-in user goes: the address AD gave this session (the <c>mail</c> attribute), or, when
/// AD couldn't be reached and there's none, the one the user mirror saved from AD on an earlier visit. Never a UPN.
/// </summary>
public static class OwnAddress
{
    public static async Task<string?> ForAsync(ICurrentUser user, IAppDbContextFactory dbs, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(user.Email)) return user.Email.Trim();
        if (!user.IsAuthenticated) return null;
        await using var db = await dbs.CreateAsync(ct);
        var saved = await db.Users.AsNoTracking().Where(u => u.Sid == user.UserId).Select(u => u.Email).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(saved) ? null : saved.Trim();
    }
}
