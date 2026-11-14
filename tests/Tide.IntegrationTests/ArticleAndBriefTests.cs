using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Files;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

public class ArticleServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<ArticleForm> NewForm(TlpLevel tlp = TlpLevel.Clear)
    {
        var svc = _db.Articles();
        var id = await svc.CreateAsync("Edge VPN zero-day", "Vendor", "https://vendor.example.com/a", tlp);
        return ArticleForm.From((await svc.GetAsync(id))!);
    }

    [Fact]
    public async Task Save_StoresEverything_AndNormalizesIndicators()
    {
        await _db.Scorecard().SeedStarterSetAsync();
        var tag = (await _db.Scorecard().ListAsync())[0].Tags[0];
        var form = await NewForm();
        form.Bluf = "Exploited in the wild.";
        form.Classification = Classification.Threat;
        form.Priority = Priority.P1;
        form.Bullets = [new() { Text = "one" }, new() { Text = "two", Tlp = TlpLevel.Amber }, new() { Text = "  " }];
        form.TagIds = [tag.Id];
        form.TechniqueIds = ["t1190"];
        form.Indicators = [new() { Type = IndicatorType.Domain, Value = "evil[.]example[.]com." }];
        form.Cves = ["cve-2026-0001"];

        await _db.Articles().SaveAsync(form);

        var a = (await _db.Articles().GetAsync(form.Id))!;
        a.Bullets.OrderBy(b => b.SortOrder).Select(b => (b.Text, b.Tlp)).Should().Equal(("one", TlpLevel.Clear), ("two", TlpLevel.Amber));
        a.Tags.Select(t => t.TagId).Should().Equal(tag.Id);
        a.Techniques.Select(t => t.TechniqueId).Should().Equal("T1190");
        a.Indicators.Single().Value.Should().Be("evil.example.com");
        a.Cves.Single().CveId.Should().Be("CVE-2026-0001");
        a.Priority.Should().Be(Priority.P1);
    }

    [Fact]
    public async Task Page_CountsEveryMatch_SortsOnTheServer_AndTakesTheFirstOnes()
    {
        var svc = _db.Articles();
        foreach (var t in new[] { "beta lure", "Alpha lure", "gamma lure", "Unrelated" }) await svc.CreateAsync(t, "Vendor", null, TlpLevel.Clear);

        var page = await svc.PageAsync("lure", null, ArticleSort.Title, descending: false, take: 2);
        page.Total.Should().Be(3);
        page.Rows.Select(r => r.Title).Should().Equal("Alpha lure", "beta lure");
        (await svc.PageAsync("LURE alpha", null, ArticleSort.Written, descending: true, take: 10)).Rows.Select(r => r.Title)
            .Should().Equal(["Alpha lure"], "every word must match, in any case");

        (await svc.PageAsync(null, null, ArticleSort.Written, descending: true, take: 10)).Rows[0].Title.Should().Be("Unrelated", "newest first");
        (await svc.PageAsync(null, null, ArticleSort.Written, descending: false, take: 10)).Rows[0].Title.Should().Be("beta lure");
        (await svc.PageAsync(null, ArticleStatus.Ready, ArticleSort.Status, descending: true, take: 10)).Total.Should().Be(0);
        (await svc.PageAsync("lure", null, ArticleSort.Tlp, descending: true, take: 0)).Rows.Should().ContainSingle("at least one row comes back");
    }

    [Fact]
    public async Task Save_FromAStaleCopy_IsRefused()
    {
        var form = await NewForm();
        var other = ArticleForm.From((await _db.Articles().GetAsync(form.Id))!);
        other.Bluf = "first";
        await _db.Articles().SaveAsync(other);

        form.Bluf = "second";
        var act = () => _db.Articles().SaveAsync(form);

        await act.Should().ThrowAsync<StaleEditException>();
    }

    // As the editor holds it: it keeps the version each save or status change hands back, so its own saves go on
    // working while a second tab's copy, loaded before them, is refused with a message the editor shows as it is.
    [Fact]
    public async Task Save_AsTheEditorDoes_KeepsWorking_WhileAnotherTabsOlderCopyIsRefused()
    {
        var svc = _db.Articles();
        var mine = await NewForm();
        var theirs = ArticleForm.From((await svc.GetAsync(mine.Id))!);

        mine.Bluf = "first";
        mine.EditVersion = await svc.SaveAsync(mine);
        mine.Bullets.Add(new BulletForm { Text = "a bullet" });
        mine.EditVersion = await svc.SaveAsync(mine);
        await svc.SetStatusAsync(mine.Id, ArticleStatus.Ready);
        mine.EditVersion = (await svc.GetAsync(mine.Id))!.EditVersion;
        mine.Bluf = "second";
        await svc.SaveAsync(mine);

        theirs.Bluf = "theirs";
        var stale = await FluentActions.Awaiting(() => svc.SaveAsync(theirs)).Should().ThrowAsync<StaleEditException>();
        Tide.Web.Services.UserFacingError.For(stale.Which).Should().Contain("Someone else saved this article while you were editing");
        (await svc.GetAsync(mine.Id))!.Bluf.Should().Be("second");
    }

    [Fact]
    public async Task Save_RefusesAnUnknownTechnique()
    {
        var form = await NewForm();
        form.TechniqueIds = ["T9999"];
        var act = () => _db.Articles().SaveAsync(form);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*T9999*");
    }

    [Theory]
    [InlineData("article")]
    [InlineData("bullet")]
    [InlineData("why")]
    public async Task Save_AnUndefinedTlp_IsRefused_NotStoredBelowEveryCeiling(string part)
    {
        var form = await NewForm(TlpLevel.Amber);
        form.Bluf = "Bottom line.";
        form.WhyItMatters = "Our angle.";
        form.WhyItMattersTlp = TlpLevel.Amber;
        form.Bullets = [new() { Text = "partner detail", Tlp = TlpLevel.Amber }];
        var bad = (TlpLevel)(-1);
        switch (part)
        {
            case "article": form.Tlp = bad; break;
            case "bullet": form.Bullets[0].Tlp = bad; break;
            default: form.WhyItMattersTlp = bad; break;
        }

        var act = () => _db.Articles().SaveAsync(form);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*TLP marking*");
        (await _db.Articles().GetAsync(form.Id))!.Tlp.Should().Be(TlpLevel.Amber);
        var create = () => _db.Articles().CreateAsync("Crafted", null, null, bad);
        await create.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Save_ARetiredTechnique_SaysWhatReplacedIt()
    {
        var form = await NewForm();
        form.TechniqueIds = ["T1574.002"];
        var act = () => _db.Articles().SaveAsync(form);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*no longer in ATT&CK v19.1: it was replaced by T1574.001 DLL. Add that one instead.*");
    }

    [Fact]
    public async Task Save_KeepsATechniqueTheArticleAlreadyHad_ThoughAnUpgradeRetiredIt()
    {
        var form = await NewForm();
        await using (var ctx = _db.Context())
        {
            // As the article is after an ATT&CK upgrade: it carries T1574.002, which v19.1 no longer has (replaced by T1574.001).
            ctx.ArticleTechniques.Add(new Tide.Domain.Entities.ArticleTechnique { ArticleId = form.Id, TechniqueId = "T1574.002" });
            await ctx.SaveChangesAsync();
        }
        form = ArticleForm.From((await _db.Articles().GetAsync(form.Id))!);
        form.Bluf = "A typo fixed in the bottom line.";
        form.TechniqueIds.Add("T1566");

        await _db.Articles().SaveAsync(form);

        var saved = (await _db.Articles().GetAsync(form.Id))!;
        saved.Bluf.Should().Be("A typo fixed in the bottom line.");
        saved.Techniques.Select(t => t.TechniqueId).Should().BeEquivalentTo(["T1574.002", "T1566"]);

        form = ArticleForm.From(saved);
        form.TechniqueIds.Add("T9999");
        await FluentActions.Invoking(() => _db.Articles().SaveAsync(form)).Should().ThrowAsync<ArgumentException>("a newly added id must still be in the catalogue");
        form = ArticleForm.From((await _db.Articles().GetAsync(form.Id))!);
        form.TechniqueIds.Remove("T1574.002");
        await _db.Articles().SaveAsync(form);
        (await _db.Articles().GetAsync(form.Id))!.Techniques.Select(t => t.TechniqueId).Should().Equal(["T1566"], "removing it still works");
    }

    [Fact]
    public async Task Save_InformationalDropsPriority()
    {
        var form = await NewForm();
        form.Classification = Classification.Informational;
        form.Priority = Priority.P2;
        await _db.Articles().SaveAsync(form);
        (await _db.Articles().GetAsync(form.Id))!.Priority.Should().BeNull();
    }

    [Fact]
    public async Task SetStatus_Ready_IsRefusedWhileABlockingCheckFails()
    {
        var form = await NewForm();
        var act = () => _db.Articles().SetStatusAsync(form.Id, ArticleStatus.Ready);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*BLUF*");
    }

    [Fact]
    public async Task Create_RefusesANonWebLink()
    {
        var act = () => _db.Articles().CreateAsync("x", null, "javascript:alert(1)");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AReader_CantWrite()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var act = () => _db.Articles().CreateAsync("x");
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Changes_AreAudited_WithWhoAndWhat()
    {
        var form = await NewForm();
        form.Bluf = "changed";
        await _db.Articles().SaveAsync(form);

        await using var ctx = _db.Context();
        var entries = await ctx.AuditLog.Where(e => e.EntityType == "Article").ToListAsync();
        entries.Select(e => e.Action).Should().Contain(["Added", "Modified"]);
        entries.Single(e => e.Action == "Modified").ChangesJson.Should().Contain("changed");
        entries.Should().OnlyContain(e => e.UserId == _db.User.UserId);
    }

    [Fact]
    public async Task Screenshots_AreSniffed_Limited_AndTakeTheArticlesTlp()
    {
        var form = await NewForm(TlpLevel.Amber);
        var shots = _db.Screenshots();

        var added = await shots.AddAsync(form.Id, [new ScreenshotUpload("../../evil name.png", TestDb.Png)]);

        added.Single().Tlp.Should().Be(TlpLevel.Amber);
        _db.Store.Files.Should().ContainKey(added[0].FileId);
        await using (var ctx = _db.Context())
            (await ctx.Files.SingleAsync()).FileName.Should().Be("evil name.png");

        var notImage = () => shots.AddAsync(form.Id, [new ScreenshotUpload("x.png", "<svg onload=alert(1)>"u8.ToArray())]);
        await notImage.Should().ThrowAsync<ArgumentException>().WithMessage("*isn't a PNG*");

        var tooMany = () => shots.AddAsync(form.Id, Enumerable.Range(0, 10).Select(i => new ScreenshotUpload($"{i}.png", TestDb.Png)).ToList());
        await tooMany.Should().ThrowAsync<ArgumentException>().WithMessage("*9 more*");
        _db.Store.Files.Should().HaveCount(1, "a refused batch stores nothing");
    }

    // A new screenshot is marked AMBER, or the article's TLP when that's higher: adding one never puts it in the
    // company-wide edition without a deliberate choice (owner decision 2026-11-11).
    [Theory]
    [InlineData(TlpLevel.Clear, TlpLevel.Amber)]
    [InlineData(TlpLevel.Green, TlpLevel.Amber)]
    [InlineData(TlpLevel.Amber, TlpLevel.Amber)]
    [InlineData(TlpLevel.AmberStrict, TlpLevel.AmberStrict)]
    [InlineData(TlpLevel.Red, TlpLevel.Red)]
    public async Task NewScreenshot_IsAmber_OrTheArticlesTlpWhenHigher(TlpLevel article, TlpLevel expected)
    {
        var form = await NewForm(article);

        var added = (await _db.Screenshots().AddAsync(form.Id, [new ScreenshotUpload("a.png", TestDb.Png)])).Single();

        added.Tlp.Should().Be(expected);
        (await _db.Articles().GetAsync(form.Id))!.Screenshots.Single().Tlp.Should().Be(expected, "that's the marking stored");
    }

    [Fact]
    public async Task NewScreenshot_OnAClearArticle_IsLeftOutOfAGreenCompanyWideEdition_UntilItsMarkingIsSaved()
    {
        var form = await NewForm(TlpLevel.Clear);
        var shot = (await _db.Screenshots().AddAsync(form.Id, [new ScreenshotUpload("a.png", TestDb.Png)])).Single();

        var saved = (await _db.Articles().GetAsync(form.Id))!;
        var company = BriefEditions.BuildArticle(saved, Edition.Public, TlpLevel.Green)!;
        company.Screenshots.Should().BeEmpty("a new screenshot is AMBER, above the GREEN ceiling");
        company.WithheldParts.Should().Be(1);
        BriefEditions.BuildArticle(ArticleForm.From(saved).ToPreview(), Edition.Public, TlpLevel.Green)!.Screenshots
            .Should().BeEmpty("the editor's preview cuts it the same way");
        BriefEditions.BuildArticle(saved, Edition.Team, TlpLevel.AmberStrict)!.Screenshots.Should().ContainSingle();

        // The analyst lowers it deliberately: the change saves through the usual save, and then it's in.
        form = ArticleForm.From(saved);
        form.Screenshots.Single(s => s.Id == shot.Id).Tlp = TlpLevel.Green;
        await _db.Articles().SaveAsync(form);

        saved = (await _db.Articles().GetAsync(form.Id))!;
        saved.Screenshots.Single().Tlp.Should().Be(TlpLevel.Green);
        BriefEditions.BuildArticle(saved, Edition.Public, TlpLevel.Green)!.Screenshots.Should().ContainSingle();
    }

    [Fact]
    public async Task Screenshot_OpenAsync_ServesTheTeam_NeverAReaderALiveScreenshot()
    {
        var form = await NewForm(TlpLevel.Clear);
        var shot = (await _db.Screenshots().AddAsync(form.Id, [new ScreenshotUpload("a.png", TestDb.Png)])).Single();

        (await _db.Screenshots().OpenAsync(shot.FileId)).Should().NotBeNull("an analyst reads the team edition");
        (await _db.Screenshots().OpenAttachmentAsync(shot.FileId))!.Value.FileName.Should().Be("a.png", "the screenshot dialog downloads it");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        (await _db.Screenshots().OpenAsync(shot.FileId)).Should().BeNull("a Reader sees only images in a sent company-wide edition");
        (await _db.Screenshots().OpenAttachmentAsync(shot.FileId)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_OnlyADraftOffEveryBrief()
    {
        var form = await NewForm();
        var brief = await _db.Briefs().GetOrStartAsync(new DateOnly(2026, 10, 26));
        await _db.Briefs().AddArticleAsync(brief.Id, form.Id);

        var act = () => _db.Articles().DeleteAsync(form.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

public class BriefServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private static readonly DateOnly Monday = new(2026, 10, 26);

    public void Dispose() => _db.Dispose();

    private async Task<Guid> ReadyArticle(string title)
    {
        var svc = _db.Articles();
        var id = await svc.CreateAsync(title);
        var form = ArticleForm.From((await svc.GetAsync(id))!);
        form.Bluf = "bottom line";
        form.Bullets = [new() { Text = "a bullet" }];
        await svc.SaveAsync(form);
        await svc.SetStatusAsync(id, ArticleStatus.Ready);
        return id;
    }

    [Fact]
    public async Task SetDetails_KeepsTheOpeningNotesTlp_AndRefusesOneThatIsntAMarking()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        var brief = await _db.Briefs().GetOrStartAsync(Monday);

        await _db.Briefs().SetDetailsAsync(brief.Id, _db.User.UserId, "Test Analyst", "Partner briefing at 2pm.", TlpLevel.Amber);

        var saved = (await _db.Briefs().GetAsync(Monday))!;
        saved.Intro.Should().Be("Partner briefing at 2pm.");
        saved.IntroTlp.Should().Be(TlpLevel.Amber);
        var crafted = () => _db.Briefs().SetDetailsAsync(brief.Id, _db.User.UserId, "Test Analyst", "x", (TlpLevel)(-1));
        await crafted.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ABrief_IsBuilt_Reordered_Presented()
    {
        var first = await ReadyArticle("first");
        var second = await ReadyArticle("second");
        var briefs = _db.Briefs();

        var brief = await briefs.GetOrStartAsync(Monday);
        (await briefs.CandidatesAsync(brief.Id)).Select(c => c.Id).Should().BeEquivalentTo([first, second]);
        (await briefs.CandidatesAsync(brief.Id)).Should().OnlyContain(c => c.AuthorId == _db.User.UserId, "the picker's \"Only mine\" filters on it");
        await briefs.AddArticleAsync(brief.Id, first);
        await briefs.AddArticleAsync(brief.Id, second);
        await briefs.AddArticleAsync(brief.Id, second);   // twice is once

        var earlier = await briefs.GetOrStartAsync(Monday.AddDays(-7));
        var offered = await briefs.CandidatesAsync(earlier.Id);
        offered.Single(c => c.Id == first).LastBriefed.Should().Be(Monday, "a past brief shows when the article was briefed, even later");
        offered.Single(c => c.Id == first).AuthorName.Should().Be(_db.User.DisplayName);

        brief = (await briefs.GetAsync(Monday))!;
        await briefs.MoveItemAsync(brief.Id, brief.Items.Single(i => i.ArticleId == second).Id, -1);
        brief = (await briefs.GetAsync(Monday))!;
        brief.Items.OrderBy(i => i.SortOrder).Select(i => i.ArticleId).Should().Equal(second, first);
        brief.AnalystName.Should().Be(_db.User.DisplayName);

        await briefs.MarkReadyAsync(brief.Id, true);
        await briefs.MarkPresentedAsync(brief.Id);

        brief = (await briefs.GetAsync(Monday))!;
        brief.Status.Should().Be(BriefStatus.Presented);
        brief.PresentedAtUtc.Should().NotBeNull();
        brief.Items.Should().OnlyContain(i => i.Article!.Status == ArticleStatus.Presented);
    }

    [Fact]
    public async Task Today_TakingAnArticleOff_IsUndoneInPlace_AndADropReorders()
    {
        var ids = new[] { await ReadyArticle("one"), await ReadyArticle("two"), await ReadyArticle("three") };
        var briefs = _db.Briefs();
        var brief = await briefs.GetOrStartAsync(Monday);
        foreach (var id in ids) await briefs.AddArticleAsync(brief.Id, id);
        async Task<List<Guid>> Order() => (await briefs.GetAsync(Monday))!.Items.OrderBy(i => i.SortOrder).Select(i => i.ArticleId).ToList();

        // Today's Remove, then its Undo: back at position 2, not the end.
        brief = (await briefs.GetAsync(Monday))!;
        await briefs.RemoveItemAsync(brief.Id, brief.Items.Single(i => i.ArticleId == ids[1]).Id);
        (await Order()).Should().Equal(ids[0], ids[2]);
        await briefs.AddArticleAsync(brief.Id, ids[1], at: 1);
        (await Order()).Should().Equal(ids);
        (await briefs.GetAsync(Monday))!.Items.OrderBy(i => i.SortOrder).Select(i => i.SortOrder).Should().Equal(0, 1, 2);

        // A drag: "three" dropped above "one".
        brief = (await briefs.GetAsync(Monday))!;
        var items = brief.Items.OrderBy(i => i.SortOrder).Select(i => i.Id).ToList();
        await briefs.ReorderAsync(brief.Id, BriefOrder.Dropped(items, items[2], items[0], after: false));
        (await Order()).Should().Equal(ids[2], ids[0], ids[1]);

        // A position past the end puts it at the end.
        await briefs.RemoveItemAsync(brief.Id, (await briefs.GetAsync(Monday))!.Items.Single(i => i.ArticleId == ids[0]).Id);
        await briefs.AddArticleAsync(brief.Id, ids[0], at: 99);
        (await Order()).Should().Equal(ids[2], ids[1], ids[0]);
    }

    [Fact]
    public async Task EndingTheMeeting_CarriesChosenUndiscussedArticles_ToTheNextBusinessDay()
    {
        var talked = await ReadyArticle("talked about");
        var skipped = await ReadyArticle("ran out of time");
        var dropped = await ReadyArticle("not worth carrying");
        var briefs = _db.Briefs();
        var friday = Monday.AddDays(-3);   // past days: the meeting can be recorded as held
        var brief = await briefs.GetOrStartAsync(friday);
        foreach (var id in new[] { talked, skipped, dropped }) await briefs.AddArticleAsync(brief.Id, id);
        brief = (await briefs.GetAsync(friday))!;
        await briefs.SetDiscussedAsync(brief.Items.Single(i => i.ArticleId == talked).Id, true);

        // A discussed article asked for is ignored: only what wasn't covered moves on.
        var carriedTo = await briefs.MarkPresentedAsync(brief.Id, carry: [skipped, talked]);

        carriedTo.Should().Be(Monday, "Friday's next business day is Monday");
        var next = (await briefs.GetAsync(Monday))!;
        next.Items.Select(i => i.ArticleId).Should().Equal(skipped);
        brief = (await briefs.GetAsync(friday))!;
        brief.Status.Should().Be(BriefStatus.Presented);
        brief.Items.Single(i => i.ArticleId == skipped).Article!.Status.Should().Be(ArticleStatus.Ready, "it wasn't presented; it will be on Monday");
        brief.Items.Where(i => i.ArticleId != skipped).Should().OnlyContain(i => i.Article!.Status == ArticleStatus.Presented);
        brief.Items.Single(i => i.ArticleId == dropped).Discussed.Should().BeFalse("the record keeps that it wasn't discussed");
    }

    [Fact]
    public async Task ThePicker_CountsWhatsStillToBrief_CarriedOnesIncluded_HowEverOld()
    {
        var talked = await ReadyArticle("presented already");
        var carried = await ReadyArticle("carried over");
        var briefs = _db.Briefs();
        var friday = Monday.AddDays(-3);
        var brief = await briefs.GetOrStartAsync(friday);
        foreach (var id in new[] { talked, carried }) await briefs.AddArticleAsync(brief.Id, id);
        await briefs.MarkPresentedAsync(brief.Id, carry: [carried]);
        // Taken back off Monday's brief and gone back to draft, written long ago: still offered for Monday.
        var monday = (await briefs.GetAsync(Monday))!;
        await briefs.RemoveItemAsync(monday.Id, monday.Items.Single().Id);
        await _db.Articles().SetStatusAsync(carried, ArticleStatus.Draft);
        await using (var db = _db.Context())
        {
            var old = await db.Articles.SingleAsync(a => a.Id == carried);
            old.CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-(BriefService.CandidateDays + 30));
            await db.SaveChangesAsync();
        }

        var offered = await briefs.CandidatesAsync(monday.Id);

        offered.Single(c => c.Id == carried).Available.Should().BeTrue("a carried-over draft is still to be briefed, however old");
        offered.Single(c => c.Id == talked).Available.Should().BeFalse("it was presented on Friday");
        offered.Count(c => c.Available).Should().Be(1, "\"Add article (N ready, M drafts)\" counts only what's still to brief");
    }

    [Fact]
    public async Task EndingTheMeeting_WithNothingToCarry_StartsNoBrief()
    {
        var a = await ReadyArticle("only one");
        var briefs = _db.Briefs();
        var brief = await briefs.GetOrStartAsync(Monday);
        await briefs.AddArticleAsync(brief.Id, a);

        (await briefs.MarkPresentedAsync(brief.Id, carry: [])).Should().BeNull();
        (await briefs.GetAsync(Monday.AddDays(1))).Should().BeNull();
    }

    [Fact]
    public async Task EndingTheMeeting_RefusesToCarryToASentBrief_AndRecordsNothing()
    {
        var a = await ReadyArticle("late");
        var briefs = _db.Briefs();
        var brief = await briefs.GetOrStartAsync(Monday);
        await briefs.AddArticleAsync(brief.Id, a);
        var tuesday = await briefs.GetOrStartAsync(Monday.AddDays(1));
        await using (var db = _db.Context())
        {
            (await db.Briefs.SingleAsync(b => b.Id == tuesday.Id)).Status = BriefStatus.Sent;
            await db.SaveChangesAsync();
        }

        var act = () => briefs.MarkPresentedAsync(brief.Id, carry: [a]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*has been sent*");
        (await briefs.GetAsync(Monday))!.Status.Should().NotBe(BriefStatus.Presented);
    }

    [Fact]
    public async Task MarkReady_IsRefusedWithAnUnfinishedArticle()
    {
        var draft = await _db.Articles().CreateAsync("unfinished");
        var brief = await _db.Briefs().GetOrStartAsync(Monday);
        await _db.Briefs().AddArticleAsync(brief.Id, draft);

        var act = () => _db.Briefs().MarkReadyAsync(brief.Id, true);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unfinished*");
    }

    [Fact]
    public async Task GetOrStart_RefusesAWeekend_AndStartsOncePerDay()
    {
        var act = () => _db.Briefs().GetOrStartAsync(new DateOnly(2026, 10, 25));
        await act.Should().ThrowAsync<ArgumentException>();

        var a = await _db.Briefs().GetOrStartAsync(Monday);
        var b = await _db.Briefs().GetOrStartAsync(Monday);
        b.Id.Should().Be(a.Id);
    }

    [Fact]
    public async Task TheEditions_OfASavedBrief_CutByTlp()
    {
        var open = await ReadyArticle("open");
        var restricted = await ReadyArticle("restricted");
        var form = ArticleForm.From((await _db.Articles().GetAsync(restricted))!);
        form.Tlp = TlpLevel.Amber;
        await _db.Articles().SaveAsync(form);
        var brief = await _db.Briefs().GetOrStartAsync(Monday);
        await _db.Briefs().AddArticleAsync(brief.Id, open);
        await _db.Briefs().AddArticleAsync(brief.Id, restricted);

        brief = (await _db.Briefs().GetAsync(Monday))!;
        BriefEditions.Build(brief, Edition.Public, TlpLevel.Green).Articles.Select(a => a.Article.Title).Should().Equal("open");
        BriefEditions.Build(brief, Edition.Team, TlpLevel.AmberStrict).Articles.Should().HaveCount(2);
    }
}

public class ScorecardServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AUsedTag_IsRetired_AnUnusedOne_Deleted()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        var svc = _db.Scorecard();
        await svc.SeedStarterSetAsync();
        var cat = (await svc.ListAsync())[0];
        var used = cat.Tags[0];
        var unused = cat.Tags[1];
        var article = await _db.Articles().CreateAsync("x");
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);
        form.TagIds = [used.Id];
        await _db.Articles().SaveAsync(form);

        await svc.SetTagRetiredAsync(used.Id, true);
        await svc.SetTagRetiredAsync(unused.Id, true);

        var all = (await svc.ListAsync(includeRetired: true)).First(c => c.Id == cat.Id).Tags;
        all.Single(t => t.Id == used.Id).IsRetired.Should().BeTrue();
        all.Should().NotContain(t => t.Id == unused.Id);

        // A retired tag stays on the article, but can't be newly chosen.
        var other = ArticleForm.From((await _db.Articles().GetAsync(await _db.Articles().CreateAsync("y")))!);
        other.TagIds = [used.Id];
        var act = () => _db.Articles().SaveAsync(other);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*retired*");
    }

    [Fact]
    public async Task Keys_StayUnique()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        var svc = _db.Scorecard();
        await svc.AddCategoryAsync("Fraud", null);
        await svc.AddCategoryAsync("fraud!", null);
        (await svc.ListAsync()).Select(c => c.Key).Should().Equal("fraud", "fraud-2");
    }

    [Fact]
    public async Task OnlyAnAdmin_ChangesTheScorecard()
    {
        var act = () => _db.Scorecard().AddCategoryAsync("x", null);
        await act.Should().ThrowAsync<ForbiddenException>();
    }
}
