using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Adversaries;
using Tide.Application.Articles;
using Tide.Application.History;
using Tide.Application.Search;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// The free-text "Threat actors" box, retired for the adversary picker: what it held (Article.Actors) is kept as the
/// earlier note. Its names a profile goes by are offered as one-click tags, the rest stay in sight as "Earlier note"
/// until cleared, the editor never writes it again, and where the text used to show (a story's actors, search) the tagged
/// profiles stand in for it.
/// </summary>
public class EarlierActorsTests : IDisposable
{
    private TestDb _db = new();

    public EarlierActorsTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    private Task<Guid> Profile(string name, string? aliases = null) =>
        _db.Adversaries().SaveAsync(new AdversaryForm { Name = name, Aliases = aliases });

    private async Task<Guid> Article(string title, string? actors)
    {
        var id = await _db.Articles().CreateAsync(title, "Vendor", "https://vendor.example.com/" + Guid.NewGuid().ToString("N"));
        await _db.SetEarlierActorsAsync(id, actors);
        return id;
    }

    private async Task<string?> Stored(Guid id)
    {
        await using var db = _db.Context();
        return await db.Articles.Where(a => a.Id == id).Select(a => a.Actors).SingleAsync();
    }

    [Fact]
    public void TheEarlierNote_SplitsOnCommasAndSemicolons_DroppingBlanksAndRepeats() =>
        AdversaryService.EarlierNames(" Scattered Spider, UNC9999 ;; apt29; scattered spider ,").Should().Equal("Scattered Spider", "UNC9999", "apt29");

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ItsNamesAProfileGoesBy_AreOfferedAsTags_AndTheRestAreTheLeftoverNote(string provider)
    {
        _db = _db.On(provider);
        var spider = await Profile("Scattered Spider", "UNC3944; Octo Tempest");
        var apt29 = await Profile("APT29", "Cozy Bear; Midnight Blizzard");
        await Profile("Volt Typhoon");
        var article = await Article("Help-desk resets", "Scattered Spider, UNC9999; cozy bear");

        var mine = await _db.Adversaries().ForArticleAsync(article);

        mine.FromNote.Select(a => a.Id).Should().Equal(spider, apt29);
        mine.Leftover.Should().Equal("UNC9999");
        mine.Suggested.Should().BeEmpty("a profile is offered once, from the note, not again as \"Names X\"");
        (await _db.Adversaries().OfferedAsync(article)).Select(p => (p.Id, p.Reason)).Take(2)
            .Should().Equal((spider, PickReason.Named), (apt29, PickReason.Named));

        // Tagging one: it's no longer offered, and the leftover stays until it's cleared.
        await _db.Adversaries().TagArticleAsync(article, spider);
        mine = await _db.Adversaries().ForArticleAsync(article);
        mine.Tagged.Select(a => a.Id).Should().Equal(spider);
        mine.FromNote.Select(a => a.Id).Should().Equal(apt29);
        mine.Leftover.Should().Equal("UNC9999");
        (await Stored(article)).Should().Be("Scattered Spider, UNC9999; cozy bear", "nothing is auto-tagged or rewritten");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ClearingTheNote_DropsOnlyTheLeftover_AndLeavesTheEditVersion(string provider)
    {
        _db = _db.On(provider);
        var spider = await Profile("Scattered Spider");
        var article = await Article("Clear the note", "Scattered Spider, UNC9999");
        var version = (await _db.Articles().GetAsync(article))!.EditVersion;

        await _db.Adversaries().ClearEarlierNoteAsync(article);

        (await Stored(article)).Should().Be("Scattered Spider", "the name a profile goes by stays offered as a tag");
        (await _db.Articles().GetAsync(article))!.EditVersion.Should().Be(version, "an editor open on it still saves");
        var mine = await _db.Adversaries().ForArticleAsync(article);
        mine.Leftover.Should().BeEmpty();
        mine.FromNote.Select(a => a.Id).Should().Equal(spider);

        // Tagged, and cleared again: the tag stands for the name, which stays stored.
        await _db.Adversaries().TagArticleAsync(article, spider);
        await _db.Adversaries().ClearEarlierNoteAsync(article);
        (await Stored(article)).Should().Be("Scattered Spider");
        (await _db.Adversaries().ForArticleAsync(article)).FromNote.Should().BeEmpty();

        // A note with nothing a profile goes by is cleared away, and an article with none is left alone.
        var other = await Article("Only leftover", "UNC1111");
        await _db.Adversaries().ClearEarlierNoteAsync(other);
        (await Stored(other)).Should().BeNull();
        await _db.Adversaries().ClearEarlierNoteAsync(other);
        await FluentActions.Awaiting(() => _db.Adversaries().ClearEarlierNoteAsync(Guid.NewGuid())).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task AddingTheLeftoverAsAProfile_AndTaggingIt_TakesItOutOfTheNote()
    {
        var article = await Article("Add the leftover", "UNC9999");
        var made = await _db.Adversaries().AddByNameAsync("UNC9999");
        await _db.Adversaries().TagArticleAsync(article, made);

        var mine = await _db.Adversaries().ForArticleAsync(article);
        mine.Leftover.Should().BeEmpty("the tag stands for it now");
        mine.FromNote.Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyAnAnalyst_ClearsTheNote()
    {
        var article = await Article("Reader clears", "UNC9999");
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Awaiting(() => _db.Adversaries().ClearEarlierNoteAsync(article)).Should().ThrowAsync<Application.Security.ForbiddenException>();
    }

    [Fact]
    public async Task SavingTheEditor_NeverWritesTheOldText()
    {
        var article = await Article("Saved again", "Scattered Spider, UNC9999");
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);
        form.Bluf = "Edited after the box was retired.";
        await _db.Articles().SaveAsync(form);

        (await Stored(article)).Should().Be("Scattered Spider, UNC9999", "the save keeps what's stored and writes nothing of its own");
        typeof(ArticleForm).GetProperty("Actors").Should().BeNull("the form (and so the AI draft that fills it) has no free-text actors to fill");
    }

    [Fact]
    public async Task TheClear_IsInTheArticlesHistory_UnderItsNewName()
    {
        await Profile("Scattered Spider");
        var article = await Article("History of the note", "Scattered Spider, UNC9999");
        await _db.Adversaries().ClearEarlierNoteAsync(article);

        var history = await new HistoryService(_db, _db.User).ArticleAsync(article);
        history.Entries.SelectMany(e => e.Changes).Should().Contain(c =>
            c.Field == "Threat actors (earlier note)" && c.Before == "Scattered Spider, UNC9999" && c.After == "Scattered Spider");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AStorysActors_AreTheTaggedProfiles_ThenTheNamesNoTagStandsFor(string provider)
    {
        _db = _db.On(provider);
        var spider = await Profile("Scattered Spider", "UNC3944");
        var apt29 = await Profile("APT29");
        var first = await Article("Story one", "unc3944, UNC9999");
        var second = await Article("Story two", null);
        await _db.Adversaries().TagArticleAsync(first, spider);
        await _db.Adversaries().TagArticleAsync(second, apt29);
        var story = await _db.Stories().CreateAsync("Help-desk campaign", null);
        await _db.Stories().LinkAsync(first, story);
        await _db.Stories().LinkAsync(second, story);

        var detail = (await _db.Stories().GetAsync(story))!;

        detail.Actors.Should().Equal("APT29", "Scattered Spider", "UNC9999");
    }

    [Fact]
    public void ShownNames_PutsTheTagsFirst_AndDropsANameATagGoesBy()
    {
        var spider = new Domain.Entities.Adversary { Name = "Scattered Spider", Aliases = "UNC3944" };
        AdversaryService.ShownNames([spider], "unc3944, UNC9999").Should().Equal("Scattered Spider", "UNC9999");
        AdversaryService.ShownNames([], "UNC9999").Should().Equal(new[] { "UNC9999" }, "with nothing tagged it shows as it always did");
        AdversaryService.ShownNames([spider], null).Should().Equal("Scattered Spider");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Search_FindsAnArticleByItsTaggedProfilesNames(string provider)
    {
        _db = _db.On(provider);
        var apt29 = await Profile("APT29", "Cozy Bear; Midnight Blizzard");
        var tagged = await _db.Articles().CreateAsync("Diplomatic phishing wave", "Vendor", "https://vendor.example.com/apt29-wave");
        await _db.Adversaries().TagArticleAsync(tagged, apt29);
        var earlier = await Article("Older report", "Midnight Blizzard");

        var hits = (await _db.Search().SearchAsync("midnight blizzard")).Articles.Items;

        hits.Select(h => h.Id).Should().BeEquivalentTo([tagged, earlier], "a tag's other names and the earlier note are both searched");
        hits.Single(h => h.Id == earlier).Snippet.Should().Contain("Midnight Blizzard", "an untagged note's names still show as typed");
    }
}

/// <summary>The ranking the adversary and "We run" pickers share.</summary>
public class NameRankTests
{
    [Fact]
    public void ARank_IsExactThenStartsThenHolds_IgnoringCase()
    {
        NameRank.Of("Scattered Spider", "scattered spider").Should().Be(NameRank.Exact);
        NameRank.Of("Scattered Spider", "SCAT").Should().Be(NameRank.Starts);
        NameRank.Of("Scattered Spider", "spider").Should().Be(NameRank.Holds);
        NameRank.Of("Scattered Spider", "bear").Should().Be(NameRank.None);
    }

    [Fact]
    public void TheBest_PrefersTheName_ThenTheCode_ThenTheFirstOtherName()
    {
        NameRank.Best("gate", "Gateway", ["Gate", "Gatekeeper"]).Should().Be((NameRank.Exact, "Gate"));
        NameRank.Best("gate", "Gate", ["Gate"]).Should().Be((NameRank.Exact, null));
        NameRank.Best("g001", "Group", [], "G0016").Should().Be((NameRank.Starts, null));
        NameRank.Best("zzz", "Group", ["Other"]).Rank.Should().Be(NameRank.None);
    }

    [Fact]
    public void ThePatterns_FindAnOtherNameWholeAndAtItsStart_TakingWildcardsAsText()
    {
        var p = NameRank.Patterns.For("50%");
        p.Any.Should().Be(SearchText.Contains("50%"));
        p.Exact.Should().Be(SearchText.Literal("50%"));
        p.Starts.Should().Be(p.Exact + "%");
        (p.OtherFirst, p.OtherLast, p.OtherMiddle, p.OtherStarts).Should().Be((p.Exact + ";%", "%; " + p.Exact, "%; " + p.Exact + ";%", "%; " + p.Starts));
    }
}
