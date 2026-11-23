using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Security;

/// <summary>A stored user's roles (<c>RolesCsv</c>, as last seen at sign-in), for those who act on others' behalf.</summary>
internal static class StoredRoles
{
    /// <summary>Whether the roles in <paramref name="rolesCsv"/> grant <paramref name="permission"/> (names that aren't a
    /// role are ignored).</summary>
    public static bool Has(string rolesCsv, Permission permission) =>
        RoleDefinitions.PermissionsForRoleNames(rolesCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Contains(permission);

    /// <summary>Everyone with a name whose stored roles let them write articles: who may review an article, or be
    /// handed one.</summary>
    public static async Task<IReadOnlyList<AppUser>> WritersAsync(IAppDbContext db, CancellationToken ct)
    {
        // The roles that can write articles narrow the read; the stored roles as a whole then decide.
        var people = new Dictionary<string, AppUser>();
        foreach (var role in WriterRoles)
            foreach (var u in await db.Users.AsNoTracking().Where(u => u.RolesCsv.Contains(role)).ToListAsync(ct))
                people.TryAdd(u.Sid, u);
        return [.. people.Values.Where(u => u.DisplayName.Length > 0 && Has(u.RolesCsv, Permission.WriteArticles))];
    }

    private static readonly string[] WriterRoles = RoleDefinitions.SystemRoles
        .Where(r => r.Value.Contains(Permission.WriteArticles)).Select(r => r.Key.ToString()).ToArray();
}
