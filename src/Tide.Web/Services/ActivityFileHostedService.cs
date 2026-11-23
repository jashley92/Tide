using System.Text.Json;
using Microsoft.Extensions.Options;
using Tide.Application.Files;

namespace Tide.Web.Services;

/// <summary>
/// Writes what's live in this process to <c>activity.json</c> in the data root every <see cref="Every"/>: when, how many
/// pages are open, and the meetings running in present mode. <c>Upgrade-Tide.ps1</c> reads it before it stops the site and
/// asks before ending them (an upgrade drops every open page's unsaved work and ends live meetings). A file, not an
/// endpoint, so the script needs no sign-in, and nothing about who is on is published: only counts and meeting days.
/// A data root it can't write is skipped quietly (Diagnostics' storage check already reports one).
/// </summary>
public sealed class ActivityFileHostedService(TransportCounts pages, LiveMeetings meetings, IOptionsMonitor<FileStoreOptions> files,
    TimeProvider time, ILogger<ActivityFileHostedService> log) : BackgroundService
{
    public const string FileName = "activity.json";

    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Write();
            try { await Task.Delay(Every, time, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Writes the file now (to a temporary name, then moved over the old one, so a reader never sees half).</summary>
    internal void Write()
    {
        try
        {
            var root = Path.GetFullPath(files.CurrentValue.RootPath);
            if (!Directory.Exists(root)) return;
            var live = meetings.Live();
            var body = JsonSerializer.Serialize(new
            {
                atUtc = time.GetUtcNow(),
                machine = Environment.MachineName,
                openPages = pages.Snapshot().Sum(t => t.OpenNow),
                liveMeetings = live.Count,
                meetingDays = live.Select(m => m.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).ToList(),
            }, Json);
            var path = Path.Combine(root, FileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, body);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "Couldn't write {File} to the data root.", FileName);
        }
    }
}
