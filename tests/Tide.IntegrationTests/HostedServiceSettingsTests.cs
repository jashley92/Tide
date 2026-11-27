using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tide.Application.Files;
using Tide.Application.History;
using Tide.Application.Operations;
using Tide.TestSupport;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>
/// The daily file purge, the audit archive and the activity file each read a setting on every pass. A server file edited
/// while TIDE runs, with a value that won't bind, makes that read throw: each service takes it as off (or skips the write)
/// and asks again, rather than letting the exception out of its loop, which would stop the app.
/// </summary>
public class HostedServiceSettingsTests
{
    private sealed class Unreadable<T> : IOptionsMonitor<T>
    {
        public T CurrentValue => throw new InvalidOperationException("Failed to convert configuration value 'ninety' to type 'System.Int32'.");
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static IServiceScopeFactory NoServices() => new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    [Fact]
    public async Task TheFilePurge_TakesAnUnreadableSettingAsOff_AndKeepsGoing()
    {
        var jobs = new JobMonitor(TimeProvider.System);
        var log = new RecordingLogger<FilePurgeHostedService>();
        using var hosted = new FilePurgeHostedService(NoServices(), new MemoryFileStore(), new Unreadable<FileStoreOptions>(), jobs, new HeldLease(),
            TimeProvider.System, log);

        await hosted.StartAsync(CancellationToken.None);

        (await Poll.Until(() => log.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("Files setting can't be read")))).Should().BeTrue();
        (await Poll.Until(() => !jobs.Get(JobMonitor.FilePurge).Enabled && hosted.ExecuteTask is { IsCompleted: false })).Should().BeTrue("it's off and waiting to ask again, not stopped");
        hosted.ExecuteTask!.IsFaulted.Should().BeFalse("a faulted background service stops the host");
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TheAuditArchive_TakesAnUnreadableSettingAsOff_AndKeepsGoing()
    {
        var jobs = new JobMonitor(TimeProvider.System);
        var log = new RecordingLogger<AuditArchiveHostedService>();
        using var hosted = new AuditArchiveHostedService(NoServices(), new Unreadable<AuditOptions>(), new Unreadable<FileStoreOptions>(),
            new TestHostEnvironment(), jobs, new HeldLease(), TimeProvider.System, log);

        await hosted.StartAsync(CancellationToken.None);

        (await Poll.Until(() => log.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("Audit setting can't be read")))).Should().BeTrue();
        (await Poll.Until(() => !jobs.Get(JobMonitor.AuditArchive).Enabled && hosted.ExecuteTask is { IsCompleted: false })).Should().BeTrue("it's off and waiting to ask again, not stopped");
        hosted.ExecuteTask!.IsFaulted.Should().BeFalse("a faulted background service stops the host");
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void TheActivityFile_SkipsAWrite_WhenTheDataRootSettingCantBeRead()
    {
        var hosted = new ActivityFileHostedService(new TransportCounts(), new LiveMeetings(TimeProvider.System), new Unreadable<FileStoreOptions>(),
            new TestHostEnvironment(), TimeProvider.System, NullLogger<ActivityFileHostedService>.Instance);

        hosted.Invoking(h => h.Write()).Should().NotThrow("the write runs in the service's loop, with nothing else to catch it");
    }
}
