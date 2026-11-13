using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Intake;
using Tide.Application.Operations;
using Tide.Infrastructure.Notifications;
using Tide.Infrastructure.Persistence;

namespace Tide.Web.Services;

public enum CheckLevel { Ok, Info, Attention, Problem }

/// <summary>One health check: how it stands, why, and where it's fixed (relative to the app).</summary>
public sealed record HealthItem(string Label, CheckLevel Level, string Detail, string? FixHref = null);

public sealed record BuildInfo(string Version, string Runtime, string Os, string Environment, string Machine, string Account, string? AppPool,
    DateTimeOffset StartedUtc, string ContentRoot);

public sealed record DatabaseInfo(string Provider, bool Reachable, string? Problem, int Applied, string? LatestApplied, IReadOnlyList<string> Pending,
    bool AppliesItsOwn);

public sealed record StorageInfo(string Label, string? Path, bool Exists, bool? Writable, long? FreeBytes, string? Note);

public sealed record EmailInfo(bool Enabled, string Delivery, string SignIn, string? Problem, TimeSpan BackedOffFor);

public sealed record SourceHealth(int Enabled, int Failing, IReadOnlyList<string> FailingNames);

/// <summary>How pages talk to the server: the asking admin's own circuit (null outside one) and every connection since the app started.</summary>
public sealed record ConnectionInfo(HttpTransportType? Current, IReadOnlyList<TransportCount> Counts)
{
    public string CurrentName => ConnectionTransport.Name(Current);
    public long SinceStart => Counts.Sum(c => c.SinceStart);
    public long FellBack => Counts.Where(c => c.Transport != HttpTransportType.WebSockets).Sum(c => c.SinceStart);

    /// <summary>The admin's own connection: Ok on WebSockets, Attention on a fallback, Info when it isn't known.</summary>
    public CheckLevel Level => Current switch
    {
        null => CheckLevel.Info,
        HttpTransportType.WebSockets => CheckLevel.Ok,
        _ => CheckLevel.Attention,
    };

    /// <summary>Whether the fix is worth showing: this page, or anyone's since the app started, fell back.</summary>
    public bool NeedsFix => Level == CheckLevel.Attention || FellBack > 0;
}

/// <summary>The report. <see cref="Lease"/> is whether this process runs the background jobs; <see cref="JobHealth"/>
/// is what <c>/health/jobs</c> answers from; <see cref="JobHistory"/> is the job history's last 30 days.</summary>
public sealed record DiagnosticsReport(
    DateTimeOffset CheckedAtUtc, BuildInfo Build, DatabaseInfo Database, IReadOnlyList<JobState> Jobs, EmailInfo Email,
    IReadOnlyList<StorageInfo> Storage, SourceHealth Sources, CveRun? LastCveRun, IReadOnlyList<HealthItem> Checks, ConnectionInfo Connection,
    LeaseStatus Lease, JobHealthReport JobHealth, JobRunHistory JobHistory);

/// <summary>
/// Whether TIDE is healthy, for Administration › System › Diagnostics: the build and how long it's been
/// up, the database and its schema, which process runs the background jobs, each job's last run (and whether it's
/// failing, overdue or hung, as <c>/health/jobs</c> says) and its history, email delivery, the folders it writes, and the
/// feeds that are failing; summed up as checks, each with where it's fixed. It reads and probes; it changes nothing
/// (the folder probe writes and deletes one empty file). Nothing it shows carries a secret: the connection is
/// described by provider only, and errors are scrubbed. Administrators only: the page says so, and so does
/// <see cref="RunAsync"/>.
/// </summary>
public sealed class SystemDiagnostics(
    IConfiguration config, IWebHostEnvironment env, IDbContextFactory<AppDbContext> contexts, IAppDbContextFactory dbs, JobMonitor jobs,
    IJobLease lease, IOptionsMonitor<EmailOptions> email, OutboundCircuit circuit, CveRunLog cveLog, IRoleDirectory roles, UpdateCheckService updates,
    TimeProvider time, ICurrentUser user, PresenceCircuit presenceCircuit, TransportCounts transportCounts)
{
    public static readonly TimeSpan DatabaseTimeLimit = TimeSpan.FromSeconds(5);

    public async Task<DiagnosticsReport> RunAsync(CancellationToken ct = default)
    {
        if (!user.Has(Tide.Domain.Enums.Permission.Administer))
            throw new Tide.Application.Security.ForbiddenException("Only an administrator can see the diagnostics.");
        var build = Build();
        var database = await DatabaseAsync(ct);
        var mail = Email();
        var storage = Storage();
        var sources = database.Reachable ? await SourcesAsync(ct) : new SourceHealth(0, 0, []);
        var states = jobs.States;
        var leaseStatus = lease.Status;
        var jobHealth = Tide.Application.Operations.JobHealth.Evaluate(states, leaseStatus, time.GetUtcNow());
        var history = database.Reachable ? await HistoryAsync(ct) : JobRunHistory.None;
        var connection = new ConnectionInfo(presenceCircuit.Transport, transportCounts.Snapshot());
        var checks = Checks(database, mail, storage, sources);
        checks.InsertRange(1, ConnectionChecks(connection));
        checks.InsertRange(checks.FindIndex(c => c.Label == "Sources") + 1, JobChecks(states, leaseStatus, jobHealth));
        return new DiagnosticsReport(time.GetUtcNow(), build, database, states, mail, storage, sources, cveLog.Last, checks, connection,
            leaseStatus, jobHealth, history);
    }

    private async Task<JobRunHistory> HistoryAsync(CancellationToken ct)
    {
        try { return await JobRunLog.HistoryAsync(dbs, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return JobRunHistory.None; }
    }

    /// <summary>
    /// Which process runs the background jobs (the job lease), and how the jobs stand: hung or overdue is a Problem,
    /// failing again and again is Attention, as <c>/health/jobs</c> says Unhealthy or Degraded; a last run that failed
    /// is Attention too. A process standing by runs none, so none is judged.
    /// </summary>
    internal static IEnumerable<HealthItem> JobChecks(IReadOnlyList<JobState> states, LeaseStatus lease, JobHealthReport health)
    {
        var holder = lease.HolderName ?? "another process";
        yield return lease.State switch
        {
            LeaseState.Held when lease.Problem is { } p => new("Job lease", CheckLevel.Attention,
                $"This process ({holder}) runs the background jobs, but couldn't renew the lease ({p}). It stops before the lease lapses unless the database answers."),
            LeaseState.Held => new("Job lease", CheckLevel.Ok, $"This process ({holder}) runs the background jobs."),
            LeaseState.StandingBy => new("Job lease", CheckLevel.Info,
                $"Standing by: {holder} runs the background jobs, so this process runs none. It takes over within a few minutes if that one stops."),
            LeaseState.Unreachable => new("Job lease", CheckLevel.Problem,
                $"Couldn't take the lease ({lease.Problem}): no background job runs here until the database answers."),
            _ => new("Job lease", CheckLevel.Info, "Not asked yet: it's taken within a minute of start-up."),
        };
        if (lease.Alarm is { } alarm) yield return new("Second process", CheckLevel.Problem, alarm);
        if (health.StandingBy)
        {
            yield return new("Background jobs", CheckLevel.Info, $"Not run here: {holder} runs them.");
            yield break;
        }
        if (health.Level != JobHealthLevel.Healthy)
        {
            var worst = health.Findings.Where(f => f.Level == health.Level).Select(f => f.Text);
            yield return new("Background jobs", health.Level == JobHealthLevel.Unhealthy ? CheckLevel.Problem : CheckLevel.Attention,
                string.Join(" ", worst) + " See the jobs below.");
            yield break;
        }
        var failed = states.Where(s => s.Enabled && s.LastSucceeded == false).Select(s => s.Job.Name).ToList();
        yield return failed.Count == 0
            ? new("Background jobs", CheckLevel.Ok, "None failing, overdue or hung.")
            : new("Background jobs", CheckLevel.Attention, $"Last run failed: {string.Join(", ", failed)}. See the jobs below.");
    }

    private BuildInfo Build()
    {
        using var process = Process.GetCurrentProcess();
        return new BuildInfo(AppVersionInfo.Display, RuntimeInformation.FrameworkDescription,
            $"{RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})", env.EnvironmentName, System.Environment.MachineName,
            $"{System.Environment.UserDomainName}\\{System.Environment.UserName}", System.Environment.GetEnvironmentVariable("APP_POOL_ID"),
            process.StartTime.ToUniversalTime(), env.ContentRootPath);
    }

    private async Task<DatabaseInfo> DatabaseAsync(CancellationToken ct)
    {
        var provider = config["Database:Provider"] ?? "Sqlite";
        var appliesItsOwn = config.GetValue("Database:ApplyMigrations", true);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(DatabaseTimeLimit);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(limit.Token);
            var applied = (await db.Database.GetAppliedMigrationsAsync(limit.Token)).ToList();
            var pending = (await db.Database.GetPendingMigrationsAsync(limit.Token)).ToList();
            return new DatabaseInfo(provider, true, null, applied.Count, applied.LastOrDefault(), pending, appliesItsOwn);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DatabaseInfo(provider, false, $"no answer within {DatabaseTimeLimit.TotalSeconds:0} seconds", 0, null, [], appliesItsOwn);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DatabaseInfo(provider, false, SecretMask.Scrub(ex.GetType().Name + ": " + ex.Message), 0, null, [], appliesItsOwn);
        }
    }

    private EmailInfo Email()
    {
        var o = email.CurrentValue;
        var delivery = !string.IsNullOrWhiteSpace(o.PickupDirectory) ? $"Written to the pickup folder {o.PickupDirectory}"
            : string.IsNullOrWhiteSpace(o.SmtpHost) ? "No relay set (Email:SmtpHost)"
            : $"{o.SmtpHost}:{o.SmtpPort}{(o.EnableSsl ? " with STARTTLS" : " without TLS")}";
        // How it signs in, never with what.
        var signIn = o.SmtpUseDefaultCredentials ? "As the app pool's account"
            : !string.IsNullOrWhiteSpace(o.SmtpUsername) ? $"As {o.SmtpUsername}, with a password ({(string.IsNullOrWhiteSpace(o.SmtpPassword) ? "not set" : "set")})"
            : "Without signing in";
        var problem = o.SignInProblem() ?? (o.Enabled && string.IsNullOrWhiteSpace(o.SmtpHost) && string.IsNullOrWhiteSpace(o.PickupDirectory)
            ? "Email:Enabled is on but no relay is set (Email:SmtpHost)." : null);
        return new EmailInfo(o.Enabled, delivery, signIn, problem, circuit.RetryIn(EmailSender.Target(o)));
    }

    private List<StorageInfo> Storage()
    {
        var files = config["Files:RootPath"];
        var filesPath = string.IsNullOrWhiteSpace(files) ? null : Path.GetFullPath(files, env.ContentRootPath);
        var keys = config["DataProtection:KeyPath"];
        var list = new List<StorageInfo> { Folder("Screenshots", filesPath, probe: true, null) };
        list.Add(string.IsNullOrWhiteSpace(keys)
            ? new StorageInfo("Data Protection keys", null, false, null, null,
                env.IsDevelopment() ? "Not set: development keeps the default key ring." : "Not set: sign-ins don't survive an app-pool recycle.")
            : Folder("Data Protection keys", keys, probe: false, KeyCount(keys)));
        return list;
    }

    private static StorageInfo Folder(string label, string? path, bool probe, string? note)
    {
        if (path is null) return new StorageInfo(label, null, false, null, null, "Not set");
        var exists = Directory.Exists(path);
        bool? writable = null;
        if (exists && probe)
        {
            var test = Path.Combine(path, ".tide-write-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllBytes(test, []);
                File.Delete(test);
                writable = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { writable = false; }
        }
        long? free = null;
        try { if (exists) free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* not shown */ }
        return new StorageInfo(label, path, exists, writable, free, note);
    }

    private static string? KeyCount(string path)
    {
        try { return Directory.Exists(path) ? $"{Directory.GetFiles(path, "key-*.xml").Length} key(s)" : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task<SourceHealth> SourcesAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbs.CreateAsync(ct);
            var live = await db.Sources.AsNoTracking().Where(s => s.Enabled && !s.IsRetired)
                .Select(s => new { s.Name, s.ConsecutiveFailures }).ToListAsync(ct);
            var failing = live.Where(s => s.ConsecutiveFailures > 0).OrderByDescending(s => s.ConsecutiveFailures).Select(s => s.Name).ToList();
            return new SourceHealth(live.Count, failing.Count, [.. failing.Take(5)]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new SourceHealth(0, 0, []); }
    }

    /// <summary>
    /// The admin's own connection (Attention unless it's WebSockets) and everyone's since the app started (Attention when
    /// any fell back), each with the fix.
    /// </summary>
    internal static IEnumerable<HealthItem> ConnectionChecks(ConnectionInfo connection)
    {
        yield return connection.Level switch
        {
            CheckLevel.Ok => new("Your connection", CheckLevel.Ok, "WebSockets."),
            CheckLevel.Info => new("Your connection", CheckLevel.Info, "Not known yet: this page isn't on a live connection. Check again once it is."),
            _ => new("Your connection", CheckLevel.Attention,
                $"{connection.CurrentName}, not WebSockets: every click and keystroke is a request of its own, so pages lag. {ConnectionTransport.Fix}"),
        };
        if (connection.SinceStart == 0) yield break;
        if (connection.FellBack == 0)
        {
            yield return new("Everyone's connections", CheckLevel.Ok, $"All {connection.SinceStart} since the app started used WebSockets.");
            yield break;
        }
        var which = string.Join(", ", connection.Counts.Where(c => c.Transport != HttpTransportType.WebSockets && c.SinceStart > 0)
            .Select(c => $"{c.SinceStart} {c.Name.ToLowerInvariant()}"));
        yield return new("Everyone's connections", CheckLevel.Attention,
            $"{connection.FellBack} of {connection.SinceStart} since the app started didn't use WebSockets ({which}). {ConnectionTransport.Fix}");
    }

    private List<HealthItem> Checks(DatabaseInfo db, EmailInfo mail, IReadOnlyList<StorageInfo> storage, SourceHealth sources)
    {
        var checks = new List<HealthItem>
        {
            db.Reachable
                ? new("Database", CheckLevel.Ok, $"{db.Provider} answers.")
                : new("Database", CheckLevel.Problem, $"{db.Provider} didn't answer: {db.Problem}", "admin/system/server"),
        };
        if (db.Reachable)
            checks.Add(db.Pending.Count == 0
                ? new("Schema", CheckLevel.Ok, $"Up to date ({db.Applied} migrations).")
                : new("Schema", CheckLevel.Problem, $"{db.Pending.Count} migration(s) not applied: {string.Join(", ", db.Pending)}. "
                    + (db.AppliesItsOwn ? "Restart the app to apply them." : "Run this release's schema script (deploy/sql).")));

        var screenshots = storage[0];
        checks.Add(screenshots.Writable == true
            ? new("Screenshot folder", CheckLevel.Ok, "Writable.")
            : new("Screenshot folder", CheckLevel.Problem, screenshots.Exists ? "The app pool can't write to it." : "It doesn't exist yet: it's made at the first upload.",
                "admin/system/server"));
        var keys = storage[1];
        checks.Add(keys.Path is not null
            ? new("Data Protection keys", keys.Exists ? CheckLevel.Ok : CheckLevel.Attention, keys.Exists ? "Kept on disk." : "The folder doesn't exist.")
            : new("Data Protection keys", env.IsDevelopment() ? CheckLevel.Info : CheckLevel.Attention, keys.Note ?? "", "admin/system/server"));

        checks.Add(!mail.Enabled ? new("Email", CheckLevel.Info, "Off (Email:Enabled): nothing is emailed.", "admin/system/server")
            : mail.Problem is { } p ? new("Email", CheckLevel.Problem, p, "admin/system/server")
            : mail.BackedOffFor > TimeSpan.Zero ? new("Email", CheckLevel.Attention, $"The relay failed several times; sends wait {Math.Ceiling(mail.BackedOffFor.TotalMinutes):0} more minute(s).")
            : new("Email", CheckLevel.Ok, mail.Delivery + "."));

        checks.Add(sources.Failing == 0
            ? new("Sources", CheckLevel.Ok, sources.Enabled == 0 ? "None turned on." : $"All {sources.Enabled} answering.")
            : new("Sources", CheckLevel.Attention, $"{sources.Failing} of {sources.Enabled} failing: {string.Join(", ", sources.FailingNames)}.", "admin/sources"));


        checks.Add(roles.IsCurrent
            ? new("Role mappings", CheckLevel.Ok, "Loaded.")
            : new("Role mappings", CheckLevel.Attention, "The last reload failed; sign-ins use the mappings from before the latest change.", "admin/access"));

        if (updates.UpdateAvailable && updates.Status.Latest is { } latest)
            checks.Add(new("Updates", CheckLevel.Info, $"TIDE {latest.Version} is available.", "admin/system/updates"));
        return checks;
    }
}
