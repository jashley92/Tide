using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Application.Intake;
using Tide.Infrastructure.Notifications;

namespace Tide.IntegrationTests;

/// <summary>The real outbound client against a small HTTP server on loopback.</summary>
public sealed class OutboundHttpTests : IAsyncDisposable
{
    // Loopback only (a test listening on every address sets off Windows Firewall's prompt for testhost.exe): the redirect
    // to 127.0.0.2 below is refused before anything connects to it.
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _paths = [];
    private readonly Task _serving;

    // 127.0.0.2 stands in for an internal address.
    private static readonly IPAddress Internal = IPAddress.Parse("127.0.0.2");

    public OutboundHttpTests()
    {
        _listener.Start();
        _serving = ServeAsync();
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var path = (await reader.ReadLineAsync())?.Split(' ')[1] ?? "/";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                lock (_paths) _paths.Add(path);
                if (path == "/slow") await Task.Delay(Timeout.Infinite, _stop.Token);   // never answers
                var response = path switch
                {
                    "/busy" => "HTTP/1.1 503 Service Unavailable\r\nRetry-After: 7\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                    "/feed" => "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello",
                    "/to-internal" => $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.2:{Port}/feed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                    "/relative" => "HTTP/1.1 301 Moved\r\nLocation: /feed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                    // As a proxy: the request names the whole address, and the name needn't resolve anywhere.
                    "http://feed.tide-test.invalid/feed" => "HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\nproxied",
                    _ => "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                };
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private static OutboundHttp Client(Func<IPAddress, string?>? refusal = null, string proxy = "")
    {
        var options = new StaticOptionsMonitor<IntakeOptions>(new IntakeOptions { Proxy = proxy });
        return refusal is null
            ? new OutboundHttp(new OutboundCircuit(), options, NullLogger<OutboundHttp>.Instance)
            : new OutboundHttp(new OutboundCircuit(), options, NullLogger<OutboundHttp>.Instance, refusal);
    }

    // Loopback allowed (the test server), the stand-in internal address refused.
    private static string? TestRule(IPAddress ip) => ip.Equals(Internal) ? "an internal address" : null;

    [Fact]
    public async Task Get_ALoopbackAddress_IsRefusedByDefault_BeforeAnyRequest()
    {
        using var http = Client();

        var fetch = await http.GetAsync($"http://127.0.0.1:{Port}/feed");

        fetch.Status.Should().Be(FetchStatus.Failed);
        fetch.Error.Should().Contain("loopback");
        lock (_paths) _paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_AnAllowedAddress_IsFetched_AndARelativeRedirectFollowed()
    {
        using var http = Client(TestRule);

        (await http.GetAsync($"http://127.0.0.1:{Port}/feed")).Content.Should().Equal("hello"u8.ToArray());
        var redirected = await http.GetAsync($"http://127.0.0.1:{Port}/relative");

        redirected.Status.Should().Be(FetchStatus.Ok);
        redirected.Content.Should().Equal("hello"u8.ToArray());
    }

    [Fact]
    public async Task Get_ARedirectToARefusedAddress_IsNotFollowed()
    {
        using var http = Client(TestRule);

        var fetch = await http.GetAsync($"http://127.0.0.1:{Port}/to-internal");

        fetch.Status.Should().Be(FetchStatus.Failed);
        fetch.Error.Should().Contain("127.0.0.2").And.Contain("internal address");
        lock (_paths) _paths.Should().Equal("/to-internal");
    }

    [Fact]
    public async Task Get_ThroughAProxy_LeavesANameThisMachineCantResolve_ToTheProxy()
    {
        using var http = Client(TestRule, proxy: $"http://127.0.0.1:{Port}");

        var fetch = await http.GetAsync("http://feed.tide-test.invalid/feed");

        fetch.Status.Should().Be(FetchStatus.Ok, fetch.Error);
        fetch.Content.Should().Equal("proxied"u8.ToArray());
        lock (_paths) _paths.Should().Equal("http://feed.tide-test.invalid/feed");
    }

    [Fact]
    public async Task Get_ThroughAProxy_StillRefusesAnAddressTheRuleDoesnt()
    {
        using var http = Client(TestRule, proxy: $"http://127.0.0.1:{Port}");

        var fetch = await http.GetAsync("http://127.0.0.2/feed");

        fetch.Status.Should().Be(FetchStatus.Failed);
        fetch.Error.Should().Contain("internal address");
        lock (_paths) _paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_ANameThatDoesntResolve_IsAFailedFetch_NotAnException()
    {
        using var http = Client(TestRule);

        var fetch = await http.GetAsync("http://no-such-host.tide-test.invalid/feed");

        fetch.Status.Should().Be(FetchStatus.Failed);
        fetch.Error.Should().Contain("no-such-host.tide-test.invalid");
    }

    [Fact]
    public async Task Get_ARequestsOwnTimeLimit_IsUsed_AndATimeoutIsWorthTryingAgain()
    {
        using var http = Client(TestRule);

        var fetch = await http.GetAsync($"http://127.0.0.1:{Port}/slow", new OutboundRequest(Timeout: TimeSpan.FromSeconds(1)));

        fetch.Status.Should().Be(FetchStatus.Failed);
        fetch.Error.Should().Be("127.0.0.1 didn't answer within 1 seconds.");
        fetch.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task Get_AServerTooBusy_IsWorthTryingAgain_AfterItsRetryAfter_AndTwoDontBackItOff()
    {
        using var http = Client(TestRule);

        var busy = await http.GetAsync($"http://127.0.0.1:{Port}/busy");
        busy.Should().Match<HttpFetch>(f => f.Status == FetchStatus.Failed && f.Retryable && f.RetryAfter == TimeSpan.FromSeconds(7));
        await http.GetAsync($"http://127.0.0.1:{Port}/busy");
        var third = await http.GetAsync($"http://127.0.0.1:{Port}/feed");

        third.Status.Should().Be(FetchStatus.Ok, "two failures in a row don't back the host off: a third try still goes out");
        var missing = await http.GetAsync($"http://127.0.0.1:{Port}/missing");
        missing.Should().Match<HttpFetch>(f => f.Status == FetchStatus.Failed && !f.Retryable && f.RetryAfter == null);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _serving;
        _stop.Dispose();
    }
}
