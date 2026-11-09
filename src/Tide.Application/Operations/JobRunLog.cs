using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;

namespace Tide.Application.Operations;

/// <summary>A job's runs over the history Diagnostics shows.</summary>
public sealed record JobHistory(string Job, int Runs, int Failed);

/// <summary>A failed run from the history.</summary>
public sealed record JobFailure(string Job, string Machine, DateTimeOffset FinishedUtc, string? Error);

/// <summary>What the job history holds, for Diagnostics: each job's runs and failures over <see cref="JobRunLog.Keep"/>,
/// and the latest failures.</summary>
public sealed record JobRunHistory(IReadOnlyList<JobHistory> Jobs, IReadOnlyList<JobFailure> RecentFailures)
{
    public static readonly JobRunHistory None = new([], []);

    public JobHistory? For(JobDefinition job) => Jobs.FirstOrDefault(j => j.Job == job.Key);
}

/// <summary>
/// The job history (the <c>JobRuns</c> table): writes the runs <see cref="JobMonitor"/> has queued, prunes runs older
/// than <see cref="Keep"/>, puts each job's latest run back into the monitor at start-up, and reads the history for
/// Diagnostics. Each run's text is the job's own summary or its scrubbed error, cut to the column. A write that fails
/// (the database down) puts the runs back to be tried again.
/// Singleton.
/// </summary>
public sealed class JobRunLog(JobMonitor jobs, TimeProvider time, ILogger<JobRunLog> log)
{
    /// <summary>How long runs are kept.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    /// <summary>How many recent failures Diagnostics lists.</summary>
    public const int RecentFailureCount = 10;

    /// <summary>Writes the runs finished since the last flush. Returns how many were written.</summary>
    public async Task<int> FlushAsync(IAppDbContextFactory dbs, CancellationToken ct = default)
    {
        var runs = jobs.TakeFinished();
        if (runs.Count == 0) return 0;
        try
        {
            await using var db = await dbs.CreateAsync(ct);
            var machine = Cut(Environment.MachineName, JobRun.MaxMachine)!;
            db.JobRuns.AddRange(runs.Select(r => new JobRun
            {
                Job = Cut(r.Job, JobRun.MaxJob)!, Machine = machine, StartedAtUtc = r.StartedUtc, FinishedAtUtc = r.FinishedUtc,
                Succeeded = r.Succeeded, Detail = Cut(r.Detail, JobRun.MaxDetail), Error = Cut(r.Error, JobRun.MaxError),
            }));
            await db.SaveChangesAsync(ct);
            return runs.Count;
        }
        catch (Exception ex)
        {
            jobs.ReturnFinished(runs);
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            log.LogWarning(ex, "Couldn't write {Count} background job run(s) to the job history; they're tried again shortly.", runs.Count);
            return 0;
        }
    }

    /// <summary>Removes runs started more than <see cref="Keep"/> ago. Returns how many.</summary>
    public async Task<int> PruneAsync(IAppDbContextFactory dbs, CancellationToken ct = default)
    {
        var cutoff = time.GetUtcNow() - Keep;
        await using var db = await dbs.CreateAsync(ct);
        return await db.JobRuns.Where(r => r.StartedAtUtc < cutoff).ExecuteDeleteAsync(ct);
    }

    /// <summary>Puts each job's latest recorded run back into the monitor (at start-up), so Diagnostics and
    /// <c>/health/jobs</c> know when it last went well across a restart.</summary>
    public async Task RestoreAsync(IAppDbContextFactory dbs, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        foreach (var job in JobMonitor.All)
        {
            var runs = db.JobRuns.AsNoTracking().Where(r => r.Job == job.Key);
            var latest = await runs.OrderByDescending(r => r.StartedAtUtc).FirstOrDefaultAsync(ct);
            if (latest is null) continue;
            // The latest run is the last success or the last failure; only the other is asked for, and the failures
            // since the last success only when the latest failed.
            var lastSuccess = latest.Succeeded ? latest : await runs.Where(r => r.Succeeded).OrderByDescending(r => r.StartedAtUtc).FirstOrDefaultAsync(ct);
            var lastFailure = !latest.Succeeded ? latest : await runs.Where(r => !r.Succeeded).OrderByDescending(r => r.StartedAtUtc).FirstOrDefaultAsync(ct);
            var failuresInARow = latest.Succeeded ? 0
                : lastSuccess is null ? await runs.CountAsync(r => !r.Succeeded, ct)
                : await runs.CountAsync(r => !r.Succeeded && r.StartedAtUtc > lastSuccess.StartedAtUtc, ct);
            jobs.Restore(job, Finished(latest), lastFailure is null ? null : Finished(lastFailure), lastSuccess?.FinishedAtUtc, failuresInARow);
        }
    }

    /// <summary>Each job's runs and failures over the history, and the latest failures.</summary>
    public static async Task<JobRunHistory> HistoryAsync(IAppDbContextFactory dbs, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var counts = await db.JobRuns.AsNoTracking().GroupBy(r => r.Job)
            .Select(g => new { g.Key, Runs = g.Count(), Failed = g.Count(r => !r.Succeeded) }).ToListAsync(ct);
        var failures = await db.JobRuns.AsNoTracking().Where(r => !r.Succeeded).OrderByDescending(r => r.StartedAtUtc).Take(RecentFailureCount)
            .Select(r => new JobFailure(r.Job, r.Machine, r.FinishedAtUtc, r.Error)).ToListAsync(ct);
        return new JobRunHistory([.. counts.Select(c => new JobHistory(c.Key, c.Runs, c.Failed))], failures);
    }

    private static FinishedRun Finished(JobRun r) => new(r.Job, r.StartedAtUtc, r.FinishedAtUtc, r.Succeeded, r.Detail, r.Error);

    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..(max - 1)] + "…";
}
