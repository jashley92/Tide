namespace Tide.Application.Operations;

/// <summary>How the background jobs stand, for <c>/health/jobs</c>: the worst of the jobs' findings.</summary>
public enum JobHealthLevel { Healthy, Degraded, Unhealthy }

/// <summary>What's wrong with one job.</summary>
public enum JobProblem
{
    /// <summary>Its recent runs failed, <see cref="JobHealth.FailuresToFlag"/> or more in a row (Degraded).</summary>
    Failing,

    /// <summary>It hasn't run well for <see cref="JobHealth.OverdueAfter"/> intervals: it failed all along, or it stopped
    /// running (Unhealthy).</summary>
    Overdue,

    /// <summary>A run has gone on past the job's timeout (Unhealthy).</summary>
    Hung,
}

/// <summary>One job's problem, with a sentence for Diagnostics.</summary>
public sealed record JobFinding(JobState State, JobProblem Problem, JobHealthLevel Level, string Text);

/// <summary>The jobs' health: the worst level, each finding, and whether this process is standing by (it then runs no job,
/// so none is counted).</summary>
public sealed record JobHealthReport(JobHealthLevel Level, IReadOnlyList<JobFinding> Findings, bool StandingBy)
{
    public JobFinding? For(JobDefinition job) => Findings.FirstOrDefault(f => f.State.Job == job);

    /// <summary>Another TIDE process is running on the database (<see cref="LeaseStatus.Alarm"/>): at least Degraded.</summary>
    public string? Alarm { get; init; }
}

/// <summary>
/// Whether the background jobs are working, from what <see cref="JobMonitor"/> knows (its latest runs are put back from
/// the job history at start-up). A job that's turned on is:
/// <list type="bullet">
/// <item><b>Hung</b> (Unhealthy) when a run has gone on longer than the job's <see cref="JobDefinition.Timeout"/>;</item>
/// <item><b>Overdue</b> (Unhealthy) when it last went well more than <see cref="OverdueAfter"/> intervals ago (at least
/// <see cref="MinimumOverdue"/>), counted from when it was turned on or this process took the lease if that's later, and
/// it isn't running now;</item>
/// <item><b>Failing</b> (Degraded) when its last <see cref="FailuresToFlag"/> or more runs failed.</item>
/// </list>
/// A job that's off isn't counted, and nothing is while this process stands by under the lease (another runs the jobs).
/// </summary>
public static class JobHealth
{
    public const int OverdueAfter = 3;
    public const int FailuresToFlag = 3;

    /// <summary>A job that runs every minute isn't overdue after three: a pass over many slow sources can take that long.</summary>
    public static readonly TimeSpan MinimumOverdue = TimeSpan.FromMinutes(15);

    public static JobHealthReport Evaluate(IReadOnlyList<JobState> states, LeaseStatus lease, DateTimeOffset now)
    {
        // A second process is Degraded at least, on the holder and the standby alike.
        var alarm = lease.Alarm;
        var floor = alarm is null ? JobHealthLevel.Healthy : JobHealthLevel.Degraded;
        if (lease.State == LeaseState.StandingBy) return new JobHealthReport(floor, [], StandingBy: true) { Alarm = alarm };
        var findings = new List<JobFinding>();
        foreach (var s in states.Where(s => s.Enabled))
        {
            if (Check(s, lease, now) is { } finding) findings.Add(finding);
        }
        var level = findings.Count == 0 ? floor : (JobHealthLevel)Math.Max((int)floor, (int)findings.Max(f => f.Level));
        return new JobHealthReport(level, findings, StandingBy: false) { Alarm = alarm };
    }

    /// <summary>How long after its last good run (or its start) the job is overdue.</summary>
    public static TimeSpan OverdueThreshold(JobState s)
    {
        var threshold = s.Interval * OverdueAfter;
        return threshold < MinimumOverdue ? MinimumOverdue : threshold;
    }

    private static JobFinding? Check(JobState s, LeaseStatus lease, DateTimeOffset now)
    {
        if (s.Running && s.LastStartedUtc is { } started)
        {
            var running = now - started;
            if (running > s.Job.Timeout)
                return new JobFinding(s, JobProblem.Hung, JobHealthLevel.Unhealthy, $"{s.Job.Name}: a run started {Span(running)} ago and hasn't finished (its limit is {Span(s.Job.Timeout)}).");
        }
        else
        {
            // From its last good run, but never before it was turned on or this process took over the jobs: a job just
            // turned on, or a standby that's just taken over, gets its intervals before it's called overdue.
            var since = Latest(s.LastSuccessUtc, s.EnabledSinceUtc, lease.State == LeaseState.Held ? lease.HeldSinceUtc : null);
            if (since is { } from && now - from > OverdueThreshold(s))
                return new JobFinding(s, JobProblem.Overdue, JobHealthLevel.Unhealthy, s.LastSuccessUtc is { } ok
                    ? $"{s.Job.Name}: last ran well {Span(now - ok)} ago; it runs {Every(s.Interval)}."
                    : $"{s.Job.Name}: hasn't run well since it was turned on {Span(now - from)} ago; it runs {Every(s.Interval)}.");
        }
        if (s.FailuresInARow >= FailuresToFlag)
            return new JobFinding(s, JobProblem.Failing, JobHealthLevel.Degraded, $"{s.Job.Name}: the last {s.FailuresInARow} runs failed.");
        return null;
    }

    private static DateTimeOffset? Latest(params DateTimeOffset?[] times) => times.Where(t => t is not null).Max();

    /// <summary>"40 minutes", "5 hours", "3 days".</summary>
    public static string Span(TimeSpan t) =>
        t.TotalDays >= 2 ? $"{(int)t.TotalDays} days" : t.TotalHours >= 2 ? $"{(int)t.TotalHours} hours" : t.TotalMinutes >= 2 ? $"{(int)t.TotalMinutes} minutes"
        : $"{Math.Max(0, (int)t.TotalSeconds)} seconds";

    // Whole days or hours are judged in ticks, which are exact, not in the fractional Total* values.
    private static string Every(TimeSpan t)
    {
        var (days, hours) = (t.Ticks / TimeSpan.TicksPerDay, t.Ticks / TimeSpan.TicksPerHour);
        return days >= 1 && t.Ticks % TimeSpan.TicksPerDay == 0 ? (days == 1 ? "daily" : $"every {days} days")
            : hours >= 1 && t.Ticks % TimeSpan.TicksPerHour == 0 ? (hours == 1 ? "hourly" : $"every {hours} hours")
            : t <= TimeSpan.FromMinutes(1) ? "every minute" : $"every {t.TotalMinutes:0} minutes";
    }
}
