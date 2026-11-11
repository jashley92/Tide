using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Articles;
using Tide.Application.Distribution;
using Tide.Application.Files;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

public class DistributionTests : IDisposable
{
    private TestDb _db = new();
    private static readonly DateOnly Day = new(2026, 10, 26);

    public DistributionTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private sealed record Setup(Guid OpenShotFile, Guid SecretShotFile, Guid AmberShotFile);

    // A brief with an open article (one open and one RED screenshot, one AMBER bullet) and an AMBER article with a
    // screenshot; recipients set for both editions.
    private async Task<Setup> Brief()
    {
        var articles = _db.Articles();
        var open = await articles.CreateAsync("Open article", "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await articles.GetAsync(open))!);
        form.Bluf = "Everyone may read this.";
        form.Bullets = [new() { Text = "public bullet" }, new() { Text = "ISAC-only bullet", Tlp = TlpLevel.Amber }];
        await articles.SaveAsync(form);
        var shots = await _db.Screenshots().AddAsync(open, [new ScreenshotUpload("a.png", TestDb.Png), new ScreenshotUpload("b.png", TestDb.Png)]);
        form = ArticleForm.From((await articles.GetAsync(open))!);
        var openShot = form.Screenshots.Single(s => s.Id == shots[0].Id);
        openShot.Caption = "open caption";
        openShot.Tlp = TlpLevel.Clear;   // a new screenshot starts at AMBER: lowered on purpose
        var secret = form.Screenshots.Single(s => s.Id == shots[1].Id);
        secret.Caption = "secret caption";
        secret.Tlp = TlpLevel.Red;
        await articles.SaveAsync(form);

        var amber = await articles.CreateAsync("Partner-only article", "Partner", null, TlpLevel.Amber);
        var amberForm = ArticleForm.From((await articles.GetAsync(amber))!);
        amberForm.Bluf = "Shared by a partner.";
        await articles.SaveAsync(amberForm);
        var amberShot = (await _db.Screenshots().AddAsync(amber, [new ScreenshotUpload("c.png", TestDb.Png)])).Single();

        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, open);
        await _db.Briefs().AddArticleAsync(brief.Id, amber);
        await _db.Briefs().MarkPresentedAsync(brief.Id);   // the meeting was held: a brief goes after it
        await _db.Settings().SaveRecipientsAsync(Edition.Team, "cti@example.org; soc@example.org");
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "everyone@example.org");
        return new Setup(shots[0].FileId, shots[1].FileId, amberShot.FileId);
    }

    [Fact]
    public async Task Send_Public_EmailsOnlyWhatTheCeilingAllows_AsCidImages()
    {
        var s = await Brief();
        var svc = _db.Distribution();

        var plan = await svc.PlanAsync(Day, Edition.Public);
        var send = await svc.SendAsync(plan);

        send.Status.Should().Be(SendStatus.Sent);
        var mail = _db.Email.Sent.Single();
        mail.To.Should().Equal("everyone@example.org");
        mail.Subject.Should().Contain("[TLP:CLEAR]");
        foreach (var leak in new[] { "ISAC-only bullet", "secret caption", "Partner-only article", "Shared by a partner." })
        {
            mail.Html.Should().NotContain(leak);
            mail.Text.Should().NotContain(leak);
        }
        mail.Images.Should().ContainSingle().Which.ContentId.Should().Be("shot1@tide");
        mail.Html.Should().Contain("cid:shot1@tide").And.NotContain("Today's numbers");
        mail.Images[0].Bytes.Should().Equal(TestDb.Png);
    }

    [Fact]
    public async Task Send_FreezesASnapshot_CopiesImages_LogsIt_AndMarksTheBriefSent()
    {
        var s = await Brief();
        var svc = _db.Distribution();

        var send = await svc.SendAsync(await svc.PlanAsync(Day, Edition.Team));

        await using var ctx = _db.Context();
        var row = await ctx.BriefSends.Include(x => x.Images).SingleAsync();
        row.IsTest.Should().BeFalse();
        row.RecipientCount.Should().Be(2);
        row.SentByName.Should().Be(_db.User.DisplayName);
        row.EmailHtml.Should().Contain("Partner-only article").And.Contain("ISAC-only bullet");
        row.WebHtml.Should().Contain("<h2 data-article=\"", "each heading names its article, for a correction");
        row.Images.Should().HaveCount(2, "the open and the AMBER screenshot; RED is above the team ceiling");
        row.Images.Select(i => i.SourceFileId).Should().BeEquivalentTo([s.OpenShotFile, s.AmberShotFile]);
        row.Images.Should().OnlyContain(i => i.FileId != i.SourceFileId && _db.Store.Files.ContainsKey(i.FileId), "the images are copies");
        (await ctx.Briefs.SingleAsync()).Status.Should().Be(BriefStatus.Sent);

        // Taking a screenshot off the article later leaves the frozen copy whole.
        var original = await ctx.ArticleScreenshots.SingleAsync(x => x.FileId == s.OpenShotFile);
        await _db.Screenshots().RemoveAsync(original.Id);
        _db.Store.Files.Should().ContainKey(row.Images.Single(i => i.SourceFileId == s.OpenShotFile).FileId);
    }

    [Fact]
    public async Task Send_IsRefused_WhenTheEditionOrListChangedSinceTheConfirmation()
    {
        await Brief();
        var svc = _db.Distribution();
        var plan = await svc.PlanAsync(Day, Edition.Team);

        await _db.Settings().SaveRecipientsAsync(Edition.Team, "cti@example.org; soc@example.org; extra@example.org");
        var act = () => svc.SendAsync(plan);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed since you confirmed*");
        _db.Email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_IsRefused_WhenTheContentChangedSinceTheConfirmation_ThoughItsCountsAndMarkingDidNot()
    {
        await Brief();
        var svc = _db.Distribution();
        var plan = await svc.PlanAsync(Day, Edition.Public);

        // A colleague rewrites a bullet: same marking, same number of articles and images.
        var articles = _db.Articles();
        var open = (await articles.ListAsync()).Single(a => a.Title == "Open article");
        var form = ArticleForm.From((await articles.GetAsync(open.Id))!);
        form.Bullets[0].Text = "a sentence nobody confirmed";
        await articles.SaveAsync(form);
        var act = () => svc.SendAsync(plan);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed since you confirmed*");
        _db.Email.Sent.Should().BeEmpty();
        var again = await svc.PlanAsync(Day, Edition.Public);
        again.Fingerprint.Should().NotBe(plan.Fingerprint);
        (await svc.SendAsync(again)).Status.Should().Be(SendStatus.Sent);
        _db.Email.Sent.Single().Html.Should().Contain("a sentence nobody confirmed");
    }

    [Fact]
    public async Task Send_IsRefused_WhenSomeoneElseSentTheEditionAfterTheConfirmation()
    {
        await Brief();
        var svc = _db.Distribution();
        var mine = await svc.PlanAsync(Day, Edition.Public);
        var theirs = await svc.PlanAsync(Day, Edition.Public);
        mine.LastSentAtUtc.Should().BeNull();

        await svc.SendAsync(theirs);
        var act = () => svc.SendAsync(mine);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed since you confirmed*");
        _db.Email.Sent.Should().ContainSingle();
        var again = await svc.PlanAsync(Day, Edition.Public);
        again.LastSentAtUtc.Should().NotBeNull("the confirmation says it already went");
        again.LastSentBy.Should().Be(_db.User.DisplayName);
        (await svc.SendAsync(again)).Status.Should().Be(SendStatus.Sent, "sending again on purpose is still allowed");
        _db.Email.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task Send_IsRefused_WhileTheSameEditionIsWithTheRelay()
    {
        await Brief();
        var relay = new HeldEmailSender();
        var svc = _db.Distribution(relay);
        var plan = await svc.PlanAsync(Day, Edition.Team);

        var first = svc.SendAsync(plan);
        await relay.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = () => svc.SendAsync(plan);

        await second.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is sending this edition now*");
        relay.Release.SetResult();
        (await first).Status.Should().Be(SendStatus.Sent);
        relay.Calls.Should().Be(1);
        await using var ctx = _db.Context();
        (await ctx.BriefSends.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Send_WhenTheOtherEditionFreezesAtTheSameMoment_StillGoes()
    {
        await Brief();
        var svc = _db.Distribution();
        var plan = await svc.PlanAsync(Day, Edition.Public);
        // As this send freezes, another (the team edition's) has just moved the brief's edit version.
        var raced = false;
        _db.BeforeSave = context =>
        {
            if (raced || !context.ChangeTracker.Entries<Tide.Domain.Entities.BriefSend>().Any(e => e.State == EntityState.Added)) return;
            raced = true;
            using var other = _db.Context();
            other.Briefs.Single().EditVersion = Guid.NewGuid();
            other.SaveChanges();
        };

        var send = await svc.SendAsync(plan);

        raced.Should().BeTrue();
        send.Status.Should().Be(SendStatus.Sent, "nothing about this edition changed, so it goes after checking again");
        _db.Email.Sent.Should().ContainSingle();
        _db.Store.Files.Count.Should().Be(3 + 1, "the three screenshots, and one frozen copy (the first attempt's copy was removed)");
    }

    [Fact]
    public async Task Send_WhenRecordingItsOutcomeFailsOnce_IsStillRecordedSent()
    {
        await Brief();
        var svc = _db.Distribution();
        var plan = await svc.PlanAsync(Day, Edition.Public);
        var failed = false;
        _db.BeforeSave = context =>
        {
            if (failed || !context.ChangeTracker.Entries<Tide.Domain.Entities.BriefSend>().Any(e => e.State == EntityState.Modified)) return;
            failed = true;
            throw new DbUpdateException("database busy");
        };

        var send = await svc.SendAsync(plan);

        failed.Should().BeTrue();
        send.Status.Should().Be(SendStatus.Sent);
        await using var ctx = _db.Context();
        (await ctx.BriefSends.SingleAsync()).Status.Should().Be(SendStatus.Sent, "the outcome was saved on the second try, not left sending");
        (await ctx.Briefs.SingleAsync()).Status.Should().Be(BriefStatus.Sent);
    }

    [Fact]
    public async Task Send_ARelayThatThrows_IsRecordedOutcomeUnknown_NotLeftSending()
    {
        await Brief();
        var svc = _db.Distribution(new ThrowingEmailSender());

        var send = await svc.SendAsync(await svc.PlanAsync(Day, Edition.Public));

        send.Status.Should().Be(SendStatus.Unknown);
        send.Error.Should().Contain("isn't known");
        await using var ctx = _db.Context();
        (await ctx.BriefSends.SingleAsync()).Status.Should().Be(SendStatus.Unknown);
        (await ctx.Briefs.SingleAsync()).Status.Should().NotBe(BriefStatus.Sent);
        (await svc.PublishedAsync()).Should().BeEmpty();
        var next = await svc.PlanAsync(Day, Edition.Public);
        next.LastOutcomeUnknown.Should().BeTrue("the next confirmation warns that it may have gone");
        next.LastSentAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Ceilings_AnUndefinedTlp_IsRefused()
    {
        var act = () => _db.Settings().SaveAsync((TlpLevel)(-1), TlpLevel.Amber);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*TLP marking*");
        var high = () => _db.Settings().SaveAsync(TlpLevel.Clear, (TlpLevel)99);
        await high.Should().ThrowAsync<ArgumentException>().WithMessage("*TLP marking*");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheTeamEdition_ListsOverdueActionsOnlyUpToItsCeiling_AndIsMarkedAtTheHighestItLists(string provider)
    {
        _db = _db.On(provider);
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var red = await _db.Articles().CreateAsync("Partner RED report", "Partner", null, TlpLevel.Red);
        var amber = await _db.Articles().CreateAsync("ISAC note", "ISAC", null, TlpLevel.Amber);
        await _db.Actions().CreateAsync(new Tide.Application.Actions.ActionForm { Title = "Block 203.0.113.7", TeamId = team, ArticleId = red, DueDate = Day.AddDays(-5) });
        await _db.Actions().CreateAsync(new Tide.Application.Actions.ActionForm { Title = "Reset partner keys", TeamId = team, ArticleId = amber, DueDate = Day.AddDays(-4) });
        var open = await _db.Articles().CreateAsync("Open article", "Vendor", null, TlpLevel.Clear);
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, open);
        await _db.Settings().SaveRecipientsAsync(Edition.Team, "cti@example.org");
        var svc = _db.Distribution();

        // The default team ceiling (AMBER+STRICT): the RED article's action is counted, not named; the AMBER one marks the email.
        var plan = await svc.PlanAsync(Day, Edition.Team);
        var email = await svc.PreviewEmailAsync(Day, Edition.Team, id => $"/files/{id}");
        var cut = await svc.EditionToSendAsync(Day, Edition.Team);
        email.Should().Contain("Reset partner keys").And.Contain("2 overdue in all").And.NotContain("203.0.113.7");
        cut.Open!.Rows.Should().NotContain(o => o.Row.Title.Contains("203.0.113.7"));
        plan.Marking.Should().Be(TlpLevel.Amber, "the email carries an action raised from an AMBER article");
        plan.Subject.Should().EndWith("[TLP:AMBER]");
        email.Should().Contain("This email is").And.Contain("TLP:AMBER");

        // A RED team ceiling lets the RED one through, and the email is marked RED.
        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.Red);
        var redPlan = await svc.PlanAsync(Day, Edition.Team);
        (await svc.PreviewEmailAsync(Day, Edition.Team, id => $"/files/{id}")).Should().Contain("Block 203.0.113.7");
        redPlan.Marking.Should().Be(TlpLevel.Red);
        redPlan.Fingerprint.Should().NotBe(plan.Fingerprint);

        // The company-wide edition never lists them, and isn't marked for them.
        var pub = await svc.PlanAsync(Day, Edition.Public);
        pub.Marking.Should().Be(TlpLevel.Clear);
        (await svc.PreviewEmailAsync(Day, Edition.Public, id => $"/files/{id}")).Should().NotContain("Still open").And.NotContain("Reset partner keys");
    }

    [Fact]
    public async Task Plan_IsStable_WhileNothingChanges()
    {
        await Brief();
        var svc = _db.Distribution();

        var first = await svc.PlanAsync(Day, Edition.Team);
        var second = await svc.PlanAsync(Day, Edition.Team);

        second.Fingerprint.Should().NotBeNullOrEmpty().And.Be(first.Fingerprint);
    }

    [Fact]
    public async Task Send_ARelayFailure_IsLogged_NotPublished_AndTheBriefStaysUnsent()
    {
        await Brief();
        _db.Email.Result = new EmailSendResult(EmailSendStatus.Failed, "The mail relay couldn't be reached.");
        var svc = _db.Distribution();

        var send = await svc.SendAsync(await svc.PlanAsync(Day, Edition.Public));

        send.Status.Should().Be(SendStatus.Failed);
        send.Error.Should().Contain("couldn't be reached");
        (await svc.PublishedAsync()).Should().BeEmpty();
        await using var ctx = _db.Context();
        (await ctx.Briefs.SingleAsync()).Status.Should().NotBe(BriefStatus.Sent);
    }

    [Fact]
    public async Task SendTest_GoesToTheSenderOnly_IsLogged_AndChangesNothingElse()
    {
        await Brief();
        var result = await _db.Distribution().SendTestAsync(Day, Edition.Team);

        result.Status.Should().Be(EmailSendStatus.Sent);
        _db.Email.Sent.Single().To.Should().Equal(_db.User.Email);
        _db.Email.Sent.Single().Subject.Should().StartWith("[TEST] ");
        await using var ctx = _db.Context();
        var row = await ctx.BriefSends.SingleAsync();
        row.IsTest.Should().BeTrue();
        row.EmailHtml.Should().BeNull("a test isn't archived");
        (await ctx.Briefs.SingleAsync()).Status.Should().Be(BriefStatus.Presented, "a test copy leaves the brief as it was (its meeting held), not Sent");
    }

    // A Windows sign-in has no email claim; the address comes from AD's mail with the display name.
    [Fact]
    public async Task SendTest_ForAWindowsSignIn_GoesToTheAddressInAd()
    {
        await Brief();
        var principal = await new Tide.Web.Security.RoleClaimsTransformer(
                new OneGroupRoles(DirectoryEmailTests.AnalystSid, AppRole.Analyst), sids: DirectoryEmailTests.NoSidNames(),
                names: DirectoryEmailTests.Ad("Jordan Rivera", "jordan.rivera@contoso.com"))
            .TransformAsync(DirectoryEmailTests.WindowsSignIn(groupSids: DirectoryEmailTests.AnalystSid));

        var result = await _db.Distribution(user: DirectoryEmailTests.UserOf(principal)).SendTestAsync(Day, Edition.Team);

        result.Status.Should().Be(EmailSendStatus.Sent);
        _db.Email.Sent.Single().To.Should().Equal("jordan.rivera@contoso.com");
    }

    [Fact]
    public async Task SendTest_WithNoAddressInAd_SaysTheMailAttributeIsMissing()
    {
        await Brief();
        var principal = await new Tide.Web.Security.RoleClaimsTransformer(
                new OneGroupRoles(DirectoryEmailTests.AnalystSid, AppRole.Analyst), sids: DirectoryEmailTests.NoSidNames(),
                names: DirectoryEmailTests.Ad("Jordan Rivera", null))
            .TransformAsync(DirectoryEmailTests.WindowsSignIn(groupSids: DirectoryEmailTests.AnalystSid));

        await FluentActions.Invoking(() => _db.Distribution(user: DirectoryEmailTests.UserOf(principal)).SendTestAsync(Day, Edition.Team))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*no email address (the mail attribute)*");
        _db.Email.Sent.Should().BeEmpty();
    }

    // AD couldn't be reached this session, so there's no email claim: the address the mirror saved on an earlier visit.
    [Fact]
    public async Task SendTest_WithNoEmailClaim_GoesToTheAddressSavedFromAd()
    {
        await Brief();
        await using (var ctx = _db.Context())
        {
            ctx.Users.Add(new Tide.Domain.Entities.AppUser { Sid = _db.User.UserId, DisplayName = "Test Analyst", Email = " saved@example.com " });
            await ctx.SaveChangesAsync();
        }
        _db.User.Email = null;
        var svc = _db.Distribution();

        (await svc.TestAddressAsync()).Should().Be("saved@example.com");
        (await svc.SendTestAsync(Day, Edition.Team)).Status.Should().Be(EmailSendStatus.Sent);
        _db.Email.Sent.Single().To.Should().Equal("saved@example.com");
    }

    [Fact]
    public async Task SendTest_WithNoEmailClaim_AndNoneSaved_NeverFallsBackToTheUpn()
    {
        await Brief();
        _db.User.Email = null;
        var svc = _db.Distribution();

        (await svc.TestAddressAsync()).Should().BeNull("the UPN isn't an address");
        await FluentActions.Invoking(() => svc.SendTestAsync(Day, Edition.Team))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*no email address (the mail attribute)*");
        _db.Email.Sent.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Readers_SeeOnlySentPublicEditions_AndOnlyTheirImages(string provider)
    {
        _db = _db.On(provider);
        var s = await Brief();
        var svc = _db.Distribution();
        await svc.SendAsync(await svc.PlanAsync(Day, Edition.Team));
        await svc.SendAsync(await svc.PlanAsync(Day, Edition.Public));
        Guid publicCopy, teamOnlyCopy;
        await using (var ctx = _db.Context())
        {
            var sends = await ctx.BriefSends.Include(x => x.Images).ToListAsync();
            publicCopy = sends.Single(x => x.Edition == Edition.Public).Images.Single().FileId;
            teamOnlyCopy = sends.Single(x => x.Edition == Edition.Team).Images.Single(i => i.SourceFileId == s.AmberShotFile).FileId;
        }

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var published = await _db.Distribution().GetPublishedAsync(Day);
        published!.WebHtml.Should().Contain("Open article").And.NotContain("Partner-only article").And.NotContain("ISAC-only bullet");
        (await _db.Distribution().PublishedAsync()).Should().ContainSingle();

        var shots = _db.Screenshots();
        (await shots.OpenAsync(publicCopy)).Should().NotBeNull("it's in a sent company-wide edition");
        (await shots.OpenAsync(teamOnlyCopy)).Should().BeNull("a team send's images aren't published");
        (await shots.OpenAsync(s.OpenShotFile)).Should().BeNull("the live screenshot isn't what was published");
        (await shots.OpenAsync(s.SecretShotFile)).Should().BeNull();

        var team = () => _db.Distribution().SendsAsync(Day);
        await team.Should().ThrowAsync<ForbiddenException>("the send log is the team's");
    }

    [Fact]
    public async Task SharePoint_IsThePublicEdition_AndItsZipHasOnlyItsImages()
    {
        await Brief();
        var svc = _db.Distribution();

        var copy = await svc.SharePointAsync(Day, id => "files/" + id);
        copy.Html.Should().Contain("Open article").And.NotContain("Partner-only article").And.NotContain("secret caption").And.NotContain("ISAC-only bullet");
        copy.Images.Should().ContainSingle();

        var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await svc.SharePointImagesZipAsync(Day)));
        zip.Entries.Select(e => e.Name).Should().Equal("01-open-caption.png");
    }

    [Fact]
    public async Task Sending_NeedsPresentPermission_AndRecipients()
    {
        await Brief();
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "");
        var svc = _db.Distribution();
        var noOne = async () => await svc.SendAsync(await svc.PlanAsync(Day, Edition.Public));
        await noOne.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No one*");

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        var owner = () => _db.Distribution().PlanAsync(Day, Edition.Team);
        await owner.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Recipients_AreValidated()
    {
        var bad = () => _db.Settings().SaveRecipientsAsync(Edition.Team, "ok@example.org, not-an-address");
        await bad.Should().ThrowAsync<ArgumentException>().WithMessage("*not-an-address*");

        await _db.Settings().SaveRecipientsAsync(Edition.Team, "a@example.org\nA@example.org; b@example.org");
        (await _db.Settings().GetRecipientsAsync(Edition.Team)).Should().Equal("a@example.org", "b@example.org");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Send_WaitsForTheMeeting_UnlessThereWasNone_AndTheOtherEditionThenFollows(string provider)
    {
        _db = _db.On(provider);
        var articles = _db.Articles();
        var id = await articles.CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await articles.GetAsync(id))!);
        form.Bluf = "Patch today.";
        await articles.SaveAsync(form);
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, id);
        await _db.Settings().SaveRecipientsAsync(Edition.Team, "cti@example.org");
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "everyone@example.org");
        var svc = _db.Distribution();

        await FluentActions.Invoking(async () => await svc.SendAsync(await svc.PlanAsync(Day, Edition.Team))).Should()
            .ThrowAsync<InvalidOperationException>().WithMessage("End the meeting first*");
        (await svc.SendsAsync(Day)).Should().BeEmpty("nothing went");

        (await svc.SendAsync(await svc.PlanAsync(Day, Edition.Team), withoutMeeting: true)).Status.Should().Be(SendStatus.Sent);
        (await svc.SendAsync(await svc.PlanAsync(Day, Edition.Public))).Status.Should().Be(SendStatus.Sent, "once one edition went, the other follows");
    }

    [Fact]
    public async Task Send_IsRefusedBeforeTheBriefsDay_EvenWithoutAMeeting_ButATestCopyGoes()
    {
        var later = WorkingDays.Weekdays.Next(_db.Clock.Today.AddDays(1));
        var articles = _db.Articles();
        var id = await articles.CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await articles.GetAsync(id))!);
        form.Bluf = "Patch today.";
        await articles.SaveAsync(form);
        var brief = await _db.Briefs().GetOrStartAsync(later);
        await _db.Briefs().AddArticleAsync(brief.Id, id);
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "everyone@example.org");
        var svc = _db.Distribution();

        await FluentActions.Invoking(async () => await svc.SendAsync(await svc.PlanAsync(later, Edition.Public), withoutMeeting: true)).Should()
            .ThrowAsync<InvalidOperationException>().WithMessage("The brief for * can be sent from that day*");
        svc.SendBlocked(later, brief, withoutMeeting: true).Should().NotBeNull();
        (await svc.SendTestAsync(later, Edition.Public)).Status.Should().Be(EmailSendStatus.Sent, "a test copy to oneself can go early");
    }

    // A send the relay took whose outcome was never saved (the server stopped, or the save failed): left "sending",
    // <paramref name="ago"/> after it was frozen.
    private async Task<Guid> LeftSendingAsync(Edition edition, TimeSpan ago)
    {
        var svc = _db.Distribution();
        var send = await svc.SendAsync(await svc.PlanAsync(Day, edition));
        await using var ctx = _db.Context();
        var row = await ctx.BriefSends.SingleAsync(x => x.Id == send.Id);
        row.Status = SendStatus.Sending;
        row.SentAtUtc = DateTimeOffset.UtcNow - ago;
        (await ctx.Briefs.SingleAsync()).Status = BriefStatus.Presented;
        await ctx.SaveChangesAsync();
        return send.Id;
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ASendLeftSending_PastTheInFlightTime_MayHaveGone_ThenIsSettled(string provider)
    {
        _db = _db.On(provider);
        await Brief();
        var id = await LeftSendingAsync(Edition.Public, TimeSpan.FromMinutes(20));
        var svc = _db.Distribution();

        var plan = await svc.PlanAsync(Day, Edition.Public);
        plan.LastSentAtUtc.Should().NotBeNull("the confirmation warns that it may have gone");
        plan.LastOutcomeUnknown.Should().BeTrue();
        var log = (await svc.SendsAsync(Day)).Single(x => x.Id == id);
        log.Status.Should().Be(SendStatus.Unknown);
        log.Error.Should().Contain("didn't finish");
        var brief = (await _db.Briefs().GetAsync(Day))!;
        Tide.Application.Briefs.BriefSteps.Send(brief, await svc.SendsAsync(Day)).Detail.Should().Be("May have gone: check", "not \"Sending…\" for good");

        var settled = await svc.SettleAsync(id, went: true);

        settled.Status.Should().Be(SendStatus.Sent);
        (await _db.Briefs().GetAsync(Day))!.Status.Should().Be(BriefStatus.Sent);
        (await svc.PublishedAsync()).Should().ContainSingle(x => x.Id == id, "marked sent, it's archived for Readers");
        (await _db.Corrections().StartAsync(id)).Send.Id.Should().Be(id, "and it can be corrected");
        (await svc.PlanAsync(Day, Edition.Public)).LastOutcomeUnknown.Should().BeFalse("it went");
        await FluentActions.Invoking(() => svc.SettleAsync(id, went: false)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ASendStillWithTheRelay_CantBeSettled_AndOneMarkedNotSent_IsntWarnedAbout(string provider)
    {
        _db = _db.On(provider);
        await Brief();
        var id = await LeftSendingAsync(Edition.Team, TimeSpan.FromMinutes(1));
        var svc = _db.Distribution();

        (await svc.PlanAsync(Day, Edition.Team)).LastSentAtUtc.Should().BeNull("one still with the relay is the in-flight rule's");
        await FluentActions.Invoking(() => svc.SettleAsync(id, went: true)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*still with the relay*");

        await using (var ctx = _db.Context())
        {
            (await ctx.BriefSends.SingleAsync()).SentAtUtc = DateTimeOffset.UtcNow.AddHours(-1);
            await ctx.SaveChangesAsync();
        }
        (await svc.SettleAsync(id, went: false)).Status.Should().Be(SendStatus.Failed);
        (await svc.PlanAsync(Day, Edition.Team)).LastSentAtUtc.Should().BeNull("nobody has it");
        (await _db.Briefs().GetAsync(Day))!.Status.Should().Be(BriefStatus.Presented);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task SentWithoutAMeeting_FinishesTheBriefsArticles_SoLaterBriefsDontOfferThem(string provider)
    {
        _db = _db.On(provider);
        var articles = _db.Articles();
        var id = await articles.CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await articles.GetAsync(id))!);
        form.Bluf = "Patch today.";
        await articles.SaveAsync(form);
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, id);
        await _db.Settings().SaveRecipientsAsync(Edition.Team, "cti@example.org");
        var svc = _db.Distribution();

        (await svc.SendAsync(await svc.PlanAsync(Day, Edition.Team), withoutMeeting: true)).Status.Should().Be(SendStatus.Sent);

        (await articles.GetAsync(id))!.Status.Should().Be(ArticleStatus.Presented, "it went out, as ending the meeting would have marked it");
        var sent = (await _db.Briefs().GetAsync(Day))!;
        sent.Status.Should().Be(BriefStatus.Sent);
        sent.MeetingHeldAtUtc.Should().BeNull("no meeting is recorded");
        var next = await _db.Briefs().GetOrStartAsync(WorkingDays.Weekdays.Next(Day));
        (await _db.Briefs().CandidatesAsync(next.Id)).Where(c => c.Available).Should().NotContain(c => c.Id == id);

        // One sent so before the fix left its articles Ready: still not counted as available.
        await using (var ctx = _db.Context())
        {
            (await ctx.Articles.SingleAsync(a => a.Id == id)).Status = ArticleStatus.Ready;
            await ctx.SaveChangesAsync();
        }
        (await _db.Briefs().CandidatesAsync(next.Id)).Single(c => c.Id == id).Available.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ThePublishedList_IsPaged_ButItsFilterLooksThroughEveryBrief(string provider)
    {
        _db = _db.On(provider);
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "everyone@example.org");
        var svc = _db.Distribution();
        var days = new[] { WorkingDays.Weekdays.Previous(WorkingDays.Weekdays.Previous(Day)), WorkingDays.Weekdays.Previous(Day), Day };
        foreach (var (day, title) in days.Zip(["Lighthouse passphrase story", "Harbour crane outage", "Edge VPN zero-day"]))
        {
            var id = await _db.Articles().CreateAsync(title, "Vendor", null, TlpLevel.Clear);
            var form = ArticleForm.From((await _db.Articles().GetAsync(id))!);
            form.Bluf = "Patch today.";
            await _db.Articles().SaveAsync(form);
            var brief = await _db.Briefs().GetOrStartAsync(day);
            await _db.Briefs().AddArticleAsync(brief.Id, id);
            await svc.SendAsync(await svc.PlanAsync(day, Edition.Public), withoutMeeting: true);
        }

        (await svc.PublishedBriefsAsync(take: 2)).Select(p => p.Send.BriefDate).Should().Equal(Day, days[1]);
        (await svc.PublishedBriefsAsync(take: 3)).Should().HaveCount(3, "a bigger page reaches the older ones");
        (await svc.PublishedBriefsAsync("lighthouse", take: 2)).Should().ContainSingle()
            .Which.Send.BriefDate.Should().Be(days[0], "the filter isn't limited to the newest briefs");
        (await svc.PublishedBriefsAsync("patch", take: 2)).Should().HaveCount(2, "a filter's matches are paged too");
    }
}
