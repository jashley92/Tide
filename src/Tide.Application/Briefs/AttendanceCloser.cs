using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;

namespace Tide.Application.Briefs;

/// <summary>
/// Closes the attendance of meetings nobody ended: the app's own doing, with no one signed in (audited as the system),
/// so it asks no permission. It only ever fills in a left time that's missing, for someone who joined: anyone who left
/// keeps their time, and a name added by hand (no times) is left alone. Whoever ends a meeting records it through
/// <see cref="MeetingService.LeftAsync"/>, as the presenter.
/// </summary>
public sealed class AttendanceCloser(IAppDbContextFactory dbs)
{
    /// <summary>
    /// The brief's live meeting was dropped with no one in it: each person still marked there left when they were last
    /// seen (<paramref name="lastSeenUtc"/>, by user id), or at <paramref name="atUtc"/> when that isn't known. Returns how
    /// many were closed.
    /// </summary>
    public async Task<int> CloseAsync(Guid briefId, IReadOnlyDictionary<string, DateTimeOffset> lastSeenUtc, DateTimeOffset atUtc,
        CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var open = await db.MeetingAttendees.Where(a => a.BriefId == briefId && a.JoinedAtUtc != null && a.LeftAtUtc == null).ToListAsync(ct);
        foreach (var a in open) a.LeftAtUtc = Left(a.JoinedAtUtc, lastSeenUtc.TryGetValue(a.UserId, out var seen) ? seen : atUtc);
        if (open.Count > 0) await db.SaveChangesAsync(ct);
        return open.Count;
    }

    /// <summary>
    /// At start-up: a meeting doesn't survive a restart (it's in memory), so whoever was marked there when the app stopped
    /// would be marked there for good. Each is closed at <paramref name="atUtc"/> (when they were last seen died with the
    /// meeting), but for a brief whose meeting <paramref name="isLive"/> says is running again: its people are there.
    /// Returns how many were closed.
    /// </summary>
    public async Task<int> CloseAllAsync(DateTimeOffset atUtc, Func<Guid, bool> isLive, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var open = (await db.MeetingAttendees.Where(a => a.JoinedAtUtc != null && a.LeftAtUtc == null).ToListAsync(ct))
            .Where(a => !isLive(a.BriefId)).ToList();
        foreach (var a in open) a.LeftAtUtc = Left(a.JoinedAtUtc, atUtc);
        if (open.Count > 0) await db.SaveChangesAsync(ct);
        return open.Count;
    }

    // Never before they joined.
    private static DateTimeOffset Left(DateTimeOffset? joinedUtc, DateTimeOffset leftUtc) =>
        joinedUtc is { } joined && joined > leftUtc ? joined : leftUtc;
}
