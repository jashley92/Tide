using AwesomeAssertions;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Distribution;
using Tide.Application.Files;
using Tide.Application.Reviews;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>EP-80: asking a colleague, commenting, deciding, an approval going out of date, and the send waiting when review is required.</summary>
public class ReviewTests : IDisposable
{
    private TestDb _db = new();
    private static readonly DateOnly Day = new(2026, 10, 26);
    private const string Author = "S-1-5-21-TEST-1", Sam = "S-1-5-21-SAM", Pat = "S-1-5-21-PAT";

    public ReviewTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private ReviewService Reviews() => new(_db, _db.User, _db.Rotation(), _db.Clock, TimeProvider.System);

    private void As(string id, string name) => (_db.User.UserId, _db.User.DisplayName) = (id, name);

    // The content version of the article as it stands: what the review page passes as what the reviewer read.
    private async Task<Guid> Read(Guid article) => ReviewService.ContentVersion((await _db.Articles().GetAsync(article))!);

    private async Task People()
    {
        await using var ctx = _db.Context();
        ctx.Users.AddRange(
            new AppUser { Sid = Author, DisplayName = "Test Analyst", RolesCsv = "Analyst,Admin" },
            new AppUser { Sid = Sam, DisplayName = "Sam", RolesCsv = "Analyst" },
            new AppUser { Sid = Pat, DisplayName = "Pat", RolesCsv = "Reader" });
        await ctx.SaveChangesAsync();
    }

    private async Task<Guid> Article(string title = "Help desk calls", TlpLevel tlp = TlpLevel.Clear, Classification kind = Classification.Informational)
    {
        var id = await _db.Articles().CreateAsync(title, "Vendor", null, tlp);
        var form = ArticleForm.From((await _db.Articles().GetAsync(id))!);
        form.Bluf = "A group called the help desk.";
        form.Classification = kind;
        if (kind == Classification.Threat) form.Priority = Priority.P2;
        form.Bullets = [new() { Text = "First" }, new() { Text = "Second" }];
        await _db.Articles().SaveAsync(form);
        return id;
    }

    [Fact]
    public async Task AnAdmin_CanBeAskedToReview_AndReviews_ButAnActionOwnerCant()
    {
        await People();
        const string Ada = "S-1-5-21-ADA", Ray = "S-1-5-21-RAY";
        await using (var ctx = _db.Context())
        {
            ctx.Users.AddRange(
                new AppUser { Sid = Ada, DisplayName = "Ada", RolesCsv = "Admin" },
                new AppUser { Sid = Ray, DisplayName = "Ray", RolesCsv = "ActionOwner" });
            await ctx.SaveChangesAsync();
        }
        var article = await Article();

        (await Reviews().ReviewersAsync(article)).Select(r => r.Name).Should().Equal(["Ada", "Sam"], "anyone who writes articles");
        await FluentActions.Invoking(() => Reviews().AskAsync(article, Ray, null)).Should().ThrowAsync<ArgumentException>();
        var review = await Reviews().AskAsync(article, Ada, null);

        As(Ada, "Ada");
        _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        (await Reviews().AskedOfMeAsync()).Single().ArticleId.Should().Be(article);
        await Reviews().CommentAsync(review, "bluf", null, "Say who.");
        await Reviews().DecideAsync(review, approve: false, [], await Read(article));
        (await Reviews().ForArticleAsync(article)).Review!.Status.Should().Be(ReviewStatus.ChangesAsked);
    }

    [Theory]
    [InlineData("Analyst", true)]
    [InlineData("Admin", true)]
    [InlineData("Reader,ActionOwner", false)]
    [InlineData("NotAnAnalyst", false)]
    [InlineData("", false)]
    public void Reviewers_AreThoseWhoseRolesWriteArticles(string roles, bool can) => ReviewService.CanWrite(roles).Should().Be(can);

    [Fact]
    public async Task AReview_IsAsked_Commented_Resolved_AndApproved_UntilTheArticleChanges()
    {
        await People();
        var article = await Article();

        (await Reviews().ReviewersAsync(article)).Select(r => r.Name).Should().Equal(["Sam"], "the author and Readers can't review it");
        await FluentActions.Invoking(() => Reviews().AskAsync(article, Pat, null)).Should().ThrowAsync<ArgumentException>();
        var first = await Reviews().AskAsync(article, Sam, "Is the TLP right?");
        var review = await Reviews().AskAsync(article, Sam, "Asked again");
        (await Reviews().ForArticleAsync(article)).Review!.Id.Should().Be(review, "asking again withdraws the open request");
        first.Should().NotBe(review);

        As(Sam, "Sam");
        (await Reviews().AskedOfMeAsync()).Single().Should().Match<ReviewAsked>(r => r.ArticleId == article && r.RequestedByName == "Test Analyst");
        var comment = await Reviews().CommentAsync(review, "bullet:2", "Second", "Source 1 says attempted.");
        await Reviews().DecideAsync(review, approve: false, ["tlp", "bluf"], await Read(article));
        var state = await Reviews().ForArticleAsync(article);
        (state.Review!.Status, state.OpenComments, state.Approved).Should().Be((ReviewStatus.ChangesAsked, 1, false));
        state.Review.Comments.Single().Should().Match<ReviewComment>(c => c.Field == "bullet:2" && c.Quote == "Second" && c.AuthorName == "Sam");
        ReviewService.Passed(state.Review.Checks).Should().BeEquivalentTo(["tlp", "bluf"]);

        As(Author, "Test Analyst");
        var read = await Read(article);
        await FluentActions.Invoking(() => Reviews().DecideAsync(review, true, [], read)).Should().ThrowAsync<ForbiddenException>("only the reviewer decides");
        await Reviews().ResolveAsync(comment);
        (await Reviews().ForArticleAsync(article)).OpenComments.Should().Be(0);
        var again = await Reviews().AskAsync(article, Sam, null);

        As(Sam, "Sam");
        await Reviews().DecideAsync(again, approve: true, ReviewService.Checklist.Select(c => c.Key).ToList(), await Read(article));
        (await Reviews().ForArticleAsync(article)).Approved.Should().BeTrue();

        As(Author, "Test Analyst");
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);
        form.Bluf = "Changed after approval.";
        await _db.Articles().SaveAsync(form);
        var after = await Reviews().ForArticleAsync(article);
        (after.OutOfDate, after.Approved).Should().Be((true, false));

        As(Pat, "Pat");
        await FluentActions.Invoking(() => Reviews().CommentAsync(again, "general", null, "Hi")).Should().ThrowAsync<ForbiddenException>("only the reviewer and author take part");
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        (await Reviews().AskedOfMeAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AWithdrawnRequest_LeavesNothingOpen_AndCommentsNeedText()
    {
        await People();
        var article = await Article();
        var review = await Reviews().AskAsync(article, Sam, null);
        await FluentActions.Invoking(() => Reviews().CommentAsync(review, "general", null, "  ")).Should().ThrowAsync<ArgumentException>();

        await Reviews().WithdrawAsync(review);

        (await Reviews().ForArticleAsync(article)).Review.Should().BeNull();
        await FluentActions.Invoking(() => Reviews().WithdrawAsync(review)).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => Reviews().CommentAsync(review, "general", null, "Late")).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AnApproval_IsOfWhatTheReviewerRead_AndGoesOutOfDate_OnlyWhenWhatItSaysChanges()
    {
        await People();
        var article = await Article();
        var review = await Reviews().AskAsync(article, Sam, null);
        As(Sam, "Sam");
        var read = await Read(article);   // the review page loads the article

        As(Author, "Test Analyst");
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);
        form.Bullets.Add(new() { Text = "Names an internal system" });
        await _db.Articles().SaveAsync(form);

        As(Sam, "Sam");
        await FluentActions.Invoking(() => Reviews().DecideAsync(review, true, [], read)).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The article has changed since you opened it*");
        (await Reviews().ForArticleAsync(article)).Review!.Status.Should().Be(ReviewStatus.Asked, "the approval was refused");
        await Reviews().DecideAsync(review, true, [], await Read(article));
        (await Reviews().ForArticleAsync(article)).Approved.Should().BeTrue();

        As(Author, "Test Analyst");
        await _db.Articles().SetStatusAsync(article, ArticleStatus.Ready);
        await _db.Articles().SetStatusAsync(article, ArticleStatus.Draft);
        (await Reviews().ForArticleAsync(article)).Approved.Should().BeTrue("a status change isn't a change to what the reviewer read");

        var shot = (await _db.Screenshots().AddAsync(article, [new ScreenshotUpload("console.png", TestDb.Png)])).Single();
        (await Reviews().ForArticleAsync(article)).Should().Match<ReviewState>(s => s.OutOfDate && !s.Approved, "a new image needs review too");

        var again = await Reviews().AskAsync(article, Sam, null);
        As(Sam, "Sam");
        await Reviews().DecideAsync(again, true, [], await Read(article));
        As(Author, "Test Analyst");
        await _db.Screenshots().RemoveAsync(shot.Id);
        (await Reviews().ForArticleAsync(article)).OutOfDate.Should().BeTrue("so does taking one away");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task OnlyTheAuthorOrWhoeverAsked_TakesARequestBack_AndOnlyTheTeamSeesTheGaps(string provider)
    {
        _db = _db.On(provider);
        await People();
        await using (var ctx = _db.Context())
        {
            ctx.Users.Add(new AppUser { Sid = "S-1-5-21-LEE", DisplayName = "Lee", RolesCsv = "Analyst" });
            await ctx.SaveChangesAsync();
        }
        var article = await Article();
        var review = await Reviews().AskAsync(article, Sam, null);

        As(Sam, "Sam");
        await FluentActions.Invoking(() => Reviews().WithdrawAsync(review)).Should().ThrowAsync<ForbiddenException>("the reviewer decides; they don't take the request back");
        As("S-1-5-21-LEE", "Lee");
        await FluentActions.Invoking(() => Reviews().WithdrawAsync(review)).Should().ThrowAsync<ForbiddenException>("an analyst with nothing to do with it");
        As(Author, "Test Analyst");
        await Reviews().WithdrawAsync(review);

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Invoking(() => Reviews().GapsAsync(Day)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task AskingAgain_TakesBackOnlyYourOwnRequest_UnlessYoureTheAuthor()
    {
        await People();
        const string Lee = "S-1-5-21-LEE", Kim = "S-1-5-21-KIM";
        await using (var ctx = _db.Context())
        {
            ctx.Users.AddRange(new AppUser { Sid = Lee, DisplayName = "Lee", RolesCsv = "Analyst" }, new AppUser { Sid = Kim, DisplayName = "Kim", RolesCsv = "Analyst" });
            await ctx.SaveChangesAsync();
        }
        var article = await Article();
        var authors = await Reviews().AskAsync(article, Sam, null);

        As(Lee, "Lee");
        await FluentActions.Invoking(() => Reviews().AskAsync(article, Kim, null)).Should().ThrowAsync<ForbiddenException>()
            .WithMessage("Test Analyst asked Sam to review this*");
        (await Reviews().ForArticleAsync(article)).Review!.Should().Match<ArticleReview>(r => r.Id == authors && r.Status == ReviewStatus.Asked, "Sam's request stays open");

        As(Author, "Test Analyst");
        await Reviews().WithdrawAsync(authors);
        As(Lee, "Lee");
        var lees = await Reviews().AskAsync(article, Kim, null);
        var again = await Reviews().AskAsync(article, Sam, null);
        (await Reviews().ForArticleAsync(article)).Review!.Id.Should().Be(again, "asking again takes back your own request");
        As(Author, "Test Analyst");
        var mine = await Reviews().AskAsync(article, Kim, null);
        (await Reviews().ForArticleAsync(article)).Review!.Id.Should().Be(mine, "the author may take back anyone's request");
        lees.Should().NotBe(again);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AnApprovalFromBeforeTheContentVersion_StaysCurrent_UntilTheArticleIsSaved(string provider)
    {
        _db = _db.On(provider);
        await People();
        var article = await Article("Approved on alpha.9");
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, article);
        var review = await Reviews().AskAsync(article, Sam, null);
        As(Sam, "Sam");
        await Reviews().DecideAsync(review, true, [], await Read(article));
        await using (var ctx = _db.Context())
        {
            // As 0.1.0-alpha.9 saved it: the article's edit version, not a fingerprint of its content.
            var row = await ctx.ArticleReviews.FindAsync(review);
            row!.ReviewedVersion = (await ctx.Articles.FindAsync(article))!.EditVersion;
            await ctx.SaveChangesAsync();
        }

        (await Reviews().ForArticleAsync(article)).Approved.Should().BeTrue("the article hasn't changed since it was approved");
        (await Reviews().GapsAsync(Day)).Should().BeEmpty();

        As(Author, "Test Analyst");
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);
        form.Bluf = "A different bottom line.";
        await _db.Articles().SaveAsync(form);
        (await Reviews().ForArticleAsync(article)).OutOfDate.Should().BeTrue();
        (await Reviews().GapsAsync(Day)).Single().Why.Should().Be("changed since Sam approved it");
    }

    // Each provider, with a screenshot added and with one removed.
    public static TheoryData<string, bool> ProvidersAddedOrRemoved()
    {
        var rows = new TheoryData<string, bool>();
        foreach (var provider in TestDb.Providers)
        {
            rows.Add(provider, false);
            rows.Add(provider, true);
        }
        return rows;
    }

    [Theory]
    [MemberData(nameof(ProvidersAddedOrRemoved))]
    public async Task AnApprovalFromBeforeTheContentVersion_IsOutOfDate_OnceAScreenshotIsAddedOrRemoved(string provider, bool remove)
    {
        _db = _db.On(provider);
        await People();
        var article = await Article("Approved on alpha.9");
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, article);
        var shot = remove ? (await _db.Screenshots().AddAsync(article, [new ScreenshotUpload("before.png", TestDb.Png)])).Single() : null;
        var review = await Reviews().AskAsync(article, Sam, null);
        As(Sam, "Sam");
        await Reviews().DecideAsync(review, true, [], await Read(article));
        await using (var ctx = _db.Context())
        {
            // As 0.1.0-alpha.9 saved it: the article's edit version, not a fingerprint of its content.
            var row = await ctx.ArticleReviews.FindAsync(review);
            row!.ReviewedVersion = (await ctx.Articles.FindAsync(article))!.EditVersion;
            await ctx.SaveChangesAsync();
        }
        (await Reviews().GapsAsync(Day)).Should().BeEmpty("a screenshot added before the decision was there to review");

        // A screenshot doesn't move the article's edit version, but it's what the edition carries.
        As(Author, "Test Analyst");
        if (shot is not null) await _db.Screenshots().RemoveAsync(shot.Id);
        else await _db.Screenshots().AddAsync(article, [new ScreenshotUpload("after.png", TestDb.Png)]);
        (await Reviews().ForArticleAsync(article)).OutOfDate.Should().BeTrue();
        (await Reviews().GapsAsync(Day)).Single().Why.Should().Be("changed since Sam approved it");
    }

    [Theory]
    [InlineData(ReviewRule.Off, TlpLevel.Red, Classification.Threat, false)]
    [InlineData(ReviewRule.AmberAndAbove, TlpLevel.Amber, Classification.Informational, true)]
    [InlineData(ReviewRule.AmberAndAbove, TlpLevel.Green, Classification.Threat, false)]
    [InlineData(ReviewRule.Threats, TlpLevel.Clear, Classification.Threat, true)]
    [InlineData(ReviewRule.AmberOrThreats, TlpLevel.Clear, Classification.Informational, false)]
    [InlineData(ReviewRule.AmberOrThreats, TlpLevel.Red, Classification.Informational, true)]
    public void TheRule_SaysWhatNeedsReview(ReviewRule rule, TlpLevel marking, Classification kind, bool required) =>
        ReviewService.Requires(rule, marking, kind).Should().Be(required);

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task WithReviewRequired_TheSendWaits_ForTheEditionsArticlesOnly(string provider)
    {
        _db = _db.On(provider);
        await People();
        var open = await Article("Open story");
        var amber = await Article("Partner story", TlpLevel.Amber);
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, open);
        await _db.Briefs().AddArticleAsync(brief.Id, amber);
        await _db.Briefs().MarkPresentedAsync(brief.Id);
        await _db.Settings().SaveRecipientsAsync(Edition.Team, "cti@example.org");
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "everyone@example.org");
        await Reviews().SaveRuleAsync(ReviewRule.AmberAndAbove);

        var gaps = await Reviews().GapsAsync(Day);
        gaps.Select(g => (g.Title, g.Why, g.Required)).Should().BeEquivalentTo([("Open story", "not reviewed", false), ("Partner story", "not reviewed", true)]);

        var team = await _db.Distribution().PlanAsync(Day, Edition.Team);
        await FluentActions.Invoking(() => _db.Distribution().SendAsync(team)).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Review is required before this edition goes: “Partner story” (not reviewed).");
        var company = await _db.Distribution().PlanAsync(Day, Edition.Public);
        (await _db.Distribution().SendAsync(company)).Status.Should().Be(SendStatus.Sent, "the AMBER article isn't in the company-wide edition");

        var review = await Reviews().AskAsync(amber, Sam, null);
        As(Sam, "Sam");
        await Reviews().DecideAsync(review, true, [], await Read(amber));
        As(Author, "Test Analyst");
        await _db.Articles().SetStatusAsync(amber, ArticleStatus.Ready);
        (await Reviews().GapsAsync(Day)).Select(g => g.Title).Should().Equal(["Open story"], "marking it Ready changes nothing the reviewer read");
        team = await _db.Distribution().PlanAsync(Day, Edition.Team);
        (await _db.Distribution().SendAsync(team)).Status.Should().Be(SendStatus.Sent);

        (await Reviews().RuleAsync()).Should().Be(ReviewRule.AmberAndAbove);
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await FluentActions.Invoking(() => Reviews().SaveRuleAsync(ReviewRule.Off)).Should().ThrowAsync<ForbiddenException>();
    }

    private async Task Edit(Guid article, Action<ArticleForm> change)
    {
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);
        change(form);
        await _db.Articles().SaveAsync(form);
    }

    // An approved threat on the day's brief, its company-wide edition sent, review then required for threats.
    private async Task<(Guid Article, BriefSend Send)> SentApprovedThreat()
    {
        await People();
        var article = await Article("Threat story", kind: Classification.Threat);
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, article);
        await _db.Briefs().MarkPresentedAsync(brief.Id);
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "everyone@example.org");
        await Reviews().SaveRuleAsync(ReviewRule.Threats);
        var review = await Reviews().AskAsync(article, Sam, null);
        As(Sam, "Sam");
        await Reviews().DecideAsync(review, true, [], await Read(article));
        As(Author, "Test Analyst");
        var dist = _db.Distribution();
        var send = await dist.SendAsync(await dist.PlanAsync(Day, Edition.Public));
        send.Status.Should().Be(SendStatus.Sent);
        return (article, send);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task WithReviewRequired_ACorrectionWithNewWords_Waits_ButATlpSlipDoesnt(string provider)
    {
        _db = _db.On(provider);
        var (article, send) = await SentApprovedThreat();
        await Edit(article, f => f.Bullets[0].Text = "UNREVIEWED CLAIM");
        (await Reviews().GapsAsync(Day)).Should().ContainSingle(g => g.Required, "the approval is out of date");

        var corrections = _db.Corrections();
        var fix = new CorrectionRequest(send.Id, false, [new CorrectedSection(1, article)], "Fixed a bullet.");
        await FluentActions.Invoking(() => corrections.PlanAsync(fix)).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Review is required before this correction goes: “Threat story” (changed since Sam approved it).");
        _db.Email.Sent.Should().NotContain(m => m.Html.Contains("UNREVIEWED CLAIM"));

        var slip = await corrections.PlanAsync(new CorrectionRequest(send.Id, true, [new CorrectedSection(1, article)], null));
        (await corrections.SendAsync(slip, acknowledgedNoRecall: true)).Status.Should().Be(SendStatus.Sent, "a slip carries no words, and it's how what slipped is withdrawn");
    }

    [Fact]
    public async Task WithReviewRequired_ACorrectionThatOnlyCutsAnArticleDown_Goes()
    {
        var (article, send) = await SentApprovedThreat();
        await Edit(article, f => f.Bullets.RemoveAt(1));
        (await Reviews().GapsAsync(Day)).Should().ContainSingle(g => g.Required);

        var corrections = _db.Corrections();
        var plan = await corrections.PlanAsync(new CorrectionRequest(send.Id, false, [new CorrectedSection(1, article)], "The second bullet was wrong."));
        (await corrections.SendAsync(plan)).Status.Should().Be(SendStatus.Sent, "it adds no words nobody reviewed");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task WithReviewRequired_TheSharePointCopy_IsHeldAsTheSendIs(string provider)
    {
        _db = _db.On(provider);
        await People();
        var article = await Article("Threat story", kind: Classification.Threat);
        await Edit(article, f => f.Bullets[0].Text = "UNREVIEWED CLAIM");
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, article);
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "everyone@example.org");
        var dist = _db.Distribution();
        (await dist.SharePointAsync(Day, id => "/files/" + id)).Html.Should().Contain("UNREVIEWED CLAIM", "review isn't required yet");

        await Reviews().SaveRuleAsync(ReviewRule.Threats);
        await FluentActions.Invoking(() => dist.SharePointAsync(Day, id => "/files/" + id)).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Review is required before this edition is copied for SharePoint: “Threat story” (not reviewed).");
        await FluentActions.Invoking(() => dist.SharePointImagesZipAsync(Day)).Should().ThrowAsync<InvalidOperationException>();

        var review = await Reviews().AskAsync(article, Sam, null);
        As(Sam, "Sam");
        await Reviews().DecideAsync(review, true, [], await Read(article));
        (await dist.SharePointAsync(Day, id => "/files/" + id)).Html.Should().Contain("UNREVIEWED CLAIM", "approved, it may be copied");
    }

    [Fact]
    public void Fields_AndTheirLabels_FollowTheArticle()
    {
        var a = new Article { Bullets = [new ArticleBullet { SortOrder = 1, Text = "b" }, new ArticleBullet { SortOrder = 0, Text = "a" }] };
        ReviewService.Fields(a).Select(f => f.Key).Should().Equal("general", "title", "bluf", "bullet:1", "bullet:2", "why", "actions");
        ReviewService.FieldLabel("bullet:2").Should().Be("Bullet 2");
        ReviewService.FieldLabel("why").Should().Be("Why it matters");
        ReviewService.FieldLabel("odd").Should().Be("odd");
        ReviewService.Marking(new Article { Tlp = TlpLevel.Green, WhyItMatters = "x", WhyItMattersTlp = TlpLevel.Amber }).Should().Be(TlpLevel.Amber);
    }
}

/// <summary>The review page, the editor's card, Today's notice and the admin setting render.</summary>
public class ReviewPageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task ThePagesRender()
    {
        Guid article;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            article = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("review-page story", "Vendor", null);

        await using var review = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.ReviewPage>(app, $"/articles/{article}/review", new() { ["Id"] = article });
        (await review.Html()).Should().Contain("Review: review-page story").And.Contain("No review requested.");
        await using var access = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.Admin.AccessAdmin>(app, "/admin/access");
        (await access.Html()).Should().Contain("Review before send").And.Contain("Required for threats");
    }

    private static object? Field(object o, string name) => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o);

    private static void Set(object o, string name, object? value) => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    [Fact]
    public async Task AReloadAfterAComment_KeepsWhatTheReviewerRead_AndSaysTheArticleChanged()
    {
        Guid article;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            article = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("review-reload story", "Vendor", null);
        await using var page = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.ReviewPage>(app, $"/articles/{article}/review", new() { ["Id"] = article });
        var read = (Guid)Field(page.Component, "_read")!;

        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var form = ArticleForm.From((await articles.GetAsync(article))!);
            form.Bullets = [new() { Text = "review-reload added bullet" }];
            await articles.SaveAsync(form);
        }
        // Calls one of the page's handlers, then renders it again, as an event does.
        Task Handle(string method, params object[] args) => page.Click(async c =>
        {
            await (Task)c.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(c, args)!;
            c.GetType().GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(c, []);
        });

        await Handle("Load", false);   // what adding a comment does once it's saved
        ((Guid)Field(page.Component, "_read")!).Should().Be(read, "the reviewer hasn't seen the new bullet");
        (await page.Html()).Should().Contain("The article has changed since you opened it").And.NotContain("review-reload added bullet");

        await Handle("ReadAgain");
        ((Guid)Field(page.Component, "_read")!).Should().NotBe(read);
        (await page.Html()).Should().Contain("review-reload added bullet").And.NotContain("The article has changed since you opened it");
    }

    [Fact]
    public async Task MovingToAnotherArticle_CarriesNoTicks_NoteOrDraft_Over()
    {
        Guid first, second;
        Tide.Domain.Entities.Article secondArticle;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            first = await articles.CreateAsync("review-move first", "Vendor", null);
            second = await articles.CreateAsync("review-move second", "Vendor", null);
            secondArticle = (await articles.GetAsync(second))!;
        }

        await using var page = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.ReviewPage>(app, $"/articles/{first}/review", new() { ["Id"] = first });
        ((HashSet<string>)Field(page.Component, "_passed")!).Add("tlp");
        Set(page.Component, "_text", "A comment for the first");
        Set(page.Component, "_quote", "words of the first");
        await page.Click(c => c.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Id"] = second })));
        (await page.Html()).Should().Contain("Review: review-move second");
        ((HashSet<string>)Field(page.Component, "_passed")!).Should().BeEmpty("the first article's ticks would be sent as the second's checks");
        (Field(page.Component, "_text"), Field(page.Component, "_quote")).Should().Be((null, null));

        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var firstArticle = (await scope.ServiceProvider.GetRequiredService<ArticleService>().GetAsync(first))!;
            await using var card = await ComponentHarness.RenderAsync<Tide.Web.Components.Shared.ReviewCard>(app, $"/articles/{first}",
                new() { ["Article"] = firstArticle, ["CanWrite"] = true });
            Set(card.Component, "_note", "Look at the first's TLP");
            Set(card.Component, "_reviewerId", "S-1-5-21-STALE");
            await card.Click(c => c.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Article"] = secondArticle, ["CanWrite"] = true })));
            Field(card.Component, "_note").Should().BeNull();
            Field(card.Component, "_reviewerId").Should().NotBe("S-1-5-21-STALE");
        }
    }
}
