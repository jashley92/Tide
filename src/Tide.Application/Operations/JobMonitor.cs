using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Tide.Application.Operations;

/// <summary>A background job Diagnostics lists. <paramref name="Interval"/> is how often it runs when nothing says
/// otherwise (a job whose interval is a setting passes it to <see cref="JobMonitor.Scheduled"/>); <paramref name="Timeout"/>
/// is how long one run may take before it's called hung; <paramref name="EventId"/> is the id its failures are logged
/// under, for an alert rule that watches one job.</summary>
public sealed record JobDefinition(string Key, string Name, string Schedule, TimeSpan Interval, TimeSpan Timeout, int EventId);

/// <summary>A run that did its work but found parts of it failed (a reminder scan whose relay was down): recorded as a
/// failure with just its message.</summary>
public sealed class JobProblemException(string message) : Exception(message);

/// <summary>A job's latest run, as Diagnostics shows it. <see cref="LastError"/> is scrubbed of credentials.</summary>
public sealed record JobState(
    JobDefinition Job, bool Enabled, DateTimeOffset? LastStartedUtc, DateTimeOffset? LastFinishedUtc, bool? LastSucceeded,
    string? LastDetail, string? LastError, DateTimeOffset? LastErrorAtUtc, DateTimeOffset? NextDueUtc, bool Running)
{
    /// <summary>When a run last went well (null: none known, here or in the job history).</summary>
    public DateTimeOffset? LastSuccessUtc { get; init; }

    /// <summary>Failed runs since the last that went well.</summary>
    public int FailuresInARow { get; init; }

    /// <summary>When this process last saw the job turned on (null while it's off), so a job just turned on isn't overdue.</summary>
    public DateTimeOffset? EnabledSinceUtc { get; init; }

    /// <summary>How often it runs, as last scheduled (its <see cref="JobDefinition.Interval"/> until then).</summary>
    public TimeSpan Interval { get; init; } = Job.Interval;
}

/// <summary>A finished run, waiting to be written to the job history (<see cref="JobRunLog"/>).</summary>
public sealed record FinishedRun(string Job, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, bool Succeeded, string? Detail, string? Error);

/// <summary>
/// What each background job last did, for Administration › System › Diagnostics and <c>/health/jobs</c>. Kept in memory;
/// each finished run is also queued for the job history (<see cref="JobRunLog"/> writes it, and puts back each job's
/// latest run at start-up). The hosted services report into it; nothing here changes what they do. A failure is logged
/// as a Warning under the job's own <see cref="JobDefinition.EventId"/>.
/// Singleton.
/// </summary>
public sealed class JobMonitor(TimeProvider time, ILogger<JobMonitor>? log = null)
{
    public const int MaxErrorChars = 400;

    /// <summary>Most finished runs held while the history can't be written (the database down); the oldest go first.</summary>
    public const int MaxPending = 2000;

    public static readonly JobDefinition FeedPoll = new("feeds", "Polling the sources", "every minute, each source on its own interval, when Intake:Enabled",
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30), 7101);
    public static readonly JobDefinition CveRefresh = new("cve", "Refreshing the CVE data", "every Cve:RefreshHours, when Cve:Enabled",
        TimeSpan.FromHours(24), TimeSpan.FromHours(2), 7102);
    public static readonly JobDefinition Prune = new("prune", "Pruning old feed items", "daily, after Intake:RetentionDays",
        TimeSpan.FromDays(1), TimeSpan.FromHours(1), 7103);
    public static readonly JobDefinition Reminders = new("reminders", "Reminder and digest emails", "every 5 minutes; each kind when enabled under Notifications",
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), 7104);
    public static readonly JobDefinition MetricDropFolder = new("metric-drop", "Metrics drop folder", "every Metrics:Import:PollMinutes, when DropFolderEnabled",
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), 7105);
    public static readonly JobDefinition UpdateCheck = new("updates", "Update check", "every Updates:CheckIntervalHours, when turned on",
        TimeSpan.FromHours(24), TimeSpan.FromMinutes(10), 7106);
    public static readonly JobDefinition Webhooks = new("webhooks", "Webhooks", "every minute, when a webhook is set up under API access",
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15), 7107);
    public static readonly JobDefinition UrlHashes = new("url-hashes", "Hashing stored addresses", "once after an upgrade, until every older article and link has its address hash",
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30), 7108);

    public static readonly IReadOnlyList<JobDefinition> All = [FeedPoll, CveRefresh, Prune, Reminders, MetricDropFolder, UpdateCheck, Webhooks, UrlHashes];

    private readonly ConcurrentDictionary<string, JobState> _states = new(
        All.Select(j => KeyValuePair.Create(j.Key, new JobState(j, false, null, null, null, null, null, null, null, false))));

    private readonly Lock _pendingLock = new();
    private readonly List<FinishedRun> _pending = [];

    /// <summary>Every job, in the order Diagnostics lists them.</summary>
    public IReadOnlyList<JobState> States => [.. All.Select(j => _states[j.Key])];

    public JobState Get(JobDefinition job) => _states[job.Key];

    /// <summary>Whether the job is on, when it's next due (null: not scheduled), and how often it runs when that's a
    /// setting (null: as before), and how long a run may take before it's called hung when that depends on settings (null:
    /// as before).</summary>
    public void Scheduled(JobDefinition job, bool enabled, DateTimeOffset? nextDueUtc = null, TimeSpan? interval = null, TimeSpan? timeout = null)
    {
        var now = time.GetUtcNow();
        Update(job, s => s with
        {
            Job = timeout is { } limit ? s.Job with { Timeout = limit } : s.Job,
            Enabled = enabled, NextDueUtc = nextDueUtc, Interval = interval ?? s.Interval,
            EnabledSinceUtc = !enabled ? null : s.Enabled && s.EnabledSinceUtc is { } since ? since : now,
        });
    }

    /// <summary>Runs <paramref name="work"/> as one run of the job, recording when it ran and how it went. The detail it
    /// returns ("3 items") is shown beside a success. An exception is recorded, logged and thrown on.</summary>
    public async Task RunAsync(JobDefinition job, Func<Task<string?>> work)
    {
        var started = time.GetUtcNow();
        Update(job, s => s with { LastStartedUtc = started, Running = true });
        try
        {
            var detail = await work();
            Succeeded(job, started, detail);
        }
        catch (OperationCanceledException)
        {
            Update(job, s => s with { Running = false });
            throw;
        }
        catch (Exception ex)
        {
            if (ex is JobProblemException) Fail(job, started, ex.Message, null);
            else Fail(job, started, Describe(ex), ex);
            throw;
        }
    }

    /// <summary>
    /// One poll of a job a hosted service runs every <paramref name="interval"/>: nothing at all unless this process holds
    /// the <paramref name="lease"/> (another runs it, and Diagnostics shows this one standing by, with no schedule of its
    /// own); else a run of <paramref name="work"/> while the job is on, then its schedule. <paramref name="isOn"/> says
    /// whether it's on (null: always); should that fail, the job stays as it was, and one that's on shows the failure as a
    /// run would. A failure is recorded and logged by the monitor and at Debug as <paramref name="retrying"/> on
    /// <paramref name="log"/>, never thrown (except for shutting down).
    /// </summary>
    public async Task PassAsync(JobDefinition job, IJobLease lease, TimeSpan interval, Func<Task<bool>>? isOn, Func<Task<string?>> work,
        ILogger log, string retrying, CancellationToken ct)
    {
        if (!await lease.HoldAsync(ct)) return;
        var on = isOn is null || Get(job).Enabled;
        try
        {
            if (isOn is not null)
            {
                try { on = await isOn(); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    if (on) Failed(job, ex);
                    throw;
                }
            }
            if (on) await RunAsync(job, work);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "{Retrying}", retrying);
        }
        Scheduled(job, on, on ? time.GetUtcNow() + interval : null);
    }

    /// <summary>Records a run made outside <see cref="RunAsync"/> that went well (a job that only sometimes has work).</summary>
    public void Succeeded(JobDefinition job, DateTimeOffset startedUtc, string? detail)
    {
        var now = time.GetUtcNow();
        Update(job, s => s with
        {
            LastStartedUtc = startedUtc, LastFinishedUtc = now, LastSucceeded = true, LastDetail = detail, Running = false,
            LastSuccessUtc = now, FailuresInARow = 0,
        });
        Queue(new FinishedRun(job.Key, startedUtc, now, true, detail, null));
    }

    /// <summary>A run made outside <see cref="RunAsync"/> started (a job that only sometimes has work, and says so once it
    /// does): it shows as running until <see cref="Succeeded"/> or <see cref="Failed(JobDefinition, DateTimeOffset, string)"/>.</summary>
    public void Started(JobDefinition job, DateTimeOffset startedUtc) => Update(job, s => s with { LastStartedUtc = startedUtc, Running = true });

    /// <summary>Records a run that finished without an exception but didn't work (the update check couldn't reach GitHub).</summary>
    public void Failed(JobDefinition job, string why) => Fail(job, time.GetUtcNow(), why, null);

    /// <summary>As <see cref="Failed(JobDefinition, string)"/>, for a run that started at <paramref name="startedUtc"/>.</summary>
    public void Failed(JobDefinition job, DateTimeOffset startedUtc, string why) => Fail(job, startedUtc, why, null);

    /// <summary>Records a run whose exception was caught outside <see cref="RunAsync"/>, logged with it.</summary>
    public void Failed(JobDefinition job, Exception exception) => Fail(job, time.GetUtcNow(), Describe(exception), exception);

    private void Fail(JobDefinition job, DateTimeOffset startedUtc, string why, Exception? exception)
    {
        var now = time.GetUtcNow();
        var error = SecretMask.Scrub(why);
        if (error.Length > MaxErrorChars) error = error[..MaxErrorChars] + "…";
        var state = Update(job, s => s with
        {
            LastFinishedUtc = now, LastSucceeded = false, LastError = error, LastErrorAtUtc = now, Running = false,
            FailuresInARow = s.FailuresInARow + 1,
        });
        Queue(new FinishedRun(job.Key, startedUtc, now, false, null, error));
        log?.Log(LogLevel.Warning, new EventId(job.EventId, "JobFailed"), exception,
            "Background job {Job} failed ({FailuresInARow} in a row); it's tried again at its next run: {Error}", job.Key, state.FailuresInARow, error);
    }

    /// <summary>
    /// Puts back what the job history last recorded for the job, at start-up: its latest run, its latest failure, when it
    /// last went well and how many runs failed since. Nothing is put back over a run this process has made.
    /// </summary>
    public void Restore(JobDefinition job, FinishedRun latest, FinishedRun? lastFailure, DateTimeOffset? lastSuccessUtc, int failuresInARow) =>
        Update(job, s => s.LastStartedUtc is not null ? s : s with
        {
            LastStartedUtc = latest.StartedUtc, LastFinishedUtc = latest.FinishedUtc, LastSucceeded = latest.Succeeded,
            LastDetail = latest.Succeeded ? latest.Detail : null, LastError = lastFailure?.Error, LastErrorAtUtc = lastFailure?.FinishedUtc,
            LastSuccessUtc = lastSuccessUtc, FailuresInARow = failuresInARow,
        });

    /// <summary>Takes the finished runs not yet written to the job history.</summary>
    public IReadOnlyList<FinishedRun> TakeFinished()
    {
        lock (_pendingLock)
        {
            var taken = _pending.ToList();
            _pending.Clear();
            return taken;
        }
    }

    /// <summary>Puts back runs the history couldn't write, ahead of any finished since, to be tried again.</summary>
    public void ReturnFinished(IReadOnlyList<FinishedRun> runs)
    {
        lock (_pendingLock)
        {
            _pending.InsertRange(0, runs);
            Trim();
        }
    }

    private void Queue(FinishedRun run)
    {
        lock (_pendingLock)
        {
            _pending.Add(run);
            Trim();
        }
    }

    private void Trim()
    {
        if (_pending.Count > MaxPending) _pending.RemoveRange(0, _pending.Count - MaxPending);
    }

    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    private JobState Update(JobDefinition job, Func<JobState, JobState> change) => _states.AddOrUpdate(job.Key, _ => throw new KeyNotFoundException(job.Key), (_, s) => change(s));
}
