using System.Net;
using System.Net.Sockets;

namespace Tide.Infrastructure.Notifications;

/// <summary>
/// Applies an address rule (<see cref="AddressPolicy"/>) to outbound HTTP: the outbound client for feeds and CVE
/// services, and the webhook client. <see cref="ConnectAsync"/> is a handler's <c>ConnectCallback</c>: it resolves the
/// host and connects only to an address the rule allows, so a name that changes between a check and the connection
/// (DNS rebinding) can't slip through. <see cref="CheckHostAsync"/> is the check made before a request, which is the
/// guard when a proxy makes the connection instead.
/// </summary>
public static class AddressGuard
{
    // Through a proxy, how long a name is looked up here before it's left to the proxy.
    private static readonly TimeSpan ProxiedLookup = TimeSpan.FromSeconds(3);

    /// <summary>Whether the system's proxy (the machine's, or HTTPS_PROXY's) carries a request to <paramref name="uri"/>.</summary>
    public static bool SystemProxied(Uri uri) =>
        !HttpClient.DefaultProxy.IsBypassed(uri) && HttpClient.DefaultProxy.GetProxy(uri) is not null;

    /// <summary>
    /// Throws <see cref="RefusedAddressException"/> when <paramref name="uri"/>'s host is, or resolves to, an address the
    /// rule refuses. Through a proxy (<paramref name="proxied"/>), a name that can't be resolved here, or not quickly, is
    /// the proxy's to resolve; going direct, a name that can't be found throws <see cref="SocketException"/>.
    /// </summary>
    public static async Task CheckHostAsync(Uri uri, bool proxied, Func<IPAddress, string?> refusal, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var literal)) addresses = [literal];
        else if (!proxied) addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, ct);
        else
        {
            using var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lookup.CancelAfter(ProxiedLookup);
            try { addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, lookup.Token); }
            catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested)) { return; }
        }
        foreach (var address in addresses)
            if (refusal(address) is { } why) throw new RefusedAddressException(uri.Host, why);
    }

    /// <summary>
    /// A <c>ConnectCallback</c> that connects only to an address the rule allows, whatever the name resolves to now. A
    /// connection to the system proxy for the request is the proxy's own address, not the target's, so it isn't judged;
    /// <see cref="CheckHostAsync"/> covers the target then.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, Func<IPAddress, string?> refusal, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var proxied = context.InitialRequestMessage.RequestUri is { } requested && !HttpClient.DefaultProxy.IsBypassed(requested)
                      && HttpClient.DefaultProxy.GetProxy(requested) is { } proxy
                      && string.Equals(proxy.IdnHost, host, StringComparison.OrdinalIgnoreCase) && proxy.Port == context.DnsEndPoint.Port;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = proxied ? addresses.ToList() : addresses.Where(a => refusal(a) is null).ToList();
        if (allowed.Count == 0)
            throw addresses.Length == 0 ? new SocketException((int)SocketError.HostNotFound) : new RefusedAddressException(host, refusal(addresses[0])!);
        for (var i = 0; ; i++)
        {
            var socket = new Socket(allowed[i].AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(allowed[i], context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) when (i < allowed.Count - 1)
            {
                socket.Dispose();   // try the next address
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    /// <summary>The refusal behind <paramref name="ex"/> (the handler wraps it), or null when it wasn't one.</summary>
    public static RefusedAddressException? Refused(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is RefusedAddressException r) return r;
        return null;
    }
}
