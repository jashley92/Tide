using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Tide.Application.Abstractions;

namespace Tide.Web.Services;

/// <summary>A message shown briefly in the corner. Scoped: one queue per circuit.</summary>
/// <param name="time">The clock a confirmation's few seconds run on (the system's unless a test holds them).</param>
public sealed class ToastService(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>A message; <see cref="Undo"/>, when there is one, takes back what it reports (a button on the toast).</summary>
    public sealed record Toast(Guid Id, string Message, bool IsError, Func<Task>? Undo = null);

    // Confirmations dismiss themselves from a timer thread while the circuit may be rendering the list, so it's only
    // changed under the lock and readers get a copy.
    private readonly List<Toast> _toasts = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<Toast> Toasts
    {
        get { lock (_lock) return _toasts.ToArray(); }
    }

    public event Action? Changed;

    public void Show(string message) => Add(message, false);

    /// <summary>A confirmation with an Undo button that runs <paramref name="undo"/>. It stays a little longer, to give
    /// time to reach the button.</summary>
    public void Show(string message, Func<Task> undo) => Add(message, false, undo);

    /// <summary>Runs a toast's Undo, once: the toast goes as it starts.</summary>
    public Task UndoAsync(Guid id)
    {
        Toast? toast;
        lock (_lock) toast = _toasts.FirstOrDefault(t => t.Id == id);
        if (toast?.Undo is not { } undo) return Task.CompletedTask;
        Dismiss(id);
        return undo();
    }

    public void Error(string message) => Add(message, true);

    public void Dismiss(Guid id)
    {
        lock (_lock) _toasts.RemoveAll(t => t.Id == id);
        Changed?.Invoke();
    }

    private void Add(string message, bool isError, Func<Task>? undo = null)
    {
        var toast = new Toast(Guid.NewGuid(), message, isError, undo);
        lock (_lock)
        {
            // A confirmation means the last attempt worked, so errors left from earlier attempts no longer apply.
            if (!isError) _toasts.RemoveAll(t => t.IsError);
            _toasts.Add(toast);
            if (_toasts.Count > 4) _toasts.RemoveAt(0);
        }
        Changed?.Invoke();
        // Errors stay until dismissed or a later confirmation; confirmations go by themselves.
        if (!isError) _ = Task.Delay(TimeSpan.FromSeconds(undo is null ? 4 : 10), _time).ContinueWith(_ => Dismiss(toast.Id), TaskScheduler.Default);
    }
}

/// <summary>Readiness: the database answers a query.</summary>
public sealed class DatabaseHealthCheck(IAppDbContextFactory dbs, TimeProvider time) : IHealthCheck
{
    /// <summary>How long the database has to answer a probe.</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        // Asynchronous, with the probe's token and a time limit of its own: a database that hangs (or that the SQL Server
        // retry policy keeps retrying) mustn't hold a probe, and a thread, for minutes while monitors keep sending more.
        // The limit runs on the app's clock, so a test can move it on rather than wait on a busy machine's timers.
        using var deadline = new CancellationTokenSource(TimeLimit, time);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            await using var db = await dbs.CreateAsync(limit.Token);
            _ = await db.Settings.AnyAsync(limit.Token);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"The database didn't answer within {TimeLimit.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("The database didn't answer.", ex);
        }
    }
}

/// <summary>What a failed action tells the person: the message for errors we raise on purpose, a generic line otherwise.
/// A refusal (<see cref="Application.Security.ForbiddenException"/>) is also logged as a warning, with who and which
/// operation, so access-control failures leave a trace beyond a toast. Any other failure the person is told only
/// "Something went wrong" about is logged as an error with its exception, under a reference the message gives them, so
/// an administrator can find it. The logs and the user come from the ambient request or circuit activity
/// (<see cref="Security.AmbientUser"/>), never from a static: a logger held past its host throws. Logging never changes
/// what the person sees: a logger that fails is dropped, not raised in their handler.</summary>
public static class UserFacingError
{
    /// <param name="user">Who was refused, when the caller has them; otherwise the ambient user, and "unknown" outside a
    /// request or circuit activity.</param>
    /// <param name="operation">What was refused, when the caller knows; otherwise the service method that refused it, from the exception.</param>
    public static string For(Exception ex, ICurrentUser? user = null, string? operation = null)
    {
        var forbidden = ex as Application.Security.ForbiddenException;
        var message = Message(ex);
        if (message is not null && forbidden is null) return message;   // nothing to log

        // Who and what, for the line logged: a refusal's warning, or an unexpected failure's error.
        var who = (user ?? Security.AmbientUser.Current)?.UserId ?? "unknown";
        var what = operation ?? Refused(ex) ?? "an operation";
        if (forbidden is not null)
        {
            TryLog(Security.AmbientUser.AccessLog, log => log.LogWarning("Access denied for {User} in {Operation}: {Reason}", who, what, forbidden.Message));
            return forbidden.Message;
        }

        // Unexpected: the person learns nothing from the exception, so it's logged where an administrator can find it.
        // The reference is short enough to read out over the phone.
        var reference = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
        TryLog(Security.AmbientUser.ErrorLog, log => log.LogError(ex, "Unexpected error {Reference} for {User} in {Operation}.", reference, who, what));
        return $"Something went wrong. Try again; if it keeps happening, tell an administrator (reference {reference}).";
    }

    // A logging failure (a provider gone with its host, a full disk) isn't the person's problem: the line is dropped.
    private static void TryLog(ILogger? log, Action<ILogger> write)
    {
        if (log is null) return;
        try { write(log); }
        catch (Exception) { /* dropped, as above */ }
    }

    /// <summary>The application service and method that threw: the first frame of the app's own code, named as a person
    /// reads it ("ActionService.TakeAsync"), or null when the trace holds none.</summary>
    public static string? Refused(Exception ex)
    {
        foreach (var frame in new System.Diagnostics.StackTrace(ex, fNeedFileInfo: false).GetFrames())
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            if (type?.Namespace?.StartsWith("Tide.Application", StringComparison.Ordinal) != true) continue;
            // The services' guards (RequireRead, RequireAdmin) throw on behalf of the method that called them.
            if (method!.Name.StartsWith("Require", StringComparison.Ordinal)) continue;
            // The compiler nests an async method's state machine ("<TakeAsync>d__12") and a lambda's closure ("<>c__DisplayClass3_0")
            // in the service, and names a local function "<SaveAsync>g__Rule|12_0": the service is the outermost type, and
            // the operation is the method named inside the brackets when there is one.
            var service = type;
            while (service.IsNested && service.Name.StartsWith('<') && service.DeclaringType is { } outer) service = outer;
            var operation = Unmangled(type.Name) ?? Unmangled(method.Name) ?? method.Name;
            return service.Name + "." + operation;
        }
        return null;
    }

    // "<SaveAsync>d__12" and "<SaveAsync>g__Rule|12_0" name SaveAsync; "<>c__DisplayClass3_0" and a plain name give null.
    private static string? Unmangled(string name)
    {
        if (!name.StartsWith('<')) return null;
        var close = name.IndexOf('>');
        return close > 1 ? name[1..close] : null;
    }

    // The message for a failure the person can act on; null for one that's unexpected.
    private static string? Message(Exception ex) => ex switch
    {
        ArgumentException or InvalidOperationException or KeyNotFoundException or Application.Security.ForbiddenException
            or Application.Articles.StaleEditException => ex.Message,
        // A save that lost a race with someone else's (the edit version moved under it): the data is safe, so say so.
        Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException =>
            "Someone else changed this at the same moment, so your change wasn't saved. Reload to see theirs, then try again.",
        _ => null,
    };
}
