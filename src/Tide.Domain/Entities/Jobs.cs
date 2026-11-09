using Tide.Domain.Common;

namespace Tide.Domain.Entities;

/// <summary>
/// Which process runs the background jobs. Every process on the database (a web garden, an overlapping recycle, a second
/// server started by mistake) asks for the one lease before each pass; the one that holds it runs the jobs and the rest
/// stand by, so a reminder isn't sent once by each. It's taken and renewed with one conditional UPDATE, and lapses at
/// <see cref="ExpiresAtUtc"/> when its holder stops renewing it.
/// </summary>
public class JobLease
{
    public const int MaxName = 64;
    public const int MaxHolder = 200;

    /// <summary>The lease's name: one for all the jobs today.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Who holds it: the machine, the process id and a value of its own ("WEB01/4312/9f0c…").</summary>
    public string Holder { get; set; } = string.Empty;

    /// <summary>When this holder took it (unchanged by a renewal).</summary>
    public DateTimeOffset AcquiredAtUtc { get; set; }

    /// <summary>When it lapses unless renewed.</summary>
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

/// <summary>
/// One run of a background job, kept for Diagnostics across restarts (pruned after 30 days). Its text is the job's own
/// summary ("3 new items") or its error, scrubbed of credentials and cut short: nothing a person typed.
/// </summary>
public class JobRun : Entity
{
    public const int MaxJob = 32;
    public const int MaxMachine = 64;
    public const int MaxDetail = 200;
    public const int MaxError = 500;

    /// <summary>The job's key ("reminders").</summary>
    public string Job { get; set; } = string.Empty;

    /// <summary>The server that ran it.</summary>
    public string Machine { get; set; } = string.Empty;

    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset FinishedAtUtc { get; set; }
    public bool Succeeded { get; set; }
    public string? Detail { get; set; }
    public string? Error { get; set; }
}
