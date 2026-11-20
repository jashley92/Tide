using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tide.Application.Intake;

namespace Tide.Infrastructure.Notifications;

/// <summary>
/// The one outbound HTTP client (feeds, the CVE data services). Each request carries the User-Agent chosen for it
/// (<see cref="UserAgents"/>), so a change of setting applies to the next one. One long-lived handler with pooled connections that
/// are recycled, so DNS changes are seen; automatic decompression; at most five redirects, never from https to http; an
/// optional proxy (<c>Intake:Proxy</c>, read at startup). Each request has a time limit and a size limit, and a host
/// that keeps failing is backed off (<see cref="OutboundCircuit"/>) so a dead feed doesn't hold the poller.
/// <para>
/// Only addresses <see cref="AddressPolicy"/> allows are reached. Redirects are followed here, not by the handler, so
/// each hop's host is resolved and checked first; going direct, every connection is checked again on the address it
/// actually connects to, so DNS that changes in between (rebinding) can't slip through. Through a proxy, which
/// resolves names itself, the per-hop check is the guard, and a name this machine can't resolve (a network that reaches
/// the internet only through the proxy) is left to the proxy; an address, or a name that does resolve, is still checked.
/// A name that can't be found going direct is a failed fetch like any other.
/// </para>
/// </summary>
public sealed class OutboundHttp : IOutboundHttp, IDisposable
{
    private readonly HttpClient _client;
    private readonly OutboundCircuit _circuit;
    private readonly IOptionsMonitor<IntakeOptions> _options;
    private readonly ILogger<OutboundHttp> _log;

    private const int MaxRedirects = 5;
    private readonly Func<IPAddress, string?> _refusal;
    private readonly WebProxy? _proxy;

    public OutboundHttp(OutboundCircuit circuit, IOptionsMonitor<IntakeOptions> options, ILogger<OutboundHttp> log)
        : this(circuit, options, log, ip => AddressPolicy.Refusal(ip, options.CurrentValue.AllowPrivateNetworks))
    {
    }

    /// <summary>With another address rule (tests reach a server on loopback).</summary>
    internal OutboundHttp(OutboundCircuit circuit, IOptionsMonitor<IntakeOptions> options, ILogger<OutboundHttp> log, Func<IPAddress, string?> refusal)
    {
        _circuit = circuit;
        _options = options;
        _log = log;
        _refusal = refusal;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        if (Proxy(options.CurrentValue.Proxy) is { } proxy)
        {
            handler.Proxy = _proxy = proxy;
            handler.UseProxy = true;
        }
        else handler.ConnectCallback = (context, ct) => AddressGuard.ConnectAsync(context, _refusal, ct);   // the system proxy, if any, still applies
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// The proxy a setting names, signing in as the service's own account; null when it's blank or not an absolute address.
    /// </summary>
    internal static WebProxy? Proxy(string? address) =>
        !string.IsNullOrWhiteSpace(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            ? new WebProxy(uri) { UseDefaultCredentials = true } : null;

    public async Task<HttpFetch> GetAsync(string url, OutboundRequest? request = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new HttpFetch(FetchStatus.Failed, Error: "That isn't an http or https address.");
        var target = "http:" + uri.Host;
        if (!_circuit.TryEnter(target))
            return new HttpFetch(FetchStatus.Failed, Error: $"{uri.Host} hasn't been answering; it's tried again in {_circuit.MinutesLeft(target)} minute(s).");

        var o = _options.CurrentValue;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var limit = request?.Timeout ?? TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 5, 120));
        timeout.CancelAfter(limit);
        try
        {
            using var response = await SendFollowingRedirectsAsync(uri, request, timeout.Token);
            if (response is null)
            {
                _circuit.Succeeded(target);
                return new HttpFetch(FetchStatus.Failed, Error: "The server redirected from https to http, or more than five times; not followed.");
            }
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                _circuit.Succeeded(target);
                return new HttpFetch(FetchStatus.NotModified, ETag: request?.ETag, LastModified: request?.LastModified);
            }
            if (!response.IsSuccessStatusCode)
            {
                var busy = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests;
                if (busy) _circuit.Failed(target);
                else _circuit.Succeeded(target);
                return new HttpFetch(FetchStatus.Failed, Error: $"The server answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}.",
                    Retryable: busy, RetryAfter: busy && response.Headers.RetryAfter is { } after ? after.Delta ?? after.Date - DateTimeOffset.UtcNow : null);
            }
            var max = Math.Max(64, o.MaxResponseKB) * 1024L;
            if (response.Content.Headers.ContentLength is > 0 and var length && length > max)
            {
                _circuit.Succeeded(target);
                return new HttpFetch(FetchStatus.Failed, Error: $"The response is larger than the {o.MaxResponseKB:N0} KB limit.");
            }
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (ms.Length + read > max)
                {
                    _circuit.Succeeded(target);
                    return new HttpFetch(FetchStatus.Failed, Error: $"The response is larger than the {o.MaxResponseKB:N0} KB limit.");
                }
                await ms.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            _circuit.Succeeded(target);
            return new HttpFetch(FetchStatus.Ok, ms.ToArray(), response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified?.ToString("R"));
        }
        catch (Exception ex) when (AddressGuard.Refused(ex) is { } refused)
        {
            // A rule, not the host failing: nothing to back off.
            _circuit.Release(target);
            _log.LogWarning("Refused to fetch from {Host}: {Error}", refused.Host, refused.Message);
            return new HttpFetch(FetchStatus.Failed, Error: refused.Message + (refused.Message.Contains("private", StringComparison.Ordinal)
                ? " An administrator can allow private networks (Intake:AllowPrivateNetworks)." : ""));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _circuit.Failed(target);
            return new HttpFetch(FetchStatus.Failed, Error: $"{uri.Host} didn't answer within {limit.TotalSeconds:0} seconds.", Retryable: true);
        }
        catch (SocketException ex)
        {
            // A name on the way (the address's own or a redirect's) couldn't be looked up: a typo, or DNS down for now.
            _circuit.Failed(target);
            _log.LogWarning("Looking up a host for {Host} failed: {Error}.", uri.Host, ex.SocketErrorCode);
            return new HttpFetch(FetchStatus.Failed, Error: ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData
                ? $"{uri.Host} couldn't be found (DNS)." : $"{uri.Host} couldn't be looked up ({ex.SocketErrorCode}).");
        }
        catch (HttpRequestException ex)
        {
            _circuit.Failed(target);
            _log.LogWarning("A request to {Host} failed: {Error}.", uri.Host, ex.HttpRequestError);
            return new HttpFetch(FetchStatus.Failed, Error: $"{uri.Host} couldn't be reached ({ex.HttpRequestError}).");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // The connection dropped mid-body, or a compressed body was corrupt.
            _circuit.Failed(target);
            _log.LogWarning("Reading the response from {Host} failed: {Error}.", uri.Host, ex.GetType().Name);
            return new HttpFetch(FetchStatus.Failed, Error: $"The response from {uri.Host} was cut off or couldn't be read.");
        }
        catch (OperationCanceledException)
        {
            // Shutting down: the request says nothing about the host, so a probe it held is handed back.
            _circuit.Release(target);
            throw;
        }
    }

    // The response, following up to five redirects (each hop's host checked first, never https to http), or null when a
    // redirect isn't followed. The conditional headers go on every hop, as the handler would send them. The caller's own
    // headers (an API key) go only to the address it asked for: a redirect to another host doesn't get them. The User-Agent
    // goes on every hop, one value, replacing any the caller's headers carry: the request's own (a source's choice) when it
    // can be sent, else the global setting as it is now.
    private async Task<HttpResponseMessage?> SendFollowingRedirectsAsync(Uri uri, OutboundRequest? request, CancellationToken ct)
    {
        var agent = UserAgents.Valid(request?.UserAgent) ?? UserAgents.Global(_options.CurrentValue);
        var current = uri;
        for (var hop = 0; ; hop++)
        {
            await CheckHostAsync(current, ct);
            using var message = new HttpRequestMessage(HttpMethod.Get, current);
            if (request?.ETag is { Length: > 0 } etag) message.Headers.TryAddWithoutValidation("If-None-Match", etag);
            if (request?.LastModified is { Length: > 0 } since) message.Headers.TryAddWithoutValidation("If-Modified-Since", since);
            if (SameOrigin(uri, current))
                foreach (var (name, value) in request?.Headers ?? new Dictionary<string, string>()) message.Headers.TryAddWithoutValidation(name, value);
            message.Headers.Remove("User-Agent");
            message.Headers.TryAddWithoutValidation("User-Agent", agent);
            var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
                return response;
            response.Dispose();
            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (hop + 1 > MaxRedirects || next.Scheme is not ("http" or "https") || (current.Scheme == "https" && next.Scheme == "http"))
                return null;
            current = next;
        }
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    // Refuses a host whose name resolves to (or is) an address the rule doesn't allow.
    private Task CheckHostAsync(Uri uri, CancellationToken ct) => AddressGuard.CheckHostAsync(uri, Proxied(uri), _refusal, ct);

    // Whether the request goes through a proxy: Intake:Proxy, or (when that's blank) the system's for this address.
    private bool Proxied(Uri uri) => _proxy is not null ? !_proxy.IsBypassed(uri) : AddressGuard.SystemProxied(uri);

    public void Dispose() => _client.Dispose();
}
