using System.Net.Mail;
using System.Net.Mime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tide.Application.Distribution;

namespace Tide.Infrastructure.Notifications;

/// <summary>
/// The one email path, kept to what a brief send needs. It reads its options on every send,
/// so a corrected relay setting applies without a restart. A message is multipart/alternative (plain text, then HTML),
/// and the HTML part carries the screenshots as linked resources (<c>cid:</c>), which Outlook shows inline without
/// fetching anything. A send is watched by the analyst who made it, so nothing is held and retried behind their back:
/// a failure is returned for them to see. A relay that stops answering is backed off (<see cref="OutboundCircuit"/>) so
/// the next try fails fast. The log gets counts and SMTP status codes, never addresses, subjects or the relay's reply.
/// </summary>
public sealed class EmailSender(IOptionsMonitor<EmailOptions> options, ILogger<EmailSender> log, OutboundCircuit circuit) : IEmailSender
{
    public bool Enabled => options.CurrentValue.Enabled;

    public static string Target(EmailOptions o) => string.IsNullOrWhiteSpace(o.PickupDirectory) ? $"smtp:{o.SmtpHost}:{o.SmtpPort}" : "pickup";

    public async Task<EmailSendResult> SendAsync(OutgoingEmail email, CancellationToken ct = default)
    {
        var o = options.CurrentValue;
        if (!o.Enabled)
        {
            log.LogInformation("Email delivery disabled; not sending to {RecipientCount} recipient(s).", email.To.Count);
            return new EmailSendResult(EmailSendStatus.Disabled);
        }
        if (o.SignInProblem() is { } problem) return new EmailSendResult(EmailSendStatus.Failed, problem);
        if (string.IsNullOrWhiteSpace(o.PickupDirectory) && string.IsNullOrWhiteSpace(o.SmtpHost))
            return new EmailSendResult(EmailSendStatus.Failed, "No mail relay is set (Email:SmtpHost).");

        var target = Target(o);
        if (!circuit.TryEnter(target))
        {
            var wait = circuit.RetryIn(target);
            return new EmailSendResult(EmailSendStatus.Failed,
                $"The mail relay hasn't been answering; try again in {Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes))} minute(s).");
        }

        MailMessage message;
        int valid;
        try { (message, valid) = Compose(o, email); }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            circuit.Release(target);   // nothing was sent
            return new EmailSendResult(EmailSendStatus.Failed, ex.Message);
        }

        SmtpClient client;
        try { client = SmtpClientFactory.Create(o); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The pickup folder can't be made or used: nothing was handed to anyone, so it's a failure, not an unknown outcome.
            message.Dispose();
            circuit.Release(target);
            log.LogError("The email pickup directory can't be used: {Failure}.", ex.GetType().Name);
            return new EmailSendResult(EmailSendStatus.Failed, "The email pickup folder (Email:PickupDirectory) can't be created or written to.");
        }

        using (message)
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(o.Timeout);
            try
            {
                await client.SendMailAsync(message, timeout.Token);
                circuit.Succeeded(target);
                log.LogInformation("Sent an email to {RecipientCount} recipient(s) with {Images} inline image(s).", valid, email.Images.Count);
                return new EmailSendResult(EmailSendStatus.Sent);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                circuit.Failed(target);
                log.LogError("The mail relay didn't accept a message within {Seconds} seconds.", o.Timeout.TotalSeconds);
                return new EmailSendResult(EmailSendStatus.Failed, $"The mail relay didn't accept the message within {o.Timeout.TotalSeconds:0} seconds.");
            }
            catch (OperationCanceledException)
            {
                circuit.Release(target);
                throw;
            }
            catch (SmtpFailedRecipientsException ex) when (ex.InnerExceptions.Length < valid)
            {
                // The relay took it for the others; sending again would repeat it to all of them.
                circuit.Succeeded(target);
                log.LogWarning("An email reached {Delivered} of {RecipientCount} recipient(s); {Refused} were refused ({Failure}).",
                    valid - ex.InnerExceptions.Length, valid, ex.InnerExceptions.Length, Failure(ex));
                return new EmailSendResult(EmailSendStatus.Sent, Refused: ex.InnerExceptions.Length);
            }
            catch (SmtpFailedRecipientException ex) when (ex is not SmtpFailedRecipientsException && valid > 1)
            {
                // When exactly one of several addresses is refused, SmtpClient throws the singular exception after the
                // others got it: the same partial delivery.
                circuit.Succeeded(target);
                log.LogWarning("An email reached {Delivered} of {RecipientCount} recipient(s); 1 was refused ({Failure}).", valid - 1, valid, Failure(ex));
                return new EmailSendResult(EmailSendStatus.Sent, Refused: 1);
            }
            catch (Exception ex) when (ex is SmtpException or IOException or InvalidOperationException or System.Net.Sockets.SocketException)
            {
                if (Unreachable(ex)) circuit.Failed(target);
                else circuit.Succeeded(target);   // it answered (with a refusal), so it isn't backed off
                log.LogError("Failed to send an email to {RecipientCount} recipient(s): {Failure}.", valid, Failure(ex));
                return new EmailSendResult(EmailSendStatus.Failed, Explain(ex));
            }
        }
    }

    // A MailMessage for the addresses that parse: text first (the fallback), then HTML with its images linked in.
    private (MailMessage Message, int Valid) Compose(EmailOptions o, OutgoingEmail email)
    {
        var message = new MailMessage();
        try
        {
            message.From = new MailAddress(o.From, o.FromName);
            message.Subject = EmailComposer.SingleLine(email.Subject);
            var bad = 0;
            foreach (var addr in email.To)
            {
                if (MailAddress.TryCreate(addr, out var parsed)) message.To.Add(parsed);
                else bad++;
            }
            if (message.To.Count == 0) throw new FormatException("None of the recipient addresses is a valid email address.");
            if (bad > 0) log.LogWarning("Skipped {Bad} recipient address(es) that aren't valid; sending to {Good}.", bad, message.To.Count);
            // Out-of-office and other automatic replies to a daily brief are noise.
            message.Headers.Add("X-Auto-Response-Suppress", "OOF, AutoReply");
            message.Headers.Add("Auto-Submitted", "auto-generated");

            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(email.Text, System.Text.Encoding.UTF8, MediaTypeNames.Text.Plain));
            var html = AlternateView.CreateAlternateViewFromString(email.Html, System.Text.Encoding.UTF8, MediaTypeNames.Text.Html);
            foreach (var img in email.Images)
            {
                var resource = new LinkedResource(new MemoryStream(img.Bytes, writable: false), img.ContentType)
                {
                    ContentId = img.ContentId,
                    TransferEncoding = TransferEncoding.Base64,
                };
                resource.ContentType.Name = img.FileName;
                html.LinkedResources.Add(resource);
            }
            message.AlternateViews.Add(html);
            return (message, message.To.Count);
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    /// <summary>The relay couldn't be reached, or dropped the session (as opposed to answering with a refusal).</summary>
    internal static bool Unreachable(Exception ex) => ex switch
    {
        SmtpFailedRecipientException => false,
        SmtpException s => s.StatusCode is SmtpStatusCode.GeneralFailure or SmtpStatusCode.ServiceNotAvailable
                           || s.InnerException is IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException,
        _ => ex is IOException or System.Net.Sockets.SocketException,
    };

    // What the analyst is told: what kind of failure, never the relay's own words (they can echo addresses).
    private static string Explain(Exception ex)
    {
        if (Unreachable(ex)) return "The mail relay couldn't be reached. Check Email:SmtpHost and Email:SmtpPort, or try again shortly.";
        var code = ex switch
        {
            SmtpFailedRecipientsException { InnerExceptions.Length: > 0 } many => (int)many.InnerExceptions[0].StatusCode,
            SmtpException s => (int)s.StatusCode,
            _ => 0,
        };
        return code switch
        {
            530 or 534 or 535 or 538 => $"The mail relay refused the sign-in (SMTP {code}). Check Email:SmtpUsername and its password, or Email:SmtpUseDefaultCredentials.",
            >= 400 and < 500 => $"The mail relay said to try again later (SMTP {code}).",
            > 0 => $"The mail relay refused the message (SMTP {code}).",
            _ => "The mail relay didn't take the message.",
        };
    }

    /// <summary>For the log: the exception's type and SMTP status, never its message (which carries the relay's reply).</summary>
    internal static string Failure(Exception ex)
    {
        var text = ex switch
        {
            SmtpFailedRecipientsException many => $"{ex.GetType().Name} {string.Join("/", many.InnerExceptions.Select(r => (int)r.StatusCode).Distinct())}",
            SmtpException s => $"{ex.GetType().Name} {(int)s.StatusCode} {s.StatusCode}",
            _ => ex.GetType().Name,
        };
        var root = ex.GetBaseException();
        return ReferenceEquals(root, ex) ? text : $"{text}, from {root.GetType().Name}";
    }
}
