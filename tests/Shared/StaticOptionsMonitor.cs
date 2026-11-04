using Microsoft.Extensions.Options;

namespace Tide.TestSupport;

/// <summary>Options that don't change.</summary>
public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
