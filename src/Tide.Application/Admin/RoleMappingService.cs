using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Admin;

/// <summary>Which AD groups hold which TIDE role. Each change reloads the role directory so it takes effect at once.</summary>
public sealed class RoleMappingService(IAppDbContextFactory dbs, ICurrentUser user, IRoleDirectory directory)
{
    public async Task<IReadOnlyList<AdGroupRoleMapping>> ListAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        return await db.RoleMappings.AsNoTracking().OrderBy(m => m.RoleName).ThenBy(m => m.AdGroup).ToListAsync(ct);
    }

    public async Task AddAsync(string adGroup, AppRole role, CancellationToken ct = default)
    {
        RequireAdmin();
        var group = (adGroup ?? "").Trim();
        if (group.Length == 0) throw new ArgumentException("Name the AD group (DOMAIN\\Group, Group, or its SID).");
        if (group.Length > 256) throw new ArgumentException("That group name is too long.");
        if (!Enum.IsDefined(role)) throw new ArgumentException("That isn't a TIDE role.");
        await using var db = await dbs.CreateAsync(ct);
        var roleName = role.ToString();
        // A group name is matched ignoring case (as the directory matches it) on both providers: SQL Server's collation
        // would refuse "domain\group" beside "DOMAIN\Group" at the unique index, so it's a no-op here instead.
        var exact = Search.SearchText.Literal(group);
        if (await db.RoleMappings.AnyAsync(m => EF.Functions.Like(m.AdGroup, exact, Search.SearchText.Escape) && m.RoleName == roleName, ct)) return;
        db.RoleMappings.Add(new AdGroupRoleMapping
        {
            AdGroup = group, RoleName = roleName, UpdatedAtUtc = DateTimeOffset.UtcNow, UpdatedBy = user.UserId,
        });
        await db.SaveChangesAsync(ct);
        directory.Invalidate();
    }

    /// <summary>Removes a mapping. The last mapping granting Admin can't be removed, so TIDE can't lock itself out.</summary>
    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var outer = await dbs.CreateAsync(ct);
        // The count and the removal in one serializable transaction: two administrators removing the last two Admin
        // mappings at once each counted two and both went, leaving no one able to administer. SQLite takes the write lock
        // at the start, so the second waits and counts one; SQL Server keeps the rows counted locked, so one of the two is
        // chosen as a deadlock victim and retried by the execution strategy, and then counts one. Inside the strategy
        // (a retrying strategy refuses a transaction opened outside it), each try on a context of its own, so a failed
        // try's removal and audit entry aren't saved again with the next.
        var strategy = outer.Database.CreateExecutionStrategy();
        var removed = await strategy.ExecuteAsync(async () =>
        {
            await using var db = await dbs.CreateAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            var row = await db.RoleMappings.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (row is null) return false;
            if (row.RoleName == nameof(AppRole.Admin) && await db.RoleMappings.CountAsync(m => m.RoleName == nameof(AppRole.Admin), ct) == 1)
                throw new InvalidOperationException("That's the only group granting Admin. Add another before removing it.");
            db.RoleMappings.Remove(row);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        });
        if (removed) directory.Invalidate();
    }

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can change role mappings.");
    }
}
