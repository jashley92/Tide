using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;

namespace Tide.Application.History;

/// <summary>Reading the audit trail's keys back.</summary>
internal static class AuditKeys
{
    /// <summary>
    /// What was tagged on articles most recently, by the audit trail: an article's tag (<paramref name="entityType"/>,
    /// as ArticleAdversary) is keyed "article/tagged" there. The newest <paramref name="take"/> additions are read; the
    /// tagged ids come newest first, each once.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> RecentTaggedAsync(IAppDbContext db, string entityType, int take, CancellationToken ct)
    {
        var keys = await db.AuditLog.AsNoTracking()
            .Where(e => e.EntityType == entityType && e.Action == nameof(EntityState.Added))
            .OrderByDescending(e => e.AtUtc).ThenBy(e => e.Id).Select(e => e.EntityKey).Take(take).ToListAsync(ct);
        return keys.Select(k => Guid.TryParse(k[(k.IndexOf('/') + 1)..], out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();
    }
}
