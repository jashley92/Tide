using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Application.Abstractions;
using Tide.Application.Intake;
using Tide.Infrastructure.Notifications;
using Tide.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Tide.IntegrationTests.Audit;

/// <summary>An API key given for one host stays with that host when the server redirects elsewhere.</summary>
public sealed class OutboundRedirectHeaderTests : IAsyncDisposable
{
    // Two loopback addresses on one port, so the second is another host to the client. Loopback only: a test listening
    // on every address sets off Windows Firewall's prompt for testhost.exe.
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TcpListener _other;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<(string Host, string Path, List<string> Headers)> _requests = [];
    private readonly Task _serving;

    public OutboundRedirectHeaderTests()
    {
        _listener.Start();
        _other = new TcpListener(IPAddress.Parse("127.0.0.3"), Port);
        _other.Start();
        _serving = Task.WhenAll(ServeAsync(_listener), ServeAsync(_other));
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task ServeAsync(TcpListener listener)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var path = (await reader.ReadLineAsync())?.Split(' ')[1] ?? "/";
                var headers = new List<string>();
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) headers.Add(line);
                var host = headers.FirstOrDefault(h => h.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim() ?? "";
                lock (_requests) _requests.Add((host, path, headers));
                var response = path switch
                {
                    "/feed" => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok",
                    // 127.0.0.3 is another host to the client; the server answers there too.
                    "/elsewhere" => $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.3:{Port}/feed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                    "/same" => "HTTP/1.1 302 Found\r\nLocation: /feed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                    _ => "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                };
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private static OutboundHttp Client() =>
        new(new OutboundCircuit(), new StaticOptionsMonitor<IntakeOptions>(new IntakeOptions()), NullLogger<OutboundHttp>.Instance, _ => null);

    private static OutboundRequest WithKey() => new(Headers: new Dictionary<string, string> { ["apiKey"] = "secret-key-123" });

    [Fact]
    public async Task Get_ARedirectToAnotherHost_DoesNotCarryTheCallersHeaders()
    {
        using var http = Client();

        var fetch = await http.GetAsync($"http://127.0.0.1:{Port}/elsewhere", WithKey());

        fetch.Status.Should().Be(FetchStatus.Ok);
        lock (_requests)
        {
            _requests.Should().HaveCount(2);
            _requests[0].Headers.Should().Contain(h => h.StartsWith("apiKey:", StringComparison.OrdinalIgnoreCase), "the asked-for host gets the key");
            _requests[1].Host.Should().StartWith("127.0.0.3");
            _requests[1].Headers.Should().NotContain(h => h.Contains("secret-key-123", StringComparison.Ordinal), "another host never sees it");
        }
    }

    [Fact]
    public async Task Get_ARedirectOnTheSameHost_KeepsTheCallersHeaders()
    {
        using var http = Client();

        var fetch = await http.GetAsync($"http://127.0.0.1:{Port}/same", WithKey());

        fetch.Status.Should().Be(FetchStatus.Ok);
        lock (_requests)
        {
            _requests.Should().HaveCount(2);
            _requests.Should().OnlyContain(r => r.Headers.Any(h => h.Contains("secret-key-123", StringComparison.Ordinal)));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        _other.Stop();
        await _serving;
        _stop.Dispose();
    }
}

/// <summary>An IPv6 address that carries an IPv4 one is judged as that IPv4 address as well.</summary>
public sealed class AddressPolicyEmbeddedIPv4Tests
{
    [Theory]
    [InlineData("64:ff9b::a9fe:a9fe", "link-local")]   // NAT64 of 169.254.169.254 (cloud metadata)
    [InlineData("64:ff9b::7f00:1", "loopback")]        // NAT64 of 127.0.0.1
    [InlineData("64:ff9b::a00:1", "private")]          // NAT64 of 10.0.0.1
    [InlineData("2002:7f00:1::", "loopback")]          // 6to4 of 127.0.0.1
    [InlineData("2002:c0a8:101::1", "private")]        // 6to4 of 192.168.1.1
    [InlineData("::a9fe:a9fe", "link-local")]          // IPv4-compatible 169.254.169.254
    [InlineData("::1", "loopback")]
    [InlineData("::", "reserved")]
    public void Refusal_AnEmbeddedRefusedIPv4_IsRefused(string address, string why) =>
        AddressPolicy.Refusal(IPAddress.Parse(address), allowPrivateNetworks: false).Should().Contain(why);

    [Theory]
    [InlineData("64:ff9b::808:808")]   // NAT64 of 8.8.8.8
    [InlineData("2002:808:808::1")]    // 6to4 of 8.8.8.8
    [InlineData("2606:4700::1111")]
    public void Refusal_AnEmbeddedPublicIPv4_IsAllowed(string address) =>
        AddressPolicy.Refusal(IPAddress.Parse(address), allowPrivateNetworks: false).Should().BeNull();

    [Fact]
    public void Refusal_AnEmbeddedPrivateIPv4_IsAllowedWhenPrivateNetworksAre() =>
        AddressPolicy.Refusal(IPAddress.Parse("64:ff9b::a00:1"), allowPrivateNetworks: true).Should().BeNull();
}

/// <summary>Presence keeps only the places someone is at.</summary>
public sealed class PresencePlacesTests
{
    [Fact]
    public void Leave_TheLastVisitorLeaving_RemovesThePlace()
    {
        var presence = new Presence();

        for (var i = 0; i < 1000; i++) presence.Join($"article:{i}", "u1", "Robin").Dispose();
        var stays = presence.Join("brief:2026-10-30", "u2", "Sam");

        presence.PlaceCount.Should().Be(1);
        presence.Others("brief:2026-10-30", "u1").Should().ContainSingle(p => p.Name == "Sam");
        stays.Dispose();
        presence.PlaceCount.Should().Be(0);
    }

    [Fact]
    public async Task JoinAndLeave_AtOnce_NeverLoseSomeoneWhoStayed()
    {
        var presence = new Presence();
        var stayers = new System.Collections.Concurrent.ConcurrentBag<IPresenceVisit>();

        await Parallel.ForAsync(0, 4000, (i, _) =>
        {
            var visit = presence.Join("brief:busy", $"u{i}", $"Person {i}");
            if (i % 2 == 0) visit.Dispose();
            else stayers.Add(visit);
            return ValueTask.CompletedTask;
        });

        presence.Others("brief:busy", "nobody").Should().HaveCount(2000);
        foreach (var v in stayers) v.Dispose();
        presence.PlaceCount.Should().Be(0);
    }
}

/// <summary>
/// The real app stops promptly while a background job is in the middle of a request: the host's stopping token reaches
/// the outbound client, which gives up the request, instead of the shutdown waiting out the request's own time limit
/// (or the host's 30-second shutdown limit, killing the job part way).
/// </summary>
public sealed class HostedShutdownTests
{
    // A server that accepts the connection and never answers: a feed that hangs.
    private sealed class SilentServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _held = [];
        public readonly TaskCompletionSource Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SilentServer()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    lock (_held) _held.Add(client);
                    Connected.TrySetResult();
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _listener.Stop();
            lock (_held) foreach (var c in _held) c.Dispose();
        }
    }

    private sealed class HangingFeedApp(int port) : TideApp
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // The CVE refresh is due at once on a new database, and its first request is the KEV catalog.
            builder.UseSetting("Cve:Enabled", "true");
            builder.UseSetting("Cve:KevUrl", $"http://127.0.0.1:{port}/kev");
            builder.UseSetting("Intake:TimeoutSeconds", "120");
            Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder, services =>
            {
                // The real client, with an address rule that lets it reach the test server on loopback.
                services.RemoveAll<IOutboundHttp>();
                services.AddSingleton<IOutboundHttp>(sp => new OutboundHttp(sp.GetRequiredService<OutboundCircuit>(),
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<IntakeOptions>>(), NullLogger<OutboundHttp>.Instance, _ => null));
            });
        }
    }

    [Fact]
    public async Task Stop_DuringAHangingFetch_EndsTheFetchAndStopsPromptly()
    {
        using var server = new SilentServer();
        var app = new HangingFeedApp(server.Port);
        _ = app.Services;   // starts the host, and with it the background jobs

        (await Task.WhenAny(server.Connected.Task, Task.Delay(TimeSpan.FromSeconds(30)))).Should().Be(server.Connected.Task,
            "the CVE refresh should have started its KEV request");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await app.DisposeAsync();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the stopping token should end the request at once");
    }

    // Holds every background database session (a token that can be cancelled) until that token is cancelled: a long
    // import, as far as the job can tell. A session without a token (start-up) goes straight through.
    private sealed class HoldingDbFactory(IAppDbContextFactory inner) : IAppDbContextFactory
    {
        public async Task<IAppDbContext> CreateAsync(CancellationToken ct = default)
        {
            if (ct.CanBeCanceled) await Task.Delay(Timeout.Infinite, ct);
            return await inner.CreateAsync(ct);
        }
    }

    private sealed class SlowImportApp(string root) : TideApp
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Files:RootPath", root);
            builder.UseSetting("Metrics:Import:DropFolderEnabled", "true");
            Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder, services =>
            {
                services.RemoveAll<IAppDbContextFactory>();
                services.AddScoped<IAppDbContextFactory>(sp => new HoldingDbFactory(
                    new Tide.Infrastructure.Persistence.AppDbContextFactoryAdapter(
                        sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Tide.Infrastructure.Persistence.AppDbContext>>())));
            });
        }
    }

    [Fact]
    public async Task Stop_DuringAMetricImport_EndsTheImportAndStopsPromptly()
    {
        var root = Path.Combine(Path.GetTempPath(), "tide-shutdown-" + Guid.NewGuid().ToString("N"));
        var inbox = Path.Combine(root, Tide.Infrastructure.Storage.MetricDropFolder.FolderName);
        Directory.CreateDirectory(inbox);
        var file = Path.Combine(inbox, "metrics.csv");
        await File.WriteAllLinesAsync(file, ["date,metric_key,value", "2026-10-29,phish,3"]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-5));   // settled
        try
        {
            var app = new SlowImportApp(root);
            var jobs = app.Services.GetRequiredService<Tide.Application.Operations.JobMonitor>();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!jobs.Get(Tide.Application.Operations.JobMonitor.MetricDropFolder).Running && DateTime.UtcNow < deadline) await Task.Delay(50);
            jobs.Get(Tide.Application.Operations.JobMonitor.MetricDropFolder).Running.Should().BeTrue("the drop folder pass should be importing the file");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await app.DisposeAsync();

            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the stopping token should reach the import");
            File.Exists(file).Should().BeTrue("an import that was stopped leaves its file for the next start");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}

/// <summary>A brief send while the relay is down, or refusing for now, ends failed and can be sent again; it's never
/// recorded as sent, and the brief isn't marked sent.</summary>
public sealed class SmtpDownSendTests : IDisposable
{
    private readonly TestDb _db = new();

    public SmtpDownSendTests() =>
        _db.User.Roles = new HashSet<Tide.Domain.Enums.AppRole> { Tide.Domain.Enums.AppRole.Analyst, Tide.Domain.Enums.AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private static EmailSender Sender(int port) => new(
        new StaticOptionsMonitor<EmailOptions>(new EmailOptions
        {
            Enabled = true, SmtpHost = "127.0.0.1", SmtpPort = port, EnableSsl = false, From = "tide@example.org", SmtpTimeoutSeconds = 5,
        }),
        NullLogger<EmailSender>.Instance, new OutboundCircuit());

    private async Task<DateOnly> BriefWithAnArticleAsync()
    {
        var articles = _db.Articles();
        var id = await articles.CreateAsync("Edge VPN zero-day", "Vendor", null, Tide.Domain.Enums.TlpLevel.Clear);
        var form = Tide.Application.Articles.ArticleForm.From((await articles.GetAsync(id))!);
        form.Bluf = "Patch today.";
        await articles.SaveAsync(form);
        var day = new DateOnly(2026, 10, 26);
        var brief = await _db.Briefs().GetOrStartAsync(day);
        await _db.Briefs().AddArticleAsync(brief.Id, id);
        await _db.Briefs().MarkPresentedAsync(brief.Id);
        await _db.Settings().SaveRecipientsAsync(Tide.Domain.Enums.Edition.Public, "all@example.org");
        return day;
    }

    // A relay that answers every command but DATA, which it puts off (451): a relay that's up but can't take mail now.
    private sealed class DeferringRelay : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public DeferringRelay()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                    await writer.WriteLineAsync("220 relay");
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        var verb = line.Split(' ', 2)[0].ToUpperInvariant();
                        if (verb is "EHLO" or "HELO") { await writer.WriteLineAsync("250-relay"); await writer.WriteLineAsync("250 8BITMIME"); }
                        else if (verb == "DATA") await writer.WriteLineAsync("451 4.3.0 try again later");
                        else if (verb == "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
                        else await writer.WriteLineAsync("250 OK");
                    }
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException) { }
        }

        public void Dispose() => _listener.Stop();
    }

    private async Task SendFailsThenSucceedsAsync(DateOnly day, int downPort, string expected)
    {
        var down = _db.Distribution(Sender(downPort));
        var failed = await down.SendAsync(await down.PlanAsync(day, Tide.Domain.Enums.Edition.Public));

        failed.Status.Should().Be(Tide.Domain.Enums.SendStatus.Failed);
        failed.Error.Should().Contain(expected);
        (await _db.Briefs().GetOrStartAsync(day)).Status.Should().NotBe(Tide.Domain.Enums.BriefStatus.Sent);
        (await down.SendsAsync(day)).Should().ContainSingle(s => s.Status == Tide.Domain.Enums.SendStatus.Failed);

        // The relay is back: the same edition goes on a second confirm.
        using var relay = new FakeSmtpServer();
        var up = _db.Distribution(Sender(relay.Port));
        var sent = await up.SendAsync(await up.PlanAsync(day, Tide.Domain.Enums.Edition.Public));

        sent.Status.Should().Be(Tide.Domain.Enums.SendStatus.Sent);
        relay.Messages.Should().ContainSingle();
        (await _db.Briefs().GetOrStartAsync(day)).Status.Should().Be(Tide.Domain.Enums.BriefStatus.Sent);
    }

    [Fact]
    public async Task Send_TheRelayDown_EndsFailed_AndSendsOnceItsBack()
    {
        var day = await BriefWithAnArticleAsync();
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();   // nothing listens there now

        await SendFailsThenSucceedsAsync(day, port, "couldn't be reached");
    }

    [Fact]
    public async Task Send_TheRelayDeferring_EndsFailed_AndSendsOnceItTakesMail()
    {
        var day = await BriefWithAnArticleAsync();
        using var relay = new DeferringRelay();

        await SendFailsThenSucceedsAsync(day, relay.Port, "try again later");
    }
}

/// <summary>The readiness probe answers within its own time limit when the database doesn't answer at all.</summary>
public sealed class DatabaseHealthCheckTests
{
    private sealed class HangingDb : IAppDbContextFactory
    {
        public async Task<IAppDbContext> CreateAsync(CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    // The limit runs on a clock the test moves, so a loaded machine's slow timers can't fail it: the probe is still
    // waiting just short of the limit, and is unhealthy as soon as the limit passes.
    [Fact]
    public async Task Check_AHangingDatabase_IsUnhealthyWithinTheTimeLimit()
    {
        var clock = new Tide.TestSupport.ManualClock();
        var check = new DatabaseHealthCheck(new HangingDb(), clock);

        var probe = check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        clock.Advance(DatabaseHealthCheck.TimeLimit - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        probe.IsCompleted.Should().BeFalse("the database still has time to answer");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var result = await probe.WaitAsync(TimeSpan.FromSeconds(30));

        result.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy);
        result.Description.Should().Contain("didn't answer within");
    }
}

/// <summary>Signs a service scope of the real app in, as a request would, with the given permissions.</summary>
public static class ServiceScopes
{
    public static void SignIn(IServiceScope scope, params Tide.Domain.Enums.Permission[] permissions)
    {
        var claims = new List<System.Security.Claims.Claim> { new(System.Security.Claims.ClaimTypes.Name, "Test User") };
        claims.AddRange(permissions.Select(p => new System.Security.Claims.Claim(Tide.Application.Security.AppClaimTypes.Permission, p.ToString())));
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "Test")),
        };
    }
}

/// <summary>The System services refuse anyone but an administrator themselves, not only through their pages.</summary>
public sealed class SystemServicesAdminOnlyTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task Diagnostics_ANonAdministrator_IsRefused()
    {
        using var scope = app.Services.CreateScope();
        ServiceScopes.SignIn(scope, Tide.Domain.Enums.Permission.ReadTeamEdition);

        var run = () => scope.ServiceProvider.GetRequiredService<SystemDiagnostics>().RunAsync();

        await run.Should().ThrowAsync<Tide.Application.Security.ForbiddenException>();
    }

    [Fact]
    public async Task Settings_ANonAdministrator_IsRefusedEveryView()
    {
        using var scope = app.Services.CreateScope();
        ServiceScopes.SignIn(scope, Tide.Domain.Enums.Permission.ReadTeamEdition);
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettings>();

        settings.Invoking(s => s.Server()).Should().Throw<Tide.Application.Security.ForbiddenException>();
        settings.Invoking(s => s.Row(Tide.Application.Operations.SettingsCatalog.Server[0])).Should().Throw<Tide.Application.Security.ForbiddenException>();
        await settings.Invoking(s => s.InAppAsync()).Should().ThrowAsync<Tide.Application.Security.ForbiddenException>();
        await settings.Invoking(s => s.InstallFactsAsync(null)).Should().ThrowAsync<Tide.Application.Security.ForbiddenException>();
    }

    [Fact]
    public async Task Diagnostics_AnAdministrator_IsServed()
    {
        using var scope = app.Services.CreateScope();
        ServiceScopes.SignIn(scope, Tide.Domain.Enums.Permission.Administer);

        (await scope.ServiceProvider.GetRequiredService<SystemDiagnostics>().RunAsync()).Database.Reachable.Should().BeTrue();
        scope.ServiceProvider.GetRequiredService<SystemSettings>().Server().Should().NotBeEmpty();
    }
}

/// <summary>
/// Outside Development, an unhandled error shows the error page with a reference, and that reference is in the scope
/// the exception handler logs the error with, so an administrator can find the entry a user quotes.
/// </summary>
public sealed partial class ErrorReferenceTests
{
    [System.Text.RegularExpressions.GeneratedRegex("<code[^>]*>([^<]+)</code>")]
    private static partial System.Text.RegularExpressions.Regex CodeText();

    // Keeps every log entry with the values of the scopes it was written in.
    private sealed class ScopeCapture : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ISupportExternalScope
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<(string Category, Exception? Error, List<string> Scope)> Entries = new();
        private Microsoft.Extensions.Logging.IExternalScopeProvider _scopes = new Microsoft.Extensions.Logging.LoggerExternalScopeProvider();

        public void SetScopeProvider(Microsoft.Extensions.Logging.IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose() { }

        private sealed class Logger(ScopeCapture owner, string category) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = new List<string>();
                owner._scopes.ForEachScope((scope, list) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs) list.AddRange(pairs.Select(p => $"{p.Key}={p.Value}"));
                    else if (scope is not null) list.Add(scope.ToString() ?? "");
                }, values);
                owner.Entries.Enqueue((category, exception, values));
            }
        }
    }

    private sealed class FailingApp(ScopeCapture logs) : TideApp
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Staging");   // not Development: the exception handler and the error page are in use
            builder.UseSetting("Auth:AllowDevSignInOutsideDevelopment", "true");
            builder.ConfigureLogging(l => l.AddProvider(logs));
            Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder, services =>
                services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, ThrowAtTheEnd>());
        }
    }

    // After the app's own pipeline, inside its exception handler: an address nothing else answers throws.
    private sealed class ThrowAtTheEnd : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Use(rest => http =>
                http.Request.Path == "/audit-boom" ? throw new InvalidOperationException("secret detail 4711") : rest(http));
        };
    }

    [Fact]
    public async Task AnUnhandledError_ShowsAReference_ThatTheLoggedErrorCarries_AndNoDetail()
    {
        var logs = new ScopeCapture();
        await using var app = new FailingApp(logs);
        var client = app.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/audit-boom");
        request.Headers.Accept.ParseAdd("text/html");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Something went wrong").And.NotContain("secret detail 4711").And.NotContain("InvalidOperationException");
        var match = CodeText().Match(html);
        match.Success.Should().BeTrue("the page shows a reference");
        var reference = match.Groups[1].Value;

        var logged = logs.Entries.Where(e => e.Error?.Message == "secret detail 4711").ToList();
        logged.Should().NotBeEmpty("the exception handler logs the error");
        // 00-<TraceId>-<SpanId>-01: the logged scope's TraceId is in it (or the request id is the reference itself).
        logged.Should().Contain(e => e.Scope.Any(v =>
            (v.StartsWith("TraceId=", StringComparison.Ordinal) && reference.Contains(v.Substring(8), StringComparison.Ordinal))
            || v == "RequestId=" + reference));
    }
}

/// <summary>An imported bundle's group mappings that give Admin, or a role to everyone, are flagged in the preview; a
/// recipient list longer than the settings page allows is refused.</summary>
public sealed class ConfigBundleRiskTests
{
    private static Tide.Application.Operations.ConfigBundle Bundle(IReadOnlyList<Tide.Application.Operations.BundleRoleMapping> mappings,
        IReadOnlyList<Tide.Application.Operations.BundleSetting>? settings = null) =>
        new(Tide.Application.Operations.ConfigBundle.FormatName, Tide.Application.Operations.ConfigBundle.CurrentVersion, DateTimeOffset.UtcNow,
            "Someone", "0.1.0", settings ?? [], mappings, [], [], []);

    private static (TestDb Db, Tide.Application.Operations.ConfigBundleService Service) Admin()
    {
        var db = new TestDb();
        db.User.Roles = new HashSet<Tide.Domain.Enums.AppRole> { Tide.Domain.Enums.AppRole.Admin };
        return (db, new Tide.Application.Operations.ConfigBundleService(db, db.User, new CountingRoleDirectory(), TimeProvider.System));
    }

    [Theory]
    [InlineData(@"CONTOSO\CTI-Admins", "Admin", true)]
    [InlineData("Everyone", "Reader", true)]
    [InlineData(@"NT AUTHORITY\Authenticated Users", "Analyst", true)]
    [InlineData(@"FABRIKAM\Domain Users", "Reader", true)]
    [InlineData(@"BUILTIN\Users", "ActionOwner", true)]
    [InlineData("S-1-1-0", "Reader", true)]
    [InlineData("S-1-5-11", "Reader", true)]
    [InlineData("S-1-5-21-1004336348-1177238915-682003330-513", "Reader", true)]
    [InlineData(@"CONTOSO\CTI-Analysts", "Analyst", false)]
    [InlineData("S-1-5-21-1004336348-1177238915-682003330-1513", "Reader", false)]
    public async Task Preview_FlagsAMappingThatGivesAdminOrReachesEveryone(string group, string role, bool flagged)
    {
        var (db, service) = Admin();
        using var _ = db;

        var preview = await service.PreviewAsync(Bundle([new(group, role)]));

        var change = preview.Changes.Single(c => c.Area == "AD groups");
        change.Kind.Should().Be(Tide.Application.Operations.BundleChangeKind.Add);
        change.Warning.Should().Be(flagged);
        if (flagged) change.Detail.Should().StartWith("Check this:");
        preview.Warnings.Should().Be(flagged ? 1 : 0);
    }

    [Fact]
    public async Task Preview_ARecipientListOverTheLimit_IsSkipped()
    {
        var (db, service) = Admin();
        using var _ = db;
        var tooMany = string.Join("; ", Enumerable.Range(0, Tide.Application.Settings.DistributionSettings.MaxRecipients + 1).Select(i => $"r{i}@contoso.com"));
        var enough = string.Join("; ", Enumerable.Range(0, Tide.Application.Settings.DistributionSettings.MaxRecipients).Select(i => $"r{i}@contoso.com"));

        var preview = await service.PreviewAsync(Bundle([], [new("Distribution:TeamRecipients", tooMany), new("Distribution:PublicRecipients", enough)]));

        preview.Changes.Single(c => c.Item == "Distribution:TeamRecipients").Kind.Should().Be(Tide.Application.Operations.BundleChangeKind.Skipped);
        preview.Changes.Single(c => c.Item == "Distribution:TeamRecipients").Detail.Should().Contain("at most");
        preview.Changes.Single(c => c.Item == "Distribution:PublicRecipients").Kind.Should().Be(Tide.Application.Operations.BundleChangeKind.Add);
    }
}

/// <summary>A directory call that never returns stops holding its domain's lookups up once it has been blocked for
/// many times its time limit, while the threads such calls hold stay bounded.</summary>
public sealed class BoundedDirectoryCallsHungTests : IDisposable
{
    private sealed class CountingLog : ILogger
    {
        public int Warnings;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Interlocked.Increment(ref Warnings);
        }
    }

    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);
    private readonly ManualResetEventSlim _never = new(false);   // set only when the test ends, to free the threads
    private readonly TestClock _clock = new(new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
    private readonly CountingLog _log = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _kept = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _asked = new();
    private readonly HashSet<string> _stuck;
    private readonly Tide.Web.Security.BoundedDirectoryCalls<string?> _calls;

    public BoundedDirectoryCallsHungTests()
    {
        _stuck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _calls = new Tide.Web.Security.BoundedDirectoryCalls<string?>(keys =>
            {
                foreach (var k in keys) _asked.Enqueue(k);
                bool stuck;
                lock (_stuck) stuck = keys.Any(_stuck.Contains);
                if (stuck) _never.Wait();
                return keys.ToDictionary(k => k, k => (string?)("Name of " + k), StringComparer.OrdinalIgnoreCase);
            },
            (keys, found) => { foreach (var (k, v) in found) _kept[k] = v; },
            key => key[..key.IndexOf((char)92)], maxInFlight: 3, maxHung: 2, _clock, _log);
    }

    private void Stuck(params string[] keys)
    {
        lock (_stuck) foreach (var k in keys) _stuck.Add(k);
    }

    private void Unstick(string key)
    {
        lock (_stuck) _stuck.Remove(key);
    }

    private int Asked(string key) => _asked.Count(k => k == key);

    [Fact]
    public void ACallThatNeverReturns_IsWrittenOff_AndTheDomainIsAskedAgain_WithinTheThreadBound()
    {
        Stuck(@"D\a", @"D\a2");
        _calls.Wait([@"D\a"], Limit);                // blocks: given up on, counted as hung
        _calls.Wait([@"D\a2"], Limit);               // and a second: the domain's limit of hung calls
        _kept.Should().NotContainKey(@"D\a");

        _calls.Wait([@"D\b"], Limit);                // the domain has as many hung calls as it may: not asked
        Asked(@"D\b").Should().Be(0);

        _clock.Now += Limit * Tide.Web.Security.BoundedDirectoryCalls<string?>.HungExpiryFactor;
        Unstick(@"D\a");                              // the directory answers again, but the first calls stay stuck
        Unstick(@"D\a2");
        _calls.Wait([@"D\a", @"D\a2", @"D\b"], TimeSpan.FromSeconds(5));

        _kept[@"D\a"].Should().Be(@"Name of D\a", "the written-off call's key is asked again");
        _kept[@"D\b"].Should().Be(@"Name of D\b");
        _log.Warnings.Should().Be(1, "two calls written off at once are logged once");

        // A third call that never returns is written off too (logged again: a call there returned in between), but the
        // three stuck threads fill maxInFlight: nothing more starts there until one returns.
        Stuck(@"D\c");
        _calls.Wait([@"D\c"], Limit);
        _clock.Now += Limit * Tide.Web.Security.BoundedDirectoryCalls<string?>.HungExpiryFactor;
        _calls.Wait([@"D\d"], Limit);
        Asked(@"D\d").Should().Be(0, "three threads are still blocked, which is all the domain may have");
        _log.Warnings.Should().Be(2);

        // Another domain is unaffected.
        _calls.Wait([@"E\x"], TimeSpan.FromSeconds(5));
        _kept[@"E\x"].Should().Be(@"Name of E\x");
    }

    public void Dispose()
    {
        _never.Set();
        _never.Dispose();
    }
}

/// <summary>The answers file is written from role mappings whose role name isn't in the app's own casing (a row put in
/// by hand or by an older version), as sign-in reads them (ignoring case), rather than failing the download.</summary>
public sealed class InstallFactsRoleCaseTests
{
    [Fact]
    public async Task InstallFacts_ARoleNameInAnotherCase_IsReadAsThatRole()
    {
        await using var app = new TideApp();
        using var scope = app.Services.CreateScope();
        ServiceScopes.SignIn(scope, Tide.Domain.Enums.Permission.Administer);
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        db.RoleMappings.Add(new Tide.Domain.Entities.AdGroupRoleMapping { AdGroup = "CONTOSO" + (char)92 + "Ops", RoleName = "admin", UpdatedBy = "dba" });
        db.RoleMappings.Add(new Tide.Domain.Entities.AdGroupRoleMapping { AdGroup = "CONTOSO" + (char)92 + "CTI", RoleName = "ANALYST", UpdatedBy = "dba" });
        await db.SaveChangesAsync();

        var facts = await scope.ServiceProvider.GetRequiredService<SystemSettings>().InstallFactsAsync(null);

        facts.Groups[Tide.Domain.Enums.AppRole.Admin].Should().Contain("CONTOSO" + (char)92 + "Ops");
        facts.Groups[Tide.Domain.Enums.AppRole.Analyst].Should().Contain("CONTOSO" + (char)92 + "CTI");
    }
}
