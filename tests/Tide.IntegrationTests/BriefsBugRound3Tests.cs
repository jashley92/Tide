using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Bug round 3, the briefs slice: a draft deleted as a brief takes it (either way round), and an article added to a
/// brief after its meeting ended, then sent with it.
/// </summary>
public class BriefsBugRound3Tests : IDisposable
{
    private readonly TestDb _db = new();
    private static readonly DateOnly Day = new(2026, 10, 26);

    public BriefsBugRound3Tests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    // Runs once, as the next save starts: a colleague's change landing between a service's checks and its write.
    private void Meanwhile(Action<Tide.Infrastructure.Persistence.AppDbContext> change) => _db.BeforeSave = _ =>
    {
        _db.BeforeSave = null;
        using var ctx = _db.PlainContext();
        change(ctx);
    };

    [Fact]
    public async Task ADraftDeletedAsABriefTakesIt_IsKeptAndSaysWhy()
    {
        var articles = _db.Articles();
        var id = await articles.CreateAsync("Late draft", "Vendor", null, TlpLevel.Clear);
        var brief = await _db.Briefs().GetOrStartAsync(Day);

        // The delete checked it's on no brief; a colleague adds it to today's before the delete is saved.
        Meanwhile(c =>
        {
            c.BriefItems.Add(new BriefItem { BriefId = brief.Id, ArticleId = id });
            c.SaveChanges();
        });
        var delete = () => articles.DeleteAsync(id);

        (await delete.Should().ThrowAsync<InvalidOperationException>()).WithMessage("Only a draft that isn't on a brief can be deleted.");
        (await articles.GetAsync(id)).Should().NotBeNull("the brief keeps its article");
        (await _db.Briefs().GetAsync(Day))!.Items.Should().ContainSingle(i => i.ArticleId == id);
    }

    [Fact]
    public async Task AnArticleDeletedAsItsAddedToABrief_SaysItsGone()
    {
        var id = await _db.Articles().CreateAsync("Draft about to go", "Vendor", null, TlpLevel.Clear);
        var briefs = _db.Briefs();
        var brief = await briefs.GetOrStartAsync(Day);

        // The add checked the article is there; its author deletes the draft before the add is saved.
        Meanwhile(c => c.Articles.Where(a => a.Id == id).ExecuteDelete());
        var add = () => briefs.AddArticleAsync(brief.Id, id);

        (await add.Should().ThrowAsync<KeyNotFoundException>()).WithMessage("That article no longer exists.");
        (await briefs.GetAsync(Day))!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ADraftDeletedAsAnActionIsRaisedFromIt_IsKeptForTheAction()
    {
        var articles = _db.Articles();
        var id = await articles.CreateAsync("Draft with an action coming", "Vendor", null, TlpLevel.Clear);
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);

        // The delete checked no action was raised from it; a colleague raises one before the delete is saved.
        Meanwhile(c =>
        {
            c.Actions.Add(new ActionItem { Title = "Sweep for it", TeamId = team, ArticleId = id, Number = 9001 });
            c.SaveChanges();
        });
        var delete = () => articles.DeleteAsync(id);

        (await delete.Should().ThrowAsync<InvalidOperationException>()).WithMessage("Actions were raised from this article, so it's kept for their record.");
        (await articles.GetAsync(id)).Should().NotBeNull();
    }

    [Fact]
    public async Task TheSameArticleAddedByTwoPeopleAtOnce_IsOnTheBriefOnce_WithoutAnError()
    {
        var id = await _db.Articles().CreateAsync("Added twice at once", "Vendor", null, TlpLevel.Clear);
        var briefs = _db.Briefs();
        var brief = await briefs.GetOrStartAsync(Day);

        // A colleague adds it to the same brief as this add is saved.
        Meanwhile(c =>
        {
            c.BriefItems.Add(new BriefItem { BriefId = brief.Id, ArticleId = id });
            c.SaveChanges();
        });

        await briefs.AddArticleAsync(brief.Id, id);

        (await briefs.GetAsync(Day))!.Items.Should().ContainSingle(i => i.ArticleId == id);
    }

    [Fact]
    public async Task AnArticleAddedAfterTheMeeting_IsPresentedOnceTheBriefIsSent_ButOneCarriedOnIsNot()
    {
        var articles = _db.Articles();
        var discussed = await articles.CreateAsync("Discussed in the meeting", "Vendor", null, TlpLevel.Clear);
        var carried = await articles.CreateAsync("Carried to the next brief", "Vendor", null, TlpLevel.Clear);
        var late = await articles.CreateAsync("Added after the meeting", "Vendor", null, TlpLevel.Clear);
        var briefs = _db.Briefs();
        var brief = await briefs.GetOrStartAsync(Day);
        await briefs.AddArticleAsync(brief.Id, discussed);
        await briefs.AddArticleAsync(brief.Id, carried);
        var next = await briefs.MarkPresentedAsync(brief.Id, carry: [carried]);
        await briefs.AddArticleAsync(brief.Id, late);
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "everyone@example.org");
        var svc = _db.Distribution();

        var send = await svc.SendAsync(await svc.PlanAsync(Day, Edition.Public));

        send.Status.Should().Be(SendStatus.Sent);
        _db.Email.Sent.Single().Html.Should().Contain("Added after the meeting");
        (await articles.GetAsync(late))!.Status.Should().Be(ArticleStatus.Presented, "it went out with the brief, so it isn't offered as still to be briefed");
        (await articles.GetAsync(carried))!.Status.Should().Be(ArticleStatus.Draft, "it's the next brief's to present");
        (await articles.GetAsync(discussed))!.Status.Should().Be(ArticleStatus.Presented);
        var later = await briefs.GetOrStartAsync(next!.Value);
        (await briefs.CandidatesAsync(later.Id)).Should().NotContain(c => c.Id == late && c.Available);
    }
}
