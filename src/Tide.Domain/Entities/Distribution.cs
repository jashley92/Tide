using Tide.Domain.Common;
using Tide.Domain.Enums;

namespace Tide.Domain.Entities;

/// <summary>
/// One send of a brief's edition by email: who sent it, to whom, and a frozen copy of exactly what went (the email HTML,
/// a plain web rendering of the same content, and copies of its images). The archive shows the frozen copy, never a
/// fresh rendering, so a later edit to an article can't change what a reader was sent. A test send to oneself is logged
/// too, without the copy.
/// </summary>
public class BriefSend : Entity
{
    public Guid BriefId { get; set; }
    public DateOnly BriefDate { get; set; }
    public Edition Edition { get; set; }

    /// <summary>A test copy to the sender only: logged, never archived, and it doesn't mark the brief sent.</summary>
    public bool IsTest { get; set; }

    /// <summary>The ceiling the edition was cut at, and the highest marking it carried.</summary>
    public TlpLevel Ceiling { get; set; }
    public TlpLevel Marking { get; set; }

    public string Subject { get; set; } = string.Empty;

    /// <summary>The addresses it went to, separated by ';'.</summary>
    public string Recipients { get; set; } = string.Empty;
    public int RecipientCount { get; set; }

    public DateTimeOffset SentAtUtc { get; set; }
    public string SentById { get; set; } = string.Empty;
    public string SentByName { get; set; } = string.Empty;

    public SendStatus Status { get; set; } = SendStatus.Sending;

    /// <summary>When its outcome was recorded after the relay answered, or settled by someone; null when known at once.</summary>
    public DateTimeOffset? OutcomeAtUtc { get; set; }

    /// <summary>Why the relay didn't take it (no addresses or message text from the relay).</summary>
    public string? Error { get; set; }

    /// <summary>The email as sent, its images referenced as <c>cid:</c>. Null for a test send.</summary>
    public string? EmailHtml { get; set; }

    /// <summary>The same content as plain semantic HTML for the archive page, its images referenced as <c>cid:</c>.</summary>
    public string? WebHtml { get; set; }

    public List<BriefSendImage> Images { get; set; } = [];
}

/// <summary>
/// An image in a frozen send: a copy of the screenshot's bytes (its own <see cref="StoredFile"/>), so removing the
/// screenshot from the article later leaves the archive whole.
/// </summary>
public class BriefSendImage : Entity
{
    public Guid SendId { get; set; }

    /// <summary>The Content-ID the HTML refers to (<c>cid:{ContentId}</c>).</summary>
    public string ContentId { get; set; } = string.Empty;

    /// <summary>The frozen copy's file.</summary>
    public Guid FileId { get; set; }
    public StoredFile? File { get; set; }

    /// <summary>The screenshot it was copied from (it may since have been removed).</summary>
    public Guid SourceFileId { get; set; }
    public string? Caption { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// A correction of one real send that the relay took (ADR 0015): a short email of its own to the original send's
/// recipients, saying what changed in the articles it names. Frozen like a send (its email and web copy are stored before
/// the relay is asked, and never rendered again) and logged with its outcome. It carries no images. A TLP slip
/// (<see cref="IsTlpSlip"/>) is the kind for content that went to an audience it shouldn't have: it carries no diff, and
/// once it's confirmed (frozen, whatever becomes of its email) the corrected company-wide send is withdrawn from the
/// published briefs (the row itself is kept).
/// </summary>
public class BriefCorrection : Entity
{
    /// <summary>The send it corrects.</summary>
    public Guid CorrectsSendId { get; set; }
    public BriefSend? CorrectsSend { get; set; }

    public Guid BriefId { get; set; }
    public DateOnly BriefDate { get; set; }
    public Edition Edition { get; set; }

    /// <summary>Content went above its audience's marking: tell the recipients, and withdraw the original.</summary>
    public bool IsTlpSlip { get; set; }

    /// <summary>What the writer said changed (required for a correction, optional for a slip).</summary>
    public string? Note { get; set; }

    /// <summary>The articles it's about, separated by ';'.</summary>
    public string ArticleIds { get; set; } = string.Empty;

    /// <summary>The ceiling the "now" side was cut at (the lower of the send's and the edition's then), and its marking.</summary>
    public TlpLevel Ceiling { get; set; }
    public TlpLevel Marking { get; set; }

    public string Subject { get; set; } = string.Empty;

    /// <summary>The addresses it went to (the original send's), separated by ';'.</summary>
    public string Recipients { get; set; } = string.Empty;
    public int RecipientCount { get; set; }

    public DateTimeOffset SentAtUtc { get; set; }
    public string SentById { get; set; } = string.Empty;
    public string SentByName { get; set; } = string.Empty;

    public SendStatus Status { get; set; } = SendStatus.Sending;

    /// <summary>Why the relay didn't take it.</summary>
    public string? Error { get; set; }

    /// <summary>The email as sent.</summary>
    public string EmailHtml { get; set; } = string.Empty;

    /// <summary>The same content as plain semantic HTML, for the published briefs page.</summary>
    public string WebHtml { get; set; } = string.Empty;
}
