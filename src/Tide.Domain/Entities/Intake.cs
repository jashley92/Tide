using Tide.Domain.Common;
using Tide.Domain.Enums;

namespace Tide.Domain.Entities;

/// <summary>
/// Somewhere items come from: an RSS or Atom feed, or the CISA Known Exploited Vulnerabilities catalog. Admin-managed;
/// polled on its own interval while enabled. Retired, never deleted, once it has items.
/// </summary>
public class Source : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public SourceKind Kind { get; set; } = SourceKind.Feed;
    public string Url { get; set; } = string.Empty;

    /// <summary>The marking an article promoted from this source starts with.</summary>
    public TlpLevel DefaultTlp { get; set; } = TlpLevel.Clear;

    /// <summary>How far the source is trusted (Admiralty scale, A to F).</summary>
    public SourceReliability Reliability { get; set; } = SourceReliability.F;

    /// <summary>What its terms allow ("Members only: don't forward outside the company").</summary>
    public string? LicenseNote { get; set; }

    public int PollMinutes { get; set; } = 60;
    public bool Enabled { get; set; } = true;
    public bool IsRetired { get; set; }

    /// <summary>Moved by each save of its form, and by pausing, retiring or bringing it back (the form carries "Poll it"),
    /// so a form opened before another administrator's change can't save over it. A poll's bookkeeping doesn't move it.</summary>
    public Guid EditVersion { get; set; } = Guid.NewGuid();

    public const int MaxBodySelector = 200;

    /// <summary>Whether an article's page from this source may be fetched for drafting, and by whom.</summary>
    public ArticleFetch ArticleFetch { get; set; } = ArticleFetch.TideOnly;

    /// <summary>A CSS selector for the article body ("div.article-content"), for a site the automatic finder gets
    /// wrong. Null: found automatically.</summary>
    public string? ArticleBodySelector { get; set; }

    public const int MaxRemoveSelector = 500;

    /// <summary>A CSS selector for what to take out of this site's article pages besides the adverts TIDE finds itself
    /// (".promo-inline, div[data-widget=newsletter]"). Null: nothing more.</summary>
    public string? ArticleRemoveSelector { get; set; }

    /// <summary>A feed's tag filter, one tag per line, matched against the item's own tags ignoring case. With
    /// <see cref="OnlyCategories"/>, only items carrying one of them are taken in; an item carrying one of
    /// <see cref="SkipCategories"/> never is. Null: no filter.</summary>
    public string? OnlyCategories { get; set; }
    public string? SkipCategories { get; set; }

    /// <summary>The title half of the filter, one word or phrase per line, matched as whole words ignoring case. An item
    /// passes the "only" side with an only-tag or an only-word in its title; a skip-tag or skip-word leaves it out.</summary>
    public string? OnlyTitleWords { get; set; }
    public string? SkipTitleWords { get; set; }

    /// <summary>The tags the last read of the feed carried, most used first, one per line (to choose a filter from).</summary>
    public string? SeenCategories { get; set; }

    /// <summary>Items the tag filter left out on the last read.</summary>
    public int LastFilteredOut { get; set; }

    /// <summary>The last poll: when, how it went, and the validators for a conditional fetch next time.</summary>
    public DateTimeOffset? LastPolledAtUtc { get; set; }
    public DateTimeOffset? LastSuccessAtUtc { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int LastNewItems { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
}

/// <summary>
/// One item from a source, waiting in the triage inbox: shortlisted, dismissed, marked as a duplicate, or promoted to an
/// article. Kept by its canonical address, so the same story from two feeds (or polled twice) is one item.
/// </summary>
public class FeedItem : Entity
{
    public const int MaxExcerpt = 2000;

    public Guid SourceId { get; set; }
    public Source? Source { get; set; }

    /// <summary>The item's address with tracking parameters stripped, and its SHA-256 (the unique key).</summary>
    public string Url { get; set; } = string.Empty;
    public string UrlHash { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>The title reduced to its words, for spotting the same story under another address.</summary>
    public string TitleKey { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public DateTimeOffset FetchedAtUtc { get; set; }

    /// <summary>The summary as plain text (feed HTML is never rendered).</summary>
    public string? Excerpt { get; set; }

    /// <summary>CVE ids found in the title and summary, comma-separated.</summary>
    public string? Cves { get; set; }

    public FeedItemStatus Status { get; set; } = FeedItemStatus.New;
    public string? DismissReason { get; set; }

    /// <summary>The item this one repeats (marked by an analyst), or one that looks like it (found on fetch).</summary>
    public Guid? DuplicateOfId { get; set; }
    public Guid? SimilarToId { get; set; }

    /// <summary>The article it was promoted to.</summary>
    public Guid? ArticleId { get; set; }

    public string? StatusByName { get; set; }
    public DateTimeOffset? StatusAtUtc { get; set; }

    /// <summary>The longest name kept for who decided (<see cref="StatusByName"/>, <see cref="DismissKeptByName"/>).</summary>
    public const int MaxName = 256;

    /// <summary>Who said a dismissal the missed-stories check questioned was right, and when: it's not questioned again.
    /// It counts only while it's newer than the item's last decision (<see cref="DismissKept"/>), so any later decision,
    /// whichever way it's made, outdates it without anything having to clear it.</summary>
    public string? DismissKeptByName { get; set; }
    public DateTimeOffset? DismissKeptAtUtc { get; set; }

    /// <summary>Whether the item is dismissed and someone has said that was right since (see
    /// <see cref="DismissKeptAtUtc"/>). IntakeService.MissedAsync applies the same rule in its query.</summary>
    public bool DismissKept => Status == FeedItemStatus.Dismissed && DismissKeptAtUtc is { } kept && (StatusAtUtc is not { } at || kept >= at);

    public IEnumerable<string> CveList => (Cves ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// An item's article text as read in triage (EP-76): fetched from its page when an analyst opens the reading pane, and
/// kept for <c>Intake:ReadingDays</c> so a colleague, and the editor after promoting, don't fetch it again. For reading
/// inside TIDE only: it's never sent, exported or shown to readers, and it goes with its item.
/// </summary>
public class FeedItemText
{
    public const int MaxHow = 100;

    public Guid FeedItemId { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>How the body was found ("the page's article markup").</summary>
    public string How { get; set; } = string.Empty;

    public DateTimeOffset FetchedAtUtc { get; set; }
}

/// <summary>
/// One report of a group someone said isn't one event ("Not the same: split", EP-84): a row per report, the split's
/// reports sharing a <see cref="Batch"/>. Two items in one batch never group again, for anyone; each can still group
/// with other reports, and an item can be in several batches. Undo deletes the batch. It goes with its item when that's
/// pruned.
/// </summary>
public class TriageSplit : Entity
{
    public const int MaxName = 256;

    /// <summary>The split: every report of the group split at once.</summary>
    public Guid Batch { get; set; }

    public Guid ItemId { get; set; }

    public DateTimeOffset SplitAtUtc { get; set; }

    /// <summary>Who split it: their user id, and their name as it was then.</summary>
    public string SplitBy { get; set; } = string.Empty;
    public string SplitByName { get; set; } = string.Empty;
}

/// <summary>
/// A threat story: a campaign, a vulnerability, an actor's run of activity, followed across days. Its timeline is the
/// articles linked to it, oldest first.
/// </summary>
public class Story : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public StoryStatus Status { get; set; } = StoryStatus.Active;
}

/// <summary>
/// What's known about a CVE from public sources: CVSS from NVD, the EPSS exploit-prediction score from FIRST, and
/// whether CISA lists it as known exploited (and by when federal agencies must fix it). Refreshed by the enrichment job.
/// </summary>
public class CveRecord
{
    /// <summary>"CVE-2026-12345".</summary>
    public string CveId { get; set; } = string.Empty;

    public string? Description { get; set; }
    public decimal? CvssScore { get; set; }
    public string? CvssSeverity { get; set; }
    public string? CvssVector { get; set; }
    public DateTimeOffset? NvdPublishedAtUtc { get; set; }

    public decimal? EpssScore { get; set; }
    public decimal? EpssPercentile { get; set; }

    public bool IsKev { get; set; }
    public DateOnly? KevAddedDate { get; set; }
    public DateOnly? KevDueDate { get; set; }
    public bool KevRansomware { get; set; }
    public string? Vendor { get; set; }
    public string? Product { get; set; }

    public DateTimeOffset? NvdCheckedAtUtc { get; set; }
    public DateTimeOffset? EpssCheckedAtUtc { get; set; }
    public DateTimeOffset? KevCheckedAtUtc { get; set; }
}
