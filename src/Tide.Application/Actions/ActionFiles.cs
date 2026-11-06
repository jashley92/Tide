using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Files;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>A file to add to an action, with an optional description.</summary>
public sealed record ActionFileUpload(string FileName, byte[] Bytes, string? DeclaredType = null, string? Description = null);

/// <summary>
/// A file on an action, as its Files section and the API list it: one added to the action itself, or one attached to
/// one of its comments (<see cref="CommentId"/> set). <see cref="FileId"/> is the stored file's id, which every download
/// address uses. <see cref="Sha256"/> is null for a file stored without one.
/// </summary>
public sealed record ActionFileRow(Guid FileId, string FileName, long SizeBytes, string? Sha256, string? ContentType, string AddedByName,
    DateTimeOffset AddedAtUtc, string? Description, bool ShowsInline, string? RiskReason, string? TypeNote)
{
    /// <summary>The comment it's attached to; null for a file added to the action.</summary>
    public Guid? CommentId { get; init; }

    /// <summary>The type the uploader's browser declared, when recorded.</summary>
    public string? DeclaredType { get; init; }

    public string? AddedById { get; init; }

    /// <summary>Whether the current user may remove it: the person who added it (a comment's file: the comment's author),
    /// an analyst or an administrator.</summary>
    public bool CanRemove { get; init; }

    public DateTimeOffset? RemovedAtUtc { get; init; }
    public string? RemovedByName { get; init; }
    public string? RemovedReason { get; init; }

    /// <summary>For a file kept from a deleted comment: that comment's author. It's listed as added by them, at the
    /// comment's time.</summary>
    public string? FromCommentBy { get; init; }

    public bool OnComment => CommentId is not null;

    /// <summary>Whether it is, or was, on a comment: one still there, or kept from a deleted one.</summary>
    public bool FromComment => OnComment || FromCommentBy is not null;

    /// <summary>Where it is, as the API names it: "action" or "comment".</summary>
    public string Source => OnComment ? "comment" : "action";
}

/// <summary>An action's files: the live ones, newest first, and (for an administrator) the removed ones, most recently
/// removed first.</summary>
public sealed record ActionFileList(IReadOnlyList<ActionFileRow> Live, IReadOnlyList<ActionFileRow> Removed)
{
    public static readonly ActionFileList None = new([], []);
}

/// <summary>A file opened to download: its bytes, the name it was added under, its size and when it was added. Disposing
/// it closes the bytes (a response that sends them closes them itself).</summary>
public sealed record FileDownload(Stream Content, string FileName, long SizeBytes, DateTimeOffset AddedAtUtc, string? RiskReason) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// Files on actions: added to an action directly (picked, dropped or pasted on its page, or when it's raised), listed
/// with its comments' files in its Files section, described, removed with a reason and restored. Each file's SHA-256 and
/// size are recorded as it arrives, and its real type is read from its first bytes as well as its name and declared
/// type (<see cref="FileRisk"/>): only a real raster image named and sent as one is ever shown inline, and one that can
/// run downloads in a password-protected zip by default.
/// <para>
/// Who may do what: whoever can work the action (analysts, administrators, and members of its team with WorkActions)
/// adds files and describes them; the person who added a file (a comment's: its author), an analyst or an administrator
/// removes it; only an administrator restores one. Whoever reads actions downloads them. Removing hides a file and keeps
/// its bytes, hash and record, and so does deleting the comment it's on. Every change is in the action's history and the
/// audit trail.
/// </para>
/// </summary>
public sealed class ActionFileService(IAppDbContextFactory dbs, ICurrentUser user, IFileStore store, IOptionsMonitor<FileStoreOptions> options)
{
    private FileStoreOptions O => options.CurrentValue;

    public int MaxImageMB => Math.Max(1, O.MaxImageMB);
    public int MaxFileMB => Math.Max(1, O.MaxEvidenceMB);
    private long MaxImageBytes => O.MaxImageBytes;
    private long MaxFileBytes => O.MaxEvidenceBytes;

    /// <summary>The most bytes read from the browser for one file: the larger of the two limits.</summary>
    public long MaxReadBytes => Math.Max(MaxImageBytes, MaxFileBytes);

    /// <summary>When a file downloads in a zip by default (Administration › Teams &amp; SLA).</summary>
    public ZipDownloads ZipMode => O.ZipDownloads;

    /// <summary>The limits as the page says them: "Up to 25 MB a file (15 MB for an image)."</summary>
    public string LimitsText => MaxImageMB == MaxFileMB
        ? $"Up to {MaxFileMB} MB a file."
        : $"Up to {MaxFileMB} MB a file ({MaxImageMB} MB for an image).";

    /// <summary>Whether this file's main download is the zip.</summary>
    public bool ZipFirst(ActionFileRow f) => FileRisk.ZipFirst(ZipMode, f.RiskReason);

    // --- adding ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Checks files before they're added: each one not empty and within its limit (an image's, or any other file's), its
    /// description within <see cref="ActionFile.MaxDescription"/>, and no more than <see cref="ActionFile.MaxPerUpload"/>
    /// at once. Throws <see cref="ArgumentException"/> saying what's wrong; the raise form calls it before the action is created.
    /// </summary>
    public void Check(IReadOnlyList<ActionFileUpload> uploads) => Checked(uploads);

    // The files' checks, as Check makes them, and what was found about each, in order.
    private List<(ActionFileUpload Upload, FileInspection Seen, string? Description)> Checked(IReadOnlyList<ActionFileUpload> uploads)
    {
        if (uploads.Count > ActionFile.MaxPerUpload) throw new ArgumentException($"Add at most {ActionFile.MaxPerUpload} files at once.");
        return uploads.Select(u =>
        {
            var (seen, description) = Inspect(u);
            return (u, seen, description);
        }).ToList();
    }

    /// <summary>
    /// Adds files to an action, all or none: every file is checked before the first is stored. Each one's SHA-256 and
    /// size are recorded, and the history names it with its hash. Returns the files added.
    /// </summary>
    public Task<IReadOnlyList<ActionFile>> AddAsync(Guid actionId, IReadOnlyList<ActionFileUpload> uploads, CancellationToken ct = default) =>
        AddAsync(x => x.Id == actionId, uploads, ct);

    /// <summary>As <see cref="AddAsync(Guid, IReadOnlyList{ActionFileUpload}, CancellationToken)"/>, to the action with
    /// this number (one just created, say).</summary>
    public Task<IReadOnlyList<ActionFile>> AddAsync(int actionNumber, IReadOnlyList<ActionFileUpload> uploads, CancellationToken ct = default) =>
        AddAsync(x => x.Number == actionNumber, uploads, ct);

    private async Task<IReadOnlyList<ActionFile>> AddAsync(Expression<Func<ActionItem, bool>> which, IReadOnlyList<ActionFileUpload> uploads,
        CancellationToken ct)
    {
        if (uploads.Count == 0) return [];
        var files = Checked(uploads);
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Actions.FirstOrDefaultAsync(which, ct) ?? throw new KeyNotFoundException("That action no longer exists.");
        await RequireWorkAsync(db, a, ct);
        var live = await db.ActionFiles.CountAsync(f => f.ActionId == a.Id && f.RemovedAtUtc == null, ct);
        if (live + uploads.Count > ActionFile.MaxPerAction)
            throw new ArgumentException($"An action holds at most {ActionFile.MaxPerAction} files; this one has {live}.");

        var now = DateTimeOffset.UtcNow;
        var named = files.Select(f => (f.Upload, f.Seen, f.Description, Name: ScreenshotService.EvidenceName(f.Upload.FileName))).ToList();
        var written = await ScreenshotService.WriteAllAsync(store,
            named.Select(f => (f.Name, f.Seen.ImageType ?? "application/octet-stream", f.Upload.Bytes)), ct);
        var added = new List<ActionFile>();
        try
        {
            foreach (var (f, stored) in named.Zip(written))
            {
                db.Files.Add(stored);
                var file = new ActionFile
                {
                    ActionId = a.Id, FileId = stored.Id, FileName = f.Name, SizeBytes = stored.SizeBytes, Sha256 = stored.Sha256,
                    DeclaredType = DeclaredOrNull(f.Upload.DeclaredType), IsImage = f.Seen.IsImage, TypeNote = f.Seen.TypeNote,
                    RiskReason = f.Seen.RiskReason, Description = f.Description, AddedById = user.UserId, AddedByName = user.DisplayName, AddedAtUtc = now,
                };
                db.ActionFiles.Add(file);
                added.Add(file);
            }
            var each = added.Select(f => $"{f.FileName} ({FileSizes.Text(f.SizeBytes)}, SHA-256 {f.Sha256})").ToList();
            var together = (added.Count == 1 ? "Added the file " : $"Added {added.Count} files: ") + string.Join("; ", each) + ".";
            // One line for the lot, unless it would be cut short (a history line is clipped) and lose later files' hashes.
            if (ActionService.ClipEvent(together) == together) db.ActionEvents.Add(Event(a.Id, together));
            else foreach (var line in each) db.ActionEvents.Add(Event(a.Id, "Added the file " + line + "."));
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            foreach (var f in written) store.Delete(f.Id);
            throw;
        }
        return added;
    }

    // A file's checks: not empty, within its limit, its description within its length. Returns what was found and the
    // description as it's stored.
    private (FileInspection Seen, string? Description) Inspect(ActionFileUpload u)
    {
        if (u.Bytes.Length == 0) throw new ArgumentException($"'{u.FileName}' is empty.");
        // Judged on the name it's stored and downloaded under: the name's odd characters are dropped when it's kept.
        var seen = FileRisk.Inspect(ScreenshotService.EvidenceName(u.FileName), u.DeclaredType, u.Bytes);
        if (seen.IsImage && u.Bytes.Length > MaxImageBytes) throw new ArgumentException($"'{u.FileName}' is over the {MaxImageMB} MB limit for images.");
        if (!seen.IsImage && u.Bytes.Length > MaxFileBytes) throw new ArgumentException($"'{u.FileName}' is over the {MaxFileMB} MB limit for files.");
        return (seen, CleanDescription(u.Description));
    }

    /// <summary>A description as it's stored: trimmed, null when blank, refused when longer than <see cref="ActionFile.MaxDescription"/>.</summary>
    public static string? CleanDescription(string? text)
    {
        var clean = ArticleService.CleanOrNull(text, int.MaxValue, multiline: true);
        if (clean is { Length: > ActionFile.MaxDescription })
            throw new ArgumentException($"Keep a file's description to {ActionFile.MaxDescription} characters or fewer.");
        return clean;
    }

    private static string? DeclaredOrNull(string? type)
    {
        var t = (type ?? "").Trim();
        return t.Length == 0 ? null : t.Length > 100 ? t[..100] : t;
    }

    // --- describing, removing, restoring ----------------------------------------------------------------------------

    /// <summary>Sets a file's description (blank clears it): a file on the action or on one of its comments, by its stored
    /// file's id, for whoever can work the action. The history says so; the audit trail keeps the text before and after.</summary>
    public async Task DescribeAsync(Guid fileId, string? description, CancellationToken ct = default)
    {
        var clean = CleanDescription(description);
        await using var db = await dbs.CreateAsync(ct);
        if (await FindFileAsync(db, fileId, ct) is not { File.IsRemoved: false } found) throw Gone();
        var a = await ActionService.FindAsync(db, found.ActionId, ct);
        await RequireWorkAsync(db, a, ct);
        if (found.File.Description == clean) return;
        found.File.Description = clean;
        db.ActionEvents.Add(Event(a.Id, Described(found.File.FileName, clean)));
        await db.SaveChangesAsync(ct);
    }

    private static string Described(string name, string? description) =>
        description is null ? $"Cleared the description of {name}." : $"Described {name}: “{description}”";

    /// <summary>
    /// Removes a file from its action, or from the comment it's attached to, saying why (required): it's hidden, and its
    /// bytes, hash and record are kept. Only the person who added it (a comment's file: the comment's author), an analyst
    /// or an administrator may.
    /// </summary>
    public async Task RemoveAsync(Guid fileId, string reason, CancellationToken ct = default)
    {
        var why = ArticleService.CleanOrNull(reason, int.MaxValue, multiline: true)
                  ?? throw new ArgumentException("Say why the file is being removed.");
        if (why.Length > ActionFile.MaxReason) throw new ArgumentException($"Keep the reason to {ActionFile.MaxReason} characters or fewer.");
        await using var db = await dbs.CreateAsync(ct);
        var found = await FindFileAsync(db, fileId, ct) ?? throw Gone();
        var file = found.File;
        if (file.IsRemoved) return;
        if (!CanRemove(user, found.AddedById)) throw new ForbiddenException("Only the person who added a file, an analyst or an administrator can remove it.");
        (file.RemovedAtUtc, file.RemovedById, file.RemovedByName, file.RemovedReason) = (DateTimeOffset.UtcNow, user.UserId, user.DisplayName, why);
        db.ActionEvents.Add(Event(found.ActionId, $"Removed the file {file.FileName} (SHA-256 {found.Sha256})"
            + (found.CommentBy is { } by ? $" from a comment by {by}" : "") + $": {why}"));
        await db.SaveChangesAsync(ct);
    }

    private static KeyNotFoundException Gone() => new("That file is no longer on the action.");

    // A file (tracked) on an action or on one of its comments, by its stored file's id: its action, who added it (a
    // comment's file: the comment's author), the comment's author's name (null for a file on the action itself) and its hash.
    private sealed record FoundFile(IRemovableFile File, Guid ActionId, string AddedById, string? CommentBy, string Sha256);

    private static async Task<FoundFile?> FindFileAsync(IAppDbContext db, Guid fileId, CancellationToken ct)
    {
        if (await db.ActionFiles.FirstOrDefaultAsync(f => f.FileId == fileId, ct) is { } own)
            return new FoundFile(own, own.ActionId, own.AddedById, null, own.Sha256);
        var file = await db.ActionCommentFiles.FirstOrDefaultAsync(f => f.FileId == fileId, ct);
        if (file is null) return null;
        var c = await db.ActionComments.AsNoTracking().Where(x => x.Id == file.CommentId).Select(x => new { x.ActionId, x.AuthorId, x.AuthorName }).FirstAsync(ct);
        var sha = await db.Files.AsNoTracking().Where(f => f.Id == fileId).Select(f => f.Sha256).FirstOrDefaultAsync(ct) ?? "";
        return new FoundFile(file, c.ActionId, c.AuthorId, c.AuthorName, sha);
    }

    /// <summary>Puts a removed file back where it was (on the action, or on its comment): administrators only. A file kept
    /// from a deleted comment comes back on the action itself.</summary>
    public async Task RestoreAsync(Guid fileId, CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can restore a removed file.");
        await using var db = await dbs.CreateAsync(ct);
        var found = await FindFileAsync(db, fileId, ct) ?? throw Gone();
        var file = found.File;
        if (!file.IsRemoved) return;
        // A comment's files don't count towards the action's limit.
        if (found.CommentBy is null
            && await db.ActionFiles.CountAsync(f => f.ActionId == found.ActionId && f.RemovedAtUtc == null, ct) >= ActionFile.MaxPerAction)
            throw new ArgumentException($"An action holds at most {ActionFile.MaxPerAction} files; remove one first.");
        var removed = $"removed by {file.RemovedByName} on {file.RemovedAtUtc!.Value.ToString("MMM d", CultureInfo.InvariantCulture)}";
        (file.RemovedAtUtc, file.RemovedById, file.RemovedByName, file.RemovedReason) = (null, null, null, null);
        db.ActionEvents.Add(Event(found.ActionId, $"Restored the file {file.FileName}"
            + (found.CommentBy is { } by ? $" to a comment by {by}" : "") + $" ({removed})."));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The files of a comment being deleted, kept on its action as removed files (reason
    /// <see cref="ActionFile.CommentDeletedReason"/>): their bytes, hashes and records stay, and an administrator can restore
    /// them onto the action. The caller removes the comment's own file rows and saves, in the same save. A file already
    /// removed from the comment keeps who removed it, when and why.
    /// </summary>
    internal static async Task KeepFilesOfDeletedCommentAsync(IAppDbContext db, ICurrentUser user, ActionComment comment,
        IReadOnlyList<ActionCommentFile> files, CancellationToken ct)
    {
        if (files.Count == 0) return;
        var ids = files.Select(f => f.FileId).ToList();
        var hashes = await db.Files.AsNoTracking().Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Sha256, ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var f in files)
            db.ActionFiles.Add(new ActionFile
            {
                ActionId = comment.ActionId, FileId = f.FileId, FileName = f.FileName, SizeBytes = f.SizeBytes, Sha256 = hashes.GetValueOrDefault(f.FileId) ?? "",
                IsImage = f.IsImage, RiskReason = f.RiskReason ?? FileRisk.Reason(f.FileName, null), Description = f.Description,
                AddedById = comment.AuthorId, AddedByName = comment.AuthorName, AddedAtUtc = comment.AtUtc,
                FromCommentBy = comment.AuthorName, FromCommentAtUtc = comment.AtUtc,
                RemovedAtUtc = f.RemovedAtUtc ?? now, RemovedById = f.RemovedAtUtc is null ? user.UserId : f.RemovedById,
                RemovedByName = f.RemovedAtUtc is null ? user.DisplayName : f.RemovedByName,
                RemovedReason = f.RemovedAtUtc is null ? ActionFile.CommentDeletedReason : f.RemovedReason,
            });
    }

    /// <summary>Whether <paramref name="who"/> may remove a file added by <paramref name="addedById"/> (a comment's file:
    /// the comment's author): that person while they still work actions, an analyst or an administrator. Someone since moved
    /// to a role that only reads (a Viewer) or to none that sees actions removes nothing, as they can't describe a file either.</summary>
    internal static bool CanRemove(ICurrentUser who, string addedById) =>
        who.Has(Permission.WriteArticles) || (who.Has(Permission.WorkActions) && addedById.Length > 0 && addedById == who.UserId);

    // --- listing and opening ----------------------------------------------------------------------------------------

    /// <summary>An action's files, for its page and the API (whoever reads actions).</summary>
    public async Task<ActionFileList> ListAsync(Guid actionId, CancellationToken ct = default)
    {
        ActionService.RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        return await ListAsync(db, user, actionId, ct);
    }

    /// <summary>The files on an action and its comments, on a context the caller holds: one query for the action's own,
    /// and one for the comments' files' hashes and types. The comments (with their files) are read too, unless the caller
    /// has them loaded already (<paramref name="comments"/>).</summary>
    internal static async Task<ActionFileList> ListAsync(IAppDbContext db, ICurrentUser user, Guid actionId, CancellationToken ct,
        IReadOnlyList<ActionComment>? comments = null)
    {
        var own = await (from f in db.ActionFiles.AsNoTracking()
                         join s in db.Files.AsNoTracking() on f.FileId equals s.Id
                         where f.ActionId == actionId
                         select new { f, s.ContentType }).ToListAsync(ct);
        comments ??= await db.ActionComments.AsNoTracking().Include(c => c.Files).Where(c => c.ActionId == actionId).ToListAsync(ct);
        var ids = comments.SelectMany(c => c.Files).Select(f => f.FileId).ToList();
        var stored = ids.Count == 0
            ? []
            : await db.Files.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, s.Sha256, s.ContentType }).ToDictionaryAsync(s => s.Id, ct);
        var rows = own.Select(x => new ActionFileRow(x.f.FileId, x.f.FileName, x.f.SizeBytes, NullIfEmpty(x.f.Sha256), x.ContentType, x.f.AddedByName,
                x.f.AddedAtUtc, x.f.Description, x.f.ShowsInline, x.f.RiskReason, x.f.TypeNote)
            {
                DeclaredType = x.f.DeclaredType, AddedById = x.f.AddedById, CanRemove = CanRemove(user, x.f.AddedById),
                RemovedAtUtc = x.f.RemovedAtUtc, RemovedByName = x.f.RemovedByName, RemovedReason = x.f.RemovedReason,
                FromCommentBy = x.f.FromCommentBy,
            })
            .ToList();
        // A comment's file added before its risk was recorded is judged by its name (its bytes were checked to be an image
        // or not when it arrived). One whose stored file's row is gone isn't listed.
        foreach (var c in comments)
            foreach (var f in c.Files)
            {
                if (!stored.TryGetValue(f.FileId, out var s)) continue;
                var risk = f.RiskReason ?? FileRisk.Reason(f.FileName, null);
                rows.Add(new ActionFileRow(f.FileId, f.FileName, f.SizeBytes, NullIfEmpty(s.Sha256), s.ContentType, c.AuthorName, c.AtUtc,
                    f.Description, ActionFile.ShowsInlineFor(f.IsImage, null, risk), risk, null)
                {
                    CommentId = f.CommentId, AddedById = c.AuthorId, CanRemove = CanRemove(user, c.AuthorId),
                    RemovedAtUtc = f.RemovedAtUtc, RemovedByName = f.RemovedByName, RemovedReason = f.RemovedReason,
                });
            }
        var live = rows.Where(r => r.RemovedAtUtc is null).OrderByDescending(r => r.AddedAtUtc).ThenBy(r => r.FileName, StringComparer.Ordinal).ToList();
        var removed = user.Has(Permission.Administer)
            ? rows.Where(r => r.RemovedAtUtc is not null).OrderByDescending(r => r.RemovedAtUtc).ToList()
            : [];
        return new ActionFileList(live, removed);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>
    /// A file on an action or one of its comments, opened to download, for whoever reads actions; null when it isn't one
    /// (or was removed, unless the reader is an administrator), or its bytes are gone. With <paramref name="actionId"/>,
    /// only a file on that action (the API's address names the action).
    /// </summary>
    public Task<FileDownload?> OpenAsync(Guid fileId, Guid? actionId = null, CancellationToken ct = default) =>
        OpenAsync(fileId, actionId, liveOnly: false, ct);

    /// <summary>As <see cref="OpenAsync(Guid, Guid?, CancellationToken)"/>; with <paramref name="liveOnly"/>, a removed file
    /// is null for an administrator too.</summary>
    internal async Task<FileDownload?> OpenAsync(Guid fileId, Guid? actionId, bool liveOnly, CancellationToken ct)
    {
        if (!ActionService.MayRead(user)) return null;
        var admin = user.Has(Permission.Administer) && !liveOnly;
        await using var db = await dbs.CreateAsync(ct);
        var own = await db.ActionFiles.AsNoTracking()
            .Where(f => f.FileId == fileId && (f.RemovedAtUtc == null || admin) && (actionId == null || f.ActionId == actionId))
            .Select(f => new { f.FileName, f.SizeBytes, f.AddedAtUtc, f.RiskReason }).FirstOrDefaultAsync(ct);
        FileDownload? Open(string name, long size, DateTimeOffset at, string? risk) =>
            store.OpenRead(fileId) is { } s ? new FileDownload(s, name, size, at, risk) : null;
        if (own is not null) return Open(own.FileName, own.SizeBytes, own.AddedAtUtc, own.RiskReason);
        var onComment = await (from f in db.ActionCommentFiles.AsNoTracking()
                               join c in db.ActionComments.AsNoTracking() on f.CommentId equals c.Id
                               where f.FileId == fileId && (f.RemovedAtUtc == null || admin) && (actionId == null || c.ActionId == actionId)
                               select new { f.FileName, f.SizeBytes, c.AtUtc, f.RiskReason }).FirstOrDefaultAsync(ct);
        return onComment is null ? null : Open(onComment.FileName, onComment.SizeBytes, onComment.AtUtc, onComment.RiskReason ?? FileRisk.Reason(onComment.FileName, null));
    }

    private Task RequireWorkAsync(IAppDbContext db, ActionItem a, CancellationToken ct) =>
        ActionService.RequireWork(db, user, a, ct, "Only analysts and members of the owning team can add or describe this action's files.");

    private ActionEvent Event(Guid actionId, string text) => ActionService.Event(user, actionId, text);
}
