using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Briefs;
using Tide.Application.History;
using Tide.Application.Reviews;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>A brief finished as "Not sent" while email is on: who, when and why are kept, it's finished as a send would
/// finish it, and a later real send makes it an ordinary sent brief.</summary>
public class BriefNotSentTests : IDisposable
{
    private readonly TestDb _db = new();
    private static readonly DateOnly Day = new(2026, 10, 26);
    private const string Why = "Office closed for the storm; the team was briefed by phone.";

    public BriefNotSentTests()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        _db.Email.Enabled = true;
    }

    public void Dispose() => _db.Dispose();

    private ReviewService Reviews() => new(_db, _db.User, _db.Rotation(), _db.Clock, TimeProvider.System);

    private async Task<Guid> Brief(bool ended = true)
    {
        var id = await _db.Articles().CreateAsync("Not sent story", "Vendor", null, TlpLevel.Clear);
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, id);
        if (ended) await _db.Briefs().MarkPresentedAsync(brief.Id);
        return id;
    }

    private async Task<Brief> Read()
    {
        await using var ctx = _db.Context();
        return await ctx.Briefs.AsNoTracking().Include(b => b.Items).ThenInclude(i => i.Article).SingleAsync(b => b.BriefDate == Day);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task FinishNotSent_RequiresAReason(string? reason)
    {
        await Brief();
        var act = () => _db.Distribution().FinishNotSentAsync(Day, reason);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("Say why*");
        var tooLong = () => _db.Distribution().FinishNotSentAsync(Day, new string('x', 501));
        await tooLong.Should().ThrowAsync<ArgumentException>().WithMessage("*500*");
        (await Read()).Status.Should().Be(BriefStatus.Presented, "nothing was finished");
    }

    [Fact]
    public async Task FinishNotSent_WithEmailOff_IsRefused_ForMarkDistributed()
    {
        await Brief();
        _db.Email.Enabled = false;
        var act = () => _db.Distribution().FinishNotSentAsync(Day, Why);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Email is off: mark the brief distributed instead.");
        await _db.Distribution().MarkDistributedAsync(Day);
        (await Read()).NotSentAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task FinishNotSent_FinishesTheBrief_LocksItsNumbers_AndKeepsWhoWhenAndWhy()
    {
        await Brief();
        _db.DistributionOptions.RequireSharePoint = true;
        (await _db.Metrics().IsLockedAsync(Day)).Should().BeFalse();

        await _db.Distribution().FinishNotSentAsync(Day, "  " + Why + "  ");

        var brief = await Read();
        brief.Status.Should().Be(BriefStatus.Sent, "it's finished as a sent brief is");
        brief.NotSentReason.Should().Be(Why, "trimmed");
        brief.NotSentByName.Should().Be(_db.User.DisplayName);
        brief.NotSentById.Should().Be(_db.User.UserId);
        brief.NotSentAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        brief.DistributedAtUtc.Should().BeNull();
        brief.SharePointRequired.Should().BeFalse("nothing went, so there's no copy to post");
        brief.Items.Single().Article!.Status.Should().Be(ArticleStatus.Presented);
        (await _db.Metrics().IsLockedAsync(Day)).Should().BeTrue("the day is finished");

        await using (var ctx = _db.Context())
        {
            (await ctx.BriefSends.AnyAsync()).Should().BeFalse("nothing was emailed or frozen");
            var audit = await ctx.AuditLog.Where(e => e.EntityType == nameof(Tide.Domain.Entities.Brief) && e.Action == "Modified").ToListAsync();
            audit.Should().Contain(e => e.ChangesJson!.Contains(nameof(Tide.Domain.Entities.Brief.NotSentReason)) && e.UserName == _db.User.DisplayName);
        }
        (await _db.Distribution().PublishedAsync()).Should().BeEmpty("nothing is published");
        _db.Email.Sent.Should().BeEmpty();

        BriefSteps.For(brief, 0, 0, "9:00 AM", [], sharePointRequired: true).Single(s => s.Kind == BriefStepKind.Send)
            .Should().Match<BriefStep>(s => s.Detail == "Not sent" && s.State == StepState.Done);
        BriefSteps.Send(brief, null).Detail.Should().Be("Not sent");
        BriefSteps.StatusLabel(brief.Status, false, false, false, notSent: true).Should().Be("Not sent");
        var row = (await _db.Briefs().ListAsync()).Single();
        row.Should().Match<BriefSummary>(r => r.NotSentReason == Why && r.NotSentByName == _db.User.DisplayName && r.NotSentAtUtc != null && !r.Emailed);
        HistoryFormatter.Label(nameof(Tide.Domain.Entities.Brief.NotSentReason)).Should().Be("Why it wasn't sent");

        var again = () => _db.Distribution().FinishNotSentAsync(Day, Why);
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already finished*");
        var edit = () => _db.Briefs().SetTeamNoteAsync(brief.Id, "Too late");
        await edit.Should().ThrowAsync<InvalidOperationException>("it's finished, as a sent brief is");
    }

    [Fact]
    public async Task FinishNotSent_HasASendsTerms_TheMeetingEnded_ItsDay_AndReview()
    {
        var article = await Brief(ended: false);
        var svc = _db.Distribution();
        var meeting = () => svc.FinishNotSentAsync(Day, Why);
        await meeting.Should().ThrowAsync<InvalidOperationException>().WithMessage("End the meeting first*");

        var future = _db.Clock.Days.Next(_db.Clock.Today.AddDays(7));
        var later = await _db.Briefs().GetOrStartAsync(future);
        await _db.Briefs().AddArticleAsync(later.Id, article);
        var early = () => svc.FinishNotSentAsync(future, Why, withoutMeeting: true);
        await early.Should().ThrowAsync<InvalidOperationException>().WithMessage("*can be sent from that day*");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        await Reviews().SaveRuleAsync(ReviewRule.AmberOrThreats);
        var form = Tide.Application.Articles.ArticleForm.From((await _db.Articles().GetAsync(article))!);
        form.Classification = Classification.Threat;
        form.Priority = Priority.P2;
        await _db.Articles().SaveAsync(form);
        var held = () => svc.FinishNotSentAsync(Day, Why, withoutMeeting: true);
        await held.Should().ThrowAsync<InvalidOperationException>().WithMessage("Review is required before the brief is finished as not sent*");
        await Reviews().SaveRuleAsync(ReviewRule.Off);

        var none = () => svc.FinishNotSentAsync(Day.AddDays(-7), Why);
        await none.Should().ThrowAsync<KeyNotFoundException>();
        var empty = await _db.Briefs().GetOrStartAsync(new DateOnly(2026, 10, 20));
        var nothing = () => svc.FinishNotSentAsync(empty.BriefDate, Why, withoutMeeting: true);
        await nothing.Should().ThrowAsync<InvalidOperationException>().WithMessage("There's nothing on this brief*");

        await svc.FinishNotSentAsync(Day, Why, withoutMeeting: true);
        (await Read()).Items.Single().Article!.Status.Should().Be(ArticleStatus.Presented, "finished as a send without a meeting finishes it");
    }

    [Fact]
    public async Task FinishNotSent_ThenARealSend_IsAnOrdinarySentBrief()
    {
        await Brief();
        _db.DistributionOptions.RequireSharePoint = true;
        var svc = _db.Distribution();
        await svc.FinishNotSentAsync(Day, Why);

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        await _db.Settings().SaveRecipientsAsync(Edition.Team, "cti@example.org");
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "all@example.org");
        await svc.SendAsync(await svc.PlanAsync(Day, Edition.Team));
        await svc.SendAsync(await svc.PlanAsync(Day, Edition.Public));

        var brief = await Read();
        var sends = await svc.SendsAsync(Day);
        BriefSteps.Send(brief, sends).Detail.Should().Be("Sent", "the label is cleared");
        brief.SharePointRequired.Should().BeTrue("once emailed, it waits for its SharePoint copy as a sent brief does");
        brief.NotSentReason.Should().Be(Why, "kept as history");
        var row = (await _db.Briefs().ListAsync()).Single();
        row.Emailed.Should().BeTrue("the archive reads it as sent");
        BriefSteps.StatusLabel(row.Status, false, true, false, notSent: row.NotSentAtUtc is not null && !row.Emailed).Should().Be("Sent");
        (await svc.PublishedAsync()).Should().ContainSingle("the real send is published");
    }

    [Fact]
    public async Task FinishNotSent_NeedsSomeoneWhoSends()
    {
        await Brief();
        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        var act = () => _db.Distribution().FinishNotSentAsync(Day, Why);
        await act.Should().ThrowAsync<ForbiddenException>();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        await _db.Distribution().FinishNotSentAsync(Day, Why);
        (await Read()).NotSentByName.Should().Be(_db.User.DisplayName);
    }
}
