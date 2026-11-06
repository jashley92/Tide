using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace Tide.Web.Security;

/// <summary>
/// The circuit's auth-state provider, re-checking the signed-in user's roles and permissions every
/// <see cref="Interval"/> against the current role directory (<see cref="SessionPermissionRefresher"/>). When they've
/// changed it publishes the updated principal, so page authorization, AuthorizeViews and service-level checks all
/// follow — someone whose access is removed loses it within minutes instead of whenever they next reconnect, and
/// someone granted access gets it without reloading.
/// <para>It also keeps the circuit's principal free of the Windows sign-in's token (<see cref="Detached"/>): the host
/// hands it the connection's WindowsPrincipal, whose claims are read from a logon token that IIS closes when that
/// connection ends. A circuit outlives its connection (a reconnect, a laptop waking), and then every permission check
/// on the page, and this re-check, failed with "Cannot access a disposed object ... SafeAccessTokenHandle".</para>
/// </summary>
public sealed class PermissionRevalidatingAuthStateProvider : ServerAuthenticationStateProvider, IDisposable
{
    /// <summary>How often an open session's access is re-derived. In-memory lookups only, so cheap.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    private readonly SessionPermissionRefresher _refresher;
    private readonly ILogger<PermissionRevalidatingAuthStateProvider> _log;
    private readonly CancellationTokenSource _stop = new();
    private bool _looping;
    private bool _publishing;

    public PermissionRevalidatingAuthStateProvider(SessionPermissionRefresher refresher,
        ILogger<PermissionRevalidatingAuthStateProvider> log)
    {
        _refresher = refresher;
        _log = log;
        AuthenticationStateChanged += OnChanged;
    }

    // The host sets the circuit's initial state once; it's detached from the sign-in's token at once, and re-checked
    // from then. Our own publishes also raise the event, which the flags ignore.
    private void OnChanged(Task<AuthenticationState> state)
    {
        if (_publishing) return;
        _ = DetachAsync(state);
        if (_looping) return;
        _looping = true;
        _ = LoopAsync(_stop.Token);
    }

    // Read while the connection that brought it is still open, so its token is still good.
    private async Task DetachAsync(Task<AuthenticationState> state)
    {
        try
        {
            var user = (await state).User;
            if (!HoldsToken(user)) return;
            Publish(Detached(user));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Couldn't copy the session's sign-in; it keeps the one its connection gave it");
        }
    }

    // A principal whose identities are all plain ClaimsIdentity reads nothing from outside itself.
    private static bool HoldsToken(ClaimsPrincipal user) => user.Identities.Any(i => i.GetType() != typeof(ClaimsIdentity));

    /// <summary>
    /// <paramref name="user"/> as plain claims identities, with each identity's claims, authentication type, name and role
    /// claim types and label copied: the same sign-in, nothing left to read from a WindowsIdentity's token (which the
    /// connection that brought it disposes). Not WindowsIdentity.Clone, which duplicates the token, a handle per circuit.
    /// </summary>
    public static ClaimsPrincipal Detached(ClaimsPrincipal user) =>
        new(user.Identities.Select(i => new ClaimsIdentity(i.Claims.Select(c => c.Clone()), i.AuthenticationType, i.NameClaimType, i.RoleClaimType)
        {
            Label = i.Label,
        }));

    private void Publish(ClaimsPrincipal user)
    {
        _publishing = true;
        try { SetAuthenticationState(Task.FromResult(new AuthenticationState(user))); }
        finally { _publishing = false; }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // One failed re-check is logged and the next tick tries again; stopping would leave a circuit (open for
                // days on an analyst's desk) with permissions a later change revoked.
                try { await RevalidateAsync(); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Session permission re-check failed; trying again in {Minutes} min", Interval.TotalMinutes);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Re-derives the current principal's access and publishes it if it changed. Returns whether it did.</summary>
    public async Task<bool> RevalidateAsync()
    {
        var state = await GetAuthenticationStateAsync();
        if (await _refresher.RefreshAsync(state.User) is not { } updated) return false;

        Publish(updated);
        _log.LogInformation("Refreshed TIDE permissions for an open session of {User}", updated.Identity?.Name);
        return true;
    }

    public void Dispose()
    {
        AuthenticationStateChanged -= OnChanged;
        _stop.Cancel();
        _stop.Dispose();
    }
}
