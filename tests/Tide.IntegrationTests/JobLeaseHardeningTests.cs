using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Application.Abstractions;
using Tide.Application.Operations;

namespace Tide.IntegrationTests;

/// <summary>A dead holder on this machine is taken over at once, rather than when its lease lapses.</summary>
public class DeadHolderTakeoverTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Here = Environment.MachineName;

    private static (BackgroundJobLease Lease, ServiceProvider Provider) Lease(IAppDbContextFactory dbs, TimeProvider clock, string holder)
    {
        var provider = new ServiceCollection().AddSingleton(dbs).BuildServiceProvider();
        return (new BackgroundJobLease(provider.GetRequiredService<IServiceScopeFactory>(), clock, NullLogger<BackgroundJobLease>.Instance, holder), provider);
    }

    /// <summary>The id of a process that has run and exited, and that no running process has taken since.</summary>
    internal static int DeadPid()
    {
        for (var tries = 0; tries < 5; tries++)
        {
            int pid;
            using (var p = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false })!)
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                pid = p.Id;
            }
            try { using var _ = Process.GetProcessById(pid); }
            catch (ArgumentException) { return pid; }
        }
        throw new InvalidOperationException("Couldn't find a process id that isn't running.");
    }

    /// <summary>Writes the lease as <paramref name="holder"/>'s, live for its whole expiry.</summary>
    private static async Task HeldBy(TestDb db, string holder, DateTimeOffset at)
    {
        await using var ctx = db.Context();
        ctx.JobLeases.Add(new Domain.Entities.JobLease { Name = BackgroundJobLease.BackgroundJobs, Holder = holder, AcquiredAtUtc = at, ExpiresAtUtc = at + BackgroundJobLease.Expiry });
        await ctx.SaveChangesAsync();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AHolderWhoseProcessIsGone_OnThisMachine_IsTakenOverAtOnce(string provider)
    {
        using var db = new TestDb(provider: provider);
        var clock = new TestClock(Start);
        var dead = $"{Here}/{DeadPid()}/aaaa";
        await HeldBy(db, dead, Start);
        var (b, pb) = Lease(db, clock, $"{Here}/{Environment.ProcessId}/bbbb");
        await using var _ = pb;

        clock.Now = Start.AddSeconds(5);
        (await b.HoldAsync()).Should().BeTrue("the holder's process is gone, so its lease is let go without waiting out the expiry");
        b.Status.HeldSinceUtc.Should().Be(clock.Now);
        await using var ctx = db.Context();
        (await ctx.JobLeases.SingleAsync()).Should().Match<Domain.Entities.JobLease>(l =>
            l.Holder == b.Holder && l.AcquiredAtUtc == clock.Now && l.ExpiresAtUtc == clock.Now + BackgroundJobLease.Expiry);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AHolderWhoseProcessIsAlive_OrOnAnotherMachine_IsWaitedOut(string provider)
    {
        // A live pid may be the holder or an unrelated process given its id: either way it isn't taken over early.
        using (var db = new TestDb(provider: provider))
        {
            var clock = new TestClock(Start);
            await HeldBy(db, $"{Here}/{Environment.ProcessId}/aaaa", Start);
            var (b, pb) = Lease(db, clock, $"{Here}/{Environment.ProcessId}/bbbb");
            await using var _ = pb;
            (await b.HoldAsync()).Should().BeFalse("its process is running");
            clock.Now = Start + BackgroundJobLease.Expiry;
            (await b.HoldAsync()).Should().BeTrue("it lapsed");
        }

        // Another machine's process can't be looked at from here.
        using (var db = new TestDb(provider: provider))
        {
            var clock = new TestClock(Start);
            await HeldBy(db, $"NOT-{Here}/{DeadPid()}/aaaa", Start);
            var (b, pb) = Lease(db, clock, $"{Here}/{Environment.ProcessId}/bbbb");
            await using var _ = pb;
            (await b.HoldAsync()).Should().BeFalse("its machine isn't this one");
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("odd", false)]
    [InlineData("WEB01/notapid/x", false)]
    [InlineData("WEB01/-4/x", false)]
    public void OnlyAWellFormedHolderOnThisMachine_IsLookedAt(string? holder, bool gone) =>
        BackgroundJobLease.IsGoneFromThisMachine(holder).Should().Be(gone);

    [Fact]
    public void AProcessIdThatIsntRunning_IsGone_AndThisOneIsnt()
    {
        BackgroundJobLease.IsGoneFromThisMachine($"{Here}/{DeadPid()}/x").Should().BeTrue();
        BackgroundJobLease.IsGoneFromThisMachine($"{Here.ToLowerInvariant()}/{DeadPid()}/x").Should().BeTrue("machine names are compared ignoring case");
        BackgroundJobLease.IsGoneFromThisMachine($"{Here}/{Environment.ProcessId}/x").Should().BeFalse();
        BackgroundJobLease.IsGoneFromThisMachine($"NOT-{Here}/{DeadPid()}/x").Should().BeFalse();
    }
}

/// <summary>A logger torn down with its host: every line throws.</summary>
public sealed class GoneLogger<T>(Exception thrown) : Microsoft.Extensions.Logging.ILogger<T>
{
    public bool Throwing { get; set; } = true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (Throwing) throw thrown;
    }
}

/// <summary>A host torn down around the lease's release at shutdown (as WebApplicationFactory's tests do): the release
/// never throws, and the lease is left to lapse.</summary>
public class ReleaseAtShutdownTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Thrown => ["disposed", "aggregate"];

    private static Exception Make(string kind) => kind == "disposed"
        ? new ObjectDisposedException("EventLogInternal")
        : new AggregateException(new ObjectDisposedException("ConsoleLoggerProvider"));

    [Theory]
    [MemberData(nameof(Thrown))]
    public async Task ReleasingAfterTheContainerAndLoggersAreDisposed_DoesntThrow(string kind)
    {
        using var db = new TestDb();
        var logger = new GoneLogger<BackgroundJobLease>(Make(kind)) { Throwing = false };
        var services = new ServiceCollection().AddSingleton<IAppDbContextFactory>(db);
        services.AddSingleton(sp => new BackgroundJobLease(sp.GetRequiredService<IServiceScopeFactory>(), new TestClock(Start), logger, "WEB01/100/aaaa"));
        var provider = services.BuildServiceProvider();
        var lease = provider.GetRequiredService<BackgroundJobLease>();
        (await lease.HoldAsync()).Should().BeTrue();

        // The container goes first, with the lease in it, and the loggers with it.
        await provider.DisposeAsync();
        logger.Throwing = true;

        await FluentActions.Awaiting(() => lease.ReleaseAsync()).Should().NotThrowAsync();
        await FluentActions.Awaiting(() => lease.ReleaseAsync()).Should().NotThrowAsync("a second release is as safe");
    }

    [Fact]
    public async Task ACancelledRelease_LeavesTheGateFree()
    {
        using var db = new TestDb();
        var provider = new ServiceCollection().AddSingleton<IAppDbContextFactory>(db).BuildServiceProvider();
        await using var _ = provider;
        var lease = new BackgroundJobLease(provider.GetRequiredService<IServiceScopeFactory>(), new TestClock(Start), NullLogger<BackgroundJobLease>.Instance, "WEB01/100/aaaa");
        (await lease.HoldAsync()).Should().BeTrue();

        await FluentActions.Awaiting(() => lease.ReleaseAsync(new CancellationToken(canceled: true))).Should().ThrowAsync<OperationCanceledException>();
        (await lease.HoldAsync()).Should().BeTrue("a cancelled wait took nothing, so it released nothing");
        await lease.ReleaseAsync();
        await using var ctx = db.Context();
        (await ctx.JobLeases.SingleAsync()).ExpiresAtUtc.Should().Be(Start, "let go at shutdown");
    }
}
