using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Domain.Common;
using Tide.Domain.Entities;

namespace Tide.Application.Intake;

/// <summary>How far one process's fill of the address hashes has got (<see cref="UrlHashFill"/>).</summary>
public sealed class UrlHashProgress
{
    /// <summary>Rows given a hash by this process.</summary>
    public int Filled { get; internal set; }

    /// <summary>Whether every row has its hash and the pending setting is gone.</summary>
    public bool Done { get; internal set; }

    // The last id read in each table: rows are read in id order after it (keyset paging), so a row with no canonical
    // address, which keeps no hash, is read once and passed. A count of those skipped would drift if one were deleted
    // or edited meanwhile; an id doesn't. A row saved meanwhile is hashed by its own save, wherever it falls.
    internal Guid? LastArticle { get; set; }
    internal Guid? LastLink { get; set; }
}

/// <summary>
/// Stores the hash of the canonical address (<see cref="Article.SourceUrlHash"/>, <see cref="ArticleLink.UrlHash"/>) on
/// the rows saved before it was kept. Canonicalizing is the app's own code, so the rows are read and written here, a
/// batch at a time, each batch's hashes written by one UPDATE (a CASE on the id); no article's modified time, edit
/// version or history moves. Run by a background job after start-up, so a large database doesn't hold up the start.
/// Each batch is committed as it goes, so a fill that stops (a recycle, the lease moving) carries on where it got to.
/// <para>
/// The migration that added the hashes writes the <see cref="PendingKey"/> setting when there were addresses to fill (an
/// upgrade), and the fill deletes it when none is left. While it's there, triage's past coverage matches addresses the
/// old way (<see cref="DoneAsync"/>), so it's complete either way. A new install never has it. Finishing is a delete, so
/// two processes finishing at once (an overlapping recycle) can't collide: the second deletes nothing.
/// </para>
/// </summary>
public static class UrlHashFill
{
    /// <summary>The setting that says some stored addresses may still be without their hash.</summary>
    public const string PendingKey = "Data:UrlHashesToFill";

    /// <summary>Rows read and written at a time: three parameters each in its UPDATE, well inside SQL Server's 2,100.</summary>
    public const int Batch = 300;

    /// <summary>Whether every stored address has its hash (the pending setting is gone).</summary>
    public static async Task<bool> DoneAsync(IAppDbContext db, CancellationToken ct = default) =>
        !await db.Settings.AsNoTracking().AnyAsync(s => s.Key == PendingKey, ct);

    /// <summary>The rows still to be read: each table's rows without a hash after the last read.</summary>
    public static async Task<int> LeftAsync(IAppDbContext db, UrlHashProgress progress, CancellationToken ct = default) =>
        await UnhashedArticles(db, progress.LastArticle).CountAsync(ct) + await UnhashedLinks(db, progress.LastLink).CountAsync(ct);

    // Each table's rows without a hash, after the last id read (all of them before the first batch).
    private static IQueryable<Article> UnhashedArticles(IAppDbContext db, Guid? after)
    {
        var q = db.Articles.AsNoTracking().Where(a => a.SourceUrl != null && a.SourceUrlHash == null);
        return after is { } last ? q.Where(a => a.Id.CompareTo(last) > 0) : q;
    }

    private static IQueryable<ArticleLink> UnhashedLinks(IAppDbContext db, Guid? after)
    {
        var q = db.ArticleLinks.AsNoTracking().Where(l => l.UrlHash == null);
        return after is { } last ? q.Where(l => l.Id.CompareTo(last) > 0) : q;
    }

    /// <summary>
    /// Fills up to <paramref name="batches"/> batches of <paramref name="batch"/> rows, articles first, then links, and
    /// deletes the pending setting once none is left. Returns whether it's done.
    /// </summary>
    public static async Task<bool> FillAsync(IAppDbContext db, UrlHashProgress progress, int batches, int batch = Batch, CancellationToken ct = default)
    {
        if (progress.Done || await DoneAsync(db, ct)) return progress.Done = true;
        for (var i = 0; i < batches; i++)
        {
            var rows = await UnhashedArticles(db, progress.LastArticle).OrderBy(a => a.Id).Take(batch)
                .Select(a => new KeyValuePair<Guid, string?>(a.Id, a.SourceUrl)).ToListAsync(ct);
            var table = rows.Count > 0 ? 0 : 1;
            if (table == 1)
                rows = await UnhashedLinks(db, progress.LastLink).OrderBy(l => l.Id).Take(batch)
                    .Select(l => new KeyValuePair<Guid, string?>(l.Id, l.Url)).ToListAsync(ct);
            if (rows.Count == 0)
            {
                await FinishAsync(db, ct);
                return progress.Done = true;
            }
            var hashed = rows.Select(r => (r.Key, Hash: UrlCanonicalizer.HashOf(r.Value))).Where(r => r.Hash is not null)
                .Select(r => (Id: r.Key, Hash: r.Hash!)).ToList();
            if (table == 0)
            {
                progress.LastArticle = rows[^1].Key;
                if (hashed.Count > 0) await StoreAsync(db.Articles.Where(a => a.SourceUrlHash == null), a => a.SourceUrlHash, hashed, ct);
            }
            else
            {
                progress.LastLink = rows[^1].Key;
                if (hashed.Count > 0) await StoreAsync(db.ArticleLinks.Where(l => l.UrlHash == null), l => l.UrlHash, hashed, ct);
            }
            progress.Filled += hashed.Count;
        }
        return false;
    }

    /// <summary>Says every address has its hash: deletes the pending setting, which is the same however many times it's
    /// done, so another process finishing at the same moment changes nothing.</summary>
    public static Task FinishAsync(IAppDbContext db, CancellationToken ct = default) =>
        db.Settings.Where(s => s.Key == PendingKey).ExecuteDeleteAsync(ct);

    // One UPDATE for the batch (CaseUpdate). Only a row still without a hash is written: one saved meanwhile has its own,
    // from its own address.
    private static Task<int> StoreAsync<T>(IQueryable<T> unhashed, Expression<Func<T, string?>> column, IReadOnlyList<(Guid Id, string Hash)> rows,
        CancellationToken ct) where T : Entity =>
        CaseUpdate.RunAsync(unhashed, [column], rows.Select(r => (r.Id, new string?[] { r.Hash })).ToList(), ct);
}
