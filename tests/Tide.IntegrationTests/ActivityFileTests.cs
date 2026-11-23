using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Application.Files;
using Tide.TestSupport;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>
/// The app writes <c>activity.json</c> into its data root for <c>Upgrade-Tide.ps1</c>: when, and how many pages and live
/// meetings there are; nobody's name. A data root that isn't there is skipped.
/// </summary>
public class ActivityFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tide-activity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ActivityFileHostedService Service(TimeProvider time) =>
        new(new TransportCounts(), new LiveMeetings(time), new StaticOptionsMonitor<FileStoreOptions>(new FileStoreOptions { RootPath = _root }),
            new Env(), time, NullLogger<ActivityFileHostedService>.Instance);

    private sealed class Env : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Tide";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public void It_writes_counts_and_a_time_and_skips_a_missing_root()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
        Service(clock).Write();
        Directory.Exists(_root).Should().BeFalse("it never makes the data root");

        Directory.CreateDirectory(_root);
        Service(clock).Write();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, ActivityFileHostedService.FileName)));
        var a = doc.RootElement;
        a.GetProperty("atUtc").GetDateTimeOffset().Should().Be(clock.Now);
        (a.GetProperty("openPages").GetInt64(), a.GetProperty("liveMeetings").GetInt32()).Should().Be((0L, 0));
        a.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["atUtc", "openPages", "liveMeetings", "meetingDays"], "counts only");
        Directory.EnumerateFiles(_root).Should().ContainSingle("the temporary file is moved over it");
    }
}
