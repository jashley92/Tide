using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;

namespace Tide.Application.Actions;

public sealed partial class ReminderService
{
    /// <summary>How long a mark that can't matter again is kept: a closed action's, once it has been closed this long, and
    /// a past period's (a digest's week, a day's nudge).</summary>
    public const int MarkRetentionDays = 90;

    // The prune's own mark: when it last ran. Its kind is none of the scans', so the prune never removes it.
    private const string PrunedMark = "marks-pruned";
    private const string PrunedKey = "last";

    // Marks looked up only by the current period's key (a week, a day, a presenter's day), so one sent longer ago than the
    // retention is for a period that's over and is never read again: the team digest ({team}:{week}), the personal digest
    // ({who}:{day} or {who}:w{week}), the brief-not-ready nudge ({day}), the presenter's reminder ({who}:{day}) and a
    // contact reminded today ({action}:{day}:{contact}). Each is written during its period, so its SentAtUtc dates it.
    private static readonly string[] PeriodMarks = ["digest", "personal-digest", "brief-not-ready", "presenting", ContactMark];

    // Rows read or deleted per statement: each delete names its keys, so it's short on SQL Server and never locks the table.
    private const int PruneBatch = 500;

    /// <summary>
    /// Removes the marks that can never stop a send again, at most once a day (the reminder job calls it every pass). A
    /// mark is kept while its absence could send something twice, so every open action keeps all of its marks, and so
    /// does a closed one that's later reopened, or a deleted one restored, whatever its age:
    /// <list type="bullet">
    /// <item>A past period's marks (<see cref="PeriodMarks"/>) go once they're older than the retention.</item>
    /// <item>For an action closed more than <see cref="MarkRetentionDays"/> ago (deleted ones too): its recipients' due-soon
    /// and overdue marks, where the action's own mark for that kind and due date is kept (a scan that finds that mark
    /// never reads its recipients'); the marks saying which personal digests listed it (should it be reopened, the next
    /// digest lists it as new to them again, in an email that goes anyway); and the follow-up marks of its outreach emails
    /// that have a reply (the follow-up only reads those without one).</item>
    /// </list>
    /// What's left of a closed action's (its own due-soon and overdue marks, the escalation ladder's and an external
    /// team's steps, an unanswered email's follow-up) is what its scans would read again were it reopened with the same
    /// due date: a few rows an action, so the table grows with the actions rather than with their recipients and days.
    /// Returns how many marks went.
    /// </summary>
    public async Task<int> PruneMarksAsync(CancellationToken ct = default)
    {
        var now = clock.Now.ToUniversalTime();
        await using var db = await dbs.CreateAsync(ct);
        var last = await db.NotificationMarks.AsNoTracking().Where(m => m.Kind == PrunedMark && m.Key == PrunedKey)
            .Select(m => (DateTimeOffset?)m.SentAtUtc).FirstOrDefaultAsync(ct);
        if (last > now.AddDays(-1)) return 0;

        var cutoff = now.AddDays(-MarkRetentionDays);
        // Actions closed since the last run's cutoff (every one, the first time): those before it were pruned then. One
        // reopened since has no closing time; closed again, it has a new one and comes round again.
        var pruned = await PrunePeriodsAsync(db, cutoff, ct) + await PruneClosedAsync(db, last?.AddDays(-MarkRetentionDays), cutoff, ct);

        // Saved only once it's all done: a run that failed partway does the same again at the next pass.
        if (await db.NotificationMarks.Where(m => m.Kind == PrunedMark && m.Key == PrunedKey)
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.SentAtUtc, now), CancellationToken.None) == 0)
        {
            db.NotificationMarks.Add(new NotificationMark { Kind = PrunedMark, Key = PrunedKey, SentAtUtc = now });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        if (pruned > 0) logger?.LogInformation("Pruned {Count} reminder marks that can't matter again (kept {Days} days).", pruned, MarkRetentionDays);
        return pruned;
    }

    private static async Task<int> PrunePeriodsAsync(IAppDbContext db, DateTimeOffset cutoff, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var page = await db.NotificationMarks.AsNoTracking().Where(m => PeriodMarks.Contains(m.Kind) && m.SentAtUtc < cutoff)
                .OrderBy(m => m.SentAtUtc).Select(m => new { m.Kind, m.Key }).Take(PruneBatch).ToListAsync(ct);
            total += await DeleteMarksAsync(db, page.Select(m => (m.Kind, m.Key)), ct);
            if (page.Count < PruneBatch) return total;
        }
    }

    private static async Task<int> PruneClosedAsync(IAppDbContext db, DateTimeOffset? after, DateTimeOffset cutoff, CancellationToken ct)
    {
        var closed = db.Actions.AsNoTracking().IgnoreQueryFilters([ActionItem.DeletedFilter])
            .Where(a => !ActionItem.OpenStatuses.Contains(a.Status) && a.ClosedAtUtc != null && a.ClosedAtUtc <= cutoff);
        if (after is { } from) closed = closed.Where(a => a.ClosedAtUtc > from);
        var total = 0;
        // A page at a time, oldest closed first, each after the last one read.
        DateTimeOffset? lastAt = null;
        var lastNumber = 0;
        while (true)
        {
            var page = closed;
            if (lastAt is { } at) page = page.Where(a => a.ClosedAtUtc > at || (a.ClosedAtUtc == at && a.Number > lastNumber));
            var actions = await page.OrderBy(a => a.ClosedAtUtc).ThenBy(a => a.Number)
                .Select(a => new { a.Id, a.Number, At = a.ClosedAtUtc!.Value }).Take(PruneBatch).ToListAsync(ct);
            if (actions.Count == 0) return total;
            (lastAt, lastNumber) = (actions[^1].At, actions[^1].Number);

            var ids = actions.Select(a => a.Id).ToList();
            var prefixes = ids.Select(id => id.ToString("N")).ToList();
            var doomed = new List<(string Kind, string Key)>();

            // Recipients' due marks, where the action's own mark for the same kind and due date is there to stop the scan.
            var due = await db.NotificationMarks.AsNoTracking()
                .Where(m => (m.Kind == "due-soon" || m.Kind == "overdue") && prefixes.Contains(m.Key.Substring(0, 32)))
                .Select(m => new { m.Kind, m.Key }).ToListAsync(ct);
            var whole = due.Where(m => !m.Key.Contains('|')).Select(m => (m.Kind, m.Key)).ToHashSet();
            doomed.AddRange(due.Where(m => m.Key.IndexOf('|') is var bar and > 0 && whole.Contains((m.Kind, m.Key[..bar]))).Select(m => (m.Kind, m.Key)));

            // Which personal digests listed it: "{who}:{action}".
            doomed.AddRange((await db.NotificationMarks.AsNoTracking()
                    .Where(m => m.Kind == "digest-seen" && prefixes.Contains(m.Key.Substring(HashLength + 1, 32))).Select(m => m.Key).ToListAsync(ct))
                .Select(k => ("digest-seen", k)));

            // Follow-ups of its emails that were answered: a reply is never taken back.
            doomed.AddRange((await db.OutreachMessages.AsNoTracking().IgnoreQueryFilters([ActionItem.DeletedFilter])
                    .Where(o => ids.Contains(o.ActionId) && o.RepliedAtUtc != null).Select(o => o.Id).ToListAsync(ct))
                .Select(id => (FollowUpMark, id.ToString("N"))));

            total += await DeleteMarksAsync(db, doomed, ct);
        }
    }

    // Deletes the marks named, a kind and a batch of keys a statement (the key's seek), whatever the caller's token says:
    // what's gone is gone, and the prune's own mark isn't moved until the end.
    private static async Task<int> DeleteMarksAsync(IAppDbContext db, IEnumerable<(string Kind, string Key)> marks, CancellationToken ct)
    {
        var total = 0;
        foreach (var kind in marks.GroupBy(m => m.Kind))
            foreach (var chunk in kind.Select(m => m.Key).Distinct().Chunk(PruneBatch))
            {
                ct.ThrowIfCancellationRequested();
                total += await db.NotificationMarks.Where(m => m.Kind == kind.Key && chunk.Contains(m.Key)).ExecuteDeleteAsync(CancellationToken.None);
            }
        return total;
    }
}
