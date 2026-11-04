using System.Collections.Concurrent;

namespace Tide.Application.Operations;

/// <summary>A background job Diagnostics lists.</summary>
public sealed record JobDefinition(string Key, string Name, string Schedule);

/// <summary>A job's latest run, as Diagnostics shows it. <see cref="LastError"/> is scrubbed of credentials.</summary>
public sealed record JobState(
    JobDefinition Job, bool Enabled, DateTimeOffset? LastStartedUtc, DateTimeOffset? LastFinishedUtc, bool? LastSucceeded,
    string? LastDetail, string? LastError, DateTimeOffset? LastErrorAtUtc, DateTimeOffset? NextDueUtc, bool Running);

/// <summary>
/// What each background job last did, kept in memory for Administration › System › Diagnostics (a restart clears it).
/// The hosted services report into it; nothing here changes what they do.
/// Singleton.
/// </summary>
public sealed class JobMonitor(TimeProvider time)
{
    public const int MaxErrorChars = 400;

    public static readonly JobDefinition FeedPoll = new("feeds", "Polling the sources", "every minute, each source on its own interval, when Intake:Enabled");
    public static readonly JobDefinition CveRefresh = new("cve", "Refreshing the CVE data", "every Cve:RefreshHours, when Cve:Enabled");
    public static readonly JobDefinition Prune = new("prune", "Pruning old feed items", "daily, after Intake:RetentionDays");
    public static readonly JobDefinition Reminders = new("reminders", "Reminder and digest emails", "every 5 minutes; each kind when enabled under Notifications");
    public static readonly JobDefinition MetricDropFolder = new("metric-drop", "Metrics drop folder", "every Metrics:Import:PollMinutes, when DropFolderEnabled");
    public static readonly JobDefinition UpdateCheck = new("updates", "Update check", "every Updates:CheckIntervalHours, when turned on");
    public static readonly JobDefinition Webhooks = new("webhooks", "Webhooks", "every minute, when a webhook is set up under API access");

    public static readonly IReadOnlyList<JobDefinition> All = [FeedPoll, CveRefresh, Prune, Reminders, MetricDropFolder, UpdateCheck, Webhooks];

    private readonly ConcurrentDictionary<string, JobState> _states = new(
        All.Select(j => KeyValuePair.Create(j.Key, new JobState(j, false, null, null, null, null, null, null, null, false))));

    /// <summary>Every job, in the order Diagnostics lists them.</summary>
    public IReadOnlyList<JobState> States => [.. All.Select(j => _states[j.Key])];

    public JobState Get(JobDefinition job) => _states[job.Key];

    /// <summary>Whether the job is on, and when it's next due (null: not scheduled).</summary>
    public void Scheduled(JobDefinition job, bool enabled, DateTimeOffset? nextDueUtc = null) =>
        _states.AddOrUpdate(job.Key, _ => throw new KeyNotFoundException(job.Key), (_, s) => s with { Enabled = enabled, NextDueUtc = nextDueUtc });

    /// <summary>Runs <paramref name="work"/> as one run of the job, recording when it ran and how it went. The detail it
    /// returns ("3 items") is shown beside a success. An exception is recorded and thrown on.</summary>
    public async Task RunAsync(JobDefinition job, Func<Task<string?>> work)
    {
        var started = time.GetUtcNow();
        Update(job, s => s with { LastStartedUtc = started, Running = true });
        try
        {
            var detail = await work();
            Update(job, s => s with { LastFinishedUtc = time.GetUtcNow(), LastSucceeded = true, LastDetail = detail, Running = false });
        }
        catch (OperationCanceledException)
        {
            Update(job, s => s with { Running = false });
            throw;
        }
        catch (Exception ex)
        {
            Failed(job, ex.GetType().Name + ": " + ex.Message);
            throw;
        }
    }

    /// <summary>Records a run made outside <see cref="RunAsync"/> that went well (a job that only sometimes has work).</summary>
    public void Succeeded(JobDefinition job, DateTimeOffset startedUtc, string? detail) =>
        Update(job, s => s with { LastStartedUtc = startedUtc, LastFinishedUtc = time.GetUtcNow(), LastSucceeded = true, LastDetail = detail, Running = false });

    /// <summary>Records a run that finished without an exception but didn't work (the update check couldn't reach GitHub).</summary>
    public void Failed(JobDefinition job, string why)
    {
        var now = time.GetUtcNow();
        var error = SecretMask.Scrub(why);
        if (error.Length > MaxErrorChars) error = error[..MaxErrorChars] + "…";
        Update(job, s => s with { LastFinishedUtc = now, LastSucceeded = false, LastError = error, LastErrorAtUtc = now, Running = false });
    }

    private void Update(JobDefinition job, Func<JobState, JobState> change) => _states.AddOrUpdate(job.Key, _ => throw new KeyNotFoundException(job.Key), (_, s) => change(s));
}
