using Microsoft.AspNetCore.Components.Server.Circuits;
using Tide.Application.Abstractions;

namespace Tide.Web.Security;

/// <summary>
/// The signed-in user of the work in progress, and the host's access and error logs, for code with no scope to ask (both
/// are written from a static helper the pages call). They follow the async flow: set around each HTTP request
/// (<see cref="UserMirrorMiddleware"/>) and each inbound circuit activity (an event, a JS interop call:
/// <see cref="AmbientUserCircuitHandler"/>), so a service refused during either is logged with who asked, to the logger
/// of the host that's serving them. Nothing host-owned is kept in a static: several hosts run in one test process, and a
/// logger held past its host's disposal throws.
/// </summary>
public static class AmbientUser
{
    /// <summary>The access log's category.</summary>
    public const string AccessLogCategory = "Tide.Access";

    /// <summary>The category of the errors a person is shown only as "Something went wrong".</summary>
    public const string ErrorLogCategory = "Tide.Errors";

    private static readonly AsyncLocal<(ICurrentUser? User, ILogger? AccessLog, ILogger? ErrorLog)> Ambient = new();

    /// <summary>The current user of this async flow, or null outside a request or circuit activity.</summary>
    public static ICurrentUser? Current => Ambient.Value.User;

    /// <summary>The serving host's access log, or null outside a request or circuit activity.</summary>
    public static ILogger? AccessLog => Ambient.Value.AccessLog;

    /// <summary>The serving host's error log, or null outside a request or circuit activity.</summary>
    public static ILogger? ErrorLog => Ambient.Value.ErrorLog;

    /// <summary>Makes <paramref name="user"/> and the logs the ambient ones until the returned scope is disposed.</summary>
    public static IDisposable Begin(ICurrentUser? user, ILogger? accessLog = null, ILogger? errorLog = null)
    {
        var previous = Ambient.Value;
        Ambient.Value = (user, accessLog, errorLog);
        return new Scope(previous);
    }

    /// <summary>Makes <paramref name="user"/> and <paramref name="loggers"/>' access and error logs ambient.</summary>
    public static IDisposable Begin(ICurrentUser? user, ILoggerFactory loggers) =>
        Begin(user, loggers.CreateLogger(AccessLogCategory), loggers.CreateLogger(ErrorLogCategory));

    private sealed class Scope((ICurrentUser? User, ILogger? AccessLog, ILogger? ErrorLog) previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

/// <summary>Makes the circuit's user and its host's logs ambient for each inbound activity (a button click, a form
/// submit, a JS callback).</summary>
public sealed class AmbientUserCircuitHandler(ICurrentUser user, ILoggerFactory loggers) : CircuitHandler
{
    private readonly ILogger _accessLog = loggers.CreateLogger(AmbientUser.AccessLogCategory);
    private readonly ILogger _errorLog = loggers.CreateLogger(AmbientUser.ErrorLogCategory);

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            using (AmbientUser.Begin(user, _accessLog, _errorLog)) await next(context);
        };
}
