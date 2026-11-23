using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Briefs;

/// <summary>A brief's meeting as recorded: who was there, in name order, and the notes, oldest first.</summary>
public sealed record MeetingRecord(IReadOnlyList<MeetingAttendee> Attendees, IReadOnlyList<MeetingNote> Notes)
{
    public static readonly MeetingRecord None = new([], []);

    /// <summary>The number of each action a note raised, by the action's id: "Raised as A-14".</summary>
    public IReadOnlyDictionary<Guid, int> Raised { get; init; } = new Dictionary<Guid, int>();

    /// <summary>The number of the action <paramref name="note"/> raised, or null.</summary>
    public int? RaisedAs(MeetingNote note) => note.ActionId is { } id && Raised.TryGetValue(id, out var n) ? n : null;
}

/// <summary>
/// The record of a brief's meeting: attendance, as people join it in present mode or anyone in it adds them, and the
/// notes each person takes. What's live (who's on which slide, raised hands) is the web app's and isn't kept.
/// </summary>
public sealed class MeetingService(IAppDbContextFactory dbs, ICurrentUser user, TimeProvider time, IMeetingsInProgress? meetings = null,
    OrgClock? clock = null, ActionService? actions = null)
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
        var attendees = await db.MeetingAttendees.AsNoTracking().Where(a => a.BriefId == briefId)
            .Join(db.Briefs, a => a.BriefId, b => b.Id, (a, b) => new { Row = a, b.BriefDate }).ToListAsync(ct);
        var notes = visitor ? [] : await db.MeetingNotes.AsNoTracking().Where(n => n.BriefId == briefId).ToListAsync(ct);
        var raisedIds = notes.Select(n => n.ActionId).OfType<Guid>().ToList();
        var raised = raisedIds.Count == 0 ? new Dictionary<Guid, int>()
            : await db.Actions.AsNoTracking().Where(a => raisedIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Number, ct);
        return new MeetingRecord(
            attendees.Where(a => !JoinedBefore(a.Row, a.BriefDate)).Select(a => a.Row)
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            notes.OrderBy(n => n.AtUtc).ThenBy(n => n.Id).ToList()) { Raised = raised };
    }

    /// <summary>
    /// Whether someone who joined (at <paramref name="joinedAtUtc"/>) joined a meeting of the brief for <paramref name="day"/> before that
    /// day: an older version let a meeting start ahead of its day and refused to end it, so they attended no meeting of
    /// it. The row is kept (and counts again once they join on its day, <see cref="JoinedAsync"/>); a name added by hand,
    /// with no join, always counts.
    /// </summary>
    public static bool JoinedBefore(OrgClock clock, DateTimeOffset? joinedAtUtc, DateOnly day) =>
        joinedAtUtc is { } at && MeetingDay.Before(clock, day, at);

    private bool JoinedBefore(MeetingAttendee row, DateOnly day) => clock is not null && JoinedBefore(clock, row.JoinedAtUtc, day);

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
            // Joined only a meeting started ahead of the brief's day (JoinedBefore): this join is the first that counts.
            if (me.JoinedAtUtc is not null && JoinedBefore(me, await db.Briefs.Where(b => b.Id == briefId).Select(b => b.BriefDate).FirstAsync(ct)))
                me.JoinedAtUtc = null;
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

    /// <summary>
    /// Adds someone who was there without joining in TIDE, as mine to take off again (<see cref="CanAddAttendees"/>):
    /// anyone seated in the brief's live meeting from the team edition, never a Visitor; or, with no meeting of it live,
    /// whoever presents, correcting the record. Someone already listed by that name isn't added twice.
    /// </summary>
    public async Task AddAttendeeAsync(Guid briefId, string name, CancellationToken ct = default)
    {
        if (!CanAddAttendees(briefId)) throw new ForbiddenException("Only someone in the brief's live meeting, or with none live whoever presents, can add to who attended.");
        var clean = ArticleService.Clean(name, 256);
        if (clean.Length == 0) throw new ArgumentException("Enter a name.");
        await using var db = await dbs.CreateAsync(ct);
        await RequireBrief(db, briefId, ct);
        var names = await db.MeetingAttendees.Where(a => a.BriefId == briefId).Select(a => a.Name).ToListAsync(ct);
        if (names.Contains(clean, StringComparer.CurrentCultureIgnoreCase)) return;
        db.MeetingAttendees.Add(new MeetingAttendee { BriefId = briefId, Name = clean, AddedById = user.UserId });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Takes someone off who attended (<see cref="CanRemoveAttendee"/>). One already gone is left so, for someone
    /// who could take anyone off; anyone else is refused as they would be for one still there.</summary>
    public async Task RemoveAttendeeAsync(Guid attendeeId, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        const string refused = "Only the presenter can take someone off who attended, or whoever added a name, that name.";
        if (await db.MeetingAttendees.FirstOrDefaultAsync(a => a.Id == attendeeId, ct) is not { } a)
        {
            if (!MayAttend && !user.Has(Permission.PresentBriefs)) throw new ForbiddenException(refused);
            return;
        }
        if (!CanRemoveAttendee(a)) throw new ForbiddenException(refused);
        db.MeetingAttendees.Remove(a);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Whether I can add to who attended the brief's meeting: I'm seated in its live meeting, from the team
    /// edition (the presenter is; a Visitor's seat isn't, as they don't read the team edition); or I correct its record
    /// (<see cref="CorrectsRecord"/>). A rehearsal has no seat, and its page writes nothing.</summary>
    public bool CanAddAttendees(Guid briefId) => SeatAt(briefId) is not null || CorrectsRecord(briefId);

    /// <summary>Whether I can take <paramref name="row"/> off who attended: anyone, joined or added by name, while I
    /// present the live meeting or correct the record (<see cref="CorrectsRecord"/>); following it, only a name I added.
    /// A rehearsal has no seat, and its page writes nothing.</summary>
    public bool CanRemoveAttendee(MeetingAttendee row) =>
        CorrectsRecord(row.BriefId)
        || SeatAt(row.BriefId) is { } seat && (seat.Presenting || row.UserId.Length == 0 && row.AddedById == user.UserId);

    // Whoever presents, with no meeting of the brief live (getting ready, or after it): the record is theirs to correct,
    // as it was before anyone following could add to it. While it's live, the meeting's own rules hold.
    private bool CorrectsRecord(Guid briefId) => user.Has(Permission.PresentBriefs) && meetings?.IsLive(briefId) != true;

    // On the team edition and able to join meetings: everyone but a Visitor (and a Reader, who joins none).
    private bool MayAttend => user.Has(Permission.ReadTeamEdition) && user.Has(Permission.JoinMeetings);

    // My seat in the brief's live meeting, if I may attend it from the team edition.
    private SeatedMeeting? SeatAt(Guid briefId) =>
        MayAttend ? MeetingAccess.SeatIn(user, meetings, briefId) : null;

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

    /// <summary>
    /// Raises my note as an action, as present mode's Raise does a recommended line: the note's text as its title,
    /// on the article it was written against (none for a note on the meeting as a whole), a kind guessed from its wording,
    /// and the team that last took that kind (else the first team). The note keeps a link to it, and raises no second one.
    /// Only its author raises it, and only someone who may create actions (<see cref="CanRaiseNotes"/>).
    /// <paramref name="overLimitReason"/> is why it goes ahead over a team's limit that warns. Returns what was raised.
    /// </summary>
    public async Task<ActionService.QuickAdded> RaiseFromNoteAsync(Guid noteId, string? overLimitReason = null, CancellationToken ct = default)
    {
        RequireNote();
        if (!CanRaiseNotes || actions is null) throw new ForbiddenException("Only analysts can raise a note as an action.");
        const string notYours = "Only its author can raise a note as an action.";
        Guid? articleId;
        string title;
        DateOnly briefDate;
        List<Guid> teams;
        await using (var db = await dbs.CreateAsync(ct))
        {
            var note = await Mine(db, noteId, ct, notYours);
            if (note.ActionId is { } already)
            {
                var number = await db.Actions.AsNoTracking().Where(a => a.Id == already).Select(a => a.Number).FirstOrDefaultAsync(ct);
                throw new InvalidOperationException($"That note was raised already, as A-{number}.");
            }
            (articleId, title) = (note.ArticleId, ActionTitle(note.Text));
            briefDate = await db.Briefs.AsNoTracking().Where(b => b.Id == note.BriefId).Select(b => b.BriefDate).FirstAsync(ct);
            // The teams present mode offers to raise for: internal ones still in use, in their set order.
            teams = (await db.Teams.AsNoTracking().Where(t => !t.IsRetired && !t.IsExternal).ToListAsync(ct))
                .InOrder(t => t.SortOrder, t => t.Name).Select(t => t.Id).ToList();
        }
        if (teams.Count == 0) throw new InvalidOperationException("There's no team to raise it for yet: an administrator adds teams under Teams.");
        var kind = RecommendedActions.GuessKind(title);
        var team = (await actions.TeamsByKindAsync(ct)).TryGetValue(kind, out var last) && teams.Contains(last) ? last : teams[0];
        var added = await actions.QuickAddAsync(articleId, title, kind, team, null, RaisedFor(briefDate), overLimitReason, ct: ct);

        await using var save = await dbs.CreateAsync(ct);
        var mine = await Mine(save, noteId, ct, notYours);
        mine.ActionId = await save.Actions.Where(a => a.Number == added.Number).Select(a => a.Id).FirstAsync(ct);
        await save.SaveChangesAsync(ct);
        return added;
    }

    // An action's title from a note: its text on one line, cut to the longest title an action takes.
    internal static string ActionTitle(string text)
    {
        var line = ArticleService.Tidy(text);
        return line.Length <= ActionItem.MaxTitle ? line : line[..(ActionItem.MaxTitle - 1)].TrimEnd() + "\u2026";
    }

    // The day an action raised from a note is raised for: the brief's, once its day has gone (as present mode raises a
    // past day's), unless that's further back than an action can be raised for; otherwise today's.
    private DateOnly? RaisedFor(DateOnly briefDate) =>
        clock is not null && briefDate < clock.Today && briefDate >= clock.Today.AddYears(-1) ? briefDate : null;

    /// <summary>Marks my note done, or (<paramref name="done"/> false) open again. Only its author does.</summary>
    public async Task MarkNoteDoneAsync(Guid noteId, bool done = true, CancellationToken ct = default)
    {
        RequireNote();
        await using var db = await dbs.CreateAsync(ct);
        var note = await Mine(db, noteId, ct, "Only its author can mark a note done.");
        if (done == note.DoneAtUtc is not null) return;
        note.DoneAtUtc = done ? time.GetUtcNow() : null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Whether I can raise my notes as actions: I take notes and may create actions (an analyst; not a Viewer or
    /// an action owner, who take notes and mark them done).</summary>
    public bool CanRaiseNotes => CanNote && user.Has(Permission.WriteArticles);

    private static async Task RequireBrief(IAppDbContext db, Guid briefId, CancellationToken ct)
    {
        if (!await db.Briefs.AnyAsync(b => b.Id == briefId, ct)) throw new KeyNotFoundException("That brief no longer exists.");
    }

    private async Task<MeetingNote> Mine(IAppDbContext db, Guid noteId, CancellationToken ct, string refused = "Only its author can change a note.")
    {
        var note = await db.MeetingNotes.FirstOrDefaultAsync(n => n.Id == noteId, ct) ?? throw new KeyNotFoundException("That note no longer exists.");
        if (note.AuthorId != user.UserId) throw new ForbiddenException(refused);
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
