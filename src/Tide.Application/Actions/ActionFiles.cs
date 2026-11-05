using System.Globalization;
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

    /// <summary>For a file kept from a deleted comment: that comment's author and time.</summary>
    public string? FromCommentBy { get; init; }
    public DateTimeOffset? FromCommentAtUtc { get; init; }

    public bool OnComment => CommentId is not null;

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
    private long MaxImageBytes => MaxImageMB * 1024L * 1024L;
    private long MaxFileBytes => MaxFileMB * 1024L * 1024L;

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
    public void Check(IReadOnlyList<ActionFileUpload> uploads)
    {
        if (uploads.Count > ActionFile.MaxPerUpload) throw new ArgumentException($"Add at most {ActionFile.MaxPerUpload} files at once.");
        foreach (var u in uploads) Inspect(u);
    }

    /// <summary>
    /// Adds files to an action, all or none: every file is checked before the first is stored. Each one's SHA-256 and
    /// size are recorded, and the history names it with its hash. Returns the files added.
    /// </summary>
    public async Task<IReadOnlyList<ActionFile>> AddAsync(Guid actionId, IReadOnlyList<ActionFileUpload> uploads, CancellationToken ct = default)
    {
        if (uploads.Count == 0) return [];
        Check(uploads);
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Actions.FirstOrDefaultAsync(x => x.Id == actionId, ct) ?? throw new KeyNotFoundException("That action no longer exists.");
        await RequireWorkAsync(db, a, ct);
        var live = await db.ActionFiles.CountAsync(f => f.ActionId == actionId && f.RemovedAtUtc == null, ct);
        if (live + uploads.Count > ActionFile.MaxPerAction)
            throw new ArgumentException($"An action holds at most {ActionFile.MaxPerAction} files; this one has {live}.");

        var now = DateTimeOffset.UtcNow;
        var added = new List<ActionFile>();
        var written = new List<Guid>();
        try
        {
            foreach (var u in uploads)
            {
                var (seen, description) = Inspect(u);
                var name = ScreenshotService.EvidenceName(u.FileName);
                var stored = await ScreenshotService.WriteAsync(store, name, seen.ImageType ?? "application/octet-stream", u.Bytes, ct);
                written.Add(stored.Id);
                db.Files.Add(stored);
                var file = new ActionFile
                {
                    ActionId = a.Id, FileId = stored.Id, FileName = name, SizeBytes = stored.SizeBytes, Sha256 = stored.Sha256,
                    DeclaredType = DeclaredOrNull(u.DeclaredType), IsImage = seen.IsImage, TypeNote = seen.TypeNote, RiskReason = seen.RiskReason,
                    Description = description, AddedById = user.UserId, AddedByName = user.DisplayName, AddedAtUtc = now,
                };
                db.ActionFiles.Add(file);
                added.Add(file);
            }
            db.ActionEvents.Add(Event(a.Id, (added.Count == 1 ? "Added the file " : $"Added {added.Count} files: ")
                + string.Join("; ", added.Select(f => $"{f.FileName} ({Ui(f.SizeBytes)}, SHA-256 {f.Sha256})")) + "."));
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            foreach (var id in written) store.Delete(id);
            throw;
        }
        return added;
    }

    // A file's checks: not empty, within its limit, its description within its length. Returns what was found and the
    // description as it's stored.
    private (FileInspection Seen, string? Description) Inspect(ActionFileUpload u)
    {
        if (u.Bytes.Length == 0) throw new ArgumentException($"'{u.FileName}' is empty.");
        var seen = FileRisk.Inspect(u.FileName, u.DeclaredType, u.Bytes);
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
        if (await db.ActionFiles.FirstOrDefaultAsync(f => f.FileId == fileId && f.RemovedAtUtc == null, ct) is { } file)
        {
            var a = await ActionOf(db, file.ActionId, ct);
            await RequireWorkAsync(db, a, ct);
            if (file.Description == clean) return;
            file.Description = clean;
            db.ActionEvents.Add(Event(a.Id, Described(file.FileName, clean)));
        }
        else if (await db.ActionCommentFiles.FirstOrDefaultAsync(f => f.FileId == fileId && f.RemovedAtUtc == null, ct) is { } onComment)
        {
            var actionId = await db.ActionComments.Where(c => c.Id == onComment.CommentId).Select(c => c.ActionId).FirstAsync(ct);
            var a = await ActionOf(db, actionId, ct);
            await RequireWorkAsync(db, a, ct);
            if (onComment.Description == clean) return;
            onComment.Description = clean;
            db.ActionEvents.Add(Event(a.Id, Described(onComment.FileName, clean)));
        }
        else throw new KeyNotFoundException("That file is no longer on the action.");
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
        var now = DateTimeOffset.UtcNow;
        if (await db.ActionFiles.FirstOrDefaultAsync(f => f.FileId == fileId, ct) is { } file)
        {
            if (file.IsRemoved) return;
            if (!CanRemove(file.AddedById)) throw NotTheirs();
            (file.RemovedAtUtc, file.RemovedById, file.RemovedByName, file.RemovedReason) = (now, user.UserId, user.DisplayName, why);
            db.ActionEvents.Add(Event(file.ActionId, $"Removed the file {file.FileName} (SHA-256 {file.Sha256}): {why}"));
        }
        else if (await CommentFileAsync(db, fileId, ct) is { } c)
        {
            if (c.File.IsRemoved) return;
            if (!CanRemove(c.AuthorId)) throw NotTheirs();
            (c.File.RemovedAtUtc, c.File.RemovedById, c.File.RemovedByName, c.File.RemovedReason) = (now, user.UserId, user.DisplayName, why);
            db.ActionEvents.Add(Event(c.ActionId, $"Removed the file {c.File.FileName} (SHA-256 {c.Sha256}) from a comment by {c.AuthorName}: {why}"));
        }
        else throw new KeyNotFoundException("That file is no longer on the action.");
        await db.SaveChangesAsync(ct);
    }

    private static ForbiddenException NotTheirs() => new("Only the person who added a file, an analyst or an administrator can remove it.");

    // A comment's file (tracked), with the comment's action and author and the file's hash.
    private sealed record CommentFile(ActionCommentFile File, Guid ActionId, string AuthorId, string AuthorName, string Sha256);

    private static async Task<CommentFile?> CommentFileAsync(IAppDbContext db, Guid fileId, CancellationToken ct)
    {
        var file = await db.ActionCommentFiles.FirstOrDefaultAsync(f => f.FileId == fileId, ct);
        if (file is null) return null;
        var c = await db.ActionComments.AsNoTracking().Where(x => x.Id == file.CommentId).Select(x => new { x.ActionId, x.AuthorId, x.AuthorName }).FirstAsync(ct);
        var sha = await db.Files.AsNoTracking().Where(f => f.Id == fileId).Select(f => f.Sha256).FirstOrDefaultAsync(ct) ?? "";
        return new CommentFile(file, c.ActionId, c.AuthorId, c.AuthorName, sha);
    }

    /// <summary>Puts a removed file back where it was (on the action, or on its comment): administrators only. A file kept
    /// from a deleted comment comes back on the action itself.</summary>
    public async Task RestoreAsync(Guid fileId, CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can restore a removed file.");
        await using var db = await dbs.CreateAsync(ct);
        if (await db.ActionFiles.FirstOrDefaultAsync(f => f.FileId == fileId, ct) is { } file)
        {
            if (!file.IsRemoved) return;
            var live = await db.ActionFiles.CountAsync(f => f.ActionId == file.ActionId && f.RemovedAtUtc == null, ct);
            if (live >= ActionFile.MaxPerAction) throw new ArgumentException($"An action holds at most {ActionFile.MaxPerAction} files; remove one first.");
            var removed = RemovedWhen(file.RemovedByName, file.RemovedAtUtc!.Value);
            (file.RemovedAtUtc, file.RemovedById, file.RemovedByName, file.RemovedReason) = (null, null, null, null);
            db.ActionEvents.Add(Event(file.ActionId, $"Restored the file {file.FileName} ({removed})."));
        }
        else if (await CommentFileAsync(db, fileId, ct) is { } c)
        {
            if (!c.File.IsRemoved) return;
            var removed = RemovedWhen(c.File.RemovedByName, c.File.RemovedAtUtc!.Value);
            (c.File.RemovedAtUtc, c.File.RemovedById, c.File.RemovedByName, c.File.RemovedReason) = (null, null, null, null);
            db.ActionEvents.Add(Event(c.ActionId, $"Restored the file {c.File.FileName} to a comment by {c.AuthorName} ({removed})."));
        }
        else throw new KeyNotFoundException("That file is no longer on the action.");
        await db.SaveChangesAsync(ct);
    }

    private static string RemovedWhen(string? by, DateTimeOffset at) => $"removed by {by} on {at.ToString("MMM d", CultureInfo.InvariantCulture)}";

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

    private bool CanRemove(string addedById) => user.Has(Permission.WriteArticles) || (addedById.Length > 0 && addedById == user.UserId);

    // --- listing and opening ----------------------------------------------------------------------------------------

    /// <summary>An action's files, for its page and the API (whoever reads actions).</summary>
    public async Task<ActionFileList> ListAsync(Guid actionId, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await ListAsync(db, user, actionId, ct);
    }

    /// <summary>The files on an action and its comments, on a context the caller holds: one query for each kind, and one
    /// for the comments' files' hashes and types.</summary>
    internal static async Task<ActionFileList> ListAsync(IAppDbContext db, ICurrentUser user, Guid actionId, CancellationToken ct)
    {
        var own = await (from f in db.ActionFiles.AsNoTracking()
                         join s in db.Files.AsNoTracking() on f.FileId equals s.Id
                         where f.ActionId == actionId
                         select new { f, s.ContentType }).ToListAsync(ct);
        var onComments = await (from f in db.ActionCommentFiles.AsNoTracking()
                                join c in db.ActionComments.AsNoTracking() on f.CommentId equals c.Id
                                join s in db.Files.AsNoTracking() on f.FileId equals s.Id
                                where c.ActionId == actionId
                                select new { f, c.AuthorId, c.AuthorName, c.AtUtc, s.Sha256, s.ContentType }).ToListAsync(ct);
        bool MayRemove(string addedById) => user.Has(Permission.WriteArticles) || (addedById.Length > 0 && addedById == user.UserId);
        var rows = own.Select(x => new ActionFileRow(x.f.FileId, x.f.FileName, x.f.SizeBytes, NullIfEmpty(x.f.Sha256), x.ContentType, x.f.AddedByName,
                x.f.AddedAtUtc, x.f.Description, x.f.ShowsInline, x.f.RiskReason, x.f.TypeNote)
            {
                DeclaredType = x.f.DeclaredType, AddedById = x.f.AddedById, CanRemove = MayRemove(x.f.AddedById),
                RemovedAtUtc = x.f.RemovedAtUtc, RemovedByName = x.f.RemovedByName, RemovedReason = x.f.RemovedReason,
                FromCommentBy = x.f.FromCommentBy, FromCommentAtUtc = x.f.FromCommentAtUtc,
            })
            .ToList();
        // A comment's file added before its risk was recorded is judged by its name (its bytes were checked to be an image
        // or not when it arrived).
        rows.AddRange(onComments.Select(x =>
        {
            var risk = x.f.RiskReason ?? FileRisk.Reason(x.f.FileName, null);
            return new ActionFileRow(x.f.FileId, x.f.FileName, x.f.SizeBytes, NullIfEmpty(x.Sha256), x.ContentType, x.AuthorName, x.AtUtc,
                x.f.Description, x.f.IsImage && risk is null, risk, null)
            {
                CommentId = x.f.CommentId, AddedById = x.AuthorId, CanRemove = MayRemove(x.AuthorId),
                RemovedAtUtc = x.f.RemovedAtUtc, RemovedByName = x.f.RemovedByName, RemovedReason = x.f.RemovedReason,
            };
        }));
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
    public async Task<FileDownload?> OpenAsync(Guid fileId, Guid? actionId = null, CancellationToken ct = default)
    {
        if (!MayRead) return null;
        var admin = user.Has(Permission.Administer);
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

    /// <summary>
    /// How many live files each action has, on it and on its comments: one grouped query for the lot, for the paperclip on
    /// lists, the board and the brief.
    /// </summary>
    internal static async Task<Dictionary<Guid, int>> CountsAsync(IAppDbContext db, IReadOnlyCollection<Guid> actionIds, CancellationToken ct)
    {
        if (actionIds.Count == 0) return [];
        var ids = actionIds.Distinct().ToList();
        var own = db.ActionFiles.Where(f => f.RemovedAtUtc == null && ids.Contains(f.ActionId)).Select(f => f.ActionId);
        var onComments = from f in db.ActionCommentFiles
                         join c in db.ActionComments on f.CommentId equals c.Id
                         where f.RemovedAtUtc == null && ids.Contains(c.ActionId)
                         select c.ActionId;
        return await own.Concat(onComments).GroupBy(id => id).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    private bool MayRead => user.Has(Permission.ReadTeamEdition) || user.Has(Permission.WorkActions);

    private void RequireRead()
    {
        if (!MayRead) throw new ForbiddenException("You don't have access to actions.");
    }

    private async Task RequireWorkAsync(IAppDbContext db, ActionItem a, CancellationToken ct)
    {
        if (!await ActionService.CanWork(db, user, a.TeamId, ct))
            throw new ForbiddenException("Only analysts and members of the owning team can add or describe this action's files.");
    }

    private static async Task<ActionItem> ActionOf(IAppDbContext db, Guid id, CancellationToken ct) =>
        await db.Actions.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("That action no longer exists.");

    private ActionEvent Event(Guid actionId, string text) => new()
    {
        ActionId = actionId, AtUtc = DateTimeOffset.UtcNow, ByName = user.DisplayName,
        Text = ActionService.ClipEvent(text),
    };

    private static string Ui(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.0} MB"),
    };
}
