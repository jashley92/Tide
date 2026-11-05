using FluentAssertions;
using Tide.Application.Adversaries;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Search finds an article by any name of a profile it's tagged with. The same searches, with the same answers, as when
/// each term was matched against the tagged profiles inside the article query: the profiles are matched first now, and
/// the articles by their ids.
/// </summary>
public class TaggedProfileSearchTests : IDisposable
{
    private TestDb _db = new();

    public TaggedProfileSearchTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    private Task<Guid> Profile(string name, string? aliases = null) =>
        _db.Adversaries().SaveAsync(new AdversaryForm { Name = name, Aliases = aliases });

    private async Task<Guid> Article(string title, params Guid[] tags)
    {
        var id = await _db.Articles().CreateAsync(title, "Vendor", "https://vendor.example.com/" + Guid.NewGuid().ToString("N"));
        foreach (var tag in tags) await _db.Adversaries().TagArticleAsync(id, tag);
        return id;
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheSameSearches_FindTheSameArticles(string provider)
    {
        _db = _db.On(provider);
        var apt29 = await Profile("APT29", "Cozy Bear; Midnight Blizzard");
        var spider = await Profile("Scattered Spider", "UNC3944; Octo Tempest");
        var lazarus = await Profile("Lazarus Group");
        var crew = await Profile("Old Crew", "Dust 50%");
        await Profile("Volt Typhoon", "Bronze Silhouette");
        var diplomatic = await Article("Diplomatic phishing wave", apt29);
        var helpDesk = await Article("Help-desk calls", spider, lazarus);
        var wiper = await Article("Wiper report", crew);
        var season = await Article("Blizzard season");
        await Article("Quarterly roundup");
        await _db.Adversaries().SetRetiredAsync(crew, true);

        var searches = new (string Query, Guid[] Found)[]
        {
            ("midnight", [diplomatic]),
            ("MIDNIGHT blizzard", [diplomatic]),
            ("blizzard", [diplomatic, season]),
            ("octo tempest", [helpDesk]),
            ("unc3944 lazarus", [helpDesk]),
            ("cozy spider", []),
            ("dust 50%", [wiper]),
            ("50", [wiper]),
            ("%", []),
            ("group", [helpDesk]),
            ("bronze", []),
            ("apt29 phishing", [diplomatic]),
            ("nothing-like-it", []),
        };
        foreach (var (query, found) in searches)
        {
            var hits = (await _db.Search().SearchAsync(query)).Articles;
            hits.Items.Select(h => h.Id).Should().BeEquivalentTo(found, $"\"{query}\" finds the same articles");
            hits.Total.Should().Be(found.Length, $"\"{query}\" counts the same articles");
        }
    }
}
