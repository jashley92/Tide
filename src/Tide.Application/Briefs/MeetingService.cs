using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Briefs;

/// <summary>A brief's meeting as recorded: who was there, in name order, and the notes, oldest first.</summary>
public sealed record MeetingRecord(IReadOnlyList<MeetingAttendee> Attendees, IReadOnlyList<MeetingNote> Notes)
{
    public static readonly MeetingRecord None = new([], []);
}

/// <summary>
/// The record of a brief's meeting: attendance, as people join it in present mode or the presenter adds them, and the
/// notes each person takes. What's live (who's on which slide, raised hands) is the web app's and isn't kept.
/// </summary>
public sealed class MeetingService(IAppDbContextFactory dbs, ICurrentUser user, TimeProvider time, IMeetingsInProgress? meetings = null)
{
    /// <summary>The longest note.</summary>
    public const int MaxNote = 2000;

    /// <summary>The record: a Visitor, from a seat in the brief's live meeting, gets who's attended (the wrap-up slide shows
    /// it) and none of the notes, which are the team's.</summary>
    public async Task<MeetingRecord> GetAsync(Guid briefId, CancellationToken ct = default)
    {
        var visitor = MeetingAccess.SeatedFor(user, meetings, briefId);
        if (!visitor) RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var attendees = await db.MeetingAttendees.AsNoTracking().Where(a => a.BriefId == briefId).ToListAsync(ct);
        var notes = visitor ? [] : await db.MeetingNotes.AsNoTracking().Where(n => n.BriefId == briefId).ToListAsync(ct);
        return new MeetingRecord(
            attendees.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            notes.OrderBy(n => n.AtUtc).ThenBy(n => n.Id).ToList());
    }

    /// <summary>Records that I joined the meeting: the first time, when; again after leaving, that I'm back. Anyone who may
    /// join meetings: a Visitor only from a seat in that brief's live meeting, recorded as attending as everyone else is.</summary>
    public async Task JoinedAsync(Guid briefId, CancellationToken ct = default)
    {
        if (!user.Has(Permission.JoinMeetings)) throw new ForbiddenException("You can't join the brief meeting.");
        if (!MeetingAccess.SeatedFor(user, meetings, briefId)) RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var name = ArticleService.Clean(user.DisplayName, 256);
        var me = await db.MeetingAttendees.FirstOrDefaultAsync(a => a.BriefId == briefId && a.UserId == user.UserId, ct);
        // Added by name before they joined in TIDE: that's them, not a second entry.
        if (me is null && await db.MeetingAttendees.Where(a => a.BriefId == briefId && a.UserId == "").ToListAsync(ct) is var named
            && named.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { } byName)
        {
            (me = byName).UserId = user.UserId;
        }
        if (me is null)
        {
            await RequireBrief(db, briefId, ct);
            db.MeetingAttendees.Add(new MeetingAttendee { BriefId = briefId, UserId = user.UserId, Name = name, JoinedAtUtc = time.GetUtcNow() });
        }
        else
        {
            me.JoinedAtUtc ??= time.GetUtcNow();
            me.LeftAtUtc = null;
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            if (!await AttendingAsync(briefId, ct)) throw;   // not another of my tabs recording me at the same moment
        }
    }

    private async Task<bool> AttendingAsync(Guid briefId, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        return await db.MeetingAttendees.AnyAsync(a => a.BriefId == briefId && a.UserId == user.UserId, ct);
    }

    /// <summary>Records when each person (by user id) last left, as the meeting ends.</summary>
    public async Task LeftAsync(Guid briefId, IReadOnlyDictionary<string, DateTimeOffset> leftAtUtc, CancellationToken ct = default)
    {
        RequirePresent();
        if (leftAtUtc.Count == 0) return;
        await using var db = await dbs.CreateAsync(ct);
        var ids = leftAtUtc.Keys.ToList();
        foreach (var a in await db.MeetingAttendees.Where(a => a.BriefId == briefId && ids.Contains(a.UserId)).ToListAsync(ct))
            a.LeftAtUtc = leftAtUtc[a.UserId];
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Adds someone who was there without joining in TIDE. Someone already listed by that name isn't added twice.</summary>
    public async Task AddAttendeeAsync(Guid briefId, string name, CancellationToken ct = default)
    {
        RequirePresent();
        var clean = ArticleService.Clean(name, 256);
        if (clean.Length == 0) throw new ArgumentException("Enter a name.");
        await using var db = await dbs.CreateAsync(ct);
        await RequireBrief(db, briefId, ct);
        var names = await db.MeetingAttendees.Where(a => a.BriefId == briefId).Select(a => a.Name).ToListAsync(ct);
        if (names.Contains(clean, StringComparer.CurrentCultureIgnoreCase)) return;
        db.MeetingAttendees.Add(new MeetingAttendee { BriefId = briefId, Name = clean });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAttendeeAsync(Guid attendeeId, CancellationToken ct = default)
    {
        RequirePresent();
        await using var db = await dbs.CreateAsync(ct);
        if (await db.MeetingAttendees.FirstOrDefaultAsync(a => a.Id == attendeeId, ct) is not { } a) return;
        db.MeetingAttendees.Remove(a);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Adds my note, against <paramref name="articleId"/> (an article on the brief) or the meeting as a whole.</summary>
    public async Task<Guid> AddNoteAsync(Guid briefId, Guid? articleId, string text, CancellationToken ct = default)
    {
        RequireNote();
        var clean = Text(text);
        await using var db = await dbs.CreateAsync(ct);
        // An article on the brief says the brief is there too.
        if (articleId is not { } aid) await RequireBrief(db, briefId, ct);
        else if (!await db.BriefItems.AnyAsync(i => i.BriefId == briefId && i.ArticleId == aid, ct))
        {
            await RequireBrief(db, briefId, ct);
            throw new ArgumentException("That article isn't on this brief.");
        }
        var note = new MeetingNote
        {
            BriefId = briefId, ArticleId = articleId, AuthorId = user.UserId, AuthorName = ArticleService.Clean(user.DisplayName, 256),
            AtUtc = time.GetUtcNow(), Text = clean,
        };
        db.MeetingNotes.Add(note);
        await db.SaveChangesAsync(ct);
        return note.Id;
    }

    public async Task EditNoteAsync(Guid noteId, string text, CancellationToken ct = default)
    {
        RequireNote();
        await using var db = await dbs.CreateAsync(ct);
        var note = await Mine(db, noteId, ct);
        // A note carried over from the old single notes may be longer than one written now; it can still be edited.
        var clean = Text(text, Math.Max(MaxNote, note.Text.Length));
        if (note.Text == clean) return;
        note.Text = clean;
        note.EditedAtUtc = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteNoteAsync(Guid noteId, CancellationToken ct = default)
    {
        RequireNote();
        await using var db = await dbs.CreateAsync(ct);
        db.MeetingNotes.Remove(await Mine(db, noteId, ct));
        await db.SaveChangesAsync(ct);
    }

    private static async Task RequireBrief(IAppDbContext db, Guid briefId, CancellationToken ct)
    {
        if (!await db.Briefs.AnyAsync(b => b.Id == briefId, ct)) throw new KeyNotFoundException("That brief no longer exists.");
    }

    private async Task<MeetingNote> Mine(IAppDbContext db, Guid noteId, CancellationToken ct)
    {
        var note = await db.MeetingNotes.FirstOrDefaultAsync(n => n.Id == noteId, ct) ?? throw new KeyNotFoundException("That note no longer exists.");
        if (note.AuthorId != user.UserId) throw new ForbiddenException("Only its author can change a note.");
        return note;
    }

    private static string Text(string text, int max = MaxNote)
    {
        var clean = ArticleService.Clean(text, max, multiline: true);
        return clean.Length > 0 ? clean : throw new ArgumentException("Write the note first.");
    }

    /// <summary>Whether I can take notes at the meeting: on the team edition, with MeetingNotes (the team's roles and a
    /// Viewer; never a Visitor, who isn't on the team edition).</summary>
    public bool CanNote => user.Has(Permission.ReadTeamEdition) && user.Has(Permission.MeetingNotes);

    private void RequireRead()
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("You don't have access to briefs.");
    }

    private void RequireNote()
    {
        if (!CanNote) throw new ForbiddenException("You can't take notes at the meeting.");
    }

    private void RequirePresent()
    {
        if (!user.Has(Permission.PresentBriefs)) throw new ForbiddenException("Only analysts can run the meeting.");
    }
}
