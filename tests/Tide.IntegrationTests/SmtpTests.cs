using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tide.Application.Articles;
using Tide.Application.Distribution;
using Tide.Application.Files;
using Tide.Domain.Enums;
using Tide.Infrastructure.Notifications;

namespace Tide.IntegrationTests;

/// <summary>
/// A minimal SMTP server on loopback that takes every message (an address starting "refuse" is refused) and keeps the
/// envelope and the raw message. Enough for <see cref="System.Net.Mail.SmtpClient"/> without TLS or sign-in.
/// </summary>
public sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public sealed record Received(string From, IReadOnlyList<string> To, string Data);

    public List<Received> Messages { get; } = [];
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FakeSmtpServer()
    {
        _listener.Start();
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 fake ESMTP");
        string from = "";
        var to = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            var verb = line.Split(' ', 2)[0].ToUpperInvariant();
            switch (verb)
            {
                case "EHLO" or "HELO":
                    await writer.WriteLineAsync("250-fake");
                    await writer.WriteLineAsync("250 8BITMIME");
                    break;
                case "MAIL":
                    from = line[(line.IndexOf(':') + 1)..].Trim();
                    to.Clear();
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "RCPT":
                    var addr = line[(line.IndexOf(':') + 1)..].Trim().Trim('<', '>');
                    if (addr.StartsWith("refuse", StringComparison.OrdinalIgnoreCase)) await writer.WriteLineAsync("550 no such user");
                    else
                    {
                        to.Add(addr);
                        await writer.WriteLineAsync("250 OK");
                    }
                    break;
                case "DATA":
                    await writer.WriteLineAsync("354 go ahead");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } d && d != ".") data.Append(d).Append("\r\n");
                    lock (Messages) Messages.Add(new Received(from, [.. to], data.ToString()));
                    await writer.WriteLineAsync("250 queued");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 bye");
                    return;
                default:
                    await writer.WriteLineAsync("250 OK");
                    break;
            }
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}

public class SmtpTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeSmtpServer _smtp = new();

    public SmtpTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose()
    {
        _smtp.Dispose();
        _db.Dispose();
    }

    private EmailSender Sender(Action<EmailOptions>? tweak = null)
    {
        var o = new EmailOptions { Enabled = true, SmtpHost = "127.0.0.1", SmtpPort = _smtp.Port, EnableSsl = false, From = "tide@example.org", SmtpTimeoutSeconds = 10 };
        tweak?.Invoke(o);
        return new EmailSender(new StaticOptionsMonitor<EmailOptions>(o), NullLogger<EmailSender>.Instance, new OutboundCircuit());
    }

    [Fact]
    public async Task SendFlow_ThroughARealSmtpSession_DeliversHtmlTextAndCidImages()
    {
        var articles = _db.Articles();
        var id = await articles.CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await articles.GetAsync(id))!);
        form.Bluf = "Patch today.";
        await articles.SaveAsync(form);
        await _db.Screenshots().AddAsync(id, [new ScreenshotUpload("shot.png", TestDb.Png)]);
        var day = new DateOnly(2026, 10, 26);
        var brief = await _db.Briefs().GetOrStartAsync(day);
        await _db.Briefs().AddArticleAsync(brief.Id, id);
        await _db.Briefs().MarkPresentedAsync(brief.Id);
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "all@example.org; refuse@example.org");
        var svc = _db.Distribution(Sender());

        var send = await svc.SendAsync(await svc.PlanAsync(day, Edition.Public));

        send.Status.Should().Be(SendStatus.Sent);
        send.Error.Should().Contain("1 address was refused", "the relay took it for the other recipient");
        var msg = _smtp.Messages.Single();
        msg.To.Should().Equal("all@example.org");
        msg.Data.Should().Contain("multipart/alternative").And.Contain("text/plain").And.Contain("text/html")
            .And.Contain("Content-ID: <shot1@tide>").And.Contain("image/png").And.Contain("X-Auto-Response-Suppress");
        msg.Data.Should().Contain(Convert.ToBase64String(TestDb.Png)[..20]);
    }

    [Fact]
    public async Task Send_Disabled_SendsNothing()
    {
        var result = await Sender(o => o.Enabled = false).SendAsync(new OutgoingEmail(["a@example.org"], "s", "<p>h</p>", "t", []));
        result.Status.Should().Be(EmailSendStatus.Disabled);
        _smtp.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_AnUnreachableRelay_FailsWithASafeMessage_AndIsBackedOffAfterRepeatedFailures()
    {
        var port = _smtp.Port;
        _smtp.Dispose();
        var sender = Sender(o => o.SmtpPort = port);
        var email = new OutgoingEmail(["a@example.org"], "s", "<p>h</p>", "t", []);

        EmailSendResult result = new(EmailSendStatus.Sent);
        for (var i = 0; i < OutboundCircuit.FailuresToOpen; i++) result = await sender.SendAsync(email);
        result.Status.Should().Be(EmailSendStatus.Failed);
        result.Error.Should().Contain("couldn't be reached");

        var fast = await sender.SendAsync(email);
        fast.Error.Should().Contain("hasn't been answering");
    }

    [Fact]
    public async Task Send_ConflictingSignInSettings_AreRefusedBeforeTheRelayIsAsked()
    {
        var result = await Sender(o => { o.SmtpUsername = "svc"; o.SmtpUseDefaultCredentials = true; })
            .SendAsync(new OutgoingEmail(["a@example.org"], "s", "<p>h</p>", "t", []));
        result.Status.Should().Be(EmailSendStatus.Failed);
        _smtp.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_APickupDirectoryThatCantBeMade_FailsWithAMessage_RatherThanThrowing()
    {
        // A folder under a file can't be created: nothing was handed to anyone, so it's a failure, not an unknown outcome.
        var blocker = Path.GetTempFileName();
        try
        {
            var result = await Sender(o => o.PickupDirectory = Path.Combine(blocker, "pickup"))
                .SendAsync(new OutgoingEmail(["a@example.org"], "s", "<p>h</p>", "t", []));
            result.Status.Should().Be(EmailSendStatus.Failed);
            result.Error.Should().Contain("pickup");
        }
        finally { File.Delete(blocker); }
    }
}
