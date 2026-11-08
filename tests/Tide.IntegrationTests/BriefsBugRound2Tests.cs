using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Articles;
using Tide.Application.Files;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>Bug round 2, the briefs slice: a screenshot whose file is gone, and a meeting ended twice.</summary>
public class BriefsBugRound2Tests : IDisposable
{
    private readonly TestDb _db = new();
    private static readonly DateOnly Day = new(2026, 10, 26);

    public BriefsBugRound2Tests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    // A presented brief with one CLEAR article and two screenshots; the first one's file is then lost from the store.
    private async Task<(Guid Kept, Guid Lost)> BriefWithALostScreenshot()
    {
        var articles = _db.Articles();
        var id = await articles.CreateAsync("Open article", "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await articles.GetAsync(id))!);
        form.Bluf = "Everyone may read this.";
        await articles.SaveAsync(form);
        var shots = await _db.Screenshots().AddAsync(id, [new ScreenshotUpload("a.png", TestDb.Png), new ScreenshotUpload("b.png", TestDb.Png)]);
        form = ArticleForm.From((await articles.GetAsync(id))!);
        form.Screenshots.Single(s => s.Id == shots[0].Id).Caption = "lost caption";
        form.Screenshots.Single(s => s.Id == shots[1].Id).Caption = "kept caption";
        await articles.SaveAsync(form);
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, id);
        await _db.Briefs().MarkPresentedAsync(brief.Id);
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "everyone@example.org");
        _db.Store.Files.Remove(shots[0].FileId);
        return (shots[1].FileId, shots[0].FileId);
    }

    [Fact]
    public async Task ASendWithAScreenshotWhoseFileIsGone_NeitherReferencesNorCountsIt()
    {
        var (kept, lost) = await BriefWithALostScreenshot();
        var svc = _db.Distribution();

        var plan = await svc.PlanAsync(Day, Edition.Public);
        plan.Images.Should().Be(1, "the confirmation counts the images that go");
        var preview = await svc.PreviewEmailAsync(Day, Edition.Public, f => "files/" + f);
        preview.Should().NotContain("files/" + lost).And.Contain("files/" + kept);

        var send = await svc.SendAsync(plan);

        send.Status.Should().Be(SendStatus.Sent);
        var mail = _db.Email.Sent.Single();
        mail.Images.Should().ContainSingle().Which.ContentId.Should().Be("shot1@tide");
        mail.Html.Should().Contain("cid:shot1@tide").And.NotContain("cid:shot2@tide").And.NotContain("lost caption");
        await using var ctx = _db.Context();
        var row = await ctx.BriefSends.Include(s => s.Images).SingleAsync();
        row.Images.Should().ContainSingle().Which.SourceFileId.Should().Be(kept);
        row.WebHtml.Should().Contain("Figure 1").And.NotContain("Figure 2").And.NotContain("lost caption");
    }

    [Fact]
    public async Task TheSharePointCopy_ShowsOnlyTheImagesItsZipHolds()
    {
        var (kept, lost) = await BriefWithALostScreenshot();
        var svc = _db.Distribution();

        var copy = await svc.SharePointAsync(Day, f => "files/" + f);
        copy.Images.Should().ContainSingle().Which.Shot.FileId.Should().Be(kept);
        copy.Html.Should().NotContain("files/" + lost).And.Contain("01-kept-caption.png");

        var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await svc.SharePointImagesZipAsync(Day)));
        zip.Entries.Select(e => e.Name).Should().Equal("01-kept-caption.png");
    }

    [Fact]
    public async Task AMeetingEndedAgain_LeavesTheArticleItsFirstEndCarriedOver()
    {
        var articles = _db.Articles();
        var stays = await articles.CreateAsync("Discussed article", "Vendor", null, TlpLevel.Clear);
        var carried = await articles.CreateAsync("Carried article", "Vendor", null, TlpLevel.Clear);
        var briefs = _db.Briefs();
        var brief = await briefs.GetOrStartAsync(Day);
        await briefs.AddArticleAsync(brief.Id, stays);
        await briefs.AddArticleAsync(brief.Id, carried);

        // Present mode ends it, carrying the undiscussed article on; the brief page's End, clicked meanwhile, ends it again.
        var next = await briefs.MarkPresentedAsync(brief.Id, carry: [carried]);
        next.Should().NotBeNull();
        await briefs.MarkPresentedAsync(brief.Id);

        (await articles.GetAsync(carried))!.Status.Should().Be(ArticleStatus.Draft, "it's the next brief's to present");
        (await articles.GetAsync(stays))!.Status.Should().Be(ArticleStatus.Presented);
        (await briefs.GetAsync(next!.Value))!.Items.Should().ContainSingle(i => i.ArticleId == carried);
    }
}
