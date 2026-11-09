using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tide.Application.Abstractions;

namespace Tide.Application.Operations;

/// <summary>Where this process stands with the job lease.</summary>
public enum LeaseState
{
    /// <summary>Not asked yet (just started).</summary>
    Unknown,

    /// <summary>This process holds it and runs the background jobs.</summary>
    Held,

    /// <summary>Another live process holds it: this one runs no background jobs until that one stops renewing it.</summary>
    StandingBy,

    /// <summary>The database didn't answer, and this process didn't hold the lease long enough to be sure it still does.</summary>
    Unreachable,
}

/// <summary>The job lease as this process last saw it. <see cref="Holder"/> is the holder's id
/// ("MACHINE/1234/9f0c…"); <see cref="HolderName"/> says it for a person.</summary>
public sealed record LeaseStatus(LeaseState State, string? Holder, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? HeldSinceUtc,
    DateTimeOffset? CheckedAtUtc, string? Problem)
{
    public static readonly LeaseStatus NotChecked = new(LeaseState.Unknown, null, null, null, null, null);

    /// <summary>"WEB01, process 4312", or null.</summary>
    public string? HolderName => BackgroundJobLease.Describe(Holder);
}

/// <summary>Whether this process runs the background jobs: each hosted service asks before each pass, and skips the pass
/// when it doesn't.</summary>
public interface IJobLease
{
    /// <summary>Takes or renews the lease, and says whether this process holds it. Never throws but to cancel.</summary>
    Task<bool> HoldAsync(CancellationToken ct = default);

    /// <summary>The lease as this process last saw it.</summary>
    LeaseStatus Status { get; }
}

/// <summary>A job run by hand (an administrator's Run now, an analyst's Pull now) on a process that's standing by: the
/// holder runs the jobs, so it's run there, where the marks and the history are kept in step.</summary>
public sealed class StandingByException(string? holder)
    : InvalidOperationException($"This server is standing by: {holder ?? "another server"} runs the background jobs. Run it there.");

public static class JobLeaseExtensions
{
    /// <summary>Throws <see cref="StandingByException"/> unless this process holds the lease. A service made without one
    /// (a test's) runs as the holder.</summary>
    public static async Task RequireHeldAsync(this IJobLease? lease, CancellationToken ct = default)
    {
        if (lease is null || await lease.HoldAsync(ct)) return;
        throw new StandingByException(lease.Status.HolderName);
    }
}

/// <summary>
/// The one lease every process on the database asks for before running a background job, kept in the <c>JobLeases</c>
/// table, so a second process (a web garden, an overlapping recycle, a standby server started by mistake) stands by
/// rather than sending every reminder a second time. It's taken and renewed with one conditional UPDATE: set this holder
/// and a new expiry where the row is this holder's or has lapsed, which is atomic on SQLite and SQL Server alike. The
/// first process to find no row inserts it; the key stops a second insert. It lapses <see cref="Expiry"/> after its last
/// renewal, so a process that dies is taken over within a few minutes, and it's let go at shutdown so the next process
/// takes over within 30 seconds. The expiry is compared on each process's own clock: servers on a domain keep theirs within
/// seconds of each other, well inside the margin.
/// Singleton (it's asked from the hosted services, so it makes a scope for each database call).
/// </summary>
public sealed class BackgroundJobLease(IServiceScopeFactory scopes, TimeProvider time, ILogger<BackgroundJobLease> log, string? holder = null) : IJobLease, IDisposable
{
    /// <summary>The lease the background jobs share.</summary>
    public const string BackgroundJobs = "background-jobs";

    /// <summary>How long the lease lasts after a renewal.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(3);

    /// <summary>A pass within this long of the last check doesn't ask the database again: a holder's renewal, or a
    /// standing-by process's look at whether the lease has come free.</summary>
    public static readonly TimeSpan RenewAfter = TimeSpan.FromSeconds(30);

    /// <summary>While the database doesn't answer, a holder keeps running until this long before the expiry it last wrote.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _askedAt = DateTimeOffset.MinValue;
    private volatile LeaseStatus _status = LeaseStatus.NotChecked;

    /// <summary>This process's id on the lease: the machine, the process id, and a value of its own (a recycled process
    /// can be given the same id).</summary>
    public string Holder { get; } = Content.Words.Clip(holder ?? $"{Environment.MachineName}/{Environment.ProcessId}/{Guid.NewGuid():N}", Domain.Entities.JobLease.MaxHolder);

    public LeaseStatus Status => _status;

    public async Task<bool> HoldAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (_status.State is LeaseState.Held or LeaseState.StandingBy && now - _askedAt < RenewAfter) return _status.State == LeaseState.Held;
            var before = _status;
            try
            {
                _status = await TakeAsync(before, now, ct);
                _askedAt = now;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The database didn't answer. A holder keeps going until shortly before the expiry it last wrote (nobody
                // else can take it before then); after that, or if it didn't hold it, it runs nothing.
                var stillHeld = before.State == LeaseState.Held && before.ExpiresAtUtc is { } until && now < until - Margin;
                var problem = SecretMask.Scrub(ex.GetType().Name + ": " + ex.Message);
                _status = stillHeld ? before with { CheckedAtUtc = now, Problem = problem }
                    : new LeaseStatus(LeaseState.Unreachable, before.Holder, before.ExpiresAtUtc, null, now, problem);
                log.LogWarning(ex, "Couldn't take or renew the background jobs' lease{Still}.", stillHeld ? "; still holding it until it nears its expiry" : "; no background job runs here until it can");
            }
            if (before.State != _status.State)
            {
                if (_status.State == LeaseState.Held) log.LogInformation("This process ({Holder}) now runs the background jobs.", Holder);
                else if (_status.State == LeaseState.StandingBy)
                    log.LogInformation("Standing by: {Other} runs the background jobs, so this process ({Holder}) runs none until it stops.", _status.HolderName, Holder);
            }
            return _status.State == LeaseState.Held;
        }
        finally { _gate.Release(); }
    }

    private async Task<LeaseStatus> TakeAsync(LeaseStatus before, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync(ct);
        var me = Holder;
        var until = now + Expiry;
        // One statement: whoever's renewal or takeover lands first wins, and the other's matches no row.
        var taken = await db.JobLeases
            .Where(l => l.Name == BackgroundJobs && (l.Holder == me || l.ExpiresAtUtc <= now))
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.AcquiredAtUtc, l => l.Holder == me ? l.AcquiredAtUtc : now)
                .SetProperty(l => l.Holder, me)
                .SetProperty(l => l.ExpiresAtUtc, until), ct) == 1;
        // A renewal is the one statement: it was this holder's, so it's held since it was taken.
        if (taken) return new LeaseStatus(LeaseState.Held, me, until, before.State == LeaseState.Held ? before.HeldSinceUtc : now, now, null);
        var row = await db.JobLeases.AsNoTracking().FirstOrDefaultAsync(l => l.Name == BackgroundJobs, ct);
        if (row is null)
        {
            db.JobLeases.Add(new Domain.Entities.JobLease { Name = BackgroundJobs, Holder = me, AcquiredAtUtc = now, ExpiresAtUtc = until });
            try
            {
                await db.SaveChangesAsync(ct);
                return new LeaseStatus(LeaseState.Held, me, until, now, now, null);
            }
            catch (DbUpdateException)
            {
                // Another process inserted it first: it holds it.
                row = await db.JobLeases.AsNoTracking().FirstOrDefaultAsync(l => l.Name == BackgroundJobs, ct);
            }
        }
        return new LeaseStatus(LeaseState.StandingBy, row?.Holder, row?.ExpiresAtUtc, null, now, null);
    }

    /// <summary>Lets the lease go (at shutdown), so a standing-by process takes over at its next check rather than when it
    /// lapses. Only this holder's lease is let go.</summary>
    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_status.State != LeaseState.Held) return;
            await using var scope = scopes.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync(ct);
            var me = Holder;
            var now = time.GetUtcNow();
            await db.JobLeases.Where(l => l.Name == BackgroundJobs && l.Holder == me)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.ExpiresAtUtc, now), ct);
            _status = LeaseStatus.NotChecked;
            log.LogInformation("Let go of the background jobs' lease ({Holder}) at shutdown.", Holder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't let go of the background jobs' lease at shutdown; another process takes it when it lapses.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>A holder id for a person: "WEB01, process 4312" (null for none).</summary>
    public static string? Describe(string? holder)
    {
        if (string.IsNullOrWhiteSpace(holder)) return null;
        var parts = holder.Split('/');
        return parts.Length >= 2 && int.TryParse(parts[1], out var pid) ? $"{parts[0]}, process {pid}" : holder;
    }

    public void Dispose() => _gate.Dispose();
}
