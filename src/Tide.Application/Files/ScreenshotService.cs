using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Files;

/// <summary>Limits on uploaded files (config section <c>Files</c>).</summary>
public sealed class FileStoreOptions
{
    /// <summary>Where the bytes are kept: an absolute path outside the web root in production.</summary>
    public string RootPath { get; set; } = "App_Data/files";

    /// <summary>The largest screenshot accepted, in megabytes.</summary>
    public int MaxImageMB { get; set; } = 15;

    /// <summary>The largest evidence file (any kind, attached to an action's comment) accepted, in megabytes.</summary>
    public int MaxEvidenceMB { get; set; } = 25;

    /// <summary>When an action's file downloads inside a zip with the password "infected" by default; set in the app
    /// (Administration › Teams &amp; SLA), saved over this.</summary>
    public ZipDownloads ZipDownloads { get; set; } = ZipDownloads.RiskyOnly;

    /// <summary>The image limit in bytes (at least 1 MB, whatever is configured).</summary>
    public long MaxImageBytes => Math.Max(1, MaxImageMB) * 1024L * 1024L;

    /// <summary>The limit for any other file, in bytes (at least 1 MB).</summary>
    public long MaxEvidenceBytes => Math.Max(1, MaxEvidenceMB) * 1024L * 1024L;
}

/// <summary>A screenshot or file as it arrives from the browser (pasted, dropped or picked), with the type the browser
/// declared for it when known (only recorded and judged, never trusted).</summary>
public sealed record ScreenshotUpload(string FileName, byte[] Bytes, string? DeclaredType = null);

/// <summary>Screenshots on articles: stored as files on disk, checked to be real raster images, shown inline.</summary>
public sealed class ScreenshotService(IAppDbContextFactory dbs, ICurrentUser user, IFileStore store, IOptions<FileStoreOptions> options,
    Briefs.IMeetingsInProgress? meetings = null)
{
    public long MaxBytes => options.Value.MaxImageBytes;

    private long MaxEvidenceBytes => options.Value.MaxEvidenceBytes;

    /// <summary>The largest file a comment takes: an evidence file's limit, or a screenshot's if that's larger.</summary>
    public long MaxAttachmentBytes => Math.Max(MaxBytes, MaxEvidenceBytes);

    /// <summary>
    /// Adds screenshots to the end of an article, all or none: every file is checked before the first is stored.
    /// They take the article's TLP. Returns the new screenshots. The article's edit version is left alone: an editor open
    /// on it saves captions only for the screenshots it shows, so adding one doesn't make its form stale.
    /// </summary>
    public async Task<IReadOnlyList<ArticleScreenshot>> AddAsync(Guid articleId, IReadOnlyList<ScreenshotUpload> uploads,
        CancellationToken ct = default)
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts can add screenshots.");
        if (uploads.Count == 0) return [];

        await using var db = await dbs.CreateAsync(ct);
        var article = await db.Articles.Include(a => a.Screenshots).FirstOrDefaultAsync(a => a.Id == articleId, ct)
                      ?? throw new KeyNotFoundException("That article no longer exists.");
        var room = Article.MaxScreenshots - article.Screenshots.Count;
        if (uploads.Count > room)
            throw new ArgumentException(room <= 0
                ? $"An article can have at most {Article.MaxScreenshots} screenshots."
                : $"Only {room} more screenshot{(room == 1 ? "" : "s")} fit on this article (the most is {Article.MaxScreenshots}).");

        var checkedFiles = uploads.Select(u => (Upload: u, Type: Checked(u))).ToList();

        var added = new List<ArticleScreenshot>();
        var written = new List<Guid>();
        try
        {
            var order = article.Screenshots.Count == 0 ? 0 : article.Screenshots.Max(s => s.SortOrder) + 1;
            foreach (var (u, type) in checkedFiles)
            {
                var file = await WriteAsync(store, SafeName(u.FileName, type), type, u.Bytes, ct);
                written.Add(file.Id);
                db.Files.Add(file);
                var shot = new ArticleScreenshot { ArticleId = article.Id, FileId = file.Id, SortOrder = order++, Tlp = article.Tlp };
                db.ArticleScreenshots.Add(shot);
                added.Add(shot);
            }
            await MarkChangedAsync(db, article.Id, ct);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            foreach (var id in written) store.Delete(id);
            throw;
        }
        return added;
    }

    /// <summary>Removes a screenshot from its article and deletes the file.</summary>
    public async Task RemoveAsync(Guid screenshotId, CancellationToken ct = default)
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts can remove screenshots.");
        await using var db = await dbs.CreateAsync(ct);
        var shot = await db.ArticleScreenshots.FirstOrDefaultAsync(s => s.Id == screenshotId, ct);
        if (shot is null) return;
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == shot.FileId, ct);
        db.ArticleScreenshots.Remove(shot);
        if (file is not null) db.Files.Remove(file);
        await MarkChangedAsync(db, shot.ArticleId, ct);
        await db.SaveChangesAsync(ct);
        store.Delete(shot.FileId);
    }

    // Records on the article when its screenshots changed (an older review reads it; ReviewService). Written past the
    // article's edit version and the audit trail, whose screenshot rows already say what changed, and before the change
    // itself: a save that then fails leaves an approval out of date, never one standing that shouldn't.
    private static Task MarkChangedAsync(IAppDbContext db, Guid articleId, CancellationToken ct) =>
        db.Articles.Where(a => a.Id == articleId).ExecuteUpdateAsync(u => u.SetProperty(a => a.ScreenshotsChangedAtUtc, DateTimeOffset.UtcNow), ct);

    /// <summary>What the inline image endpoint answers: nothing (null: not allowed, or no such image), the entity tag alone
    /// (the browser's copy is current), or the bytes with their sniffed type and the entity tag.</summary>
    public sealed record InlineImage(string ETag, Stream? Content = null, string? ContentType = null)
    {
        public bool NotModified => Content is null;
    }

    /// <summary>
    /// An image for the inline image endpoint. The viewer's access and the file's hash are read together, and when
    /// <paramref name="ifNoneMatch"/> (the browser's If-None-Match header) names that hash the answer is the tag alone,
    /// without opening the file: that's the common case, once per image per page view. Who may see what:
    /// <list type="bullet">
    /// <item>A screenshot on an article: the team (anyone with the team edition), whatever its marking; the meeting and the
    /// previews show the team edition.</item>
    /// <item>A frozen copy in a send: the team for any send; a Reader only for a copy in a sent company-wide edition. A
    /// Reader never gets a live screenshot, only what was actually published, so an image above the public ceiling (or
    /// one never sent) can't be fetched by guessing its id.</item>
    /// </list>
    /// </summary>
    public async Task<InlineImage?> OpenAsync(Guid fileId, string? ifNoneMatch = null, CancellationToken ct = default)
    {
        string? sha256;
        await using (var db = await dbs.CreateAsync(ct))
        {
            if (!await MaySeeAsync(db, fileId, ct)) return null;
            sha256 = await db.Files.AsNoTracking().Where(f => f.Id == fileId).Select(f => f.Sha256).FirstOrDefaultAsync(ct);
        }
        if (sha256 is null) return null;
        var etag = "\"" + sha256 + "\"";
        if (ifNoneMatch is not null && Matches(ifNoneMatch, etag)) return new InlineImage(etag);
        if (store.OpenRead(fileId) is not { } stream) return null;

        // Trust the bytes, not the stored type: only a raster image is ever served inline.
        var header = new byte[16];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        var type = ImageSniffer.RasterContentType(header.AsSpan(0, read));
        if (type is null)
        {
            await stream.DisposeAsync();
            return null;
        }
        stream.Position = 0;
        return new InlineImage(etag, stream, type);
    }

    // Whether an If-None-Match header names the tag: "*", or the tag among its (possibly weak, W/"…") values.
    private static bool Matches(string ifNoneMatch, string etag)
    {
        var strong = etag.Trim();
        foreach (var raw in ifNoneMatch.Split(','))
        {
            var v = raw.Trim();
            if (v == "*") return true;
            if (v.StartsWith("W/", StringComparison.Ordinal)) v = v[2..];
            if (v == strong) return true;
        }
        return false;
    }

    /// <summary>
    /// Stores a screenshot of a hunt's result as evidence (checked to be a real raster image, like any screenshot), and
    /// returns its file id for <c>ActionService.RecordHuntResultAsync</c>.
    /// </summary>
    public async Task<Guid> StoreEvidenceAsync(ScreenshotUpload upload, CancellationToken ct = default)
    {
        if (!user.Has(Permission.WorkActions) && !user.Has(Permission.WriteArticles)) throw new ForbiddenException("You can't add evidence to actions.");
        var type = Checked(upload);
        var file = await WriteAsync(store, SafeName(upload.FileName, type), type, upload.Bytes, ct);
        try
        {
            await using var db = await dbs.CreateAsync(ct);
            db.Files.Add(file);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            store.Delete(file.Id);
            throw;
        }
        return file.Id;
    }

    /// <summary>
    /// Deletes evidence stored by <see cref="StoreEvidenceAsync"/> that was never used: the recording it was for was
    /// refused (a colleague recorded the query first, say). A file anything holds (a hunt, a comment, an article, a send)
    /// is left alone, so this can't take bytes from under them.
    /// </summary>
    public async Task DiscardEvidenceAsync(Guid fileId, CancellationToken ct = default)
    {
        if (!user.Has(Permission.WorkActions) && !user.Has(Permission.WriteArticles)) throw new ForbiddenException("You can't add evidence to actions.");
        await using var db = await dbs.CreateAsync(ct);
        if (await InUseAsync(db, fileId, null, ct) || await db.BriefSendImages.AnyAsync(i => i.SourceFileId == fileId, ct))
            return;
        if (await db.Files.FirstOrDefaultAsync(f => f.Id == fileId, ct) is not { } file) return;
        db.Files.Remove(file);
        await db.SaveChangesAsync(ct);
        store.Delete(fileId);
    }

    /// <summary>Whether anything holds the stored file: a hunt (other than <paramref name="exceptHuntId"/>), a comment, an
    /// action, an article's screenshot or a frozen send's image. A deleted action's are in use too: it can be restored.</summary>
    internal static async Task<bool> InUseAsync(IAppDbContext db, Guid fileId, Guid? exceptHuntId, CancellationToken ct) =>
        await db.HuntQueries.IgnoreQueryFilters([ActionItem.DeletedFilter]).AnyAsync(h => h.EvidenceFileId == fileId && h.Id != exceptHuntId, ct)
        || await db.ActionCommentFiles.IgnoreQueryFilters([ActionItem.DeletedFilter]).AnyAsync(f => f.FileId == fileId, ct)
        || await db.ActionFiles.IgnoreQueryFilters([ActionItem.DeletedFilter]).AnyAsync(f => f.FileId == fileId, ct)
        || await db.ArticleScreenshots.AnyAsync(s => s.FileId == fileId, ct)
        || await db.BriefSendImages.AnyAsync(i => i.FileId == fileId, ct);

    /// <summary>
    /// Checks and writes a comment's files, before the comment is saved: each one a screenshot (its bytes a raster image,
    /// within the screenshot limit) or an evidence file (any other bytes, within the evidence limit, only ever served as
    /// a download). Returns the files to add to the database with the comment; if that save fails, the caller passes
    /// their ids to <see cref="Discard"/>.
    /// </summary>
    public async Task<IReadOnlyList<(StoredFile File, bool IsImage)>> WriteAttachmentsAsync(IReadOnlyList<ScreenshotUpload> uploads,
        CancellationToken ct = default)
    {
        if (uploads.Count > ActionCommentFile.MaxPerComment)
            throw new ArgumentException($"A comment can have at most {ActionCommentFile.MaxPerComment} files.");
        var checkedFiles = uploads.Select(u =>
        {
            if (u.Bytes.Length == 0) throw new ArgumentException($"'{u.FileName}' is empty.");
            var image = ImageSniffer.RasterContentType(u.Bytes);
            if (image is not null && u.Bytes.Length > MaxBytes) throw new ArgumentException($"'{u.FileName}' is over the {options.Value.MaxImageMB} MB limit for screenshots.");
            if (image is null && u.Bytes.Length > MaxEvidenceBytes) throw new ArgumentException($"'{u.FileName}' is over the {options.Value.MaxEvidenceMB} MB limit for evidence files.");
            return (Upload: u, Image: image);
        }).ToList();

        var written = await WriteAllAsync(store, checkedFiles.Select(c =>
            (c.Image is not null ? SafeName(c.Upload.FileName, c.Image) : EvidenceName(c.Upload.FileName), c.Image ?? "application/octet-stream", c.Upload.Bytes)), ct);
        return written.Select((file, i) => (file, checkedFiles[i].Image is not null)).ToList();
    }

    /// <summary>
    /// Writes each file to the store, in order, and returns their rows for the caller to add to the database in one save
    /// (deleting them from the store if that save fails). All or none: if one can't be written, those written before it
    /// are deleted.
    /// </summary>
    internal static async Task<IReadOnlyList<StoredFile>> WriteAllAsync(IFileStore store,
        IEnumerable<(string FileName, string ContentType, byte[] Bytes)> files, CancellationToken ct)
    {
        var written = new List<StoredFile>();
        try
        {
            foreach (var (name, type, bytes) in files) written.Add(await WriteAsync(store, name, type, bytes, ct));
        }
        catch
        {
            foreach (var f in written) store.Delete(f.Id);
            throw;
        }
        return written;
    }

    // A screenshot's type, once it's checked to be a raster image within the limit.
    private string Checked(ScreenshotUpload u)
    {
        if (u.Bytes.Length == 0) throw new ArgumentException($"'{u.FileName}' is empty.");
        if (u.Bytes.Length > MaxBytes) throw new ArgumentException($"'{u.FileName}' is over the {options.Value.MaxImageMB} MB limit.");
        return ImageSniffer.RasterContentType(u.Bytes) ?? throw new ArgumentException($"'{u.FileName}' isn't a PNG, JPEG, GIF or WebP image.");
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to the store and returns the file's row, for the caller to add to the database in
    /// the same save as whatever holds it (or to delete from the store if that save fails).
    /// </summary>
    internal static async Task<StoredFile> WriteAsync(IFileStore store, string fileName, string contentType, byte[] bytes, CancellationToken ct)
    {
        var file = new StoredFile
        {
            FileName = fileName, ContentType = contentType, SizeBytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        };
        using (var ms = new MemoryStream(bytes, writable: false)) await store.WriteAsync(file.Id, ms, ct);
        return file;
    }

    /// <summary>Deletes the bytes of files that were written but never saved.</summary>
    public void Discard(IEnumerable<Guid> fileIds)
    {
        foreach (var id in fileIds) store.Delete(id);
    }

    /// <summary>
    /// A file to download (the screenshot dialog's Download): its bytes and its name. A comment's files (screenshots and
    /// evidence) and a hunt's evidence screenshot, for anyone who reads actions; an article's screenshot, for the team
    /// (who see it inline). Nothing else is served this way.
    /// </summary>
    public async Task<(Stream Content, string FileName)?> OpenAttachmentAsync(Guid fileId, CancellationToken ct = default)
    {
        var team = user.Has(Permission.ReadTeamEdition);
        if (!team && !user.Has(Permission.WorkActions)) return null;
        await using var db = await dbs.CreateAsync(ct);
        var name = await db.ActionCommentFiles.AsNoTracking().Where(f => f.FileId == fileId && f.RemovedAtUtc == null).Select(f => f.FileName).FirstOrDefaultAsync(ct);
        if (name is null && (await db.HuntQueries.AnyAsync(h => h.EvidenceFileId == fileId, ct)
                             || (team && await db.ArticleScreenshots.AnyAsync(s => s.FileId == fileId, ct))))
            name = await db.Files.AsNoTracking().Where(f => f.Id == fileId).Select(f => f.FileName).FirstOrDefaultAsync(ct);
        if (name is null || store.OpenRead(fileId) is not { } stream) return null;
        return (stream, name);
    }

    /// <summary>Whether what this user is served inline mustn't be kept by the browser: a Visitor's slide images are theirs
    /// only while they're in the meeting, so the next look asks again (and is refused once it's over).</summary>
    public bool NoStore => Briefs.MeetingAccess.MeetingOnly(user);

    private async Task<bool> MaySeeAsync(IAppDbContext db, Guid fileId, CancellationToken ct)
    {
        // A Visitor, from a seat in a live meeting: a screenshot on an article of that meeting's brief (MeetingAccess). Not
        // an action's or a comment's file, nor another brief's. Then whatever a Reader may see.
        if (Briefs.MeetingAccess.Seats(user, meetings) is { Count: > 0 } seats)
        {
            var briefs = seats.Select(x => x.BriefId).ToList();
            if (await (from s in db.ArticleScreenshots
                       join i in db.BriefItems on s.ArticleId equals i.ArticleId
                       where s.FileId == fileId && briefs.Contains(i.BriefId)
                       select s.Id).AnyAsync(ct))
                return true;
        }
        var team = user.Has(Permission.ReadTeamEdition);
        if (!team && !user.Has(Permission.ReadPublished) && !user.Has(Permission.WorkActions)) return false;
        if (team || user.Has(Permission.WorkActions))
        {
            if (await db.HuntQueries.AnyAsync(h => h.EvidenceFileId == fileId, ct)) return true;
            if (await db.ActionCommentFiles.AnyAsync(f => f.FileId == fileId && f.IsImage && f.RemovedAtUtc == null, ct)) return true;
            // An action's own file, only while it's on the action and shown inline (ActionFile.ShowsInlineFor, as SQL).
            if (await db.ActionFiles.AnyAsync(f => f.FileId == fileId && f.RemovedAtUtc == null && f.IsImage && f.TypeNote == null && f.RiskReason == null, ct))
                return true;
            if (!team) return false;
            return await db.ArticleScreenshots.AnyAsync(s => s.FileId == fileId, ct)
                   || await db.BriefSendImages.AnyAsync(i => i.FileId == fileId, ct);
        }
        return await Distribution.DistributionService.Published(db)
            .AnyAsync(s => s.Images.Any(i => i.FileId == fileId), ct);
    }

    internal async Task DeleteFilesAsync(IEnumerable<Guid> fileIds, CancellationToken ct)
    {
        var ids = fileIds.ToList();
        if (ids.Count == 0) return;
        await using var db = await dbs.CreateAsync(ct);
        var rows = await db.Files.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
        db.Files.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        foreach (var id in ids) store.Delete(id);
    }

    // An evidence file's display name: as SafeName, but its own extension kept (it's only ever downloaded, as bytes).
    // The name after its last folder, whichever separator the uploader's machine used: Path.GetFileName only knows the
    // server's own, so on Linux "C:\Windows\evil.exe" would keep its folders.
    private static string LastSegment(string? name) => name is null ? "" : name[(name.LastIndexOfAny(['/', '\\']) + 1)..];

    internal static string EvidenceName(string? name)
    {
        var file = LastSegment(name);
        var clean = new string(file.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' or '.').ToArray()).Trim(' ', '.');
        if (clean.Length == 0) clean = "evidence";
        return clean.Length > 150 ? clean[^150..] : clean;
    }

    // A display name only (the bytes are stored under the file's id): no path, no odd characters, the right extension.
    private static string SafeName(string? name, string contentType)
    {
        var bare = Path.GetFileNameWithoutExtension(LastSegment(name));
        var clean = new string(bare.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' or '.').ToArray()).Trim();
        if (clean.Length == 0) clean = "screenshot";
        if (clean.Length > 100) clean = clean[..100];
        var ext = contentType switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", _ => ".webp" };
        return clean + ext;
    }
}
