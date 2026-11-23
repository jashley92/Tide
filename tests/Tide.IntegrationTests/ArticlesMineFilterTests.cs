using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Articles;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// The article list's Mine and By …: Mine is what the signed-in person is writing now (ADR 0031), By … what one person
/// authored, by id; either goes with
/// the other filters, and an id nobody has lists nothing rather than everything.
/// </summary>
public class ArticlesMineFilterTests : IDisposable
{
    private readonly TestDb _db = new();

    public ArticlesMineFilterTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    private const string Dana = "S-1-5-21-DANA", Sam = "S-1-5-21-SAM";

    // An article written by this person: signed in as them for the save, then back as the test's own user.
    private async Task<Guid> WrittenBy(string id, string name, string title)
    {
        var (was, wasName) = (_db.User.UserId, _db.User.DisplayName);
        (_db.User.UserId, _db.User.DisplayName) = (id, name);
        try { return await _db.Articles().CreateAsync(title); }
        finally { (_db.User.UserId, _db.User.DisplayName) = (was, wasName); }
    }

    private async Task<IReadOnlyList<string>> Titles(ArticleFilter filter) =>
        [.. (await _db.Articles().PageAsync(filter, ArticleSort.Title, descending: false, take: 50)).Rows.Select(r => r.Title)];

    [Fact]
    public async Task MineAndBy_ListOnlyThatPersonsArticles()
    {
        await WrittenBy(Dana, "Dana Analyst", "Dana's phishing wave");
        await WrittenBy(Dana, "Dana Analyst", "Dana's ransomware note");
        await WrittenBy(Sam, "Sam Analyst", "Sam's VPN zero-day");
        await _db.Articles().CreateAsync("My own KEV round-up");

        (await Titles(new ArticleFilter())).Should().HaveCount(4);
        (await Titles(new ArticleFilter { Author = ArticleFilter.Me })).Should().Equal("My own KEV round-up");
        (await Titles(new ArticleFilter { Author = Dana })).Should().Equal("Dana's phishing wave", "Dana's ransomware note");
        (await Titles(new ArticleFilter { Author = Sam })).Should().Equal("Sam's VPN zero-day");
        (await Titles(new ArticleFilter { Author = _db.User.UserId })).Should().Equal(["My own KEV round-up"], "By … the person's own id: nothing handed on yet");

        // Mine follows whoever is signed in: the same link lists each person's own.
        _db.User.UserId = Dana;
        (await Titles(new ArticleFilter { Author = ArticleFilter.Me })).Should().Equal("Dana's phishing wave", "Dana's ransomware note");
    }

    [Fact]
    public async Task AnUnknownPerson_ListsNothing_NotEverything()
    {
        await WrittenBy(Dana, "Dana Analyst", "Dana's phishing wave");
        await _db.Articles().CreateAsync("My own KEV round-up");

        var page = await _db.Articles().PageAsync(new ArticleFilter { Author = "S-1-5-21-NOBODY" }, ArticleSort.Written, true, 50);
        page.Rows.Should().BeEmpty();
        page.Total.Should().Be(0);

        // Read from an address, too: an id nobody has is still a filter (it isn't dropped, which would list everyone's).
        var linked = ArticleFilter.FromQuery(n => n == "author" ? "S-1-5-21-NOBODY" : null);
        linked.Narrowed.Should().BeTrue();
        (await Titles(linked)).Should().BeEmpty();
    }

    [Fact]
    public async Task Mine_GoesWithTheOtherFilters()
    {
        await _db.Articles().CreateAsync("My draft");
        var ready = await _db.Articles().CreateAsync("My ready one");
        await using (var db = _db.Context())
            await db.Articles.Where(a => a.Id == ready).ExecuteUpdateAsync(x => x.SetProperty(a => a.Status, ArticleStatus.Ready));
        await WrittenBy(Dana, "Dana Analyst", "Dana's draft");

        (await Titles(new ArticleFilter { Author = ArticleFilter.Me, Status = ArticleStatus.Draft })).Should().Equal("My draft");
        (await Titles(new ArticleFilter { Author = ArticleFilter.Me, Search = "ready" })).Should().Equal("My ready one");
        (await Titles(new ArticleFilter { Author = Dana, Status = ArticleStatus.Ready })).Should().BeEmpty();
    }

    [Fact]
    public async Task AHandedOnDraft_IsUnderTheNewWritersMine_NotTheAuthors_AndByStillCreditsTheAuthor()
    {
        var handed = await _db.Articles().CreateAsync("Mine to hand on");
        await _db.Articles().CreateAsync("Mine to keep");
        await WrittenBy(Dana, "Dana Analyst", "Dana's own");
        await using (var db = _db.Context())
            await db.Articles.Where(a => a.Id == handed).ExecuteUpdateAsync(x => x.SetProperty(a => a.WriterId, Dana).SetProperty(a => a.WriterName, "Dana Analyst"));

        (await Titles(new ArticleFilter { Author = ArticleFilter.Me })).Should().Equal(["Mine to keep"], "a draft handed on is Dana's to finish");
        (await Titles(new ArticleFilter { Author = _db.User.UserId })).Should().Equal(["Mine to hand on", "Mine to keep"], "By … credits the author");

        _db.User.UserId = Dana;
        (await Titles(new ArticleFilter { Author = ArticleFilter.Me })).Should().Equal("Dana's own", "Mine to hand on");
        (await Titles(new ArticleFilter { Author = Dana })).Should().Equal("Dana's own");
    }

    [Fact]
    public async Task AnArticleWithNoWriterRecorded_IsItsAuthors()
    {
        var id = await _db.Articles().CreateAsync("Written before there were writers");
        await using (var db = _db.Context())
            await db.Articles.Where(a => a.Id == id).ExecuteUpdateAsync(x => x.SetProperty(a => a.WriterId, (string?)null).SetProperty(a => a.WriterName, (string?)null));

        (await Titles(new ArticleFilter { Author = ArticleFilter.Me })).Should().Equal("Written before there were writers");
    }

    [Fact]
    public async Task Authors_AreEveryoneWhoHasWritten_ByName_WithHowMany()
    {
        await WrittenBy(Sam, "Sam Analyst", "One");
        await WrittenBy(Dana, "Dana Analyst", "Two");
        await WrittenBy(Dana, "Dana Analyst", "Three");
        await WrittenBy(Dana, "Dana Renamed", "Four");   // a name changed since: listed once, by the name on most

        (await _db.Articles().AuthorsAsync()).Should().Equal(
            new ArticleAuthor(Dana, "Dana Analyst", 3), new ArticleAuthor(Sam, "Sam Analyst", 1));
    }

    [Fact]
    public async Task Authors_NeedReadAccess()
    {
        _db.User.Roles = new HashSet<AppRole>();
        await FluentActions.Invoking(() => _db.Articles().AuthorsAsync()).Should().ThrowAsync<Tide.Application.Security.ForbiddenException>();
    }
}
