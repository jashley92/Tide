using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Bug round 4, the briefs slice: ending a meeting and carrying its undiscussed articles to the next brief are one save,
/// so a colleague editing, starting or sending the next brief at that moment doesn't leave the meeting ended with its
/// carry lost.
/// </summary>
public class BriefsBugRound4Tests : IDisposable
{
    private readonly TestDb _db = new();
    private static readonly DateOnly Day = new(2026, 10, 26);   // a Monday: the next business day is Tuesday
    private static readonly DateOnly Next = Day.AddDays(1);

    public BriefsBugRound4Tests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    // Runs once, as the next save starts: a colleague's change landing between a service's checks and its write.
    private void Meanwhile(Action<Tide.Infrastructure.Persistence.AppDbContext> change) => _db.BeforeSave = _ =>
    {
        _db.BeforeSave = null;
        using var ctx = _db.PlainContext();
        change(ctx);
    };

    private async Task<(Guid Brief, Guid Discussed, Guid Carried)> Meeting()
    {
        var articles = _db.Articles();
        var discussed = await articles.CreateAsync("Discussed in the meeting", "Vendor", null, TlpLevel.Clear);
        var carried = await articles.CreateAsync("Ran out of time", "Vendor", null, TlpLevel.Clear);
        var briefs = _db.Briefs();
        var brief = await briefs.GetOrStartAsync(Day);
        await briefs.AddArticleAsync(brief.Id, discussed);
        await briefs.AddArticleAsync(brief.Id, carried);
        var item = (await briefs.GetAsync(Day))!.Items.Single(i => i.ArticleId == discussed);
        await briefs.SetDiscussedAsync(item.Id, true);
        return (brief.Id, discussed, carried);
    }

    [Fact]
    public async Task ANextBriefEditedAsTheMeetingEnds_StillGetsTheCarriedArticle_AndKeepsTheEdit()
    {
        var (brief, discussed, carried) = await Meeting();
        var briefs = _db.Briefs();
        var tomorrow = await briefs.GetOrStartAsync(Next);
        var already = await _db.Articles().CreateAsync("Already on tomorrow's", "Vendor", null, TlpLevel.Clear);
        await briefs.AddArticleAsync(tomorrow.Id, already);

        // A colleague saves tomorrow's opening note as the meeting's end is saved.
        Meanwhile(c =>
        {
            var b = c.Briefs.Single(x => x.Id == tomorrow.Id);
            b.Intro = "A colleague's note";
            b.EditVersion = Guid.NewGuid();
            c.SaveChanges();
        });

        var to = await briefs.MarkPresentedAsync(brief, carry: [carried]);

        to.Should().Be(Next);
        var next = (await briefs.GetAsync(Next))!;
        next.Intro.Should().Be("A colleague's note");
        next.Items.OrderBy(i => i.SortOrder).Select(i => i.ArticleId).Should().Equal(already, carried);
        var ended = (await briefs.GetAsync(Day))!;
        ended.Status.Should().Be(BriefStatus.Presented);
        ended.Items.Single(i => i.ArticleId == discussed).Article!.Status.Should().Be(ArticleStatus.Presented);
        ended.Items.Single(i => i.ArticleId == carried).Article!.Status.Should().NotBe(ArticleStatus.Presented, "it's tomorrow's to present");
    }

    [Fact]
    public async Task ANextBriefSentAsTheMeetingEnds_RefusesTheCarry_AndTheMeetingIsNotEnded()
    {
        var (brief, discussed, carried) = await Meeting();
        var briefs = _db.Briefs();
        var tomorrow = await briefs.GetOrStartAsync(Next);

        // Tomorrow's brief goes out as the meeting's end is saved (a send moves the brief's edit version).
        Meanwhile(c =>
        {
            var b = c.Briefs.Single(x => x.Id == tomorrow.Id);
            b.Status = BriefStatus.Sent;
            b.EditVersion = Guid.NewGuid();
            c.SaveChanges();
        });

        var act = () => briefs.MarkPresentedAsync(brief, carry: [carried]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*has been sent*");
        var still = (await briefs.GetAsync(Day))!;
        still.Status.Should().NotBe(BriefStatus.Presented, "the meeting's end and its carry are one save");
        still.PresentedAtUtc.Should().BeNull();
        still.Items.Single(i => i.ArticleId == discussed).Article!.Status.Should().NotBe(ArticleStatus.Presented);
        (await briefs.GetAsync(Next))!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ANextBriefStartedByAColleagueAsTheMeetingEnds_TakesTheCarriedArticle()
    {
        var (brief, _, carried) = await Meeting();
        var briefs = _db.Briefs();

        // A colleague opens tomorrow's brief, starting it, as the meeting's end (which would start it too) is saved.
        Meanwhile(c =>
        {
            c.Briefs.Add(new Brief { BriefDate = Next, AnalystId = "colleague", AnalystName = "A Colleague" });
            c.SaveChanges();
        });

        var to = await briefs.MarkPresentedAsync(brief, carry: [carried]);

        to.Should().Be(Next);
        await using var db = _db.Context();
        var next = await db.Briefs.Include(b => b.Items).SingleAsync(b => b.BriefDate == Next);
        next.AnalystName.Should().Be("A Colleague", "it's the brief the colleague started");
        next.Items.Select(i => i.ArticleId).Should().Equal(carried);
        (await db.Briefs.SingleAsync(b => b.Id == brief)).Status.Should().Be(BriefStatus.Presented);
    }

    [Fact]
    public async Task EndingTheMeeting_StartsTheNextBrief_WithTheCarriedArticlesInTheirOrder()
    {
        var articles = _db.Articles();
        var first = await articles.CreateAsync("First not discussed", "Vendor", null, TlpLevel.Clear);
        var second = await articles.CreateAsync("Second not discussed", "Vendor", null, TlpLevel.Clear);
        var briefs = _db.Briefs();
        var brief = await briefs.GetOrStartAsync(Day);
        await briefs.AddArticleAsync(brief.Id, first);
        await briefs.AddArticleAsync(brief.Id, second);

        (await briefs.MarkPresentedAsync(brief.Id, carry: [second, first])).Should().Be(Next);

        var next = (await briefs.GetAsync(Next))!;
        next.Items.OrderBy(i => i.SortOrder).Select(i => i.ArticleId).Should().Equal(first, second);
        next.Items.Select(i => i.SortOrder).Order().Should().Equal(0, 1);
        next.AnalystName.Should().NotBeNullOrEmpty();
    }
}
