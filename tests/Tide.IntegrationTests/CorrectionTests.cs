using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Articles;
using Tide.Application.Distribution;
using Tide.Application.Files;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Corrections after a send (ADR 0015): a short frozen email to the send's recipients, cut like a send so it never holds
/// anything above its ceiling, and a TLP slip that withdraws a company-wide send from what Readers see.
/// </summary>
public class CorrectionTests : IDisposable
{
    private TestDb _db = new();
    private static readonly DateOnly Day = new(2026, 10, 26);

    public CorrectionTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private sealed record Setup(Guid Open, Guid Amber);

    // As DistributionTests' brief: an open article (a CLEAR and an AMBER bullet, an open and a RED screenshot) and an
    // AMBER article; recipients set for both editions.
    private async Task<Setup> Brief()
    {
        var articles = _db.Articles();
        var open = await articles.CreateAsync("Open article", "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await articles.GetAsync(open))!);
        form.Bluf = "Everyone may read this.";
        form.Bullets = [new() { Text = "public bullet" }, new() { Text = "ISAC-only bullet", Tlp = TlpLevel.Amber }, new() { Text = "second public bullet" }];
        await articles.SaveAsync(form);
        var shots = await _db.Screenshots().AddAsync(open, [new ScreenshotUpload("a.png", TestDb.Png), new ScreenshotUpload("b.png", TestDb.Png)]);
        form = ArticleForm.From((await articles.GetAsync(open))!);
        form.Screenshots.Single(s => s.Id == shots[0].Id).Caption = "open caption";
        var secret = form.Screenshots.Single(s => s.Id == shots[1].Id);
        secret.Caption = "secret caption";
        secret.Tlp = TlpLevel.Red;
        await articles.SaveAsync(form);

        var amber = await articles.CreateAsync("Partner-only article", "Partner", null, TlpLevel.Amber);
        var amberForm = ArticleForm.From((await articles.GetAsync(amber))!);
        amberForm.Bluf = "Shared by a partner.";
        await articles.SaveAsync(amberForm);

        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, open);
        await _db.Briefs().AddArticleAsync(brief.Id, amber);
        await _db.Briefs().MarkPresentedAsync(brief.Id);   // the meeting was held: a brief goes after it
        await _db.Settings().SaveRecipientsAsync(Edition.Team, "cti@example.org; soc@example.org");
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "everyone@example.org");
        return new Setup(open, amber);
    }

    private async Task<Tide.Domain.Entities.BriefSend> Send(Edition edition)
    {
        var svc = _db.Distribution();
        return await svc.SendAsync(await svc.PlanAsync(Day, edition));
    }

    private async Task Edit(Guid article, Action<ArticleForm> change)
    {
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);
        change(form);
        await _db.Articles().SaveAsync(form);
    }

    private static CorrectionRequest Fix(Guid sendId, Guid article, string note = "The first bullet was wrong.", int number = 1) =>
        new(sendId, false, [new CorrectedSection(number, article)], note);

    private static readonly string[] Restricted = ["ISAC-only bullet", "secret caption", "Partner-only article", "Shared by a partner."];

    [Fact]
    public async Task Correction_GoesToTheOriginalRecipients_WithTheNoteAndTheDiff_AndIsFrozenAndLogged()
    {
        var s = await Brief();
        var send = await Send(Edition.Public);
        await Edit(s.Open, f => f.Bullets[0].Text = "public bullet, corrected");
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "everyone@example.org; newcomer@example.org");
        var corrections = _db.Corrections();

        var plan = await corrections.PlanAsync(Fix(send.Id, s.Open));
        var row = await corrections.SendAsync(plan);

        plan.NewOnList.Should().Be(1, "the confirmation says the list differs");
        plan.Articles.Single().State.Should().Be(CorrectedState.Changed);
        row.Status.Should().Be(SendStatus.Sent);
        var mail = _db.Email.Sent[^1];
        mail.To.Should().Equal(["everyone@example.org"], "a correction goes to whoever had the send, and no one else");
        mail.Subject.Should().Be("Correction: " + send.Subject);
        mail.Html.Should().Contain("The first bullet was wrong.").And.Contain("• public bullet, corrected").And.Contain(">Was<").And.Contain(">Now<")
            .And.Contain("Article 1: Open article").And.NotContain("<style");
        mail.Images.Should().BeEmpty("a correction carries no images");
        await using var ctx = _db.Context();
        var frozen = await ctx.BriefCorrections.SingleAsync();
        frozen.CorrectsSendId.Should().Be(send.Id);
        frozen.EmailHtml.Should().Be(mail.Html);
        frozen.WebHtml.Should().Contain("<del>Was: • public bullet</del>").And.Contain("<ins>Now: • public bullet, corrected</ins>");
        frozen.ArticleIds.Should().Be(s.Open.ToString());

        // The archive and the next send are as they were: a correction is no send of the edition.
        var distribution = _db.Distribution();
        (await distribution.GetPublishedAsync(Day))!.Id.Should().Be(send.Id);
        (await distribution.PlanAsync(Day, Edition.Public)).LastSentAtUtc.Should().Be(send.SentAtUtc);
        (await distribution.SendAsync(await distribution.PlanAsync(Day, Edition.Public))).Status.Should().Be(SendStatus.Sent);
        (await corrections.CorrectionsAsync(Day)).Should().ContainSingle().Which.Id.Should().Be(row.Id);
    }

    [Fact]
    public async Task CompanyWideCorrection_NeverRepeatsAPartRaisedToAmberSinceTheSend_OnEitherSide()
    {
        var s = await Brief();
        var send = await Send(Edition.Public);
        await Edit(s.Open, f =>
        {
            f.Bullets[0].Tlp = TlpLevel.Amber;   // it went at CLEAR; it's AMBER now
            f.Bullets[2].Text = "second public bullet, reworded";
        });

        var plan = await _db.Corrections().PlanAsync(Fix(send.Id, s.Open));
        await _db.Corrections().SendAsync(plan);

        var article = plan.Articles.Single();
        article.RestrictedNow.Should().Be(1);
        article.Lines.Should().NotContain(l => l.Text.Contains("• public bullet", StringComparison.Ordinal) && !l.Text.Contains("second", StringComparison.Ordinal));
        var mail = _db.Email.Sent[^1];
        foreach (var text in new[] { mail.Html, mail.Text, plan.WebHtml })
        {
            text.Split('\n').Should().NotContain(l => l.TrimEnd().EndsWith("• public bullet", StringComparison.Ordinal) || l.Contains("• public bullet<", StringComparison.Ordinal));
            text.Should().Contain("second public bullet, reworded");
            foreach (var leak in Restricted) text.Should().NotContain(leak);
        }
        (plan.Marking <= TlpLevel.Green).Should().BeTrue();
    }

    [Fact]
    public async Task CompanyWideCorrection_OfAnArticleRaisedToAmber_SaysItLeft_AndRepeatsNothingOfIt()
    {
        var s = await Brief();
        var send = await Send(Edition.Public);
        await Edit(s.Open, f => f.Tlp = TlpLevel.Amber);

        var plan = await _db.Corrections().PlanAsync(Fix(send.Id, s.Open, "This article should not have gone company-wide."));

        plan.Articles.Single().State.Should().Be(CorrectedState.LeftTheEdition);
        plan.Articles.Single().Headline.Should().BeNull();
        plan.WebHtml.Should().Contain("Article 1").And.NotContain("Open article").And.NotContain("Everyone may read this.").And.NotContain("public bullet");
        await _db.Corrections().SendAsync(plan);
        _db.Email.Sent[^1].Html.Should().NotContain("Open article").And.NotContain("Everyone may read this.");
    }

    [Fact]
    public async Task TheNowSide_IsCutAtTheLowerCeiling_WhenAnAdministratorLoweredIt()
    {
        var s = await Brief();
        await _db.Settings().SaveAsync(TlpLevel.Amber, TlpLevel.AmberStrict);
        var send = await Send(Edition.Public);
        send.Ceiling.Should().Be(TlpLevel.Amber);
        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.AmberStrict);
        await Edit(s.Open, f => f.Bullets[0].Text = "public bullet, corrected");

        var plan = await _db.Corrections().PlanAsync(Fix(send.Id, s.Open));
        await _db.Corrections().SendAsync(plan);

        plan.Ceiling.Should().Be(TlpLevel.Green);
        plan.Marking.Should().Be(TlpLevel.Green, "it never exceeds the ceiling it was cut at");
        var mail = _db.Email.Sent[^1];
        mail.Subject.Should().EndWith("[TLP:GREEN]");
        foreach (var text in new[] { mail.Html, mail.Text, plan.WebHtml })
            text.Should().NotContain("ISAC-only bullet", "it went when the ceiling was AMBER, and is above the ceiling now").And.Contain("public bullet, corrected");
    }

    [Fact]
    public async Task AfterTheCeilingIsLowered_ACorrection_RepeatsNoLineThatWentAboveIt_EvenOnceDeletedOrLowered()
    {
        // B6-1, B7-1, B7-2: the company-wide edition went at AMBER, with an AMBER "why it matters" on the open article; the
        // ceiling is lowered to GREEN, then the AMBER bullet is deleted, the "why" rewritten (its old words carry no mark of
        // their own), and the AMBER article lowered to GREEN and retitled, so none is above the ceiling on the article now.
        var s = await Brief();
        await Edit(s.Open, f => { f.WhyItMatters = "Our VPN concentrators run this build."; f.WhyItMattersTlp = TlpLevel.Amber; });
        await _db.Settings().SaveAsync(TlpLevel.Amber, TlpLevel.AmberStrict);
        var send = await Send(Edition.Public);
        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.AmberStrict);
        await Edit(s.Open, f => { f.Bullets.RemoveAt(1); f.WhyItMatters = "We may run it."; f.WhyItMattersTlp = TlpLevel.Clear; });
        await Edit(s.Amber, f => { f.Tlp = TlpLevel.Green; f.Bluf = "Now public."; f.Title = "Public advisory"; });

        var plan = await _db.Corrections().PlanAsync(new CorrectionRequest(send.Id, false,
            [new CorrectedSection(1, s.Open), new CorrectedSection(2, s.Amber)], "Both articles changed."));
        await _db.Corrections().SendAsync(plan);

        plan.Marking.Should().Be(TlpLevel.Green);
        var mail = _db.Email.Sent[^1];
        foreach (var text in new[] { mail.Html, mail.Text, plan.WebHtml })
            text.Should().NotContain("ISAC-only bullet", "it went at AMBER, above the ceiling now, though it's gone from the article")
                .And.NotContain("Shared by a partner.", "its article went at AMBER, and is GREEN only since")
                .And.NotContain("Our VPN concentrators", "the \"why\" went at AMBER; its lines carried no mark of their own")
                .And.NotContain("Partner-only article", "the AMBER headline as it went isn't repeated either")
                .And.Contain("Now public.").And.Contain("Public advisory");
    }

    [Fact]
    public async Task ASlipAfterTheCeilingIsLowered_NamesTheArticleByNumber_EvenWhenAskedForTheHeadline()
    {
        // B8-1: the company-wide edition went at AMBER; once the ceiling is GREEN, "name the headline" would repeat the
        // AMBER article's headline under a GREEN marking.
        var s = await Brief();
        await _db.Settings().SaveAsync(TlpLevel.Amber, TlpLevel.AmberStrict);
        var send = await Send(Edition.Public);
        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.AmberStrict);

        var plan = await _db.Corrections().PlanAsync(new CorrectionRequest(send.Id, true, [new CorrectedSection(2, s.Amber)], null, NameHeadline: true));
        await _db.Corrections().SendAsync(plan, acknowledgedNoRecall: true);

        foreach (var text in new[] { _db.Email.Sent[^1].Html, _db.Email.Sent[^1].Text, plan.WebHtml })
            text.Should().NotContain("Partner-only article").And.Contain("article 2");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ATlpSlip_RepeatsNothingThatSlipped_AndWithdrawsTheSendFromReaders_ButNotFromTheTeam(string provider)
    {
        _db = _db.On(provider);
        var s = await Brief();
        await _db.Settings().SaveAsync(TlpLevel.Amber, TlpLevel.AmberStrict);   // set too high by mistake
        var send = await Send(Edition.Public);
        var image = await FirstImageAsync(send.Id);
        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.AmberStrict);
        (await _db.Distribution().PublishedBriefsAsync()).Should().ContainSingle();   // its words are in the filter's cache
        _db.Published.TryGet(send.Id, out _).Should().BeTrue();

        var slip = new CorrectionRequest(send.Id, true, [new CorrectedSection(2, s.Amber)], null);
        var plan = await _db.Corrections().PlanAsync(slip);
        var unticked = () => _db.Corrections().SendAsync(plan);
        await unticked.Should().ThrowAsync<InvalidOperationException>().WithMessage("*can't be recalled*");
        var row = await _db.Corrections().SendAsync(plan, acknowledgedNoRecall: true);

        row.Status.Should().Be(SendStatus.Sent);
        var mail = _db.Email.Sent[^1];
        foreach (var text in new[] { mail.Html, mail.Text, plan.WebHtml })
        {
            foreach (var leak in Restricted) text.Should().NotContain(leak, "a slip never repeats what leaked, the headline included");
            text.Should().Contain("article 2").And.Contain("cannot be recalled");
        }
        _db.Published.TryGet(send.Id, out _).Should().BeFalse("its entry in the filter's cache is dropped");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var reader = _db.Distribution();
        (await reader.GetPublishedAsync(Day)).Should().BeNull();
        var day = (await reader.PublishedBriefsAsync()).Should().ContainSingle().Subject;
        day.Withdrawn.Should().BeTrue();
        day.Headlines.Should().BeEmpty();
        day.Corrections.Should().ContainSingle(c => c.IsTlpSlip);
        foreach (var q in new[] { "Partner", "partner Shared", "ISAC-only", "Open article", "Everyone" })
            (await reader.PublishedBriefsAsync(q)).Should().BeEmpty(q);
        var notice = (await reader.PublishedCorrectionsAsync(Day)).Should().ContainSingle().Subject;
        foreach (var leak in Restricted) notice.WebHtml.Should().NotContain(leak);
        (await _db.Screenshots().OpenAsync(image)).Should().BeNull("the withdrawn send's images aren't served to Readers");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        (await _db.Distribution().GetSendAsync(send.Id))!.WebHtml.Should().Contain("Partner-only article", "the team keeps the frozen row");
        (await _db.Screenshots().OpenAsync(image)).Should().NotBeNull("the team still sees it");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ATlpSlip_AlsoWithdrawsTheSendsOtherCorrections_FromReadersAndTheirSearch_AndNoCorrectionOfItCanFollow(string provider)
    {
        _db = _db.On(provider);
        var s = await Brief();
        await _db.Settings().SaveAsync(TlpLevel.Amber, TlpLevel.AmberStrict);   // set too high by mistake
        var send = await Send(Edition.Public);
        await Edit(s.Amber, f => f.Bluf = "Shared by a partner, reworded.");
        var reworded = await _db.Corrections().SendAsync(await _db.Corrections().PlanAsync(Fix(send.Id, s.Amber, "The partner's wording.", number: 2)));
        await Edit(s.Open, f => f.Bullets[0].Text = "public bullet, corrected");
        await _db.Corrections().SendAsync(await _db.Corrections().PlanAsync(Fix(send.Id, s.Open)));
        reworded.WebHtml.Should().Contain("Was: Bottom line: Shared by a partner.", "the correction quotes what went");
        (await _db.Distribution().PublishedBriefsAsync("reworded")).Should().ContainSingle("its words are in the filter's cache");

        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.AmberStrict);
        var slip = await _db.Corrections().PlanAsync(new CorrectionRequest(send.Id, true, [new CorrectedSection(2, s.Amber)], null));
        var slipped = await _db.Corrections().SendAsync(slip, acknowledgedNoRecall: true);
        var later = await _db.Distribution().SendAsync(await _db.Distribution().PlanAsync(Day, Edition.Public));   // sent again, right
        var fixOfLater = await _db.Corrections().SendAsync(await _db.Corrections().PlanAsync(Fix(later.Id, s.Open, "Another word.")));

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var reader = _db.Distribution();
        (await reader.PublishedCorrectionsAsync(Day)).Select(c => c.Id).Should().BeEquivalentTo([slipped.Id, fixOfLater.Id],
            "of the withdrawn send only the slip shows; the next send's correction does");
        foreach (var c in await reader.PublishedCorrectionsAsync(Day))
            foreach (var leak in Restricted) c.WebHtml.Should().NotContain(leak);
        foreach (var q in new[] { "reworded", "Partner", "partner's wording", "first bullet was wrong" })
            (await reader.PublishedBriefsAsync(q)).Should().BeEmpty(q);
        (await reader.PublishedBriefsAsync()).Single().Corrections.Select(c => c.Id).Should().NotContain(reworded.Id);

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        var again = () => _db.Corrections().PlanAsync(Fix(send.Id, s.Open, "Removed a bullet."));
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("A TLP slip withdrew that send*");
        (await _db.Corrections().PlanAsync(new CorrectionRequest(send.Id, true, [new CorrectedSection(1, s.Open)], null))).Articles.Should()
            .ContainSingle("another slip of it carries no words, so it may still go");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ATlpSlipOfTheDaysLatestSend_AlsoWithdrawsItsEarlierSends_ButNotOneSentAfter(string provider)
    {
        _db = _db.On(provider);
        var s = await Brief();
        await _db.Settings().SaveAsync(TlpLevel.Amber, TlpLevel.AmberStrict);   // set too high by mistake
        var first = await Send(Edition.Public);
        var firstImage = await FirstImageAsync(first.Id);
        await Edit(s.Open, f => f.Bullets[0].Text = "public bullet, reworded");
        var second = await Send(Edition.Public);   // the edition sent again, the partner's article still in it
        (await _db.Distribution().GetPublishedAsync(Day))!.Id.Should().Be(second.Id);

        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.AmberStrict);
        var slip = await _db.Corrections().PlanAsync(new CorrectionRequest(second.Id, true, [new CorrectedSection(2, s.Amber)], null));
        await _db.Corrections().SendAsync(slip, acknowledgedNoRecall: true);

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var reader = _db.Distribution();
        (await reader.GetPublishedAsync(Day)).Should().BeNull("the earlier send carried what slipped too, so it doesn't come back");
        (await reader.PublishedBriefsAsync()).Should().ContainSingle().Which.Withdrawn.Should().BeTrue();
        foreach (var q in new[] { "Partner", "Shared by a partner", "Open article" })
            (await reader.PublishedBriefsAsync(q)).Should().BeEmpty(q);
        (await _db.Screenshots().OpenAsync(firstImage)).Should().BeNull("nor are its images served");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var fix = () => _db.Corrections().PlanAsync(Fix(first.Id, s.Open));
        await fix.Should().ThrowAsync<InvalidOperationException>().WithMessage("A TLP slip withdrew that send*");
        var right = await Send(Edition.Public);   // sent again after the slip, at the right ceiling

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        (await reader.GetPublishedAsync(Day))!.Id.Should().Be(right.Id);
        var day = (await reader.PublishedBriefsAsync()).Should().ContainSingle().Subject;
        day.Withdrawn.Should().BeFalse();
        day.Headlines.Should().Equal(["Open article"]);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ATlpSlipOfAnEarlierSend_AlsoWithdrawsALaterReSend_MadeBeforeTheSlip(string provider)
    {
        _db = _db.On(provider);
        var s = await Brief();
        await _db.Settings().SaveAsync(TlpLevel.Amber, TlpLevel.AmberStrict);   // set too high by mistake
        var first = await Send(Edition.Public);
        var again = await Send(Edition.Public);   // the same edition again, to reach someone added late
        var againImage = await FirstImageAsync(again.Id);

        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.AmberStrict);
        var slip = await _db.Corrections().PlanAsync(new CorrectionRequest(first.Id, true, [new CorrectedSection(2, s.Amber)], null));
        await _db.Corrections().SendAsync(slip, acknowledgedNoRecall: true);

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var reader = _db.Distribution();
        (await reader.GetPublishedAsync(Day)).Should().BeNull("the re-send went before the slip, with what slipped in it");
        (await reader.PublishedBriefsAsync()).Should().ContainSingle().Which.Withdrawn.Should().BeTrue();
        (await reader.PublishedBriefsAsync("Shared by a partner")).Should().BeEmpty();
        (await _db.Screenshots().OpenAsync(againImage)).Should().BeNull();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var right = await Send(Edition.Public);   // sent after the slip, at the right ceiling
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        (await reader.GetPublishedAsync(Day))!.Id.Should().Be(right.Id);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ATlpSlipOfAnEarlierSend_AlsoWithdrawsAReSendThatPutItRight_WhichTheEditionSentAgainReplaces(string provider)
    {
        _db = _db.On(provider);
        var s = await Brief();
        await _db.Settings().SaveAsync(TlpLevel.Amber, TlpLevel.AmberStrict);   // set too high by mistake
        var first = await Send(Edition.Public);
        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.AmberStrict);
        var putRight = await Send(Edition.Public);   // put right and sent again, the partner's article left out; then the slip
        var slip = await _db.Corrections().SendAsync(await _db.Corrections().PlanAsync(new CorrectionRequest(first.Id, true, [new CorrectedSection(2, s.Amber)], null)),
            acknowledgedNoRecall: true);

        // Made before the slip, the re-send goes with it: a slip doesn't say which line slipped, so "put right" can't be told.
        (await _db.Distribution().WithdrawnSendsAsync(Day)).Should().BeEquivalentTo([first.Id, putRight.Id]);
        await FluentActions.Invoking(() => _db.Corrections().PlanAsync(Fix(putRight.Id, s.Open))).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A TLP slip withdrew that send*");
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var reader = _db.Distribution();
        (await reader.GetPublishedAsync(Day)).Should().BeNull();
        (await reader.PublishedBriefsAsync()).Should().ContainSingle().Which.Withdrawn.Should().BeTrue();
        (await reader.PublishedCorrectionsAsync(Day)).Should().ContainSingle().Which.Id.Should().Be(slip.Id);

        // The edition sent again after the slip is the day's: published, and it can be corrected.
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var again = await Send(Edition.Public);
        (await _db.Distribution().WithdrawnSendsAsync(Day)).Should().NotContain(again.Id);
        await Edit(s.Open, f => f.Bullets[0].Text = "public bullet, corrected");
        var fix = await _db.Corrections().SendAsync(await _db.Corrections().PlanAsync(Fix(again.Id, s.Open)));
        fix.Status.Should().Be(SendStatus.Sent);
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        (await reader.GetPublishedAsync(Day))!.Id.Should().Be(again.Id);
        var day = (await reader.PublishedBriefsAsync()).Should().ContainSingle().Subject;
        day.Withdrawn.Should().BeFalse();
        day.Headlines.Should().Equal(["Open article"]);
        (await reader.PublishedCorrectionsAsync(Day)).Select(c => c.Id).Should().BeEquivalentTo([slip.Id, fix.Id]);
        (await reader.PublishedBriefsAsync("Partner")).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ATlpSlip_WithdrawsTheSendOnceConfirmed_WhileItsEmailIsWithTheRelay_AndWhenTheRelayRefusesIt(string provider)
    {
        _db = _db.On(provider);
        var s = await Brief();
        await _db.Settings().SaveAsync(TlpLevel.Amber, TlpLevel.AmberStrict);   // set too high by mistake
        var send = await Send(Edition.Public);
        var image = await FirstImageAsync(send.Id);
        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.AmberStrict);
        (await _db.Distribution().PublishedBriefsAsync("Partner")).Should().ContainSingle();   // its words are in the filter's cache
        var corrections = _db.Corrections();
        var request = new CorrectionRequest(send.Id, true, [new CorrectedSection(2, s.Amber)], null);

        Tide.Domain.Entities.BriefSend? seen = send;
        _db.Email.Fault = _ =>
        {
            // A Reader opens the archive while the slip is with the relay (a long list takes minutes).
            var roles = _db.User.Roles;
            _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
            seen = _db.Distribution().GetPublishedAsync(Day).GetAwaiter().GetResult();
            _db.User.Roles = roles;
            return null;
        };
        _db.Email.Result = new EmailSendResult(EmailSendStatus.Failed, Error: "relay down");
        var refused = await corrections.SendAsync(await corrections.PlanAsync(request), acknowledgedNoRecall: true);
        _db.Email.Fault = null;

        refused.Status.Should().Be(SendStatus.Failed);
        seen.Should().BeNull("frozen, the slip has withdrawn the send");
        _db.Published.TryGet(send.Id, out _).Should().BeFalse("its entry in the filter's cache is dropped");
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var reader = _db.Distribution();
        (await reader.GetPublishedAsync(Day)).Should().BeNull("a refused email doesn't put back what the team said slipped");
        var day = (await reader.PublishedBriefsAsync()).Should().ContainSingle().Subject;
        day.Withdrawn.Should().BeTrue();
        day.Corrections.Should().ContainSingle().Which.Id.Should().Be(refused.Id);
        (await reader.PublishedBriefsAsync("Partner")).Should().BeEmpty();
        (await reader.PublishedCorrectionsAsync(Day)).Should().ContainSingle("the notice says why it's gone").Which.Id.Should().Be(refused.Id);
        (await _db.Screenshots().OpenAsync(image)).Should().BeNull();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        await FluentActions.Invoking(() => corrections.PlanAsync(Fix(send.Id, s.Open))).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A TLP slip withdrew that send*");
        _db.Email.Result = new EmailSendResult(EmailSendStatus.Sent);
        var again = await corrections.SendAsync(await corrections.PlanAsync(request), acknowledgedNoRecall: true);   // to reach the inboxes
        again.Status.Should().Be(SendStatus.Sent);
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        (await reader.PublishedCorrectionsAsync(Day)).Should().ContainSingle("the refused one and the one that went are one notice").Which.Id.Should().Be(again.Id);
        (await reader.PublishedBriefsAsync()).Single().Corrections.Should().ContainSingle().Which.Id.Should().Be(again.Id);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ATlpSlipLeftSending_MayHaveGone_AndMarkedSent_WithdrawsTheSend(string provider)
    {
        _db = _db.On(provider);
        var s = await Brief();
        var send = await Send(Edition.Public);
        var slip = await _db.Corrections().PlanAsync(new CorrectionRequest(send.Id, true, [new CorrectedSection(1, s.Open)], null));
        var row = await _db.Corrections().SendAsync(slip, acknowledgedNoRecall: true);
        await using (var ctx = _db.Context())
        {
            // The server stopped as the relay took it: the slip is left "sending", long enough ago that it never will finish.
            (await ctx.BriefSends.SingleAsync(x => x.Id == send.Id)).SentAtUtc = DateTimeOffset.UtcNow.AddHours(-1);
            var c = await ctx.BriefCorrections.SingleAsync(x => x.Id == row.Id);
            c.Status = SendStatus.Sending;
            c.SentAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30);
            await ctx.SaveChangesAsync();
        }
        var corrections = _db.Corrections();
        var log = (await corrections.CorrectionsAsync(Day)).Single();
        log.Status.Should().Be(SendStatus.Unknown);
        log.Error.Should().StartWith("This correction didn't finish").And.EndWith("then mark it sent or not.");
        (await _db.Distribution().GetPublishedAsync(Day)).Should().BeNull("the team confirmed it slipped: withdrawing the send doesn't wait on the slip's email");

        var settled = await corrections.SettleAsync(row.Id, went: true);

        settled.Status.Should().Be(SendStatus.Sent);
        (await corrections.CorrectionsAsync(Day)).Single().Error.Should().Contain("checked and marked it sent");
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        (await _db.Distribution().GetPublishedAsync(Day)).Should().BeNull("the send it withdrew is still the team's only");
        (await _db.Distribution().PublishedBriefsAsync()).Should().ContainSingle().Which.Withdrawn.Should().BeTrue();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        await FluentActions.Invoking(() => corrections.SettleAsync(row.Id, went: false)).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Only a correction whose outcome isn't known*");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ACorrectionStillWithTheRelay_CantBeSettled_AndOneMarkedNotSent_IsntPublished(string provider)
    {
        _db = _db.On(provider);
        var s = await Brief();
        var send = await Send(Edition.Public);
        await Edit(s.Open, f => f.Bullets[0].Text = "public bullet, corrected");
        var corrections = _db.Corrections();
        var row = await corrections.SendAsync(await corrections.PlanAsync(Fix(send.Id, s.Open)));
        await using (var ctx = _db.Context())
        {
            (await ctx.BriefCorrections.SingleAsync(x => x.Id == row.Id)).Status = SendStatus.Sending;
            await ctx.SaveChangesAsync();
        }
        await FluentActions.Invoking(() => corrections.SettleAsync(row.Id, went: true)).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*still with the relay*");

        await using (var ctx = _db.Context())
        {
            (await ctx.BriefCorrections.SingleAsync(x => x.Id == row.Id)).Status = SendStatus.Unknown;
            await ctx.SaveChangesAsync();
        }
        (await corrections.SettleAsync(row.Id, went: false)).Status.Should().Be(SendStatus.Failed);
        (await _db.Distribution().PublishedCorrectionsAsync(Day)).Should().BeEmpty("nobody has it");
        (await corrections.PlanAsync(Fix(send.Id, s.Open))).LastSentAtUtc.Should().BeNull();
    }

    private async Task<Guid> FirstImageAsync(Guid sendId)
    {
        await using var ctx = _db.Context();
        return (await ctx.BriefSendImages.FirstAsync(i => i.SendId == sendId)).FileId;
    }

    [Fact]
    public async Task ATlpSlip_NamesTheHeadline_OnlyWhenTheWriterSaysItIsntWhatSlipped()
    {
        var s = await Brief();
        var send = await Send(Edition.Public);

        var named = await _db.Corrections().PlanAsync(new CorrectionRequest(send.Id, true, [new CorrectedSection(1, s.Open)], null, NameHeadline: true));
        var numbered = await _db.Corrections().PlanAsync(new CorrectionRequest(send.Id, true, [new CorrectedSection(1, s.Open)], null));

        named.WebHtml.Should().Contain("article 1 (“Open article”)");
        numbered.WebHtml.Should().Contain("article 1.").And.NotContain("Open article");
    }

    [Fact]
    public async Task ASlipNoteAboveTheCeiling_AndACorrectionWithoutANoteOrArticle_AreRefused()
    {
        var s = await Brief();
        var send = await Send(Edition.Public);
        var c = _db.Corrections();

        var high = () => c.PlanAsync(new CorrectionRequest(send.Id, true, [], "an amber detail", TlpLevel.Amber));
        await high.Should().ThrowAsync<InvalidOperationException>().WithMessage("*above what this correction may carry*");
        var noNote = () => c.PlanAsync(Fix(send.Id, s.Open, note: "  "));
        await noNote.Should().ThrowAsync<InvalidOperationException>().WithMessage("*needs a note*");
        var noArticle = () => c.PlanAsync(new CorrectionRequest(send.Id, false, [], "a note"));
        await noArticle.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Pick the article*");
        var wrong = () => c.PlanAsync(Fix(send.Id, s.Amber));
        await wrong.Should().ThrowAsync<InvalidOperationException>().WithMessage("*isn't the article picked*");
        var missing = () => c.PlanAsync(Fix(send.Id, s.Open, number: 7));
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no article 7*");
    }

    [Fact]
    public async Task Send_IsRefused_WhenTheCorrectionChangedSinceTheConfirmation()
    {
        var s = await Brief();
        var send = await Send(Edition.Public);
        await Edit(s.Open, f => f.Bullets[0].Text = "public bullet, corrected");
        var c = _db.Corrections();
        var plan = await c.PlanAsync(Fix(send.Id, s.Open));

        await Edit(s.Open, f => f.Bullets[0].Text = "public bullet, corrected again");
        var act = () => c.SendAsync(plan);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed since you confirmed*");
        _db.Email.Sent.Should().ContainSingle("only the send itself went");
        var again = await c.PlanAsync(plan.Request);
        again.Fingerprint.Should().NotBe(plan.Fingerprint);
        (await c.SendAsync(again)).Status.Should().Be(SendStatus.Sent);

        // A correction that went since this confirmation refuses it too, and the next confirmation says so.
        var stale = await c.PlanAsync(plan.Request);
        await c.SendAsync(await c.PlanAsync(plan.Request));
        var late = () => c.SendAsync(stale);
        await late.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed since you confirmed*");
        (await c.PlanAsync(plan.Request)).LastSentBy.Should().Be(_db.User.DisplayName);
    }

    [Fact]
    public async Task Send_IsRefused_WhileAnotherCorrectionOfTheSendIsWithTheRelay()
    {
        var s = await Brief();
        var send = await Send(Edition.Public);
        var relay = new HeldEmailSender();
        var c = _db.Corrections(relay);
        var plan = await c.PlanAsync(Fix(send.Id, s.Open));

        var first = c.SendAsync(plan);
        await relay.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = () => c.SendAsync(plan);

        await second.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is sending a correction of this send now*");
        relay.Release.SetResult();
        (await first).Status.Should().Be(SendStatus.Sent);
        relay.Calls.Should().Be(1);
        await using var ctx = _db.Context();
        (await ctx.BriefCorrections.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task OnlyASendTheRelayTook_CanBeCorrected()
    {
        var s = await Brief();
        await _db.Distribution().SendTestAsync(Day, Edition.Public);
        _db.Email.Result = new EmailSendResult(EmailSendStatus.Failed, "The relay couldn't be reached.");
        var failed = await Send(Edition.Public);
        Guid test;
        await using (var ctx = _db.Context()) test = (await ctx.BriefSends.SingleAsync(x => x.IsTest)).Id;
        var unknown = await _db.Distribution(new ThrowingEmailSender()).SendAsync(await _db.Distribution().PlanAsync(Day, Edition.Public));
        unknown.Status.Should().Be(SendStatus.Unknown);

        foreach (var id in new[] { test, failed.Id, unknown.Id })
        {
            var act = () => _db.Corrections().PlanAsync(Fix(id, s.Open));
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Only a send the relay took*");
            var start = () => _db.Corrections().StartAsync(id);
            await start.Should().ThrowAsync<InvalidOperationException>();
        }
    }

    [Fact]
    public async Task Corrections_NeedPresentPermission_AndTheLogIsTheTeams()
    {
        var s = await Brief();
        var send = await Send(Edition.Team);

        foreach (var role in new[] { AppRole.Reader, AppRole.ActionOwner })
        {
            _db.User.Roles = new HashSet<AppRole> { role };
            var start = () => _db.Corrections().StartAsync(send.Id);
            await start.Should().ThrowAsync<ForbiddenException>();
            var plan = () => _db.Corrections().PlanAsync(Fix(send.Id, s.Open));
            await plan.Should().ThrowAsync<ForbiddenException>();
        }
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var log = () => _db.Corrections().CorrectionsAsync(Day);
        await log.Should().ThrowAsync<ForbiddenException>();
        var one = () => _db.Corrections().GetAsync(Guid.NewGuid());
        await one.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ATeamCorrection_IsCutAtTheTeamCeiling_AndListsTheSendsArticles()
    {
        var s = await Brief();
        var send = await Send(Edition.Team);
        await Edit(s.Amber, f => f.Bluf = "Shared by a partner, corrected.");

        var start = await _db.Corrections().StartAsync(send.Id);
        start.Sections.Select(x => (x.Number, x.ArticleId)).Should().Equal((1, s.Open), (2, s.Amber));
        start.Send.WebHtml.Should().BeNull("the form doesn't need the frozen copy");
        var plan = await _db.Corrections().PlanAsync(new CorrectionRequest(send.Id, false, [new CorrectedSection(2, s.Amber)], "The partner's summary changed."));

        plan.Recipients.Should().BeEquivalentTo(["cti@example.org", "soc@example.org"]);
        plan.Marking.Should().Be(TlpLevel.Amber);
        plan.WebHtml.Should().Contain("Now: Bottom line: Shared by a partner, corrected.").And.NotContain("secret caption");
    }

    [Fact]
    public async Task AnOlderSend_IsMatchedByHeadline_OrByTheWriter_WhenTheHeadlineChanged()
    {
        var s = await Brief();
        var send = await Send(Edition.Public);
        await using (var ctx = _db.Context())
        {
            // A send frozen before headings named their article.
            var row = await ctx.BriefSends.SingleAsync(x => x.Id == send.Id);
            row.WebHtml = row.WebHtml!.Replace($" {SentSections.ArticleAttribute}=\"{s.Open}\"", "", StringComparison.Ordinal);
            await ctx.SaveChangesAsync();
        }

        (await _db.Corrections().StartAsync(send.Id)).Sections.Single().ArticleId.Should().Be(s.Open, "matched by its headline");

        await Edit(s.Open, f => f.Title = "Open article, retitled");
        var start = await _db.Corrections().StartAsync(send.Id);
        start.Sections.Single().ArticleId.Should().BeNull();
        start.BriefArticles.Should().Contain(a => a.Id == s.Open);
        var plan = await _db.Corrections().PlanAsync(Fix(send.Id, s.Open, "Retitled."));
        plan.Articles.Single().Lines.Should().Contain(l => l.Kind == DiffKind.Added && l.Text == "Open article, retitled");
    }

    [Fact]
    public async Task TheMigration_AddsTheCorrectionsTable()
    {
        await using var ctx = _db.Context();
        (await ctx.Database.GetAppliedMigrationsAsync()).Should().Contain(m => m.EndsWith("_BriefCorrections", StringComparison.Ordinal));
        (await ctx.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await ctx.BriefCorrections.CountAsync()).Should().Be(0);
    }
}
