using FluentAssertions;
using Tide.Application.Articles;
using Tide.Application.Trends;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

public class ArticleFilterTests
{
    private static ArticleFilter Read(IReadOnlyDictionary<string, object?> query) => ArticleFilter.FromQuery(n => query.GetValueOrDefault(n)?.ToString());

    private static ArticleFilter Read(params (string Name, string Value)[] query) => ArticleFilter.FromQuery(n => query.FirstOrDefault(q => q.Name == n).Value);

    [Fact]
    public void EveryFilter_RoundTripsThroughTheAddress()
    {
        var f = new ArticleFilter
        {
            Search = "lure", Status = ArticleStatus.Ready, From = new(2026, 10, 1), To = new(2026, 10, 31), Assessment = ArticleAssessment.P1P2,
            Tag = "ransomware", Category = "fraud", Tactic = MitreTactic.InitialAccess, Technique = "T1566", WithSubtechniques = true,
        };
        var query = f.ToQuery();
        query.Where(q => q.Value is not null).ToDictionary().Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["q"] = "lure", ["status"] = "ready", ["from"] = "2026-10-01", ["to"] = "2026-10-31", ["assess"] = "p1-p2", ["tag"] = "ransomware",
            ["category"] = "fraud", ["tactic"] = "TA0001", ["technique"] = "T1566", ["withsubs"] = "true",
        });
        Read(query).Should().Be(f);
        Read(new ArticleFilter().ToQuery()).Should().Be(new ArticleFilter());
        new ArticleFilter().ToQuery().Values.Should().OnlyContain(v => v == null, "an empty filter leaves the address bare");
        foreach (var a in Enum.GetValues<ArticleAssessment>()) Read(new ArticleFilter { Assessment = a }.ToQuery()).Assessment.Should().Be(a);
        Read(new ArticleFilter { Technique = ArticleFilter.None, Tag = ArticleFilter.None }.ToQuery())
            .Should().Be(new ArticleFilter { Technique = ArticleFilter.None, Tag = ArticleFilter.None });
    }

    [Fact]
    public void FromQuery_TidiesWhatItCan_AndDropsWhatItCant()
    {
        var f = Read(("q", "  "), ("status", "nonsense"), ("from", "2026-13-01"), ("to", "2026-10-31"), ("assess", "THREAT"), ("tag", " Ransomware "),
            ("tactic", "ta0002"), ("technique", "t1566.001"), ("withsubs", "true"));
        f.Search.Should().BeNull();
        f.Status.Should().BeNull();
        (f.From, f.To).Should().Be(((DateOnly?)null, new DateOnly(2026, 10, 31)));
        f.Assessment.Should().Be(ArticleAssessment.Threat);
        f.Tag.Should().Be("ransomware");
        f.Tactic.Should().Be(MitreTactic.Execution);
        f.Technique.Should().Be("T1566.001", "the catalogue's spelling");
        Read(("technique", "t9999")).Technique.Should().Be("T9999", "a retired id is still an id");
        Read(("technique", "phishing")).Technique.Should().BeNull();
        Read(("technique", "NONE"), ("withsubs", "true")).Should().Be(new ArticleFilter { Technique = ArticleFilter.None });
        Read(("withsubs", "true")).WithSubtechniques.Should().BeFalse("sub-techniques of no technique");
        Read(("tactic", "TA9999")).Tactic.Should().BeNull();
        Read(("status", "draft")).Status.Should().Be(ArticleStatus.Draft);
    }

    [Theory]
    [InlineData("2026-10-01", "2026-10-31", "Oct 2026")]
    [InlineData("2026-07-01", "2026-09-30", "Q3 2026")]
    [InlineData("2026-05-01", "2026-10-31", "May 2026 – Oct 2026")]
    [InlineData("2026-10-03", "2026-10-15", "3 Oct 2026 – 15 Oct 2026")]
    [InlineData("2026-10-03", null, "From 3 Oct 2026")]
    [InlineData(null, "2026-10-15", "Until 15 Oct 2026")]
    [InlineData(null, null, null)]
    public void Dates_ReadAsTheirMonthQuarterOrDays(string? from, string? to, string? label) =>
        Read(("from", from ?? ""), ("to", to ?? "")).DatesLabel.Should().Be(label);

    [Fact]
    public void Chips_NameEachFilter_AndEachRemovesOnlyItself()
    {
        var f = new ArticleFilter
        {
            Search = "lure", Status = ArticleStatus.Draft, From = new(2026, 10, 1), To = new(2026, 10, 31), Assessment = ArticleAssessment.Threat,
            Category = "fraud", Tag = "ransomware", Tactic = MitreTactic.InitialAccess, Technique = "T1566", WithSubtechniques = true,
        };
        var chips = f.Chips(k => k == "ransomware" ? "Ransomware" : null, k => k == "fraud" ? "Fraud & scams" : null);
        chips.Select(c => c.Label).Should().Equal("Oct 2026", "Status: Draft", "Assessment: Threats", "Category: Fraud & scams", "Tag: Ransomware",
            "Tactic: Initial Access", "Technique: T1566 Phishing and sub-techniques");
        chips[0].Without.Should().Be(f with { From = null, To = null });
        chips[^1].Without.Should().Be(f with { Technique = null, WithSubtechniques = false });
        chips.Should().OnlyContain(c => c.Without.Search == "lure", "the words stay");
        f.Cleared().Should().Be(new ArticleFilter { Search = "lure" });
        f.Cleared().Narrowed.Should().BeFalse();

        var none = new ArticleFilter { Tag = ArticleFilter.None, Technique = ArticleFilter.None, Category = "gone" }.Chips(_ => null, _ => null);
        none.Select(c => c.Label).Should().Equal("Category: gone", "No scorecard tag", "No technique");
    }

    [Theory]
    [InlineData("articles:all", true, "")]
    [InlineData("articles:threat", true, "assess=threat")]
    [InlineData("articles:p1-p2", true, "assess=p1-p2")]
    [InlineData("articles:informational", true, "assess=informational")]
    [InlineData("tag:ransomware", true, "tag=ransomware")]
    [InlineData("tag:none", true, "tag=none")]
    [InlineData("category:fraud", true, "category=fraud")]
    [InlineData("tactic:TA0001", true, "tactic=TA0001")]
    [InlineData("tactic:TA0001:T1566", true, "tactic=TA0001&technique=T1566&withsubs=true")]
    [InlineData("tactic:TA0001:T1566.001", false, "tactic=TA0001&technique=T1566.001")]
    [InlineData("technique:T1566", true, "technique=T1566&withsubs=true")]
    [InlineData("technique:T1566", false, "technique=T1566")]
    [InlineData("technique:none", true, "technique=none")]
    public void ATrendLine_IsAFilter(string key, bool rolledUp, string query)
    {
        var f = TrendsService.FilterFor(key, rolledUp)!;
        string.Join('&', f.ToQuery().Where(q => q.Value is not null).Select(q => $"{q.Key}={q.Value}")).Should().Be(query);
    }

    [Theory]
    [InlineData("actions:raised")]
    [InlineData("articles:nonsense")]
    [InlineData("tactic:TA9999")]
    [InlineData("nonsense")]
    public void OtherLines_AreNotFilters(string key) => TrendsService.FilterFor(key, rolledUp: true).Should().BeNull();

    [Fact]
    public void MatchingTechniques_AreTheOnesTheFilterListsAnArticleFor()
    {
        string[] mapped = ["T1059", "T1566.001", "T1190", "T1566", "T1566.002", "T1190", "T9999"];
        new ArticleFilter().MatchingTechniques(mapped).Should().BeEmpty("no ATT&CK filter, nothing to explain");
        new ArticleFilter { Status = ArticleStatus.Ready }.MatchingTechniques(mapped).Should().BeEmpty();
        new ArticleFilter { Technique = ArticleFilter.None }.MatchingTechniques(mapped).Should().BeEmpty();
        new ArticleFilter { Technique = ArticleFilter.None, Tactic = MitreTactic.InitialAccess }.MatchingTechniques(mapped).Should().BeEmpty();

        // A tactic: every technique under it, a sub-technique by its own tactics, once each, by id.
        new ArticleFilter { Tactic = MitreTactic.InitialAccess }.MatchingTechniques(mapped)
            .Should().Equal("T1190", "T1566", "T1566.001", "T1566.002");
        new ArticleFilter { Tactic = MitreTactic.Execution }.MatchingTechniques(mapped).Should().Equal("T1059");

        // A technique: itself; its sub-techniques only rolled up; a sub-technique only itself.
        new ArticleFilter { Technique = "T1566" }.MatchingTechniques(mapped).Should().Equal("T1566");
        new ArticleFilter { Technique = "T1566", WithSubtechniques = true }.MatchingTechniques(mapped).Should().Equal("T1566", "T1566.001", "T1566.002");
        new ArticleFilter { Technique = "T1566.001" }.MatchingTechniques(mapped).Should().Equal("T1566.001");
        new ArticleFilter { Technique = "T156", WithSubtechniques = true }.MatchingTechniques(["T1566"]).Should().BeEmpty("a prefix isn't a parent");
        new ArticleFilter { Technique = "T9999" }.MatchingTechniques(mapped).Should().ContainSingle().Which.Should().Be("T9999", "an id the catalogue doesn't know still matches");

        // Both: the technique under the tactic.
        new ArticleFilter { Tactic = MitreTactic.InitialAccess, Technique = "T1566", WithSubtechniques = true }.MatchingTechniques(mapped)
            .Should().Equal("T1566", "T1566.001", "T1566.002");
        new ArticleFilter { Tactic = MitreTactic.Execution, Technique = "T1566", WithSubtechniques = true }.MatchingTechniques(mapped).Should().BeEmpty();
        new ArticleFilter { Tactic = MitreTactic.InitialAccess }.MatchesTechniques.Should().BeTrue();
        new ArticleFilter { Technique = ArticleFilter.None }.MatchesTechniques.Should().BeFalse();
    }
}

public class StartOfDayTests
{
    [Theory]
    [InlineData("America/New_York")]
    [InlineData("Europe/London")]
    [InlineData("Asia/Kolkata")]
    public void StartOfDay_IsTheFirstInstantOnThatLocalDay_AllYear(string zone)
    {
        var clock = new Tide.Application.Org.OrgClock(Microsoft.Extensions.Options.Options.Create(new Tide.Application.Org.OrgOptions { TimeZone = zone }));
        DateOnly Local(DateTimeOffset at) => DateOnly.FromDateTime(clock.ToLocal(at).DateTime);
        for (var day = new DateOnly(2026, 1, 1); day.Year == 2026; day = day.AddDays(1))
        {
            var start = clock.StartOfDay(day);
            Local(start).Should().Be(day);
            Local(start.AddTicks(-1)).Should().Be(day.AddDays(-1), $"{day} starts no later in {zone}");
        }
        clock.StartOfDay(new DateOnly(2026, 10, 1)).Should().Be(clock.ToLocal(clock.StartOfDay(new DateOnly(2026, 10, 1))));
    }
}
