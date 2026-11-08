using AwesomeAssertions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

public class BriefEditionTests
{
    private static Brief BriefWith(params Article[] articles)
    {
        var brief = new Brief { BriefDate = new DateOnly(2026, 10, 26) };
        for (var i = 0; i < articles.Length; i++)
            brief.Items.Add(new BriefItem { ArticleId = articles[i].Id, Article = articles[i], SortOrder = i });
        return brief;
    }

    private static Article Article(TlpLevel tlp, params (string Text, TlpLevel Tlp)[] bullets)
    {
        var a = new Article { Title = "A " + tlp, Tlp = tlp, Bluf = "x" };
        for (var i = 0; i < bullets.Length; i++) a.Bullets.Add(new ArticleBullet { Text = bullets[i].Text, Tlp = bullets[i].Tlp, SortOrder = i });
        return a;
    }

    [Fact]
    public void Build_Public_LeavesOutArticlesAboveTheCeiling()
    {
        var open = Article(TlpLevel.Clear, ("public", TlpLevel.Clear));
        var amber = Article(TlpLevel.Amber, ("isac", TlpLevel.Amber));

        var edition = BriefEditions.Build(BriefWith(open, amber), Edition.Public, TlpLevel.Green);

        edition.Articles.Select(a => a.Article).Should().Equal(open);
        edition.WithheldArticles.Should().Be(1);
    }

    [Fact]
    public void Build_Public_LeavesOutRestrictedBulletsAndScreenshots_IncludingCaptions()
    {
        var a = Article(TlpLevel.Clear, ("open", TlpLevel.Clear), ("restricted", TlpLevel.Amber));
        a.Screenshots.Add(new ArticleScreenshot { FileId = Guid.NewGuid(), Caption = "open shot", Tlp = TlpLevel.Green, SortOrder = 0 });
        a.Screenshots.Add(new ArticleScreenshot { FileId = Guid.NewGuid(), Caption = "secret shot", Tlp = TlpLevel.Red, SortOrder = 1 });

        var edition = BriefEditions.Build(BriefWith(a), Edition.Public, TlpLevel.Green);

        var shown = edition.Articles.Single();
        shown.Bullets.Select(b => b.Text).Should().Equal("open");
        shown.Screenshots.Select(s => s.Caption).Should().Equal("open shot");
        shown.WithheldParts.Should().Be(2);
    }

    [Fact]
    public void Build_APartCountsAtItsArticlesTlp_WhenThatsHigher()
    {
        // The article is AMBER; a bullet left at CLEAR is still AMBER, so a GREEN edition can't show it by itself.
        var a = Article(TlpLevel.Amber, ("left at clear", TlpLevel.Clear));

        var edition = BriefEditions.Build(BriefWith(a), Edition.Public, TlpLevel.Green);

        edition.Articles.Should().BeEmpty();
        edition.WithheldArticles.Should().Be(1);
    }

    [Fact]
    public void Build_Team_KeepsEverythingUpToItsCeiling_InRunningOrder()
    {
        var first = Article(TlpLevel.Amber, ("a", TlpLevel.AmberStrict));
        var second = Article(TlpLevel.Clear, ("b", TlpLevel.Clear), ("named people only", TlpLevel.Red));
        var brief = BriefWith(first, second);
        brief.Items[0].SortOrder = 5;   // second now runs first

        var edition = BriefEditions.Build(brief, Edition.Team, TlpLevel.AmberStrict);

        edition.Articles.Select(x => x.Article).Should().Equal(second, first);
        edition.Articles[0].Bullets.Select(b => b.Text).Should().Equal("b");
        edition.Marking.Should().Be(TlpLevel.AmberStrict);
    }

    [Fact]
    public void Marking_IsTheHighestTlpShown()
    {
        var a = Article(TlpLevel.Clear, ("x", TlpLevel.Green));
        BriefEditions.Build(BriefWith(a), Edition.Public, TlpLevel.Green).Marking.Should().Be(TlpLevel.Green);
        BriefEditions.Build(BriefWith(), Edition.Public, TlpLevel.Green).Marking.Should().Be(TlpLevel.Clear);
    }

    [Fact]
    public void Build_WhyItMatters_HasItsOwnTlp_AndIsWithheldAboveTheCeiling()
    {
        var a = Article(TlpLevel.Clear, ("open", TlpLevel.Clear));
        a.WhyItMatters = "Our vendor's VPN runs this version.";
        a.WhyItMattersTlp = TlpLevel.Amber;

        var pub = BriefEditions.Build(BriefWith(a), Edition.Public, TlpLevel.Green).Articles.Single();
        var team = BriefEditions.Build(BriefWith(a), Edition.Team, TlpLevel.AmberStrict).Articles.Single();

        pub.ShowWhy.Should().BeFalse();
        pub.WithheldParts.Should().Be(1);
        pub.Marking.Should().Be(TlpLevel.Clear);
        team.ShowWhy.Should().BeTrue();
        team.Marking.Should().Be(TlpLevel.Amber, "the article holds an AMBER part, though it's marked CLEAR");
        a.HighestTlp.Should().Be(TlpLevel.Amber);
    }

    [Fact]
    public void Build_IndicatorsAndActions_AreOnTheTeamEditionOnly()
    {
        var a = Article(TlpLevel.Clear);
        a.Indicators.Add(new ArticleIndicator { Type = IndicatorType.IpAddress, Value = "203.0.113.7" });
        var actions = new[] { (a.Id, new EditionAction(12, "Sweep the proxy logs", "Cyber Defense", null, ActionStatus.Open)) }
            .ToLookup(x => x.Item1, x => x.Item2);

        var team = BriefEditions.Build(BriefWith(a), Edition.Team, TlpLevel.AmberStrict, actions).Articles.Single();
        var pub = BriefEditions.Build(BriefWith(a), Edition.Public, TlpLevel.Green, actions).Articles.Single();

        team.Indicators.Should().ContainSingle();
        team.Actions.Select(x => x.Number).Should().Equal(12);
        pub.Indicators.Should().BeEmpty();
        pub.Actions.Should().BeEmpty();
    }

    [Fact]
    public void Build_TheOpeningNote_IsLeftOutAboveTheCeiling_AndMarksTheEditionsThatCarryIt()
    {
        var brief = BriefWith(Article(TlpLevel.Clear));
        brief.Intro = "Partner briefing at 2pm.";
        brief.IntroTlp = TlpLevel.Amber;

        var pub = BriefEditions.Build(brief, Edition.Public, TlpLevel.Green);
        var team = BriefEditions.Build(brief, Edition.Team, TlpLevel.AmberStrict);

        pub.ShowIntro.Should().BeFalse();
        pub.Marking.Should().Be(TlpLevel.Clear, "the note it leaves out doesn't count");
        team.ShowIntro.Should().BeTrue();
        team.Marking.Should().Be(TlpLevel.Amber, "the articles are CLEAR, the note is AMBER");

        brief.Intro = "  ";
        BriefEditions.Build(brief, Edition.Team, TlpLevel.AmberStrict).ShowIntro.Should().BeFalse("there's nothing to show");
    }

    // ---- One cut for a saved brief and an unsaved draft (BuildArticle, ArticleForm.ToPreview) ----
    //
    // Each fixture is an article as a save leaves it. For both editions and every ceiling, the cut Build makes of it
    // must match the cut BuildArticle makes of the editor's unsaved copy (ArticleForm.From(...).ToPreview()), and both
    // must match the golden file (tests/Tide.UnitTests/Golden/editions.txt). To accept a deliberate change, run the
    // tests with TIDE_UPDATE_GOLDEN=1 and review the diff.

    private static readonly IReadOnlyDictionary<string, Article> Saved = new Dictionary<string, Article>
    {
        // GREEN, with a part left below it (from before the pickers held the floor), parts above it, and an AMBER "why".
        ["mixed"] = Fixture(TlpLevel.Green, "The VPN appliance flaw is being exploited.", TlpLevel.Amber,
            [("left at clear", TlpLevel.Clear), ("green", TlpLevel.Green), ("amber", TlpLevel.Amber), ("red", TlpLevel.Red)],
            [("open shot", TlpLevel.Green), ("strict shot", TlpLevel.AmberStrict), (null, TlpLevel.Clear)]),
        // CLEAR with no "why it matters" at all: nothing to withhold or show.
        ["no-why"] = Fixture(TlpLevel.Clear, null, TlpLevel.Clear, [("one", TlpLevel.Clear), ("two", TlpLevel.Green)], []),
        // AMBER, so the company-wide edition leaves it out whole; its "why" is stored below it.
        ["amber"] = Fixture(TlpLevel.Amber, "Our vendor runs this.", TlpLevel.Clear,
            [("isac", TlpLevel.AmberStrict)], [("partner shot", TlpLevel.Amber)]),
        // RED: only a RED ceiling shows it.
        ["red"] = Fixture(TlpLevel.Red, "Named people only.", TlpLevel.Red, [("red", TlpLevel.Red)], []),
    };

    private static readonly ILookup<Guid, EditionAction> SavedActions = Saved.Values
        .SelectMany(a => new[]
        {
            (a.Id, new EditionAction(7, "Patch the VPN for " + a.Title, "Infrastructure", new DateOnly(2026, 11, 2), ActionStatus.Open)),
            (a.Id, new EditionAction(9, "Hunt for " + a.Title, "Cyber Defense", null, ActionStatus.InProgress)),
        })
        .ToLookup(x => x.Item1, x => x.Item2);

    private static Article Fixture(TlpLevel tlp, string? why, TlpLevel whyTlp, (string Text, TlpLevel Tlp)[] bullets,
        (string? Caption, TlpLevel Tlp)[] shots)
    {
        var a = new Article
        {
            Title = $"Article at {tlp}", SourceName = "BleepingComputer", SourceUrl = "https://example.org/story",
            Tlp = tlp, Classification = Classification.Threat, Priority = Priority.P1, Bluf = "Bottom line.",
            WhyItMatters = why, WhyItMattersTlp = whyTlp, RecommendedActions = "Patch it.",
        };
        for (var i = 0; i < bullets.Length; i++)
            a.Bullets.Add(new ArticleBullet { ArticleId = a.Id, Text = bullets[i].Text, Tlp = bullets[i].Tlp, SortOrder = i });
        for (var i = 0; i < shots.Length; i++)
            a.Screenshots.Add(new ArticleScreenshot { ArticleId = a.Id, FileId = Guid.NewGuid(), Caption = shots[i].Caption, Tlp = shots[i].Tlp, SortOrder = i });
        a.Indicators.Add(new ArticleIndicator { ArticleId = a.Id, Type = IndicatorType.Domain, Value = "evil.example", Note = "C2" });
        a.Indicators.Add(new ArticleIndicator { ArticleId = a.Id, Type = IndicatorType.IpAddress, Value = "203.0.113.7" });
        return a;
    }

    public static TheoryData<string, Edition, TlpLevel> Cuts()
    {
        var data = new TheoryData<string, Edition, TlpLevel>();
        foreach (var name in Saved.Keys)
            foreach (var edition in Enum.GetValues<Edition>())
                foreach (var ceiling in Enum.GetValues<TlpLevel>())
                    data.Add(name, edition, ceiling);
        return data;
    }

    /// <summary>One line for what an edition shows of an article: what the golden file holds, and what the parity
    /// test compares.</summary>
    private static string Describe(string name, Edition edition, TlpLevel ceiling, EditionArticle? cut)
    {
        var head = $"{name} {edition} {Tlp.Label(ceiling)}:";
        if (cut is null) return head + " left out";
        var a = cut.Article;
        return string.Join(" | ",
            head + " shown, marked " + Tlp.Label(cut.Marking),
            "bullets " + List(cut.Bullets.Select(b => $"{b.Text} ({Tlp.Label(a.Effective(b.Tlp))})")),
            "screenshots " + List(cut.Screenshots.Select(s => $"{s.Caption ?? "(no caption)"} ({Tlp.Label(a.Effective(s.Tlp))})")),
            "why " + (cut.ShowWhy ? a.WhyItMatters : "none"),
            $"withheld {cut.WithheldParts}",
            "indicators " + List(cut.Indicators.Select(i => $"{i.Type} {i.Value} {i.Note}".TrimEnd())),
            "actions " + List(cut.Actions.Select(x => $"#{x.Number} {x.Status}")));

        static string List(IEnumerable<string> items) => string.Join(", ", items) is { Length: > 0 } s ? s : "none";
    }

    private static string DescribeBuild(string name, Edition edition, TlpLevel ceiling)
    {
        var built = BriefEditions.Build(BriefWith(Saved[name]), edition, ceiling, SavedActions);
        built.WithheldArticles.Should().Be(built.Articles.Count == 0 ? 1 : 0);
        return Describe(name, edition, ceiling, built.Articles.SingleOrDefault());
    }

    [Theory]
    [MemberData(nameof(Cuts))]
    public void BuildArticle_OnTheEditorsUnsavedCopy_CutsAsBuildDoes(string name, Edition edition, TlpLevel ceiling)
    {
        var saved = Saved[name];
        var preview = ArticleForm.From(saved).ToPreview();

        var cut = BriefEditions.BuildArticle(preview, edition, ceiling, SavedActions[saved.Id].ToList());

        Describe(name, edition, ceiling, cut).Should().Be(DescribeBuild(name, edition, ceiling));
        if (edition == Edition.Public && cut is not null)
        {
            cut.Indicators.Should().BeEmpty("the company-wide edition never carries indicators");
            cut.Actions.Should().BeEmpty("nor actions");
        }
    }

    [Fact]
    public void Build_MatchesTheGoldenEditions()
    {
        var actual = string.Join("\n", Cuts().Select(row => DescribeBuild((string)row[0], (Edition)row[1], (TlpLevel)row[2]))) + "\n";
        if (Environment.GetEnvironmentVariable("TIDE_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Golden", "editions.txt"), actual);
            return;
        }
        actual.Should().Be(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "editions.txt")).Replace("\r\n", "\n"));
    }

    [Fact]
    public void BuildArticle_WithoutActions_ListsNone()
    {
        var cut = BriefEditions.BuildArticle(Saved["no-why"], Edition.Team, TlpLevel.Red);
        cut!.Actions.Should().BeEmpty();
        cut.Indicators.Should().HaveCount(2);
    }

    [Fact]
    public void ToPreview_MapsTheFormAsASaveWould()
    {
        var shot = Guid.NewGuid();
        var tag = Guid.NewGuid();
        var form = new ArticleForm
        {
            Id = Guid.NewGuid(),
            Title = "  A title\nover two lines ",
            SourceName = "  ",
            SourceUrl = " https://example.org/a ",
            Tlp = TlpLevel.Green,
            Classification = Classification.Informational,
            Priority = Priority.P1,
            Bluf = " Bottom line. ",
            WhyItMatters = "  Line one\nline two  ",
            WhyItMattersTlp = TlpLevel.Amber,
            RecommendedActions = "   ",
            Bullets = [new() { Text = "  second  ", Tlp = TlpLevel.Amber }, new() { Text = "   " }, new() { Text = "third" }],
            Screenshots = [new() { Id = shot, FileId = Guid.NewGuid(), Caption = "  a shot ", Tlp = TlpLevel.Red }],
            TagIds = [tag],
            TechniqueIds = ["t1566.001", "T1566.001", "T0000.999"],
            Indicators =
            [
                new() { Type = IndicatorType.Domain, Value = " evil[.]example. ", Note = "  C2 " },
                new() { Type = IndicatorType.Domain, Value = "EVIL.example", Note = "a repeat" },
                new() { Type = IndicatorType.IpAddress, Value = "  " },
            ],
            Cves = ["cve-2026-12345", "CVE-2026-12345", "not a cve"],
        };

        var a = form.ToPreview();

        a.Id.Should().Be(form.Id);
        a.Title.Should().Be("A title over two lines");
        a.SourceName.Should().BeNull();
        a.SourceUrl.Should().Be("https://example.org/a");
        a.Priority.Should().BeNull("only a threat keeps its priority");
        a.Bluf.Should().Be("Bottom line.");
        a.WhyItMatters.Should().Be("Line one\nline two");
        a.WhyItMattersTlp.Should().Be(TlpLevel.Amber);
        a.RecommendedActions.Should().BeNull();
        a.Bullets.Select(b => (b.Text, b.Tlp, b.SortOrder)).Should().Equal(("second", TlpLevel.Amber, 0), ("third", TlpLevel.Clear, 1));
        a.Screenshots.Should().ContainSingle().Which.Should().Match<ArticleScreenshot>(s =>
            s.Id == shot && s.Caption == "a shot" && s.Tlp == TlpLevel.Red && s.SortOrder == 0 && s.ArticleId == form.Id);
        a.Tags.Select(t => t.TagId).Should().Equal(tag);
        a.Techniques.Select(t => t.TechniqueId).Should().Equal("T1566.001");
        a.Indicators.Select(i => (i.Type, i.Value, i.Note)).Should().Equal((IndicatorType.Domain, "evil.example", "C2"));
        a.Cves.Select(c => c.CveId).Should().Equal("CVE-2026-12345");
    }

    [Fact]
    public void ToPreview_KeepsWhatASaveWouldRefuse_ButNeverABadLink()
    {
        var form = new ArticleForm
        {
            Title = "",
            SourceUrl = "javascript:alert(1)",
            Classification = Classification.Threat,
            Priority = Priority.P4,
            Bluf = new string('b', ArticleService.MaxBluf + 1),
            Bullets = Enumerable.Range(0, Domain.Entities.Article.MaxBullets + 1).Select(i => new BulletForm { Text = $"b{i}" }).ToList(),
            Indicators = [new() { Type = IndicatorType.Other, Value = new string('x', 2001), Note = new string('n', 301) }],
        };

        var a = form.ToPreview();

        a.Title.Should().BeEmpty();
        a.SourceUrl.Should().BeNull("a link a save would refuse is never shown");
        a.Priority.Should().Be(Priority.P4);
        a.Bluf.Should().HaveLength(ArticleService.MaxBluf + 1);
        a.Bullets.Should().HaveCount(Domain.Entities.Article.MaxBullets + 1);
        a.Indicators.Should().ContainSingle().Which.Note.Should().HaveLength(301);
    }
}
