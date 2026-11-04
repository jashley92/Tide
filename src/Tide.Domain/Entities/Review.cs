using Tide.Domain.Common;

namespace Tide.Domain.Entities;

/// <summary>Where a review stands. Stored; append, never renumber.</summary>
public enum ReviewStatus
{
    /// <summary>Asked for, waiting for the reviewer.</summary>
    Asked = 0,

    Approved = 1,

    /// <summary>The reviewer asked for changes.</summary>
    ChangesAsked = 2,

    /// <summary>Replaced by a later request, or taken back by the author.</summary>
    Withdrawn = 3,
}

/// <summary>
/// A colleague's review of an article before it's sent (EP-80): who asked whom, the outcome, the checklist, and the
/// version of the article's content the reviewer read, so a change after approval shows the review is out of date. Optional
/// unless an administrator requires it (<c>Review:Required</c>). Kept with the article's history.
/// </summary>
public class ArticleReview : Entity
{
    public const int MaxNote = 500;
    public const int MaxChecks = 200;

    public Guid ArticleId { get; set; }

    public string RequestedById { get; set; } = string.Empty;
    public string RequestedByName { get; set; } = string.Empty;
    public DateTimeOffset RequestedAtUtc { get; set; }

    /// <summary>What the author asked the reviewer to look at, if anything.</summary>
    public string? Note { get; set; }

    public string ReviewerId { get; set; } = string.Empty;
    public string ReviewerName { get; set; } = string.Empty;

    public ReviewStatus Status { get; set; } = ReviewStatus.Asked;
    public DateTimeOffset? DecidedAtUtc { get; set; }

    /// <summary>
    /// The content version of the article as the reviewer read it (ReviewService.ContentVersion, a fingerprint of its
    /// words, markings, bullets and screenshots), set when the review is decided. Not the article's
    /// <see cref="Article.EditVersion"/>, which also moves on a status change.
    /// </summary>
    public Guid? ReviewedVersion { get; set; }

    /// <summary>The checklist as the reviewer left it: each item's key and whether it passed ("tlp:1;sources:0").</summary>
    public string? Checks { get; set; }

    public List<ReviewComment> Comments { get; set; } = [];
}

/// <summary>A comment on one part of the article under review, with the words it's about; the author resolves it.</summary>
public class ReviewComment : Entity
{
    public const int MaxField = 40;
    public const int MaxQuote = 300;
    public const int MaxText = 2000;

    public Guid ReviewId { get; set; }

    /// <summary>The part it's about: "title", "bluf", "bullet:2", "why", "actions" or "general".</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>The words selected when it was written, if any.</summary>
    public string? Quote { get; set; }

    public string Text { get; set; } = string.Empty;
    public string AuthorId { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public string? ResolvedByName { get; set; }
}
