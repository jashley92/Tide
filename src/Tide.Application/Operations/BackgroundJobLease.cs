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

    /// <summary>This process's own id on the lease, once it has asked.</summary>
    public string? Self { get; init; }

    /// <summary>The other processes on the database whose heartbeat hasn't lapsed (their ids): none, when TIDE is the one
    /// process it's meant to be.</summary>
    public IReadOnlyList<string> Others { get; init; } = [];

    /// <summary>A sentence naming this process and the others, when there are others; null otherwise. TIDE runs as one
    /// process (ADR 0001): some of its locks hold only within a process, so a second one serving pages can get round them.</summary>
    public string? Alarm => Others.Count == 0 ? null
        : $"Another TIDE process is running on this database: {string.Join(" and ", Others.Select(o => BackgroundJobLease.Describe(o)))}, as well as this one "
          + $"({BackgroundJobLease.Describe(Self) ?? "this process"}). TIDE is meant to run as one process: some of its locks (rotation decisions, "
          + "metric imports, reminder scans) hold only within a process, so two can make conflicting changes. Stop the extra one (a web garden, "
          + "an overlapping recycle, or a second server pointed at this database).";
}

/// <summary>Whether this process runs the background jobs: each hosted service asks before each pass, and skips the pass
/// when it doesn't.</summary>
public interface IJobLease
{
    /// <summary>Takes or renews the lease, and says whether this process holds it. Never throws but to cancel.</summary>
    Task<bool> HoldAsync(CancellationToken ct = default);

    /// <summary>The lease as this process last saw it.</summary>
    LeaseStatus Status { get; }

    /// <summary>Raised when <see cref="LeaseStatus.Alarm"/> starts, stops or names other processes, for a page to redraw.</summary>
    event Action? AlarmChanged;
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

    public event Action? AlarmChanged;

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
                // The other processes aren't known afresh: the last seen are kept, rather than an alarm flickering off.
                _status = stillHeld ? before with { CheckedAtUtc = now, Problem = problem }
                    : new LeaseStatus(LeaseState.Unreachable, before.Holder, before.ExpiresAtUtc, null, now, problem) { Self = Holder, Others = before.Others };
                log.LogWarning(ex, "Couldn't take or renew the background jobs' lease{Still}.", stillHeld ? "; still holding it until it nears its expiry" : "; no background job runs here until it can");
            }
            if (before.State != _status.State)
            {
                if (_status.State == LeaseState.Held) log.LogInformation("This process ({Holder}) now runs the background jobs.", Holder);
                else if (_status.State == LeaseState.StandingBy)
                    log.LogInformation("Standing by: {Other} runs the background jobs, so this process ({Holder}) runs none until it stops.", _status.HolderName, Holder);
            }
            if (!before.Others.SequenceEqual(_status.Others))
            {
                if (_status.Alarm is { } alarm) log.LogError("{Alarm}", alarm);
                else log.LogInformation("No other TIDE process is running on this database now.");
                AlarmChanged?.Invoke();
            }
            return _status.State == LeaseState.Held;
        }
        finally { _gate.Release(); }
    }

    private async Task<LeaseStatus> TakeAsync(LeaseStatus before, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var dbs = scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>();
        var others = await HeartbeatAsync(dbs, now, ct);
        return await LeaseAsync(dbs, before, now, ct) with { Self = Holder, Others = others };
    }

    /// <summary>Writes this process's heartbeat, deletes the lapsed ones, and says which other processes are running: those
    /// whose heartbeat hasn't lapsed, but for one on this machine whose process is gone.</summary>
    private async Task<IReadOnlyList<string>> HeartbeatAsync(IAppDbContextFactory dbs, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        var me = Holder;
        var lapsed = now - Expiry;
        await db.JobProcesses.Where(p => p.LastSeenAtUtc < lapsed).ExecuteDeleteAsync(ct);
        if (await db.JobProcesses.Where(p => p.Holder == me).ExecuteUpdateAsync(u => u.SetProperty(p => p.LastSeenAtUtc, now), ct) == 0)
        {
            db.JobProcesses.Add(new Domain.Entities.JobProcess { Holder = me, StartedAtUtc = now, LastSeenAtUtc = now });
            await db.SaveChangesAsync(ct);
        }
        var others = await db.JobProcesses.AsNoTracking().Where(p => p.Holder != me).OrderBy(p => p.Holder).Select(p => p.Holder).ToListAsync(ct);
        return [.. others.Where(h => !IsGoneFromThisMachine(h))];
    }

    private async Task<LeaseStatus> LeaseAsync(IAppDbContextFactory dbs, LeaseStatus before, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        var me = Holder;
        var until = now + Expiry;
        // One statement: set this holder and a new expiry where the row is this holder's, has lapsed, or (on the second
        // try) is still the dead holder just read. Whoever's renewal or takeover lands first wins; the other's matches no row.
        Task<int> ClaimAsync(string? dead) => db.JobLeases
            .Where(l => l.Name == BackgroundJobs && (l.Holder == me || l.ExpiresAtUtc <= now || (dead != null && l.Holder == dead)))
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.AcquiredAtUtc, l => l.Holder == me ? l.AcquiredAtUtc : now)
                .SetProperty(l => l.Holder, me)
                .SetProperty(l => l.ExpiresAtUtc, until), ct);
        // A renewal is the one statement: it was this holder's, so it's held since it was taken.
        if (await ClaimAsync(null) == 1) return new LeaseStatus(LeaseState.Held, me, until, before.State == LeaseState.Held ? before.HeldSinceUtc : now, now, null);
        var row = await db.JobLeases.AsNoTracking().FirstOrDefaultAsync(l => l.Name == BackgroundJobs, ct);
        // Its holder's process is gone from this machine (it was killed, or crashed): taken over now rather than when it lapses.
        if (row is not null && IsGoneFromThisMachine(row.Holder))
        {
            if (await ClaimAsync(row.Holder) == 1)
            {
                log.LogInformation("Took over the background jobs' lease from {Dead}, whose process is no longer running.", Describe(row.Holder));
                return new LeaseStatus(LeaseState.Held, me, until, now, now, null);
            }
            row = await db.JobLeases.AsNoTracking().FirstOrDefaultAsync(l => l.Name == BackgroundJobs, ct);
        }
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
    /// lapses, and its heartbeat with it, so no other process is alarmed by it. Only this holder's lease is let go.</summary>
    /// <remarks>At shutdown the host may already have disposed the container and the loggers (a test host does): nothing
    /// here throws but to cancel, the gate is never disposed, and a log line that can't be written is dropped.</remarks>
    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        var entered = false;
        try
        {
            await _gate.WaitAsync(ct);
            entered = true;
            if (_status.Self is null) return;   // never asked, or let go already: nothing written
            var held = _status.State == LeaseState.Held;
            _status = LeaseStatus.NotChecked;
            await using var scope = scopes.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync(ct);
            var me = Holder;
            await db.JobProcesses.Where(p => p.Holder == me).ExecuteDeleteAsync(ct);
            if (!held) return;
            var now = time.GetUtcNow();
            await db.JobLeases.Where(l => l.Name == BackgroundJobs && l.Holder == me)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.ExpiresAtUtc, now), ct);
            TryLog(() => log.LogInformation("Let go of the background jobs' lease ({Holder}) at shutdown.", Holder));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryLog(() => log.LogWarning(ex, "Couldn't let go of the background jobs' lease at shutdown; another process takes it when it lapses."));
        }
        finally
        {
            if (entered) _gate.Release();
        }
    }

    // A logger disposed with its host throws ObjectDisposedException, or an AggregateException of them from several
    // providers: the line is dropped.
    private static void TryLog(Action write)
    {
        try { write(); }
        catch (Exception ex) when (ex is ObjectDisposedException or AggregateException) { /* the logging is gone */ }
    }

    /// <summary>Whether <paramref name="holder"/> ("MACHINE/pid/…") names this machine and a process id that isn't running
    /// here. Conservative: a running process with that id (the holder, or another given its id since) is taken as alive,
    /// and so is anything that can't be read or looked up.</summary>
    public static bool IsGoneFromThisMachine(string? holder)
    {
        var parts = holder?.Split('/');
        if (parts is not { Length: >= 3 } || !string.Equals(parts[0], Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid)) return false;
        try
        {
            using var _ = System.Diagnostics.Process.GetProcessById(pid);
            return false;
        }
        catch (ArgumentException) { return true; }   // no process has that id
        catch (Exception) { return false; }
    }

    /// <summary>A holder id for a person: "WEB01, process 4312" (null for none).</summary>
    public static string? Describe(string? holder)
    {
        if (string.IsNullOrWhiteSpace(holder)) return null;
        var parts = holder.Split('/');
        return parts.Length >= 2 && int.TryParse(parts[1], out var pid) ? $"{parts[0]}, process {pid}" : holder;
    }

    /// <summary>Nothing to do. The gate isn't disposed: a host disposes its singletons before or while its hosted services
    /// stop (a test host does), and the release at shutdown still waits on it. It holds no wait handle, so nothing leaks.</summary>
    public void Dispose()
    {
        // Deliberately empty: see the summary.
    }
}
