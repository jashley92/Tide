namespace Tide.Web.Security;

/// <summary>
/// The logger the directory lookups write to. They're static and live as long as the process (<see cref="SidNames"/>,
/// <see cref="WindowsNames"/>, the server-domain lookup), so they can't hold a host's logger: several hosts run in one
/// test process, and a logger kept past its host's stop throws. This delegates each line to the most recent host's
/// logger (<see cref="Use"/>, called on each request) and drops a line the logger can't take, so a lookup on a sign-in's
/// path never fails because of logging.
/// </summary>
public sealed class DirectoryLog : ILogger
{
    public const string Category = "Tide.Directory";

    public static readonly DirectoryLog Instance = new();

    private static volatile ILogger? _current;

    private DirectoryLog() { }

    /// <summary>Makes <paramref name="log"/> the logger written to from now on (the serving host's).</summary>
    public static void Use(ILogger? log)
    {
        if (log is not null) _current = log;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel)
    {
        try { return _current?.IsEnabled(logLevel) == true; }
        catch (Exception) { return false; }
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try { _current?.Log(logLevel, eventId, state, exception, formatter); }
        catch (Exception) { /* the logger's host is gone, or a provider failed: not a sign-in's problem */ }
    }
}
