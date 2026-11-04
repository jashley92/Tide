using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tide.Application.Intake;

namespace Tide.Application.Operations;

public enum UpdateState
{
    /// <summary>The check is turned off.</summary>
    Disabled,
    /// <summary>A local build with no release version: nothing to compare (the latest release is still listed).</summary>
    DevBuild,
    /// <summary>On, but not checked yet (the first check runs a few minutes after start).</summary>
    NotChecked,
    UpToDate,
    Available,
    /// <summary>The last check failed; <see cref="UpdateStatus.Problem"/> says why.</summary>
    Failed,
}

/// <param name="Releases">Every published release newer than the running one, newest first (for a development build,
/// the latest few).</param>
public sealed record UpdateStatus(UpdateState State, ReleaseVersion? Running, DateTimeOffset? CheckedAtUtc, IReadOnlyList<AvailableRelease> Releases, string? Problem)
{
    public AvailableRelease? Latest => Releases.Count > 0 ? Releases[0] : null;
}

/// <summary>
/// Asks GitHub which TIDE releases are newer than this one, and keeps the answer in
/// memory for Administration › System › Updates and the "new" mark on its tab. One anonymous GET to the public Releases
/// API through the app's outbound client (its proxy and size limit), with a 10-second limit of its own: nothing about
/// this installation is sent. It never blocks start-up or a page, and never throws for a failure: an offline server
/// sees "couldn't check" and why. A server running a pre-release follows pre-releases; one running a release doesn't.
/// Singleton; <see cref="Enabled"/> is kept in step with the saved setting by the hosted service and the Updates page.
/// </summary>
public sealed class UpdateCheckService(IOutboundHttp http, IOptionsMonitor<UpdateOptions> options, ILogger<UpdateCheckService> log,
    TimeProvider? time = null, ReleaseVersion? running = null, bool useBuildVersion = true) : IDisposable
{
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(10);

    // A "Check now" more often than this returns the last answer (the anonymous API allows 60 calls an hour).
    public static readonly TimeSpan ManualThrottle = TimeSpan.FromMinutes(1);

    private const int DevBuildReleasesShown = 3;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ReleaseVersion? _running = running ?? (useBuildVersion ? AppVersionInfo.Current : null);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateStatus? _last;
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;
    private string? _loggedProblem;

    /// <summary>Raised after each check, so an open page (and the tab's mark) can redraw.</summary>
    public event Action? Changed;

    /// <summary>Whether the check is on (the saved setting, else <c>Updates:CheckEnabled</c>).</summary>
    public bool Enabled { get; set; } = options.CurrentValue.CheckEnabled;

    public ReleaseVersion? Running => _running;

    public UpdateStatus Status
    {
        get
        {
            if (!Enabled) return new(UpdateState.Disabled, _running, null, [], null);
            return _last ?? new(UpdateState.NotChecked, _running, null, [], null);
        }
    }

    /// <summary>True when the check is on and found a newer release.</summary>
    public bool UpdateAvailable => Status.State == UpdateState.Available;

    /// <summary>Checks now; a <paramref name="manual"/> check within a minute of the last returns that answer. Never
    /// throws, except for the caller's own cancellation.</summary>
    public async Task<UpdateStatus> CheckAsync(bool manual = false, CancellationToken ct = default)
    {
        if (!Enabled) return Status;
        await _gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            if (manual && _last is not null && now - _lastAttemptUtc < ManualThrottle) return Status;
            _lastAttemptUtc = now;
            _last = await FetchAsync(now, ct);
        }
        finally { _gate.Release(); }
        LogOnce(_last);
        Changed?.Invoke();
        return Status;
    }

    private async Task<UpdateStatus> FetchAsync(DateTimeOffset now, CancellationToken ct)
    {
        UpdateStatus Failed(string why) => new(UpdateState.Failed, _running, now, _last?.Releases ?? [], why);

        var repository = options.CurrentValue.Repository;
        if (!ReleaseFeed.IsRepository(repository)) return Failed("Updates:Repository isn't an owner/name repository");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeLimit);
        HttpFetch fetch;
        try
        {
            fetch = await http.GetAsync(ReleaseFeed.ApiUrl(repository), new OutboundRequest(Headers: new Dictionary<string, string>
            {
                ["Accept"] = "application/vnd.github+json",
                ["X-GitHub-Api-Version"] = "2022-11-28",
            }), limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed($"no answer within {TimeLimit.TotalSeconds:0} seconds");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return Failed("couldn't reach GitHub (" + ex.GetType().Name + ")");
        }
        if (fetch.Status != FetchStatus.Ok || fetch.Content is null)
            return Failed(limit.IsCancellationRequested && !ct.IsCancellationRequested
                ? $"no answer within {TimeLimit.TotalSeconds:0} seconds"
                : Explain(fetch.Error, repository));

        try
        {
            var json = Encoding.UTF8.GetString(fetch.Content);
            if (_running is null)
            {
                var latest = ReleaseFeed.Newer(json, repository, null, includePrereleases: true);
                return new(UpdateState.DevBuild, null, now, [.. latest.Take(DevBuildReleasesShown)], null);
            }
            var newer = ReleaseFeed.Newer(json, repository, _running, includePrereleases: _running.IsPrerelease);
            return new(newer.Count > 0 ? UpdateState.Available : UpdateState.UpToDate, _running, now, newer, null);
        }
        catch (JsonException) { return Failed("the answer wasn't a list of releases"); }
    }

    // The outbound client's reason, as a clause; GitHub's 404 for a repository an anonymous request can't see says why.
    internal static string Explain(string? error, string repository)
    {
        var why = (error ?? "GitHub didn't answer").Trim().TrimEnd('.');
        if (why.Contains("404", StringComparison.Ordinal))
            return $"GitHub has no public releases for {repository} (404): the check is anonymous, so it sees a public repository only";
        if (why.Contains("403", StringComparison.Ordinal) || why.Contains("429", StringComparison.Ordinal))
            return why + ": GitHub may be rate-limiting this address (60 anonymous requests an hour)";
        return why;
    }

    public void Dispose() => _gate.Dispose();

    // A failing check is logged once, not every day, until it fails for another reason or works again.
    private void LogOnce(UpdateStatus s)
    {
        if (s.State == UpdateState.Failed)
        {
            if (s.Problem != _loggedProblem) log.LogWarning("Update check failed: {Problem}.", s.Problem);
            _loggedProblem = s.Problem;
            return;
        }
        if (_loggedProblem is not null) log.LogInformation("Update check is working again.");
        _loggedProblem = null;
    }
}
