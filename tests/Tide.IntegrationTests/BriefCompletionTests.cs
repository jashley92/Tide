using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Briefs;
using Tide.Application.Reviews;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>A brief finished without email (email off on the server), and the optional SharePoint acknowledgement.</summary>
public class BriefCompletionTests : IDisposable
{
    private readonly TestDb _db = new();
    private static readonly DateOnly Day = new(2026, 10, 26);

    public BriefCompletionTests()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        _db.Email.Enabled = false;
    }

    public void Dispose() => _db.Dispose();

    private ReviewService Reviews() => new(_db, _db.User, _db.Rotation(), _db.Clock, TimeProvider.System);

    private async Task<(Guid Brief, Guid Article)> Brief(bool ended = true)
    {
        var articles = _db.Articles();
        var id = await articles.CreateAsync("Distributed story", "Vendor", null, TlpLevel.Clear);
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, id);
        if (ended) await _db.Briefs().MarkPresentedAsync(brief.Id);
        return (brief.Id, id);
    }

    private async Task<Brief> Read()
    {
        await using var ctx = _db.Context();
        return await ctx.Briefs.AsNoTracking().Include(b => b.Items).ThenInclude(i => i.Article).SingleAsync(b => b.BriefDate == Day);
    }

    [Fact]
    public async Task MarkDistributed_EmailOff_FinishesTheBrief_LocksItsNumbers_AndRecordsWhoAndWhen()
    {
        await Brief();
        (await _db.Metrics().IsLockedAsync(Day)).Should().BeFalse();

        await _db.Distribution().MarkDistributedAsync(Day);

        var brief = await Read();
        brief.Status.Should().Be(BriefStatus.Sent, "it's finished as an emailed brief is");
        brief.DistributedByName.Should().Be(_db.User.DisplayName);
        brief.DistributedById.Should().Be(_db.User.UserId);
        brief.DistributedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        (await _db.Metrics().IsLockedAsync(Day)).Should().BeTrue("what went out is the record");

        await using var ctx = _db.Context();
        (await ctx.BriefSends.AnyAsync()).Should().BeFalse("nothing was emailed");
        var audit = await ctx.AuditLog.Where(e => e.EntityType == nameof(Tide.Domain.Entities.Brief) && e.Action == "Modified").ToListAsync();
        audit.Should().Contain(e => e.ChangesJson!.Contains(nameof(Tide.Domain.Entities.Brief.DistributedByName)) && e.UserName == _db.User.DisplayName);
        (await _db.Distribution().PublishedAsync()).Should().BeEmpty("nothing was frozen and sent company-wide");

        var steps = BriefSteps.For(brief, 0, 0, "9:00 AM", []);
        steps.Single(s => s.Kind == BriefStepKind.Send).Should().Match<BriefStep>(s => s.Detail == "Distributed" && s.State == StepState.Done);
        BriefSteps.StatusLabel(brief.Status, true, false, false).Should().Be("Distributed");
        (await _db.Briefs().ListAsync()).Single().DistributedAtUtc.Should().NotBeNull("the archive shows it as distributed");
    }

    [Fact]
    public async Task MarkDistributed_WithoutAMeeting_FinishesItsArticles_AsASendWould()
    {
        var (_, article) = await Brief(ended: false);

        var refused = () => _db.Distribution().MarkDistributedAsync(Day);
        await refused.Should().ThrowAsync<InvalidOperationException>().WithMessage("End the meeting first*");

        await _db.Distribution().MarkDistributedAsync(Day, withoutMeeting: true);

        var brief = await Read();
        brief.Status.Should().Be(BriefStatus.Sent);
        brief.Items.Single(i => i.ArticleId == article).Article!.Status.Should().Be(ArticleStatus.Presented);
    }

    [Fact]
    public async Task MarkDistributed_IsRefused_WithEmailOn_OnceDone_AndWhileReviewIsRequired()
    {
        await Brief();
        _db.Email.Enabled = true;
        var withEmail = () => _db.Distribution().MarkDistributedAsync(Day);
        await withEmail.Should().ThrowAsync<InvalidOperationException>().WithMessage("Email is on*");

        _db.Email.Enabled = false;
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        await Reviews().SaveRuleAsync(ReviewRule.AmberOrThreats);
        var article = (await Read()).Items.Single().Article!;
        var form = Tide.Application.Articles.ArticleForm.From((await _db.Articles().GetAsync(article.Id))!);
        form.Classification = Classification.Threat;
        form.Priority = Priority.P2;
        await _db.Articles().SaveAsync(form);
        var held = () => _db.Distribution().MarkDistributedAsync(Day);
        await held.Should().ThrowAsync<InvalidOperationException>().WithMessage("Review is required before the brief is marked distributed*");
        await Reviews().SaveRuleAsync(ReviewRule.Off);

        await _db.Distribution().MarkDistributedAsync(Day);
        var again = () => _db.Distribution().MarkDistributedAsync(Day);
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already distributed*");
    }

    [Fact]
    public async Task MarkDistributed_NeedsSomeoneWhoSends()
    {
        await Brief();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var act = () => _db.Distribution().MarkDistributedAsync(Day);
        await act.Should().ThrowAsync<Tide.Application.Security.ForbiddenException>();
    }

    [Fact]
    public async Task SharePoint_Required_KeepsTheBriefOpenUntilMarkedPosted_WhoAndWhenAreKept_AndItCanBeTakenBack()
    {
        await Brief();
        _db.DistributionOptions.RequireSharePoint = true;
        await _db.Distribution().MarkDistributedAsync(Day);
        var svc = _db.Distribution();
        svc.SharePointRequired.Should().BeTrue();

        var brief = await Read();
        var send = BriefSteps.For(brief, 0, 0, "9:00 AM", [], sharePointRequired: true).Single(s => s.Kind == BriefStepKind.Send);
        send.Detail.Should().Be("Distributed · SharePoint to post");
        send.State.Should().Be(StepState.Current, "the brief isn't finished yet");
        BriefSteps.Next(brief.Status, 1, BriefSteps.SharePointDue(brief, true)).Should().Be(BriefNext.PostToSharePoint);
        BriefSteps.StatusLabel(brief.Status, true, false, true).Should().Be("SharePoint to post");

        await svc.MarkSharePointPostedAsync(Day);

        brief = await Read();
        brief.SharePointPostedByName.Should().Be(_db.User.DisplayName);
        brief.SharePointPostedAtUtc.Should().NotBeNull();
        BriefSteps.For(brief, 0, 0, "9:00 AM", [], sharePointRequired: true).Single(s => s.Kind == BriefStepKind.Send)
            .Should().Match<BriefStep>(s => s.Detail == "Distributed" && s.State == StepState.Done);
        BriefSteps.Next(brief.Status, 1, BriefSteps.SharePointDue(brief, true)).Should().Be(BriefNext.ViewAsSent);
        await using (var ctx = _db.Context())
            (await ctx.AuditLog.AnyAsync(e => e.EntityType == nameof(Tide.Domain.Entities.Brief) && e.ChangesJson!.Contains(nameof(Tide.Domain.Entities.Brief.SharePointPostedByName))))
                .Should().BeTrue("who marked it posted is in the audit log");

        await svc.MarkSharePointPostedAsync(Day, posted: false);
        brief = await Read();
        brief.SharePointPostedAtUtc.Should().BeNull();
        brief.SharePointPostedByName.Should().BeNull();
    }

    [Fact]
    public async Task SharePoint_NotRequired_ASentBriefIsDone_AndAnEmailedOneWaitsWhenRequired()
    {
        _db.Email.Enabled = true;
        await Brief();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        await _db.Settings().SaveRecipientsAsync(Edition.Team, "cti@example.org");
        var svc = _db.Distribution();
        await svc.SendAsync(await svc.PlanAsync(Day, Edition.Team));
        var brief = await Read();
        var sends = await svc.SendsAsync(Day);

        BriefSteps.Send(brief, sends, [Edition.Public]).State.Should().Be(StepState.Done, "SharePoint isn't required by default");
        (await _db.Briefs().ListAsync()).Single(b => b.BriefDate == Day).Emailed.Should().BeTrue("the archive knows it was emailed");
        brief.SharePointRequired = true;   // as when it's required as the brief goes out
        BriefSteps.Send(brief, sends, [Edition.Public], sharePointRequired: true)
            .Should().Match<BriefStep>(s => s.Detail == "Sent · nothing in the other edition · SharePoint to post" && s.State == StepState.Todo);
        BriefSteps.StatusLabel(brief.Status, false, false, false).Should().Be("Sent");
    }

    [Fact]
    public async Task SharePoint_MarkingAnEmptyBrief_IsRefused()
    {
        await _db.Briefs().GetOrStartAsync(Day);
        var act = () => _db.Distribution().MarkSharePointPostedAsync(Day);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("There's nothing on this brief to post.");
    }

    [Fact]
    public async Task SharePoint_RequiredOnlyAfterABriefWentOut_DoesntMakeItLookUnfinished()
    {
        await Brief();
        await _db.Distribution().MarkDistributedAsync(Day);
        _db.DistributionOptions.RequireSharePoint = true;

        var brief = await Read();
        brief.SharePointRequired.Should().BeFalse("it wasn't required when the brief went out");
        BriefSteps.SharePointDue(brief, true).Should().BeFalse();
        BriefSteps.For(brief, 0, 0, "9:00 AM", [], sharePointRequired: true).Single(s => s.Kind == BriefStepKind.Send)
            .Should().Match<BriefStep>(s => s.Detail == "Distributed" && s.State == StepState.Done);
    }

    [Fact]
    public void ABriefDistributedWithoutEmail_ThenEmailedAfterAll_IsSent()
    {
        var brief = new Tide.Domain.Entities.Brief { Status = BriefStatus.Sent, DistributedAtUtc = DateTimeOffset.UtcNow };
        var sends = new[]
        {
            new Tide.Domain.Entities.BriefSend { Edition = Edition.Team, Status = SendStatus.Sent, SentAtUtc = DateTimeOffset.UtcNow },
            new Tide.Domain.Entities.BriefSend { Edition = Edition.Public, Status = SendStatus.Sent, SentAtUtc = DateTimeOffset.UtcNow },
        };
        BriefSteps.Send(brief, sends).Detail.Should().Be("Sent");
        BriefSteps.Send(brief, []).Detail.Should().Be("Distributed");
    }

    [Fact]
    public void RequireSharePoint_IsAnOperationalSetting_OffByDefault()
    {
        new DistributionOptions().RequireSharePoint.Should().BeFalse();
        OperationalSettings.Normalize(OperationalSettings.RequireSharePoint, "True").Should().Be("true");
        OperationalSettings.Problem(OperationalSettings.RequireSharePoint, "maybe").Should().NotBeNull();
    }
}
