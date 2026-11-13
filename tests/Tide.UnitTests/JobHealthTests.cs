using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Tide.Application.Operations;

namespace Tide.UnitTests;

public class JobHealthTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);
    private static readonly LeaseStatus Held = new(LeaseState.Held, "WEB01/42/abc", Start.AddMinutes(3), Start, Start, null);

    private static (JobMonitor Jobs, TestClock Clock) Monitor()
    {
        var clock = new TestClock(Start);
        return (new JobMonitor(clock), clock);
    }

    private static JobHealthReport Evaluate(JobMonitor jobs, TestClock clock, LeaseStatus? lease = null) =>
        JobHealth.Evaluate(jobs.States, lease ?? Held, clock.Now);

    [Fact]
    public async Task AJobRunningOnSchedule_IsHealthy()
    {
        var (jobs, clock) = Monitor();
        jobs.Scheduled(JobMonitor.Reminders, true, clock.Now.AddMinutes(5));
        await jobs.RunAsync(JobMonitor.Reminders, () => Task.FromResult<string?>(null));
        clock.Now = clock.Now.AddMinutes(10);
        Evaluate(jobs, clock).Should().Match<JobHealthReport>(r => r.Level == JobHealthLevel.Healthy && r.Findings.Count == 0 && !r.StandingBy);
    }

    [Fact]
    public void ThreeFailuresInARow_AreDegraded_AndASuccessClearsThem()
    {
        var (jobs, clock) = Monitor();
        jobs.Scheduled(JobMonitor.Reminders, true);
        jobs.Failed(JobMonitor.Reminders, "relay down");
        jobs.Failed(JobMonitor.Reminders, "relay down");
        Evaluate(jobs, clock).Level.Should().Be(JobHealthLevel.Healthy, "two failures aren't yet a pattern");
        jobs.Failed(JobMonitor.Reminders, "relay down");
        var report = Evaluate(jobs, clock);
        report.Level.Should().Be(JobHealthLevel.Degraded);
        report.For(JobMonitor.Reminders)!.Should().Match<JobFinding>(f => f.Problem == JobProblem.Failing && f.Text.Contains("last 3 runs failed"));

        jobs.Succeeded(JobMonitor.Reminders, clock.Now, null);
        jobs.Get(JobMonitor.Reminders).FailuresInARow.Should().Be(0);
        Evaluate(jobs, clock).Level.Should().Be(JobHealthLevel.Healthy);
    }

    [Fact]
    public async Task AJobThatHasntRunWellForThreeIntervals_IsOverdue()
    {
        var (jobs, clock) = Monitor();
        jobs.Scheduled(JobMonitor.CveRefresh, true, interval: TimeSpan.FromHours(12));
        await jobs.RunAsync(JobMonitor.CveRefresh, () => Task.FromResult<string?>("ok"));
        clock.Now = clock.Now.AddHours(35);
        Evaluate(jobs, clock).Level.Should().Be(JobHealthLevel.Healthy, "under three 12-hour intervals");
        clock.Now = clock.Now.AddHours(2);
        var finding = Evaluate(jobs, clock).For(JobMonitor.CveRefresh)!;
        finding.Should().Match<JobFinding>(f => f.Problem == JobProblem.Overdue && f.Level == JobHealthLevel.Unhealthy);
        finding.Text.Should().Contain("37 hours").And.Contain("every 12 hours");
    }

    [Fact]
    public void AJobThatNeverRan_IsOverdue_CountedFromWhenItWasTurnedOn_WithAFloorForMinutelyJobs()
    {
        var (jobs, clock) = Monitor();
        jobs.Scheduled(JobMonitor.Webhooks, true);
        clock.Now = clock.Now.AddMinutes(10);
        Evaluate(jobs, clock).Level.Should().Be(JobHealthLevel.Healthy, $"a minutely job isn't overdue before {JobHealth.MinimumOverdue}");
        jobs.Scheduled(JobMonitor.Webhooks, true);
        jobs.Get(JobMonitor.Webhooks).EnabledSinceUtc.Should().Be(Start, "still on: it's been on since it was first seen on");
        clock.Now = clock.Now.AddMinutes(6);
        Evaluate(jobs, clock).For(JobMonitor.Webhooks)!.Text.Should().Contain("since it was turned on");
    }

    [Fact]
    public void AJobThatsOff_IsntCounted()
    {
        var (jobs, clock) = Monitor();
        jobs.Scheduled(JobMonitor.FeedPoll, true);
        for (var i = 0; i < 5; i++) jobs.Failed(JobMonitor.FeedPoll, "boom");
        clock.Now = clock.Now.AddDays(3);
        Evaluate(jobs, clock).Level.Should().Be(JobHealthLevel.Unhealthy);
        jobs.Scheduled(JobMonitor.FeedPoll, false);
        Evaluate(jobs, clock).Level.Should().Be(JobHealthLevel.Healthy);
    }

    [Fact]
    public async Task ARunPastItsTimeout_IsHung_ButOneStillWithinIt_IsntOverdue()
    {
        var (jobs, clock) = Monitor();
        jobs.Scheduled(JobMonitor.FeedPoll, true);
        var release = new TaskCompletionSource<string?>();
        var run = jobs.RunAsync(JobMonitor.FeedPoll, () => release.Task);
        clock.Now = clock.Now.AddMinutes(20);
        Evaluate(jobs, clock).Level.Should().Be(JobHealthLevel.Healthy, "a long pass within its timeout is working, not overdue");
        clock.Now = clock.Now.AddMinutes(11);
        var finding = Evaluate(jobs, clock).For(JobMonitor.FeedPoll)!;
        finding.Should().Match<JobFinding>(f => f.Problem == JobProblem.Hung && f.Level == JobHealthLevel.Unhealthy && f.Text.Contains("31 minutes"));
        release.SetResult(null);
        await run;
        Evaluate(jobs, clock).Level.Should().Be(JobHealthLevel.Healthy);
    }

    [Fact]
    public void StandingBy_NothingIsCounted()
    {
        var (jobs, clock) = Monitor();
        jobs.Scheduled(JobMonitor.Reminders, true);
        clock.Now = clock.Now.AddDays(1);
        Evaluate(jobs, clock).Level.Should().Be(JobHealthLevel.Unhealthy);
        var standby = new LeaseStatus(LeaseState.StandingBy, "WEB02/7/def", clock.Now.AddMinutes(2), null, clock.Now, null);
        Evaluate(jobs, clock, standby).Should().Match<JobHealthReport>(r => r.Level == JobHealthLevel.Healthy && r.StandingBy && r.Findings.Count == 0);
    }

    [Fact]
    public void AProcessThatJustTookTheLeaseOver_GetsItsIntervalsBeforeItsOverdue()
    {
        var (jobs, clock) = Monitor();
        jobs.Scheduled(JobMonitor.Reminders, true);
        clock.Now = clock.Now.AddDays(1);
        var tookOver = Held with { HeldSinceUtc = clock.Now.AddMinutes(-1) };
        Evaluate(jobs, clock, tookOver).Level.Should().Be(JobHealthLevel.Healthy);
        clock.Now = clock.Now.AddMinutes(20);
        Evaluate(jobs, clock, tookOver).Level.Should().Be(JobHealthLevel.Unhealthy);
    }

    [Fact]
    public void ASecondProcess_IsDegraded_WhetherThisOneHoldsTheLeaseOrStandsBy()
    {
        var (jobs, clock) = Monitor();
        var held = Held with { Self = "WEB01/42/abc", Others = ["WEB02/7/def"] };
        var report = Evaluate(jobs, clock, held);
        report.Level.Should().Be(JobHealthLevel.Degraded);
        report.Alarm.Should().Contain("WEB01, process 42").And.Contain("WEB02, process 7");

        var standby = new LeaseStatus(LeaseState.StandingBy, "WEB02/7/def", clock.Now.AddMinutes(2), null, clock.Now, null) { Self = "WEB01/42/abc", Others = ["WEB02/7/def"] };
        Evaluate(jobs, clock, standby).Should().Match<JobHealthReport>(r => r.Level == JobHealthLevel.Degraded && r.StandingBy && r.Alarm != null);

        // An Unhealthy job stays Unhealthy.
        jobs.Scheduled(JobMonitor.Reminders, true);
        clock.Now = clock.Now.AddDays(1);
        Evaluate(jobs, clock, held).Level.Should().Be(JobHealthLevel.Unhealthy);
        Evaluate(jobs, clock).Alarm.Should().BeNull();
    }

    [Fact]
    public void AHolderIsNamedForAPerson()
    {
        BackgroundJobLease.Describe("WEB01/4312/9f0c").Should().Be("WEB01, process 4312");
        BackgroundJobLease.Describe(null).Should().BeNull();
        new LeaseStatus(LeaseState.StandingBy, "odd", null, null, null, null).HolderName.Should().Be("odd");
    }
}

public class JobMonitorHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private sealed class Recorder : ILogger
    {
        public List<(LogLevel Level, EventId Id, string Message, Exception? Exception)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, eventId, formatter(state, exception), exception));
    }

    private sealed class Logger<T>(Recorder inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

    [Fact]
    public async Task EachFailure_IsAWarning_UnderTheJobsOwnEventId_Scrubbed()
    {
        var log = new Recorder();
        var jobs = new JobMonitor(new TestClock(Start), new Logger<JobMonitor>(log));
        var boom = new InvalidOperationException("Password=hunter2 refused");
        await FluentActions.Awaiting(() => jobs.RunAsync(JobMonitor.Webhooks, () => throw boom)).Should().ThrowAsync<InvalidOperationException>();
        jobs.Failed(JobMonitor.UpdateCheck, "couldn't reach GitHub");

        log.Lines.Should().HaveCount(2).And.OnlyContain(l => l.Level == LogLevel.Warning && l.Id.Name == "JobFailed");
        log.Lines[0].Should().Match<(LogLevel, EventId Id, string Message, Exception? Exception)>(l =>
            l.Id.Id == JobMonitor.Webhooks.EventId && l.Message.Contains("webhooks") && l.Message.Contains("1 in a row") && !l.Message.Contains("hunter2")
            && l.Exception == boom);
        log.Lines[1].Id.Id.Should().Be(JobMonitor.UpdateCheck.EventId);
        JobMonitor.All.Select(j => j.EventId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task FinishedRuns_AreQueuedForTheHistory_AndPutBackAheadWhenTheyCantBeWritten()
    {
        var clock = new TestClock(Start);
        var jobs = new JobMonitor(clock);
        await jobs.RunAsync(JobMonitor.Prune, () => Task.FromResult<string?>("3 removed"));
        jobs.Failed(JobMonitor.Prune, "busy");
        var taken = jobs.TakeFinished();
        taken.Select(r => (r.Job, r.Succeeded, r.Detail, r.Error)).Should().Equal(("prune", true, "3 removed", null), ("prune", false, null, "busy"));
        jobs.TakeFinished().Should().BeEmpty();

        jobs.Succeeded(JobMonitor.Prune, clock.Now, "later");
        jobs.ReturnFinished(taken);
        jobs.TakeFinished().Select(r => r.Detail ?? r.Error).Should().Equal("3 removed", "busy", "later");
    }

    [Fact]
    public void Restore_PutsBackTheLatestRun_ButNotOverOneThisProcessMade()
    {
        var clock = new TestClock(Start);
        var jobs = new JobMonitor(clock);
        var latest = new FinishedRun("reminders", Start.AddHours(-2), Start.AddHours(-2), false, null, "relay down");
        jobs.Restore(JobMonitor.Reminders, latest, latest, Start.AddHours(-3), 4);
        jobs.Get(JobMonitor.Reminders).Should().Match<JobState>(s =>
            s.LastSucceeded == false && s.LastError == "relay down" && s.LastSuccessUtc == Start.AddHours(-3) && s.FailuresInARow == 4);

        jobs.Succeeded(JobMonitor.Webhooks, clock.Now, "mine");
        jobs.Restore(JobMonitor.Webhooks, latest with { Job = "webhooks" }, null, null, 9);
        jobs.Get(JobMonitor.Webhooks).Should().Match<JobState>(s => s.LastDetail == "mine" && s.FailuresInARow == 0);
    }
}
