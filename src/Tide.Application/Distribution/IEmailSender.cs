namespace Tide.Application.Distribution;

/// <summary>An image carried inside an email and referenced from its HTML as <c>cid:{ContentId}</c>.</summary>
public sealed record InlineImage(string ContentId, string FileName, string ContentType, byte[] Bytes);

/// <summary>One email to send: an HTML body with a plain-text alternative, and its inline images. <paramref name="ReplyTo"/>, when
/// given, is where a reply goes instead of the sender (an external team's reminder, answered by whoever follows its work).</summary>
public sealed record OutgoingEmail(IReadOnlyList<string> To, string Subject, string Html, string Text, IReadOnlyList<InlineImage> Images,
    IReadOnlyList<string>? ReplyTo = null)
{
    /// <summary>The template it was made from (<see cref="EmailTemplate.Id"/>), so its copies go with it; null for one
    /// that isn't a template's (a brief's edition, a correction).</summary>
    public string? Template { get; init; }

    /// <summary>Copies: seen by every recipient (CC), or not (BCC). Added by <see cref="CopyingEmailSender"/> from the
    /// template's and the archive's copy addresses.</summary>
    public IReadOnlyList<string> Cc { get; init; } = [];
    public IReadOnlyList<string> Bcc { get; init; } = [];
}

public enum EmailSendStatus
{
    /// <summary>The relay took it (for some recipients at least).</summary>
    Sent = 0,

    /// <summary>Email is turned off (<c>Email:Enabled</c>); nothing was sent.</summary>
    Disabled = 1,

    /// <summary>The relay clearly didn't take it (it refused the recipient, the sign-in or the sender before any data, or
    /// it couldn't be reached at all); <see cref="EmailSendResult.Error"/> says why. Safe to send again.</summary>
    Failed = 2,

    /// <summary>The relay may have taken it: it didn't answer in time, or the connection dropped once the session had
    /// begun. It isn't sent again by itself, as that could email everyone twice; <see cref="EmailSendResult.Error"/> says to
    /// check whether it arrived first.</summary>
    Unknown = 3,
}

/// <summary>What became of a send. <see cref="Error"/> is safe to show: it never echoes the relay's reply, which can
/// carry addresses.</summary>
public sealed record EmailSendResult(EmailSendStatus Status, string? Error = null, int Refused = 0)
{
    /// <summary>Copy addresses (CC or BCC) the relay refused. They never count against the email itself: it went to
    /// its recipients.</summary>
    public int RefusedCopies { get; init; }

    /// <summary>Whether it went or may have: either way it isn't sent again by itself.</summary>
    public bool MayHaveGone => Status is EmailSendStatus.Sent or EmailSendStatus.Unknown;
}

/// <summary>Sends email through the organization's SMTP relay (configured in <c>Email:*</c>).</summary>
public interface IEmailSender
{
    /// <summary>Whether email is turned on.</summary>
    bool Enabled { get; }

    Task<EmailSendResult> SendAsync(OutgoingEmail email, CancellationToken ct = default);
}
