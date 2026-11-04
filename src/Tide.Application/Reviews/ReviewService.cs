using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Org;
using Tide.Application.Rotation;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Reviews;

/// <summary>When review is required before a send (EP-80). Off by default: review is optional.</summary>
public enum ReviewRule
{
    Off = 0,
    AmberAndAbove = 1,
    Threats = 2,
    AmberOrThreats = 3,
}

/// <summary>One of the reviewer's checklist items, by key, with how it reads.</summary>
public sealed record ReviewCheck(string Key, string Text);

/// <summary>A colleague who can review: an analyst other than the author.</summary>
public sealed record Reviewer(string Id, string Name, bool Presenting);

/// <summary>An article's review as the editor and the review page show it: the current one (if any) and whether the article changed since.</summary>
public sealed record ReviewState(ArticleReview? Review, bool OutOfDate)
{
    public bool Approved => Review is { Status: ReviewStatus.Approved } && !OutOfDate;
    public int OpenComments => Review?.Comments.Count(c => c.ResolvedAtUtc is null) ?? 0;
}

/// <summary>A review asked of the current user, for Today.</summary>
public sealed record ReviewAsked(Guid ReviewId, Guid ArticleId, string Title, string RequestedByName, DateTimeOffset RequestedAtUtc);

/// <summary>An article on a brief that hasn't a current approval, and whether the rule requires one.</summary>
public sealed record ReviewGap(Guid ArticleId, string Title, string Why, bool Required);

/// <summary>
/// A second pair of eyes before send (EP-80). An author asks a colleague; the reviewer reads the article as each edition
/// will show it, comments on its parts, works through a short checklist, and approves or asks for changes; the author
/// resolves each comment. A change to what the article says after approval puts the review out of date
/// (<see cref="ContentVersion"/>). Review is optional unless an administrator requires it (<see cref="ReviewRule"/>);
/// then the send waits for it.
/// </summary>
public sealed class ReviewService(IAppDbContextFactory dbs, ICurrentUser user, RotationService rotation, OrgClock clock, TimeProvider time)
{
    public const string RuleKey = "Review:Required";

    /// <summary>The checklist, in order.</summary>
    public static readonly IReadOnlyList<ReviewCheck> Checklist =
    [
        new("tlp", "TLP is right for every part"),
        new("sources", "Every claim has a source"),
        new("internal", "Nothing internal in the company-wide edition"),
        new("bluf", "The bottom line stands on its own"),
    ];

    /// <summary>The parts of an article a comment can be about, by key, with their labels.</summary>
    public static IReadOnlyList<(string Key, string Label)> Fields(Article a) =>
    [
        .. new[] { "general", "title", "bluf" }.Concat(a.Bullets.OrderBy(b => b.SortOrder).Select((_, i) => $"bullet:{i + 1}")).Concat(["why", "actions"])
            .Select(k => (k, FieldLabel(k))),
    ];

    /// <summary>A field key's label ("bullet:2" → "Bullet 2").</summary>
    public static string FieldLabel(string key) => key switch
    {
        "general" => "The whole article",
        "title" => "Title",
        "bluf" => "Bottom line",
        "why" => "Why it matters",
        "actions" => "Recommended actions",
        _ when key.StartsWith("bullet:", StringComparison.Ordinal) => "Bullet " + key[7..],
        _ => key,
    };

    // --- the rule ---------------------------------------------------------------------------------------------------

    public async Task<ReviewRule> RuleAsync(CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        return await RuleAsync(db, ct);
    }

    private static async Task<ReviewRule> RuleAsync(IAppDbContext db, CancellationToken ct) =>
        Enum.TryParse<ReviewRule>(await db.Settings.AsNoTracking().Where(s => s.Key == RuleKey).Select(s => s.Value).FirstOrDefaultAsync(ct), out var r)
        && Enum.IsDefined(r) ? r : ReviewRule.Off;

    public async Task SaveRuleAsync(ReviewRule rule, CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can change when review is required.");
        if (!Enum.IsDefined(rule)) throw new ArgumentException("That isn't a review rule.");
        await using var db = await dbs.CreateAsync(ct);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == RuleKey, ct);
        if (row is null) db.Settings.Add(row = new AppSetting { Key = RuleKey });
        row.Value = rule.ToString();
        row.UpdatedAtUtc = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Whether <paramref name="rule"/> requires review for an article marked <paramref name="marking"/> of <paramref name="classification"/>.</summary>
    public static bool Requires(ReviewRule rule, TlpLevel marking, Classification classification) => rule switch
    {
        ReviewRule.AmberAndAbove => marking >= TlpLevel.Amber,
        ReviewRule.Threats => classification == Classification.Threat,
        ReviewRule.AmberOrThreats => marking >= TlpLevel.Amber || classification == Classification.Threat,
        _ => false,
    };

    /// <summary>The highest TLP of anything an article holds: itself, why it matters, its bullets and screenshots.</summary>
    public static TlpLevel Marking(Article a) =>
        new[] { a.Tlp, string.IsNullOrWhiteSpace(a.WhyItMatters) ? TlpLevel.Clear : a.WhyItMattersTlp }
            .Concat(a.Bullets.Select(b => b.Tlp)).Concat(a.Screenshots.Select(s => s.Tlp)).Max();

    /// <summary>
    /// A fingerprint of what the article says, as the editions carry it: its words and markings, its bullets, its
    /// screenshots and their captions, and its indicators, CVEs and techniques (all loaded). A review records it, so only
    /// a change to those puts an approval out of date. The article's <see cref="Article.EditVersion"/> isn't it: that
    /// also moves when the article is only marked Ready, and stays put when a screenshot is added or removed.
    /// </summary>
    public static Guid ContentVersion(Article a)
    {
        var content = JsonSerializer.Serialize(new object?[]
        {
            a.Title, a.SourceName, a.SourceUrl, a.Tlp, a.Classification, a.Priority, a.Bluf,
            a.WhyItMatters, a.WhyItMattersTlp, a.RecommendedActions,
            a.Bullets.OrderBy(b => b.SortOrder).Select(b => new object[] { b.Text, b.Tlp }),
            a.Screenshots.OrderBy(s => s.SortOrder).Select(s => new object?[] { s.FileId, s.Caption, s.Tlp }),
            a.Indicators.Select(i => $"{i.Type}:{i.Value}:{i.Note}").Order(StringComparer.Ordinal),
            a.Cves.Select(c => c.CveId).Order(StringComparer.Ordinal),
            a.Techniques.Select(t => t.TechniqueId).Order(StringComparer.Ordinal),
        });
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(content)).AsSpan(0, 16));
    }

    // Whether a review's recorded version is the article as it is now. A review decided on 0.1.0-alpha.9 recorded the
    // article's EditVersion instead of its content version; that one still holds while the article is unsaved since and
    // no screenshot was added to it or removed from it since the decision (a screenshot doesn't move the EditVersion; the
    // article keeps when one last did). Undecided, it doesn't hold.
    private static bool Unchanged(ArticleReview r, Article a)
    {
        if (r.ReviewedVersion is not { } reviewed) return false;
        if (reviewed == ContentVersion(a)) return true;
        if (reviewed != a.EditVersion || r.DecidedAtUtc is not { } decided) return false;
        return a.ScreenshotsChangedAtUtc is not { } changed || changed < decided;
    }

    // Articles with every part their content version covers.
    private static IQueryable<Article> WithContent(IAppDbContext db) => db.Articles.AsNoTracking()
        .Include(a => a.Bullets).Include(a => a.Screenshots).Include(a => a.Indicators).Include(a => a.Cves).Include(a => a.Techniques)
        .AsSplitQuery();

    // --- asking -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Who can review <paramref name="articleId"/>: everyone who can write articles (Analysts and Admins) but its author
    /// and you, this week's presenter first.
    /// </summary>
    public async Task<IReadOnlyList<Reviewer>> ReviewersAsync(Guid articleId, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var author = await db.Articles.AsNoTracking().Where(a => a.Id == articleId).Select(a => a.AuthorId).FirstOrDefaultAsync(ct)
                     ?? throw new KeyNotFoundException("That article no longer exists.");
        var presenter = await rotation.PresenterAsync(clock.CurrentBriefDate, ct);
        var users = new Dictionary<string, AppUser>();
        foreach (var role in Writers)
            foreach (var u in await db.Users.AsNoTracking().Where(u => u.RolesCsv.Contains(role)).ToListAsync(ct))
                users.TryAdd(u.Sid, u);
        return users.Values.Where(u => u.Sid != author && u.Sid != user.UserId && u.DisplayName.Length > 0 && CanWrite(u.RolesCsv))
            .Select(u => new Reviewer(u.Sid, u.DisplayName, presenter?.Id == u.Sid))
            .OrderByDescending(r => r.Presenting).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // The roles that can write articles, so review them; a user's roles are as last seen at sign-in.
    private static readonly string[] Writers = RoleDefinitions.SystemRoles
        .Where(r => r.Value.Contains(Permission.WriteArticles)).Select(r => r.Key.ToString()).ToArray();

    internal static bool CanWrite(string rolesCsv) =>
        RoleDefinitions.PermissionsForRoleNames(rolesCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Contains(Permission.WriteArticles);

    /// <summary>
    /// Asks <paramref name="reviewerId"/> to review the article; an earlier request still open is withdrawn. Refused while
    /// someone else's request is open, unless you're the author: only they, or whoever asked, take a request back
    /// (<see cref="WithdrawAsync"/>).
    /// </summary>
    public async Task<Guid> AskAsync(Guid articleId, string reviewerId, string? note, CancellationToken ct = default)
    {
        RequireWrite();
        var reviewer = (await ReviewersAsync(articleId, ct)).FirstOrDefault(r => r.Id == reviewerId)
                       ?? throw new ArgumentException("Choose someone other than the author to review it.");
        await using var db = await dbs.CreateAsync(ct);
        var author = await db.Articles.AsNoTracking().Where(a => a.Id == articleId).Select(a => a.AuthorId).FirstOrDefaultAsync(ct);
        var asked = await db.ArticleReviews.Where(r => r.ArticleId == articleId && r.Status == ReviewStatus.Asked).ToListAsync(ct);
        if (user.UserId != author && asked.FirstOrDefault(r => r.RequestedById != user.UserId) is { } theirs)
            throw new ForbiddenException($"{theirs.RequestedByName} asked {theirs.ReviewerName} to review this. Only they, or the author, can take that request back.");
        foreach (var open in asked) open.Status = ReviewStatus.Withdrawn;
        var review = new ArticleReview
        {
            ArticleId = articleId, ReviewerId = reviewer.Id, ReviewerName = reviewer.Name, RequestedById = user.UserId, RequestedByName = user.DisplayName,
            RequestedAtUtc = time.GetUtcNow(), Note = Clean(note, ArticleReview.MaxNote),
        };
        db.ArticleReviews.Add(review);
        await db.SaveChangesAsync(ct);
        return review.Id;
    }

    /// <summary>Takes back an open request (the author, or whoever asked).</summary>
    public async Task WithdrawAsync(Guid reviewId, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var review = await db.ArticleReviews.FirstOrDefaultAsync(r => r.Id == reviewId, ct) ?? throw new KeyNotFoundException("That review no longer exists.");
        var author = await db.Articles.AsNoTracking().Where(a => a.Id == review.ArticleId).Select(a => a.AuthorId).FirstOrDefaultAsync(ct);
        if (user.UserId != review.RequestedById && user.UserId != author)
            throw new ForbiddenException("Only the article's author, or whoever asked, can take the request back.");
        if (review.Status != ReviewStatus.Asked) throw new InvalidOperationException("That review has already been decided.");
        review.Status = ReviewStatus.Withdrawn;
        await db.SaveChangesAsync(ct);
    }

    // --- reading ------------------------------------------------------------------------------------------------

    /// <summary>The article's latest review that wasn't withdrawn, its comments, and whether the article changed since it was decided.</summary>
    public async Task<ReviewState> ForArticleAsync(Guid articleId, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        // Few per article: the latest is picked here (SQLite can't order by a DateTimeOffset).
        var review = (await db.ArticleReviews.AsNoTracking().Include(r => r.Comments)
            .Where(r => r.ArticleId == articleId && r.Status != ReviewStatus.Withdrawn).ToListAsync(ct))
            .MaxBy(r => r.RequestedAtUtc);
        if (review is null) return new(null, false);
        review.Comments = review.Comments.OrderBy(c => c.CreatedAtUtc).ToList();
        var article = await WithContent(db).FirstOrDefaultAsync(a => a.Id == articleId, ct);
        return new(review, review.ReviewedVersion is not null && (article is null || !Unchanged(review, article)));
    }

    /// <summary>The reviews waiting for the current user, oldest first.</summary>
    public async Task<IReadOnlyList<ReviewAsked>> AskedOfMeAsync(CancellationToken ct = default)
    {
        if (!user.Has(Permission.WriteArticles)) return [];
        await using var db = await dbs.CreateAsync(ct);
        var asked = await db.ArticleReviews.AsNoTracking().Where(r => r.ReviewerId == user.UserId && r.Status == ReviewStatus.Asked)
            .Join(db.Articles, r => r.ArticleId, a => a.Id, (r, a) => new ReviewAsked(r.Id, a.Id, a.Title, r.RequestedByName, r.RequestedAtUtc))
            .ToListAsync(ct);
        return asked.OrderBy(r => r.RequestedAtUtc).ToList();   // in memory: SQLite can't order by a DateTimeOffset
    }

    /// <summary>
    /// The articles on the brief for <paramref name="day"/> without a current approval: not asked, waiting, changes
    /// asked, or edited since approval; each says whether the rule requires review.
    /// </summary>
    public async Task<IReadOnlyList<ReviewGap>> GapsAsync(DateOnly day, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await GapsAsync(db, day, ct);
    }

    /// <summary>
    /// The EP-80 hold: refused when an administrator requires review and one of <paramref name="articles"/> (of the brief
    /// for <paramref name="day"/>) has no current approval. <paramref name="before"/> says what waits ("this edition goes").
    /// </summary>
    internal static async Task RequireApprovedAsync(IAppDbContext db, DateOnly day, IReadOnlySet<Guid> articles, string before, CancellationToken ct)
    {
        if (articles.Count == 0) return;
        var required = (await GapsAsync(db, day, ct)).Where(g => g.Required && articles.Contains(g.ArticleId)).ToList();
        if (required.Count > 0)
            throw new InvalidOperationException($"Review is required before {before}: " + string.Join("; ", required.Select(g => $"“{g.Title}” ({g.Why})")) + ".");
    }

    internal static async Task<IReadOnlyList<ReviewGap>> GapsAsync(IAppDbContext db, DateOnly day, CancellationToken ct)
    {
        // In the brief's running order.
        var ids = await db.BriefItems.AsNoTracking().Join(db.Briefs, i => i.BriefId, b => b.Id, (i, b) => new { i.ArticleId, i.SortOrder, b.BriefDate })
            .Where(x => x.BriefDate == day).OrderBy(x => x.SortOrder).Select(x => x.ArticleId).ToListAsync(ct);
        if (ids.Count == 0) return [];
        var rule = await RuleAsync(db, ct);
        var articles = await WithContent(db).Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        var reviews = (await db.ArticleReviews.AsNoTracking().Where(r => ids.Contains(r.ArticleId) && r.Status != ReviewStatus.Withdrawn).ToListAsync(ct))
            .GroupBy(r => r.ArticleId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.RequestedAtUtc).First());
        var gaps = new List<ReviewGap>();
        foreach (var a in articles.OrderBy(a => ids.IndexOf(a.Id)))
        {
            var r = reviews.GetValueOrDefault(a.Id);
            var changed = r is { Status: not (ReviewStatus.Asked or ReviewStatus.ChangesAsked), ReviewedVersion: not null } && !Unchanged(r, a);
            string? why = r switch
            {
                null => "not reviewed",
                { Status: ReviewStatus.Asked } => $"waiting for {r.ReviewerName}",
                { Status: ReviewStatus.ChangesAsked } => $"{r.ReviewerName} asked for changes",
                _ when changed => $"changed since {r.ReviewerName} approved it",
                _ => null,
            };
            if (why is not null) gaps.Add(new ReviewGap(a.Id, a.Title, why, Requires(rule, Marking(a), a.Classification)));
        }
        return gaps;
    }

    // --- reviewing ----------------------------------------------------------------------------------------------

    /// <summary>A comment on a part of the article, by the reviewer or the author, while the review is open or changes are asked.</summary>
    public async Task<Guid> CommentAsync(Guid reviewId, string field, string? quote, string text, CancellationToken ct = default)
    {
        RequireWrite();
        var body = Clean(text, ReviewComment.MaxText) ?? throw new ArgumentException("Write the comment.");
        await using var db = await dbs.CreateAsync(ct);
        var review = await Mine(db, reviewId, ct);
        if (review.Status is ReviewStatus.Withdrawn) throw new InvalidOperationException("That review was withdrawn.");
        var comment = new ReviewComment
        {
            ReviewId = review.Id, Field = Clean(field, ReviewComment.MaxField) ?? "general", Quote = Clean(quote, ReviewComment.MaxQuote), Text = body,
            AuthorId = user.UserId, AuthorName = user.DisplayName, CreatedAtUtc = time.GetUtcNow(),
        };
        db.ReviewComments.Add(comment);
        await db.SaveChangesAsync(ct);
        return comment.Id;
    }

    /// <summary>Marks a comment dealt with (or not, again).</summary>
    public async Task ResolveAsync(Guid commentId, bool resolved = true, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var comment = await db.ReviewComments.FirstOrDefaultAsync(c => c.Id == commentId, ct) ?? throw new KeyNotFoundException("That comment no longer exists.");
        await Mine(db, comment.ReviewId, ct);
        comment.ResolvedAtUtc = resolved ? time.GetUtcNow() : null;
        comment.ResolvedByName = resolved ? user.DisplayName : null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The reviewer's decision, with the checklist as they left it (keys that passed). <paramref name="read"/> is the
    /// <see cref="ContentVersion"/> of the article as the reviewer read it, and is recorded, so a later change shows the
    /// approval is out of date. Approving is refused when the article has changed since it was read: the reviewer would
    /// be approving words they haven't seen. Only the reviewer decides.
    /// </summary>
    public async Task DecideAsync(Guid reviewId, bool approve, IReadOnlyCollection<string> passed, Guid read, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var review = await db.ArticleReviews.FirstOrDefaultAsync(r => r.Id == reviewId, ct) ?? throw new KeyNotFoundException("That review no longer exists.");
        if (review.ReviewerId != user.UserId) throw new ForbiddenException($"Only {review.ReviewerName}, who was asked, can decide this review.");
        if (review.Status == ReviewStatus.Withdrawn) throw new InvalidOperationException("That review was withdrawn.");
        var article = await WithContent(db).FirstOrDefaultAsync(a => a.Id == review.ArticleId, ct) ?? throw new KeyNotFoundException("That article no longer exists.");
        if (approve && read != ContentVersion(article))
            throw new InvalidOperationException("The article has changed since you opened it. Reload the page and read it again before approving.");
        review.Status = approve ? ReviewStatus.Approved : ReviewStatus.ChangesAsked;
        review.DecidedAtUtc = time.GetUtcNow();
        review.ReviewedVersion = read;
        review.Checks = string.Join(';', Checklist.Select(c => $"{c.Key}:{(passed.Contains(c.Key) ? 1 : 0)}"));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The checklist keys a review's <see cref="ArticleReview.Checks"/> says passed.</summary>
    public static IReadOnlySet<string> Passed(string? checks) =>
        (checks ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(c => c.Split(':')).Where(p => p.Length == 2 && p[1] == "1").Select(p => p[0]).ToHashSet();

    // --- helpers ------------------------------------------------------------------------------------------------

    // The review, which the current user may act on as its reviewer or as the author or requester of its article.
    private async Task<ArticleReview> Mine(IAppDbContext db, Guid reviewId, CancellationToken ct)
    {
        var review = await db.ArticleReviews.FirstOrDefaultAsync(r => r.Id == reviewId, ct) ?? throw new KeyNotFoundException("That review no longer exists.");
        var author = await db.Articles.AsNoTracking().Where(a => a.Id == review.ArticleId).Select(a => a.AuthorId).FirstOrDefaultAsync(ct);
        if (user.UserId != review.ReviewerId && user.UserId != review.RequestedById && user.UserId != author)
            throw new ForbiddenException("Only the reviewer and the article's author take part in its review.");
        return review;
    }

    private static string? Clean(string? s, int max)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length <= max ? t : t[..max];
    }

    private void RequireRead()
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("Only the team sees reviews.");
    }

    private void RequireWrite()
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts review articles.");
    }
}
