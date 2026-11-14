using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Tide.Web.Services;

/// <summary>
/// Whether this circuit's browser is still connected, for work a page started that shouldn't outlive the person watching
/// it (Pull all). A browser that closes or reloads disposes its page; one that vanishes (a crash, a lost connection) leaves
/// the circuit waiting minutes for it to come back, and <see cref="Lost"/> is cancelled at once. One per circuit (scoped),
/// and the circuit's handler.
/// </summary>
public sealed class CircuitConnection : CircuitHandler, IDisposable
{
    private CancellationTokenSource _up = new();

    /// <summary>Cancelled when the connection goes down; a fresh one once it's back.</summary>
    public CancellationToken Lost => _up.Token;

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _up.Cancel();
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_up.IsCancellationRequested) Interlocked.Exchange(ref _up, new CancellationTokenSource()).Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _up.Dispose();
}
