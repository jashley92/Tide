using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;

namespace Tide.Application.Files;

/// <summary>
/// The removed-file byte purge, off unless <c>Files:PurgeRemovedAfterDays</c> is set (decided 2026-11-21: a setting, off by
/// default; the row and hash stay). A stored file's bytes are deleted only when every place that used it is an action's
/// file or a comment's, each removed (or its action deleted) longer ago than that, and no article, brief send or live file
/// still uses it. Its row records when (<see cref="StoredFile.BytesPurgedAtUtc"/>), and the action's removed files say so;
/// such a file can't be restored.
/// </summary>
public static class RemovedFilePurge
{
    /// <summary>The shortest wait: a month to change one's mind and restore a file.</summary>
    public const int MinimumDays = 30;

    public const int Batch = 200;

    /// <summary>The wait in force: null (never purge) when unset or not positive, else at least <see cref="MinimumDays"/>.</summary>
    public static int? Days(int? configured) => configured is { } d and > 0 ? Math.Max(d, MinimumDays) : null;

    /// <summary>Purges the bytes of up to <paramref name="batches"/> batches of files past the wait; how many it purged.</summary>
    public static async Task<int> RunAsync(IAppDbContext db, IFileStore store, int? configuredDays, DateTimeOffset now, int batches = 10,
        CancellationToken ct = default)
    {
        if (Days(configuredDays) is not { } days) return 0;
        var cutoff = now.AddDays(-days);
        var purged = 0;
        for (var i = 0; i < batches; i++)
        {
            var ids = await Due(db, cutoff).OrderBy(id => id).Take(Batch).ToListAsync(ct);
            if (ids.Count == 0) break;
            foreach (var id in ids) store.Delete(id);
            purged += await db.Files.IgnoreQueryFilters().Where(f => ids.Contains(f.Id) && f.BytesPurgedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.BytesPurgedAtUtc, now), ct);
        }
        return purged;
    }

    // The stored files whose every use is an action's or a comment's file gone (removed, or its action deleted) since
    // before the cutoff, and nothing else uses them.
    private static IQueryable<Guid> Due(IAppDbContext db, DateTimeOffset cutoff)
    {
        var actions = db.Actions.IgnoreQueryFilters();
        var gone = actions.Where(a => a.DeletedAtUtc != null && a.DeletedAtUtc < cutoff).Select(a => a.Id);
        var actionFiles = db.ActionFiles.IgnoreQueryFilters();
        var commentFiles = db.ActionCommentFiles.IgnoreQueryFilters()
            .Join(db.ActionComments.IgnoreQueryFilters(), f => f.CommentId, c => c.Id, (f, c) => new { f.FileId, f.RemovedAtUtc, c.ActionId });
        var oldAction = actionFiles.Where(f => (f.RemovedAtUtc != null && f.RemovedAtUtc < cutoff) || gone.Contains(f.ActionId)).Select(f => f.FileId);
        var oldComment = commentFiles.Where(f => (f.RemovedAtUtc != null && f.RemovedAtUtc < cutoff) || gone.Contains(f.ActionId)).Select(f => f.FileId);
        var stillAction = actionFiles.Where(f => !((f.RemovedAtUtc != null && f.RemovedAtUtc < cutoff) || gone.Contains(f.ActionId))).Select(f => f.FileId);
        var stillComment = commentFiles.Where(f => !((f.RemovedAtUtc != null && f.RemovedAtUtc < cutoff) || gone.Contains(f.ActionId))).Select(f => f.FileId);
        return db.Files.IgnoreQueryFilters().Where(f => f.BytesPurgedAtUtc == null
                && (oldAction.Contains(f.Id) || oldComment.Contains(f.Id))
                && !stillAction.Contains(f.Id) && !stillComment.Contains(f.Id)
                && !db.ArticleScreenshots.Any(s => s.FileId == f.Id)
                && !db.BriefSendImages.Any(b => b.FileId == f.Id || b.SourceFileId == f.Id))
            .Select(f => f.Id);
    }
}
