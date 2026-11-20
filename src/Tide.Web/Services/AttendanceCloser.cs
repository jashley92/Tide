using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;

namespace Tide.Web.Services;

/// <summary>
/// Closes the attendance of a meeting the sweep dropped with no one in it (nobody ended it). The app does this itself,
/// with no one signed in (audited as the system), so it asks no permission. That's why it's internal and not in DI: only
/// <see cref="MeetingAttendanceHostedService"/> makes one, and no page can. It only fills in a missing left time, and
/// only for someone the meeting saw leave. Whoever ends a meeting records it through
/// <see cref="Tide.Application.Briefs.MeetingService.LeftAsync"/>, as the presenter, and that overwrites this.
/// </summary>
internal sealed class AttendanceCloser(IAppDbContextFactory dbs)
{
    /// <summary>
    /// Each person still marked there in the brief's record left when the meeting last saw them
    /// (<paramref name="lastSeenUtc"/>, by user id). Anyone it has no time for is left as they are, rather than given a
    /// time that may be wrong; anyone who left keeps their time; a name added by hand has no times. Returns how many
    /// were closed.
    /// </summary>
    public async Task<int> CloseAsync(Guid briefId, IReadOnlyDictionary<string, DateTimeOffset> lastSeenUtc, CancellationToken ct = default)
    {
        if (lastSeenUtc.Count == 0) return 0;
        await using var db = await dbs.CreateAsync(ct);
        var ids = lastSeenUtc.Keys.ToList();
        var open = await db.MeetingAttendees
            .Where(a => a.BriefId == briefId && ids.Contains(a.UserId) && a.JoinedAtUtc != null && a.LeftAtUtc == null).ToListAsync(ct);
        foreach (var a in open)
        {
            var seen = lastSeenUtc[a.UserId];
            a.LeftAtUtc = a.JoinedAtUtc is { } joined && joined > seen ? joined : seen;   // never before they joined
        }
        if (open.Count > 0) await db.SaveChangesAsync(ct);
        return open.Count;
    }
}
