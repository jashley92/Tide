using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Adversaries;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Bug round 4 (articles: adversary profiles): a retired or merged profile brought back beside a live one of its name,
/// or still "merged into" another; a merge onto a profile retired meanwhile; "Link all" racing a colleague's tag.
/// </summary>
public class ArticlesBugRound4Tests : IDisposable
{
    private readonly TestDb _db = new();

    public ArticlesBugRound4Tests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    private Task<Guid> Profile(string name, string? aliases = null) => _db.Adversaries().SaveAsync(new AdversaryForm { Name = name, Aliases = aliases });

    private async Task<Adversary> Read(Guid id)
    {
        await using var ctx = _db.Context();
        return await ctx.Adversaries.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    [Fact]
    public async Task ARetiredProfile_IsntBroughtBack_BesideALiveOneOfItsName()
    {
        // R4-1: a new profile may take a retired one's name; bringing the old one back then made two live profiles of one
        // name, and neither could be saved again ("already has a profile").
        var old = await Profile("Volt Typhoon");
        await _db.Adversaries().SetRetiredAsync(old, true);
        var fresh = await Profile("volt typhoon");

        await FluentActions.Invoking(() => _db.Adversaries().SetRetiredAsync(old, false)).Should()
            .ThrowAsync<InvalidOperationException>().WithMessage("*already a live profile*");
        (await Read(old)).IsRetired.Should().BeTrue();

        await _db.Adversaries().SetRetiredAsync(fresh, true);
        await _db.Adversaries().SetRetiredAsync(old, false);
        (await Read(old)).IsRetired.Should().BeFalse("with the name free again, it comes back");
    }

    [Fact]
    public async Task AMergedProfileBroughtBack_IsNoLongerMergedIntoTheOther()
    {
        // R4-2: brought back, it kept its MergedIntoId: its page still said "Merged into …", and retired again it was
        // listed as Merged.
        var from = await Profile("Sandworm");
        var into = await Profile("Seashell Blizzard");
        await _db.Adversaries().MergeAsync(from, into);
        (await Read(from)).MergedIntoId.Should().Be(into);

        await _db.Adversaries().SetRetiredAsync(from, false);

        var back = await Read(from);
        (back.IsRetired, back.MergedIntoId).Should().Be((false, (Guid?)null));
        (await _db.Adversaries().GetAsync(from))!.MergedIntoName.Should().BeNull();
        await _db.Adversaries().SetRetiredAsync(from, true);
        (await Read(from)).MergedIntoId.Should().BeNull("retired again by hand, it's retired, not merged");
    }

    [Fact]
    public async Task AMergeOntoAProfileRetiredMeanwhile_IsRefused()
    {
        // R4-3: the page offers the live profiles; one a colleague retired (or merged) since took the tags and names,
        // leaving them on a retired profile, with the merged one pointing at it.
        var from = await Profile("Charming Kitten");
        var into = await Profile("APT35");
        var article = await _db.Articles().CreateAsync("Phishing from Charming Kitten");
        await _db.Adversaries().TagArticleAsync(article, from);
        await _db.Adversaries().SetRetiredAsync(into, true);

        await FluentActions.Invoking(() => _db.Adversaries().MergeAsync(from, into)).Should()
            .ThrowAsync<InvalidOperationException>().WithMessage("*Choose another*");

        (await Read(from)).IsRetired.Should().BeFalse();
        await using var ctx = _db.Context();
        (await ctx.ArticleAdversaries.SingleAsync()).AdversaryId.Should().Be(from, "the tag stayed where it was");

        await _db.Adversaries().SetRetiredAsync(into, false);
        await _db.Adversaries().SetRetiredAsync(from, true);
        await FluentActions.Invoking(() => _db.Adversaries().MergeAsync(from, into)).Should()
            .ThrowAsync<InvalidOperationException>().WithMessage("*meanwhile*", "one already retired isn't merged either");
    }

    [Fact]
    public async Task LinkAll_RacingAColleaguesTag_TagsTheRest()
    {
        // R4-4: "Link all" read the tags, a colleague tagged one of the same articles, and the save failed on that tag's
        // key: nothing was linked and the analyst saw an error.
        var profile = await Profile("Midnight Blizzard");
        var first = await _db.Articles().CreateAsync("Midnight Blizzard password spraying");
        var second = await _db.Articles().CreateAsync("Midnight Blizzard OAuth apps");
        _db.BeforeSave = saving =>
        {
            if (!saving.ChangeTracker.Entries<ArticleAdversary>().Any(e => e.State == EntityState.Added)) return;
            _db.BeforeSave = null;
            using var ctx = _db.PlainContext();
            ctx.ArticleAdversaries.Add(new ArticleAdversary { ArticleId = first, AdversaryId = profile });
            ctx.SaveChanges();
        };

        // The link now runs in one serializable transaction, which a colleague's tag can't get into; this one, made on the
        // test's shared connection, does, and is rolled back with the failed save, so the retry may tag both. Either way the
        // link doesn't fail and both end tagged.
        (await _db.Adversaries().LinkAsync(profile, [first, second])).Should().BeInRange(1, 2);

        await using var check = _db.Context();
        (await check.ArticleAdversaries.Where(t => t.AdversaryId == profile).Select(t => t.ArticleId).ToListAsync())
            .Should().BeEquivalentTo([first, second]);
    }
}
