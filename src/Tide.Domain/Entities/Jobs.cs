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
/// One TIDE process on the database, as its last heartbeat said: each process writes its own row each time it checks the
/// job lease (every 30 seconds), so every process, the lease's holder included, sees whether another is running. TIDE is
/// one process (ADR 0001) and some of its locks are the process's own, so a second one is an alarm. A row not renewed
/// within <c>BackgroundJobLease.Expiry</c> has lapsed (its process stopped) and is deleted.
/// </summary>
public class JobProcess
{
    /// <summary>The process's id on the lease ("WEB01/4312/9f0c…").</summary>
    public string Holder { get; set; } = string.Empty;

    /// <summary>Its first heartbeat.</summary>
    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>Its latest heartbeat.</summary>
    public DateTimeOffset LastSeenAtUtc { get; set; }
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

/// <summary>
/// A day's snapshot of how big TIDE is: its tables' rows and sizes, the data root's bytes, and the disk's free space.
/// Written once a day by the statistics job; Diagnostics' Data &amp; retention panel shows the latest and the growth since 30
/// and 90 days before. Bookkeeping: not audited, never edited.
/// </summary>
public class SystemStat : Entity
{
    public const int MaxTables = 20_000;

    /// <summary>The organization's day it was taken for; one row a day.</summary>
    public DateOnly Day { get; set; }

    public DateTimeOffset TakenAtUtc { get; set; }

    /// <summary>The database's size in bytes, when the provider says (SQL Server's reserved pages; SQLite's page count).</summary>
    public long? DatabaseBytes { get; set; }

    /// <summary>The bytes under the data root (Files:RootPath), when it could be read.</summary>
    public long? FilesBytes { get; set; }

    /// <summary>Free and total bytes on the data root's disk, when it could be read.</summary>
    public long? DiskFreeBytes { get; set; }
    public long? DiskTotalBytes { get; set; }

    /// <summary>Each table's rows and bytes, as JSON: <c>[{"name":"AuditLog","rows":123,"bytes":456}]</c> (bytes null on SQLite).</summary>
    public string Tables { get; set; } = "[]";

    /// <summary>The data root's top-level folders and their bytes, as JSON: <c>[{"name":"screenshots","bytes":123}]</c>.</summary>
    public string Folders { get; set; } = "[]";
}

/// <summary>
/// A run of <c>Backup-Tide.ps1</c>, as the script records it in the database it backed up: a full set or a log backup,
/// when it finished and whether it succeeded. <c>/health/jobs</c> reads the newest successful full backup's time when
/// <c>Backup:StaleAfterHours</c> is set; Diagnostics shows the latest. A backup product that doesn't run the script records
/// nothing here, and the check stays off. Bookkeeping: not audited.
/// </summary>
public class BackupRun : Entity
{
    public const int MaxKind = 10;
    public const int MaxMachine = 100;
    public const int MaxPath = 400;
    public const int MaxNote = 500;

    /// <summary>"full" (a nightly set) or "log" (a transaction-log backup).</summary>
    public string Kind { get; set; } = "full";

    public DateTimeOffset FinishedAtUtc { get; set; }
    public bool Succeeded { get; set; }
    public string Machine { get; set; } = string.Empty;

    /// <summary>Where the set or the log backup was written.</summary>
    public string? Path { get; set; }

    public long? Bytes { get; set; }
    public string? Note { get; set; }
}
