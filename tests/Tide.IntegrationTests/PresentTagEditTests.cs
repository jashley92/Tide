using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Application.Reviews;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// An article's ATT&amp;CK techniques and scorecard tags edited from its slide in present mode: by analysts only, through
/// the article editor's own save, so the article page's rules (stale saves, reviews, sent briefs) hold alike.
/// </summary>
public class PresentTagEditTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:tagedit-owner", "Olive Owner", "ActionOwner");
    private static readonly SignIn Robin = new("dev:tagedit-robin", "Robin Analyst", "Analyst");

    // Read once: a test that straddles midnight mustn't see two days.
    private DateOnly Today { get; } = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    private static DateOnly BusinessDaysBefore(DateOnly day, int n)
    {
        for (var i = 0; i < n; i++) day = WorkingDays.Weekdays.Previous(day);
        return day;
    }

    private sealed record Setup(DateOnly Day, Guid Brief, Guid First, string FirstTitle, Guid Second, Guid Tag, string TagName);

    // A past day's brief with two untagged articles, and a live scorecard tag to put on them.
    private async Task<Setup> Brief(int daysBack)
    {
        var day = BusinessDaysBefore(Today, daysBack);
        var title = $"Tag edit first {daysBack}";
        Guid briefId, first, second;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var brief = await briefs.GetOrStartAsync(day);
            briefId = brief.Id;
            first = await articles.CreateAsync(title, "Vendor", null, TlpLevel.Clear);
            second = await articles.CreateAsync($"Tag edit second {daysBack}", "Vendor", null, TlpLevel.Clear);
            await briefs.AddArticleAsync(brief.Id, first);
            await briefs.AddArticleAsync(brief.Id, second);
        }
        var name = $"Edited tag {daysBack}";
        var tag = new ScorecardTag { Key = $"edited-{daysBack}", Name = name, Category = new ScorecardCategory { Key = $"edited-cat-{daysBack}", Name = $"Edited category {daysBack}" } };
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ScorecardTags.Add(tag);
            await db.SaveChangesAsync();
        }
        return new Setup(day, briefId, first, title, second, tag.Id, name);
    }

    private static async Task FirstArticle(Rendered<Present> r)
    {
        await r.Click(p => p.OnPresentKey("last"));
        await r.Click(p => p.OnPresentKey("prev"));
        await r.Click(p => p.OnPresentKey("prev"));
    }

    // Chooses in the open tag panel, then saves it.
    private static async Task SaveTags(Rendered<Present> r, Guid[] tags, string[] techniques)
    {
        await r.Click(p =>
        {
            p.TagEditor.Should().NotBeNull("the panel is open");
            p.TagEditor!.Choose(tags, techniques);
            return Task.CompletedTask;
        });
        await r.Click(p => p.TagEditor!.Save());
    }

    private static Change Heard(Guid article) => new(ChangeFeed.Article(article), "dev:tester", "Tester", Guid.NewGuid());

    [Fact]
    public async Task ThePresenter_SavesTagsFromTheSlide_AndAFollowersChipsUpdateQuietly()
    {
        var s = await Brief(111);
        await using var pat = await Open(app, s.Day);
        await Do(pat, p => p.StartMeeting());
        await using var robin = await Open(app, s.Day, Robin);
        await Do(robin, p => p.JoinIfLive());
        await using var owner = await Open(app, s.Day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await FirstArticle(pat);
        await Do(robin, p => p.Follow());
        await Do(owner, p => p.Follow());

        var html = await pat.Html();
        html.Should().Contain("aria-label=\"Add tags to " + s.FirstTitle + "\"", "an untagged article can be tagged from its slide");
        (await robin.Html()).Should().Contain("tide-slide-tags-edit", "a following analyst can edit too");
        (await owner.Html()).Should().NotContain("tide-slide-tags-edit").And.NotContain("tide-slide-tagsec", "an action owner can't, and an untagged article has no section");

        await Do(pat, p => p.EditTags(s.First));
        html = await pat.Html();
        html.Should().Contain("id=\"present-tagedit-title\"").And.Contain("Only you see this").And.Contain(s.TagName).And.Contain("Choose techniques");
        var slide = Meetings.Get(s.Day)!.Slide;
        await pat.Click(p => p.OnPresentKey("next"));
        Meetings.Get(s.Day)!.Slide.Should().Be(slide, "the slide stays under the panel");
        (await robin.Html()).Should().NotContain("present-tagedit-title", "the panel is the editor's alone");

        // Esc cancels, without saving.
        await Do(pat, p => p.OnPresentKey("close"));
        (await pat.Html()).Should().NotContain("present-tagedit-title");

        await Do(pat, p => p.EditTags(s.First));
        await SaveTags(pat, [s.Tag], ["T1190"]);
        html = await pat.Html();
        html.Should().NotContain("present-tagedit-title").And.Contain(">Initial Access<").And.Contain($":</span> {s.TagName}</span>").And.Contain("> Edit tags</button>");

        await Do(robin, p => p.OnChangedElsewhere([Heard(s.First)]));
        (await robin.Html()).Should().Contain(">Initial Access<").And.Contain($":</span> {s.TagName}</span>");
        robin.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().NotContain(t => t.Message.StartsWith("Tester"), "a tag edit is quiet");
        EndMeeting(app, s.Day);
    }

    [Fact]
    public async Task AFollower_SavesTags_TheirSlideStayingPutUntilTheyClose()
    {
        var s = await Brief(112);
        await using var pat = await Open(app, s.Day);
        await Do(pat, p => p.StartMeeting());
        await using var robin = await Open(app, s.Day, Robin);
        await Do(robin, p => p.JoinIfLive());
        await FirstArticle(pat);
        await Do(robin, p => p.Follow());
        (await robin.Html()).Should().Contain($"<h2>{s.FirstTitle}</h2>");

        await Do(robin, p => p.EditTags(s.First));
        await pat.Click(p => p.OnPresentKey("next"));
        await Do(robin, p => p.Follow());
        (await robin.Html()).Should().Contain($"<h2>{s.FirstTitle}</h2>", "the editor's slide doesn't change under the panel").And.Contain("present-tagedit-title");

        await SaveTags(robin, [s.Tag], ["T1566.001", "T1486"]);
        var html = await robin.Html();
        html.Should().NotContain("present-tagedit-title").And.Contain("<h2>Tag edit second 112</h2>", "closed, they catch up with the presenter");

        await Do(pat, p => p.OnChangedElsewhere([Heard(s.First)]));
        await pat.Click(p => p.OnPresentKey("prev"));
        (await pat.Html()).Should().Contain(">Initial Access<").And.Contain(">Impact<").And.Contain($":</span> {s.TagName}</span>");
        pat.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().NotContain(t => t.Message.StartsWith("Tester"));

        await using var scope = SignedIn(app, SignIn.Analyst);
        var saved = (await scope.ServiceProvider.GetRequiredService<ArticleService>().GetAsync(s.First))!;
        saved.Techniques.Select(t => t.TechniqueId).Should().BeEquivalentTo(["T1566.001", "T1486"]);
        saved.Tags.Select(t => t.TagId).Should().Equal(s.Tag);
        EndMeeting(app, s.Day);
    }

    [Theory]
    [InlineData("ActionOwner", 113)]
    [InlineData("Viewer", 114)]
    [InlineData("Visitor", 115)]
    [InlineData("Reader", 116)]
    public async Task OnlyAnalysts_CanSaveTags_TheServerRefusingTheRest(string role, int daysBack)
    {
        var s = await Brief(daysBack);
        Guid version;
        await using (var analyst = SignedIn(app, SignIn.Analyst))
            version = (await analyst.ServiceProvider.GetRequiredService<ArticleService>().GetAsync(s.First))!.EditVersion;

        var who = new SignIn($"dev:tagedit-{role.ToLowerInvariant()}", $"{role} Person", role);
        await using var scope = SignedIn(app, who);
        var save = () => scope.ServiceProvider.GetRequiredService<ArticleService>().SaveTagsAsync(s.First, version, [s.Tag], ["T1190"]);
        await save.Should().ThrowAsync<ForbiddenException>();

        await using var check = SignedIn(app, SignIn.Analyst);
        var a = (await check.ServiceProvider.GetRequiredService<ArticleService>().GetAsync(s.First))!;
        a.Tags.Should().BeEmpty();
        a.Techniques.Should().BeEmpty();
        a.EditVersion.Should().Be(version);
    }

    [Fact]
    public async Task OnASentBrief_WithApprovedReviews_TheSlidesSaveDoesWhatTheArticlePagesDoes()
    {
        var s = await Brief(121);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Briefs.SingleAsync(b => b.Id == s.Brief)).Status = BriefStatus.Sent;
            await db.SaveChangesAsync();
        }
        await Approve(s.First);
        await Approve(s.Second);

        // A tag alone, then a technique: on one article through the editor's save, on the other through the slide's.
        await using var analyst = SignedIn(app, SignIn.Analyst);
        var articles = analyst.ServiceProvider.GetRequiredService<ArticleService>();
        var reviews = analyst.ServiceProvider.GetRequiredService<ReviewService>();
        async Task<(bool OutOfDate, ArticleStatus Status, int Audited)> State(Guid id)
        {
            var review = await reviews.ForArticleAsync(id);
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audited = await db.AuditLog.CountAsync(e => e.EntityKey.Contains(id.ToString()));
            return (review.OutOfDate, (await articles.GetAsync(id))!.Status, audited);
        }
        async Task ByEditor(Guid id, Guid[] tags, string[] techniques)
        {
            var form = ArticleForm.From((await articles.GetAsync(id))!);
            form.TagIds = tags.ToHashSet();
            form.TechniqueIds = techniques.ToList();
            await articles.SaveAsync(form);
        }
        async Task BySlide(Guid id, Guid[] tags, string[] techniques) =>
            await articles.SaveTagsAsync(id, (await articles.GetAsync(id))!.EditVersion, tags, techniques);

        var (editorBefore, slideBefore) = (await State(s.First), await State(s.Second));
        await ByEditor(s.First, [s.Tag], []);
        await BySlide(s.Second, [s.Tag], []);
        var (editorTag, slideTag) = (await State(s.First), await State(s.Second));
        slideTag.OutOfDate.Should().Be(editorTag.OutOfDate).And.BeFalse("a scorecard tag isn't what a reviewer approved");
        slideTag.Status.Should().Be(editorTag.Status);
        (slideTag.Audited - slideBefore.Audited).Should().Be(editorTag.Audited - editorBefore.Audited).And.BePositive("both are audited alike");

        await ByEditor(s.First, [s.Tag], ["T1190"]);
        await BySlide(s.Second, [s.Tag], ["T1190"]);
        var (editorTech, slideTech) = (await State(s.First), await State(s.Second));
        slideTech.OutOfDate.Should().Be(editorTech.OutOfDate).And.BeTrue("a technique is part of what was approved");
        slideTech.Status.Should().Be(editorTech.Status);
        (slideTech.Audited - slideTag.Audited).Should().Be(editorTech.Audited - editorTag.Audited);
    }

    private async Task Approve(Guid articleId)
    {
        await using var analyst = SignedIn(app, SignIn.Analyst);
        var a = (await analyst.ServiceProvider.GetRequiredService<ArticleService>().GetAsync(articleId))!;
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ArticleReviews.Add(new ArticleReview
        {
            ArticleId = articleId, RequestedById = "dev:tester", RequestedByName = "Tester", RequestedAtUtc = DateTimeOffset.UtcNow,
            ReviewerId = Robin.UserId, ReviewerName = Robin.Name, Status = ReviewStatus.Approved, DecidedAtUtc = DateTimeOffset.UtcNow,
            ReviewedVersion = ReviewService.ContentVersion(a),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AStaleSave_IsRefused_ShowingTheTagsNowSaved_AndKeepingTheChoice()
    {
        var s = await Brief(122);
        await using var pat = await Open(app, s.Day);
        await FirstArticle(pat);
        await Do(pat, p => p.EditTags(s.First));

        // A colleague saves the article meanwhile.
        await using (var robin = SignedIn(app, Robin))
        {
            var articles = robin.ServiceProvider.GetRequiredService<ArticleService>();
            await articles.SaveTagsAsync(s.First, (await articles.GetAsync(s.First))!.EditVersion, [], ["T1486"]);
        }

        await SaveTags(pat, [s.Tag], ["T1190"]);
        var html = await pat.Html();
        html.Should().Contain("present-tagedit-title", "the panel stays open").And.Contain("Someone else saved this article")
            .And.Contain("Saved now: T1486; no scorecard tags.")
            .And.Contain($"aria-label=\"Remove {s.TagName}\"", "the tag chosen stays chosen").And.Contain("T1190");
        await using (var check = SignedIn(app, SignIn.Analyst))
            (await check.ServiceProvider.GetRequiredService<ArticleService>().GetAsync(s.First))!.Techniques.Select(t => t.TechniqueId).Should().Equal("T1486");

        // Saving again, having seen what's there, puts the choice in its place.
        await pat.Click(p => p.TagEditor!.Save());
        (await pat.Html()).Should().NotContain("present-tagedit-title").And.Contain(">Initial Access<").And.NotContain(">Impact<");
    }

    // The panel's scorecard tags are the article editor's picker: chosen chips, "Add a tag", and the rest folded.
    [Fact]
    public async Task ThePanelsTags_AreTheEditorsPicker_SearchAddsAndAChipRemoves_AndSaveKeepsThem()
    {
        var s = await Brief(124);
        await using var pat = await Open(app, s.Day);
        await FirstArticle(pat);
        await Do(pat, p => p.EditTags(s.First));
        var html = await pat.Html();
        html.Should().Contain("Scorecard tags <span class=\"tide-tagpick-count\">0 chosen</span>").And.Contain("None chosen.")
            .And.Contain("for=\"present-tags-search\">Add a tag</label>").And.Contain("role=\"combobox\"")
            .And.Contain("aria-controls=\"present-tags-browse\"").And.Contain("<div id=\"present-tags-browse\" class=\"tide-disclosure-body tide-tagpick-all\" hidden>");

        await pat.Click(p =>
        {
            var picker = p.TagEditor!.TagPicker!;
            picker.Search(s.TagName.ToUpperInvariant());
            return picker.Add(picker._found.Single(h => h.Tag.Id == s.Tag));
        });
        html = await pat.Html();
        html.Should().Contain($"aria-label=\"Remove {s.TagName}\"").And.Contain("1 chosen");

        // Removed, then added again from the browse list.
        await pat.Click(p => p.TagEditor!.TagPicker!.Toggle(s.Tag));
        (await pat.Html()).Should().NotContain($"aria-label=\"Remove {s.TagName}\"").And.Contain("0 chosen");
        await pat.Click(p => p.TagEditor!.TagPicker!.Toggle(s.Tag));

        await pat.Click(p => p.TagEditor!.Save());
        (await pat.Html()).Should().NotContain("present-tagedit-title");
        await using var check = SignedIn(app, SignIn.Analyst);
        (await check.ServiceProvider.GetRequiredService<ArticleService>().GetAsync(s.First))!.Tags.Select(t => t.TagId).Should().Equal(s.Tag);
    }

    [Fact]
    public async Task AViewerAndAVisitor_SeeTheRoomsTagRow_ScorecardTagsIncluded_ButNoEditButtonAndNoLinks()
    {
        var s = await Brief(123);
        await using (var analyst = SignedIn(app, SignIn.Analyst))
        {
            var articles = analyst.ServiceProvider.GetRequiredService<ArticleService>();
            await articles.SaveTagsAsync(s.First, (await articles.GetAsync(s.First))!.EditVersion, [s.Tag], ["T1190"]);
        }
        await using var pat = await Open(app, s.Day);
        await Do(pat, p => p.StartMeeting());
        await FirstArticle(pat);
        await using var viewer = await Open(app, s.Day, new SignIn("dev:tagedit-val", "Val Viewer", "Viewer"));
        await Do(viewer, p => p.JoinIfLive());
        await using var visitor = await Open(app, s.Day, new SignIn("dev:tagedit-vic", "Vic Visitor", "Visitor"));
        await Do(visitor, p => p.GoLive());

        foreach (var r in new[] { viewer, visitor })
        {
            await Do(r, p => p.Follow());
            var html = await r.Html();
            html.Should().Contain($"<h2>{s.FirstTitle}</h2>");
            var row = PresentSlideTagsTests.Section(html);
            row.Should().NotBeNull("the room's tags and ATT&CK");
            row!.Should().Contain(">Initial Access<").And.Contain($":</span> {s.TagName}</span>", "the team edition's scorecard tags, as the room sees them")
                .And.NotContain("<a ", "the chips link nowhere").And.NotContain("href").And.NotContain("tide-slide-tags-edit");

            await Do(r, p => p.EditTags(s.First));
            (await r.Html()).Should().NotContain("present-tagedit-title", "they can't open the panel either");
        }
        EndMeeting(app, s.Day);
    }
}
