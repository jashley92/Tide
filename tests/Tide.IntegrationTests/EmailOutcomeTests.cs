using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tide.Application.Actions;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Notifications;
using Tide.TestSupport;

namespace Tide.IntegrationTests;

/// <summary>
/// An SMTP server on loopback that misbehaves in one chosen way, for what a send records when the relay may or may not
/// have taken the message.
/// </summary>
public sealed class MisbehavingSmtpServer : IDisposable
{
    public enum Fault
    {
        /// <summary>Takes the message's data, then never answers.</summary>
        HangAfterData,

        /// <summary>Takes the message's data, then drops the connection without answering.</summary>
        DropAfterData,

        /// <summary>Refuses the sender before any data (550 at MAIL FROM).</summary>
        RefuseSender,

        /// <summary>Refuses the session at its greeting (554).</summary>
        RefuseAtGreeting,
    }

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Fault _fault;
    private int _data;
    private bool _disposed;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>How many messages' data it read in full.</summary>
    public int DataReceived => Volatile.Read(ref _data);

    public MisbehavingSmtpServer(Fault fault)
    {
        _fault = fault;
        _listener.Start();
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        var stop = _stop.Token;
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(stop); }
            catch (Exception) { return; }
            _ = Serve(client, stop);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken stop)
    {
        try
        {
            using var _ = client;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            if (_fault == Fault.RefuseAtGreeting)
            {
                await writer.WriteLineAsync("554 no service here");
                await Task.Delay(TimeSpan.FromSeconds(30), stop);
                return;
            }
            await writer.WriteLineAsync("220 fake ESMTP");
            while (await reader.ReadLineAsync(stop) is { } line)
            {
                switch (line.Split(' ', 2)[0].ToUpperInvariant())
                {
                    case "EHLO" or "HELO":
                        await writer.WriteLineAsync("250-fake");
                        await writer.WriteLineAsync("250 8BITMIME");
                        break;
                    case "MAIL" when _fault == Fault.RefuseSender:
                        await writer.WriteLineAsync("550 sender refused");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 go ahead");
                        while (await reader.ReadLineAsync(stop) is { } d && d != ".") { /* the message */ }
                        Interlocked.Increment(ref _data);
                        if (_fault == Fault.HangAfterData) await Task.Delay(TimeSpan.FromMinutes(5), stop);
                        else client.Client.LingerState = new LingerOption(true, 0);   // dropped: a reset, no reply
                        return;
                    case "QUIT":
                        await writer.WriteLineAsync("221 bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 OK");
                        break;
                }
            }
        }
        catch (Exception) { /* the test is over, or the client went */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}

/// <summary>
/// Owner's decision: an email the relay may have taken (it didn't answer in time, or the connection dropped once the
/// session began) is Unknown, not Failed, and isn't sent again by itself: sending it again could email everyone twice.
/// A clear refusal stays Failed. Every sender path treats Unknown the same way: the brief's send is settled by hand, a
/// reminder or an escalation step is marked as gone, and the run's note says to check whether it arrived.
/// </summary>
public class EmailOutcomeTests : IDisposable
{
    private readonly TestDb _db = new();

    public EmailOutcomeTests() =>
        (_db.User.UserId, _db.User.DisplayName, _db.User.Roles) = ("S-1-5-21-TEST-1", "Test Analyst", new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin });

    public void Dispose() => _db.Dispose();

    private static EmailSender Sender(int port) =>
        new(new StaticOptionsMonitor<EmailOptions>(new EmailOptions
        {
            Enabled = true, SmtpHost = "127.0.0.1", SmtpPort = port, EnableSsl = false, From = "tide@example.org", SmtpTimeoutSeconds = 5,
        }), NullLogger<EmailSender>.Instance, new OutboundCircuit());

    private static readonly OutgoingEmail Mail = new(["soc@example.org"], "s", "<p>h</p>", "t", []);

    private static EmailSendResult MayHaveGone => new(EmailSendStatus.Unknown, EmailSender.Dropped);

    // --- the sender --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARelayThatTakesTheDataAndThenNeverAnswers_IsUnknown_SayingToCheckBeforeSendingAgain()
    {
        using var relay = new MisbehavingSmtpServer(MisbehavingSmtpServer.Fault.HangAfterData);

        var result = await Sender(relay.Port).SendAsync(Mail);

        relay.DataReceived.Should().Be(1, "the relay had the whole message");
        result.Status.Should().Be(EmailSendStatus.Unknown);
        result.Error.Should().Contain("didn't answer within 5 seconds").And.Contain("Check whether it arrived before sending it again");
    }

    [Fact]
    public async Task AConnectionDroppedAfterTheData_IsUnknown()
    {
        using var relay = new MisbehavingSmtpServer(MisbehavingSmtpServer.Fault.DropAfterData);

        var result = await Sender(relay.Port).SendAsync(Mail);

        result.Status.Should().Be(EmailSendStatus.Unknown);
        result.Error.Should().Contain("dropped partway through").And.Contain("Check whether it arrived before sending it again");
    }

    [Theory]
    [InlineData(MisbehavingSmtpServer.Fault.RefuseSender)]
    [InlineData(MisbehavingSmtpServer.Fault.RefuseAtGreeting)]
    public async Task ARelayThatRefusesBeforeTheData_IsFailed(MisbehavingSmtpServer.Fault fault)
    {
        using var relay = new MisbehavingSmtpServer(fault);

        var result = await Sender(relay.Port).SendAsync(Mail);

        relay.DataReceived.Should().Be(0);
        result.Status.Should().Be(EmailSendStatus.Failed, "it said no: nothing went, so it's safe to send again");
    }

    [Fact]
    public async Task ARelayThatCantBeReached_IsFailed()
    {
        var relay = new MisbehavingSmtpServer(MisbehavingSmtpServer.Fault.RefuseSender);
        var port = relay.Port;
        relay.Dispose();

        (await Sender(port).SendAsync(Mail)).Status.Should().Be(EmailSendStatus.Failed, "no connection was made");
    }

    // --- the brief ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABriefSendThatMayHaveGone_IsLoggedUnknown_NotFailed_AndTheNextConfirmationSaysSo()
    {
        var day = new DateOnly(2026, 10, 26);
        var id = await _db.Articles().CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);
        var brief = await _db.Briefs().GetOrStartAsync(day);
        await _db.Briefs().AddArticleAsync(brief.Id, id);
        await _db.Briefs().MarkPresentedAsync(brief.Id);
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "everyone@example.org");
        using var relay = new MisbehavingSmtpServer(MisbehavingSmtpServer.Fault.DropAfterData);
        var svc = _db.Distribution(Sender(relay.Port));

        var send = await svc.SendAsync(await svc.PlanAsync(day, Edition.Public));

        send.Status.Should().Be(SendStatus.Unknown, "the relay may have taken it");
        send.Error.Should().Contain("Check whether it arrived before sending it again");
        (await svc.PlanAsync(day, Edition.Public)).LastOutcomeUnknown.Should().BeTrue("the next confirmation warns it may have gone");
    }

    [Fact]
    public async Task ATestCopyThatMayHaveGone_IsLoggedUnknown()
    {
        var day = new DateOnly(2026, 10, 26);
        await _db.Briefs().GetOrStartAsync(day);
        await using (var ctx = _db.Context())
        {
            ctx.Users.Add(new AppUser { Sid = _db.User.UserId, DisplayName = "Test Analyst", Email = "analyst@example.org", LastSeenUtc = DateTimeOffset.UtcNow });
            await ctx.SaveChangesAsync();
        }
        _db.Email.Result = MayHaveGone;

        (await _db.Distribution().SendTestAsync(day, Edition.Team)).Status.Should().Be(EmailSendStatus.Unknown);

        await using var check = _db.Context();
        (await check.BriefSends.AsNoTracking().SingleAsync()).Status.Should().Be(SendStatus.Unknown);
    }

    // --- the reminders -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AReminderThatMayHaveGone_IsMarked_NotSentAgainByItself_AndTheRunSaysToCheck()
    {
        var team = await _db.Teams().AddAsync("Vulnerability Management", null, "vm@example.org");
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch it", TeamId = team, DueDate = _db.Clock.Today.AddDays(-3) });
        _db.Email.Result = MayHaveGone;
        var log = new ReminderLog();

        await _db.Reminders(log: log).RunAsync(ReminderKind.Overdue);
        var run = log.Last(ReminderKind.Overdue)!;
        _db.Email.Result = new EmailSendResult(EmailSendStatus.Sent);
        await _db.Reminders(log: log).RunAsync(ReminderKind.Overdue);

        _db.Email.Sent.Should().HaveCount(1, "a reminder that may have gone isn't sent again by itself");
        run.Unsure.Should().Be(1);
        run.Note.Should().Be(ReminderService.UnsureNote(1)).And.Contain("check with the recipients before sending again");
    }

    [Fact]
    public async Task AnExternalReminderThatMayHaveGone_IsMarked_NotSentAgainByItself()
    {
        var clock = new OrgClock(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(2026, 11, 20, 14, 0, 0, TimeSpan.Zero)));
        var a = await DueTuesday();
        var options = new ReminderOptions { ExternalReminders = { Enabled = true } };
        _db.Email.Result = MayHaveGone;

        var pass = await _db.Reminders(options, clock).RunDueAsync();
        _db.Email.Result = new EmailSendResult(EmailSendStatus.Sent);
        await _db.Reminders(options, clock).RunDueAsync();

        _db.Email.Sent.Should().HaveCount(1);
        pass.Unsure.Should().ContainSingle().Which.Kind.Should().Be(ReminderKind.ExternalReminder, "Diagnostics names it");
        a.Should().NotBeNull();
    }

    [Fact]
    public async Task AnEscalationStepWhoseEmailMayHaveGone_Climbs_AndItsHistorySaysToCheck()
    {
        var clock = new OrgClock(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(2026, 11, 20, 14, 0, 0, TimeSpan.Zero)));
        await DueTuesday();
        var options = new ReminderOptions { Escalation = { Enabled = true, Steps = "before 2: remind" } };
        _db.Email.Result = MayHaveGone;

        var pass = await _db.Reminders(options, clock).RunDueAsync();
        _db.Email.Result = new EmailSendResult(EmailSendStatus.Sent);
        await _db.Reminders(options, clock).RunDueAsync();

        _db.Email.Sent.Should().HaveCount(1, "the step ran: it isn't tried again at the next poll");
        pass.Unsure.Should().ContainSingle().Which.Kind.Should().Be(ReminderKind.Escalation, "Diagnostics names it");
        await using var check = _db.Context();
        var history = await check.ActionEvents.AsNoTracking().Where(e => e.ByName == ReminderService.EscalatedBy).Select(e => e.Text).ToListAsync();
        history.Should().ContainSingle().Which.Should().Contain("reminded Network Engineering at network@network.example, though it may not have arrived")
            .And.Contain("check before sending again");
    }

    [Fact]
    public async Task AHandOffEmailThatMayHaveGone_CountsAsEmailed()
    {
        var from = await _db.Teams().AddAsync("Applications", null, null);
        var to = await _db.Teams().AddAsync("Network", null, "network@example.org");
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = "Open the port", TeamId = from });
        _db.Email.Result = MayHaveGone;

        (await _db.Handoffs().HandOffAsync((await _db.Actions().GetAsync(number))!.Action.Id, to, "Theirs"))
            .Should().Be(1, "the relay most likely took it: the page says they've been emailed, not that they weren't");
    }

    // An external team's action due Tuesday Nov 24 2026: Friday Nov 20 is two business days before it.
    private async Task<ActionItem> DueTuesday()
    {
        var team = await _db.Teams().AddExternalAsync(new ExternalTeamForm { Name = "Network Engineering", ContactEmail = "network@network.example" });
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = "Block the domains", TeamId = team, Priority = Priority.P3 });
        await using var ctx = _db.Context();
        var row = await ctx.Actions.SingleAsync(x => x.Number == number);
        row.DueDate = new DateOnly(2026, 11, 24);
        await ctx.SaveChangesAsync();
        return row;
    }
}
