namespace Tide.Application.Abstractions;

/// <summary>
/// How the save in progress was made, for the audit trail: a save the article editor made by itself after a pause in
/// typing (<see cref="Autosave"/>) rather than one someone asked for. The audit interceptor records it on each row of that
/// save as the field <see cref="Field"/>, so the log says truthfully which saves were automatic, and the history can show
/// one editing session's autosaves as one entry. It flows with the save's own async call only (an
/// <see cref="AsyncLocal{T}"/>), so another save made meanwhile in the same circuit isn't marked by it.
/// </summary>
public static class AuditVia
{
    /// <summary>The field an autosave's audit rows carry, with the value <see cref="Yes"/>.</summary>
    public const string Field = "Autosaved";

    /// <summary>The value of <see cref="Field"/> on an autosave's rows.</summary>
    public const string Yes = "yes";

    private static readonly AsyncLocal<bool> Current = new();

    /// <summary>Whether the save in progress is an autosave.</summary>
    public static bool Autosaving => Current.Value;

    /// <summary>Marks the saves made until it's disposed, in this async flow, as autosaves.</summary>
    public static IDisposable Autosave()
    {
        Current.Value = true;
        return new Reset();
    }

    private sealed class Reset : IDisposable
    {
        public void Dispose() => Current.Value = false;
    }
}
