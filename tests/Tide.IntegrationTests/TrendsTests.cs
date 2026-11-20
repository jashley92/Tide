using AwesomeAssertions;
using Tide.Application.Metrics;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Articles;
using Tide.Application.Mitre;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Application.Trends;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

public class TrendsServiceTests : IDisposable
{
    private static readonly Period Sep = new(PeriodKind.Month, 2026, 9);
    private static readonly Period Aug = Sep.Previous;

    private TestDb _db = new();

    public TrendsServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    private TrendsService Trends() => new(_db, _db.User, _db.Clock);

    private static DateTimeOffset Noon(int month, int day) => new(2026, month, day, 16, 0, 0, TimeSpan.Zero);

    /// <summary>Adds rows as they'd be after the fact, then backdates what the audit stamp set to now.</summary>
    private async Task Add(params object[] rows)
    {
        await using var db = _db.Context();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task Written(Guid articleId, DateTimeOffset at)
    {
        await using var db = _db.Context();
        await db.Articles.Where(a => a.Id == articleId).ExecuteUpdateAsync(s => s.SetProperty(a => a.CreatedAtUtc, at));
    }

    private async Task Raised(Guid actionId, DateTimeOffset at)
    {
        await using var db = _db.Context();
        await db.Actions.Where(a => a.Id == actionId).ExecuteUpdateAsync(s => s.SetProperty(a => a.CreatedAtUtc, at));
    }

    private static Article Article(string title, ArticleStatus status = ArticleStatus.Ready, Classification classification = Classification.Informational) =>
        new() { Title = title, Status = status, Classification = classification, Priority = classification == Classification.Threat ? Priority.P2 : null };

    private static Brief Briefed(DateOnly day, params Article[] articles)
    {
        var b = new Brief { BriefDate = day };
        foreach (var a in articles) b.Items.Add(new BriefItem { ArticleId = a.Id, SortOrder = b.Items.Count });
        return b;
    }

    [Fact]
    public async Task Articles_CountInTheMonthFirstBriefed_OrWritten_AndDraftsDont()
    {
        var updated = Article("Briefed twice", classification: Classification.Threat);   // written Aug, briefed Sep 2 and 9
        var unbriefed = Article("Never briefed");                                         // written Sep
        var draft = Article("Still a draft", ArticleStatus.Draft);                        // written Sep
        var august = Article("August");                                                   // briefed Aug
        var october = Article("October");                                                 // briefed Oct
        await Add(updated, unbriefed, draft, august, october,
            Briefed(new DateOnly(2026, 9, 2), updated), Briefed(new DateOnly(2026, 9, 9), updated),
            Briefed(new DateOnly(2026, 8, 6), august), Briefed(new DateOnly(2026, 10, 1), october));
        await Written(updated.Id, Noon(8, 20));
        await Written(unbriefed.Id, Noon(9, 10));
        await Written(draft.Id, Noon(9, 11));

        var report = await Trends().ReportAsync(Sep);

        report.Periods.Should().HaveCount(TrendsService.Months).And.EndWith(Sep);
        report.Periods[0].Should().Be(new Period(PeriodKind.Month, 2026, 4));
        var all = report.Find("articles:all")!;
        all.Ids[^1].Should().BeEquivalentTo([updated.Id, unbriefed.Id], "each article once, in the month it was first briefed");
        all.Ids[^2].Should().Equal(august.Id);
        (all.Current, all.Previous, all.Change, all.ChangeRatio).Should().Be((2, 1, 1, 1.0));
        report.Find("articles:threat")!.Ids[^1].Should().Equal(updated.Id);
        report.Find("articles:p1-p2")!.Current.Should().Be(1);
        report.CountedOn.Should().NotContainKey(draft.Id).And.NotContainKey(october.Id);
        report.CountedOn[updated.Id].Should().Be(new DateOnly(2026, 9, 2));
    }

    [Fact]
    public async Task Tags_RollUpToTheirCategory_CountingEachArticleOnce()
    {
        var cat = new ScorecardCategory { Key = "ransomware", Name = "Ransomware & extortion" };
        var ransomware = new ScorecardTag { Key = "ransomware", Name = "Ransomware", Category = cat, SortOrder = 0 };
        var extortion = new ScorecardTag { Key = "data-extortion", Name = "Data extortion", Category = cat, SortOrder = 1 };
        var unused = new ScorecardTag { Key = "old", Name = "Old tag", Category = cat, SortOrder = 2, IsRetired = true };
        var both = Article("Both tags");
        var one = Article("One tag");
        var none = Article("No tags");
        both.Tags = [new ArticleTag { ArticleId = both.Id, TagId = ransomware.Id }, new ArticleTag { ArticleId = both.Id, TagId = extortion.Id }];
        one.Tags = [new ArticleTag { ArticleId = one.Id, TagId = ransomware.Id }];
        await Add(cat, ransomware, extortion, unused, both, one, none,
            Briefed(new DateOnly(2026, 9, 1), both, none), Briefed(new DateOnly(2026, 8, 3), one));

        var report = await Trends().ReportAsync(Sep);

        report.Tags.Select(r => r.Key).Should().Equal("category:ransomware", "tag:ransomware", "tag:data-extortion", "tag:none");
        var category = report.Find("category:ransomware")!;
        category.IsRollup.Should().BeTrue();
        category.Ids[^1].Should().Equal(both.Id);
        category.Ids[^2].Should().Equal(one.Id);
        report.Find("tag:ransomware")!.Ids.Select(p => p.Count).Should().Equal(0, 0, 0, 0, 1, 1);
        report.Find("tag:data-extortion")!.Current.Should().Be(1);
        report.Find("tag:none")!.Ids[^1].Should().Equal(none.Id);
    }

    [Fact]
    public async Task Attack_RollsSubtechniquesUp_AndCountsTacticsOncePerArticle()
    {
        var phish = Article("Phishing wave");
        var plain = Article("Nothing mapped");
        phish.Techniques = [new ArticleTechnique { ArticleId = phish.Id, TechniqueId = "T1566.001" }, new ArticleTechnique { ArticleId = phish.Id, TechniqueId = "T1566.002" }];
        await Add(phish, plain, Briefed(new DateOnly(2026, 9, 1), phish, plain));

        var rolled = await Trends().ReportAsync(Sep);
        rolled.Find("technique:T1566")!.Current.Should().Be(1, "both sub-techniques are Phishing, on one article");
        rolled.Find("technique:T1566")!.Name.Should().Be("Initial Access › T1566 Phishing", "named under its primary tactic");
        rolled.Find("technique:T1566.001").Should().BeNull();
        rolled.Find("tactic:TA0001")!.Current.Should().Be(1, "Initial Access, once");
        rolled.Find("tactic:TA0040")!.Current.Should().Be(0);
        rolled.Attack.Count(r => r.Group == "Tactics").Should().Be(15, "every tactic is listed, seen or not");
        rolled.Find("technique:none")!.Ids[^1].Should().Equal(plain.Id);

        var split = await Trends().ReportAsync(Sep, rollUpSubtechniques: false);
        split.Find("technique:T1566.001")!.Current.Should().Be(1);
        split.Find("technique:T1566.002")!.Current.Should().Be(1);
        split.Find("technique:T1566").Should().BeNull();
    }

    [Fact]
    public async Task Attack_ListsEachTacticsTechniques_UnderEveryTacticTheyBelongTo()
    {
        // Valid Accounts sits in four tactics (Defense Evasion first); Phishing in one.
        var accounts = Article("Stolen logins");
        var both = Article("Phish then log in");
        accounts.Techniques = [new ArticleTechnique { ArticleId = accounts.Id, TechniqueId = "T1078.004" }];
        both.Techniques = [new ArticleTechnique { ArticleId = both.Id, TechniqueId = "T1078" }, new ArticleTechnique { ArticleId = both.Id, TechniqueId = "T1566.001" }];
        await Add(accounts, both, Briefed(new DateOnly(2026, 9, 3), accounts, both));

        var report = await Trends().ReportAsync(Sep);

        var catalog = AttackCatalog.Find("T1078")!;
        catalog.Tactics.Should().HaveCountGreaterThan(1);
        foreach (var tactic in catalog.Tactics)
        {
            var key = "tactic:" + AttackMatrix.TacticId(tactic);
            var line = report.Find($"{key}:T1078")!;
            line.Name.Should().Be($"{AttackMatrix.TacticName(tactic)} › T1078 Valid Accounts");
            line.Ids[^1].Should().BeEquivalentTo([accounts.Id, both.Id], "the sub-technique rolls up, and each tactic lists it");
            report.TechniquesOf(key).Should().Contain(r => r.Key == line.Key);
        }
        report.Find("technique:T1078")!.Name.Should().Be(TrendsService.TechniqueName("T1078", catalog.PrimaryTactic), "once, under its primary tactic");
        report.Find("technique:T1078")!.Current.Should().Be(2, "the flat list counts an article once however many tactics");
        report.TechniquesOf("tactic:TA0001").Select(r => r.Key).Should().Equal("tactic:TA0001:T1078", "tactic:TA0001:T1566");
        report.TechniquesOf("tactic:TA0040").Should().BeEmpty();
        report.TacticTechniques.Should().OnlyContain(r => report.Find(r.Key.Substring(0, r.Key.LastIndexOf(':')))!.Ids
            .Zip(r.Ids).All(p => p.Second.All(id => p.First.Contains(id))), "a tactic counts every article its techniques do");
    }

    [Fact]
    public void TechniqueName_PutsTheTacticFirst_WhenThereIsOne()
    {
        TrendsService.TechniqueName("T1059", MitreTactic.Execution).Should().Be("Execution › T1059 Command and Scripting Interpreter");
        TrendsService.TechniqueName("T1059", null).Should().Be("T1059 Command and Scripting Interpreter");
        TrendsService.TechniqueName("T1059", MitreTactic.Unspecified).Should().Be("T1059 Command and Scripting Interpreter");
        TrendsService.TechniqueName("T9999", MitreTactic.Execution).Should().Be("Execution › T9999", "an id the catalogue doesn't know keeps its id");
    }

    [Fact]
    public async Task Span_ChoosesHowManyPeriods_FromTheOnesOffered()
    {
        var old = Article("A year ago");
        await Add(old, Briefed(new DateOnly(2025, 10, 6), old));

        (await Trends().ReportAsync(Sep)).Periods.Should().HaveCount(6);
        var year = await Trends().ReportAsync(Sep, span: 12);
        year.Periods.Should().HaveCount(12).And.EndWith(Sep);
        year.Periods[0].Should().Be(new Period(PeriodKind.Month, 2025, 10));
        year.Find("articles:all")!.Ids[0].Should().Equal(old.Id);
        (await Trends().ReportAsync(Sep, span: 7)).Periods.Should().HaveCount(TrendsService.Months, "7 isn't offered");
        (await Trends().ReportAsync(new Period(PeriodKind.Quarter, 2026, 3), span: 8)).Periods.Should().HaveCount(8);

        TrendsService.Spans(PeriodKind.Month).Should().Equal(6, 12, 24);
        TrendsService.Spans(PeriodKind.Quarter).Should().Equal(5, 8, 12);
        TrendsService.SpanOrDefault(PeriodKind.Quarter, 24).Should().Be(5);
        TrendsService.SpanOrDefault(PeriodKind.Month, null).Should().Be(6);
    }

    [Fact]
    public async Task Actions_RaisedForAPastDaysBrief_CountOnThatDay_NotWhenRecorded()
    {
        var team = new Team { Key = "cyber-defense", Name = "Cyber Defense" };
        var late = new ActionItem { Number = 1, Title = "Written up afterwards", Kind = ActionKind.Block, Team = team, RaisedOn = new(2026, 8, 31) };
        await Add(team, late);
        await Raised(late.Id, Noon(9, 2));   // recorded in September

        var report = await Trends().ReportAsync(Sep);

        report.Find("actions:raised")!.Ids[^2].Should().Equal([late.Id], "it belongs to August 31's brief");
        report.Find("actions:raised")!.Ids[^1].Should().BeEmpty();
    }

    [Fact]
    public async Task Actions_OverdueInAPeriodStillUnderWay_IsAsOfToday_NotItsEnd()
    {
        var team = new Team { Key = "cyber-defense", Name = "Cyber Defense" };
        var late = new ActionItem { Number = 1, Title = "Late", Kind = ActionKind.Block, Team = team, DueDate = new(2026, 9, 10) };
        var later = new ActionItem { Number = 2, Title = "Due next week", Kind = ActionKind.Block, Team = team, DueDate = new(2026, 9, 22) };
        await Add(team, late, later);
        await Raised(late.Id, Noon(9, 2));
        await Raised(later.Id, Noon(9, 2));
        var midSeptember = new OrgClock(Microsoft.Extensions.Options.Options.Create(new OrgOptions()), new TestClock(Noon(9, 15)));

        var report = await new TrendsService(_db, _db.User, midSeptember).ReportAsync(Sep);

        report.Find("actions:overdue-at-end")!.Ids[^1].Should().Equal([late.Id], "the other isn't due until the 22nd");
        report.Find("actions:open-at-end")!.Ids[^1].Should().BeEquivalentTo([late.Id, later.Id]);
    }

    [Fact]
    public async Task Actions_CountRaisedClosedHuntedAndOpenAtTheEnd()
    {
        var team = new Team { Key = "cyber-defense", Name = "Cyber Defense" };
        var onTime = new ActionItem { Number = 1, Title = "Sweep", Kind = ActionKind.IocSweep, Team = team, DueDate = new(2026, 9, 10),
            Status = ActionStatus.Done, Outcome = ActionOutcome.NoHits, ClosedDate = new(2026, 9, 8) };
        var late = new ActionItem { Number = 2, Title = "Patch", Kind = ActionKind.PatchRemediate, Team = team, DueDate = new(2026, 9, 1),
            Status = ActionStatus.Done, Outcome = ActionOutcome.Remediated, ClosedDate = new(2026, 9, 5) };
        var open = new ActionItem { Number = 3, Title = "Block", Kind = ActionKind.Block, Team = team, DueDate = new(2026, 9, 20) };
        var hunt = new HuntQuery { ActionId = onTime.Id, Dialect = "kql", Result = HuntResult.Hits, RunAtUtc = Noon(9, 7) };
        await Add(team, onTime, late, open, hunt);
        await Raised(onTime.Id, Noon(9, 3));
        await Raised(late.Id, Noon(8, 25));
        await Raised(open.Id, Noon(9, 15));

        var report = await Trends().ReportAsync(Sep);

        report.Find("actions:raised")!.Ids[^1].Should().BeEquivalentTo([onTime.Id, open.Id]);
        report.Find("actions:raised")!.Ids[^2].Should().Equal(late.Id);
        report.Find("actions:raised-kind:Block")!.Current.Should().Be(1);
        report.Find("actions:raised-kind:Awareness").Should().BeNull("a kind never raised isn't listed");
        report.Find("actions:raised-team:cyber-defense")!.Current.Should().Be(2);
        report.Find("actions:done")!.Current.Should().Be(2);
        report.Find("actions:on-time")!.Ids[^1].Should().Equal(onTime.Id);
        report.Find("actions:late")!.Ids[^1].Should().Equal(late.Id);
        report.Find("actions:outcome:Remediated")!.Current.Should().Be(1);
        report.OnTimeRatio.Should().Be(0.5);
        report.Find("actions:hunted")!.Ids[^1].Should().Equal(onTime.Id);
        report.Find("actions:hits")!.Current.Should().Be(1);
        report.Find("actions:open-at-end")!.Ids[^1].Should().Equal(open.Id);
        report.Find("actions:open-at-end")!.Ids[^2].Should().Equal([late.Id], "raised Aug 25, closed Sep 5: open when August ended");
        report.Find("actions:overdue-at-end")!.Ids[^1].Should().Equal(open.Id);
        report.IsActionRow(report.Find("actions:raised")!).Should().BeTrue();
        report.IsActionRow(report.Find("articles:all")!).Should().BeFalse();

        var rows = await _db.Actions().ListByIdAsync(report.Find("actions:raised")!.Ids[^1]);
        rows.Select(r => r.Number).Should().Equal(3, 1);
    }

    [Fact]
    public async Task DrillDown_ListsTheArticles_AndTheCsvsCarryThem()
    {
        var cat = new ScorecardCategory { Key = "phishing", Name = "Phishing & fraud" };
        var tag = new ScorecardTag { Key = "phishing", Name = "Phishing", Category = cat };
        var a = Article("=Lure, \"urgent\"", classification: Classification.Threat);
        a.Bluf = "Credential phishing.";
        a.Tags = [new ArticleTag { ArticleId = a.Id, TagId = tag.Id }];
        a.Techniques = [new ArticleTechnique { ArticleId = a.Id, TechniqueId = "T1566.002" }];
        await Add(cat, tag, a, Briefed(new DateOnly(2026, 9, 14), a));

        var report = await Trends().ReportAsync(new Period(PeriodKind.Quarter, 2026, 3));
        var row = report.Find("tag:phishing")!;
        var list = await Trends().ArticlesAsync(row.Ids[^1], report.CountedOn);

        list.Should().ContainSingle().Which.Should().Match<TrendArticle>(x =>
            x.CountedOn == new DateOnly(2026, 9, 14) && x.Briefed && x.Tags.Single() == "Phishing" && x.Techniques.Single() == "T1566.002");
        var csv = TrendsService.ArticlesCsv(list, "https://tide.example.org/");
        csv.Should().StartWith("counted_on,briefed,title,article_url,tlp,classification,priority,source,source_url,bluf,tags,techniques,tactics,actions\n")
            .And.Contain($"2026-09-14,yes,\"'=Lure, \"\"urgent\"\"\",https://tide.example.org/articles/{a.Id},TLP:CLEAR,Threat,P2,,,Credential phishing.,Phishing,T1566.002,Initial Access,0\n");
        var table = TrendsService.ToCsv(report);
        table.Should().StartWith("section,group,line,key,2025-Q3,2025-Q4,2026-Q1,2026-Q2,2026-Q3,change\n")
            .And.Contain("Scorecard tags,Phishing & fraud,Phishing,tag:phishing,0,0,0,0,1,1\n")
            .And.Contain("ATT&CK,Techniques,Initial Access › T1566 Phishing,technique:T1566,0,0,0,0,1,1\n")
            .And.Contain("ATT&CK by tactic,Initial Access,Initial Access › T1566 Phishing,tactic:TA0001:T1566,0,0,0,0,1,1\n");
    }

    [Fact]
    public async Task EveryArticleLine_OpensInTheArticleList_AsExactlyTheArticlesItCounted()
    {
        var cat = new ScorecardCategory { Key = "ransomware", Name = "Ransomware & extortion" };
        var ransomware = new ScorecardTag { Key = "ransomware", Name = "Ransomware", Category = cat };
        var extortion = new ScorecardTag { Key = "data-extortion", Name = "Data extortion", Category = cat, SortOrder = 1 };
        var fraudCat = new ScorecardCategory { Key = "fraud", Name = "Fraud", SortOrder = 1 };
        var phishing = new ScorecardTag { Key = "phishing", Name = "Phishing", Category = fraudCat };
        static void Tag(Article a, params ScorecardTag[] tags) => a.Tags = tags.Select(t => new ArticleTag { ArticleId = a.Id, TagId = t.Id }).ToList();
        static void Map(Article a, params string[] ids) => a.Techniques = ids.Select(id => new ArticleTechnique { ArticleId = a.Id, TechniqueId = id }).ToList();

        var updated = Article("Briefed in September, again in October", classification: Classification.Threat);   // counts Sep 2
        Tag(updated, ransomware); Map(updated, "T1566.001");
        var lateNight = Article("Written just before midnight, Aug 31");                                          // counts Aug (local day)
        Tag(lateNight, extortion); Map(lateNight, "T1078", "T1059");
        var unbriefedDraft = Article("A draft never briefed", ArticleStatus.Draft);                               // never counts
        Tag(unbriefedDraft, ransomware); Map(unbriefedDraft, "T1566");
        var briefedDraft = Article("A draft that went on a brief", ArticleStatus.Draft);                          // counts Sep 5
        Tag(briefedDraft, phishing); Map(briefedDraft, "T1566");
        var plain = Article("Nothing tagged or mapped", classification: Classification.Threat);                  // counts Jul 1
        plain.Priority = Priority.P1;
        var monthEnd = Article("Written at 23:59 on Sep 30");                                                    // counts Sep (local day)
        Tag(monthEnd, phishing, ransomware); Map(monthEnd, "T1566.002", "T1078.004");
        var older = Article("First briefed before the report, again in September");                              // never counts
        Tag(older, phishing); Map(older, "T1566");
        await Add(cat, ransomware, extortion, fraudCat, phishing, updated, lateNight, unbriefedDraft, briefedDraft, plain, monthEnd, older,
            Briefed(new DateOnly(2026, 9, 2), updated), Briefed(new DateOnly(2026, 10, 1), updated),
            Briefed(new DateOnly(2026, 9, 5), briefedDraft), Briefed(new DateOnly(2026, 7, 1), plain),
            Briefed(new DateOnly(2026, 3, 16), older), Briefed(new DateOnly(2026, 9, 21), older));
        await Written(lateNight.Id, new DateTimeOffset(2026, 9, 1, 3, 30, 0, TimeSpan.Zero));   // 23:30 on Aug 31 in New York
        await Written(monthEnd.Id, new DateTimeOffset(2026, 10, 1, 3, 59, 0, TimeSpan.Zero));   // 23:59 on Sep 30
        await Written(unbriefedDraft.Id, Noon(9, 10));

        var articles = _db.Articles();
        async Task<List<Guid>> Listed(ArticleFilter f)
        {
            var rows = (await articles.PageAsync(f, ArticleSort.Written, descending: true, take: ArticleService.MaxPage)).Rows;
            // Each row says why an ATT&CK filter listed it, and only then.
            rows.Should().OnlyContain(r => (r.MatchedTechniques.Count > 0) == f.MatchesTechniques, $"{f} explains its rows");
            return rows.Select(r => r.Id).ToList();
        }

        foreach (var rolledUp in new[] { true, false })
        {
            var report = await Trends().ReportAsync(Sep, rollUpSubtechniques: rolledUp);
            var named = rolledUp
                ? new[] { "tag:ransomware", "category:ransomware", "tag:none", "tactic:TA0001", "tactic:TA0001:T1566", "technique:T1566", "technique:T1078", "technique:none", "articles:p1-p2" }
                : ["tag:phishing", "category:fraud", "tactic:TA0002", "technique:T1566", "technique:T1566.002", "technique:T1078.004", "tactic:TA0005:T1078", "articles:threat"];
            foreach (var key in named) report.Find(key).Should().NotBeNull($"{key} is a line (rolled up: {rolledUp})");
            report.Find(rolledUp ? "technique:T1566" : "technique:T1566.001")!.Ids[^1].Should().Contain(updated.Id);

            var lines = report.Articles.Concat(report.Tags).Concat(report.Attack).Concat(report.TacticTechniques).ToList();
            foreach (var row in lines)
            {
                for (var i = 0; i < report.Periods.Count; i++)
                {
                    var filter = report.ArticlesFilter(row, report.Periods[i]);
                    filter.Should().NotBeNull(row.Key);
                    (await Listed(filter!)).Should().BeEquivalentTo(row.Ids[i], $"{row.Key} in {report.Periods[i]} (rolled up: {rolledUp})");
                }
                (await Listed(report.ArticlesFilter(row)!)).Should().BeEquivalentTo(row.Ids.SelectMany(p => p), $"{row.Key} over the report");
            }
            report.ArticlesFilter(report.Find("actions:raised")!).Should().BeNull("actions aren't articles");
        }

        var page = await articles.PageAsync(new ArticleFilter { From = Sep.Start, To = Sep.End }, ArticleSort.Title, descending: false, take: 1);
        page.Total.Should().Be(3, "updated, the briefed draft and the month-end article");
        page.Rows.Should().ContainSingle();
    }

    /// <summary>
    /// Articles and actions either side of a report's first and last days, for reports that read only those days: each
    /// line's count in each period, written out by title. The expected text is what the report gave when it read every
    /// article, brief and action, so reading less must leave every number as it was.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheReport_CountsTheSame_AtItsEdges_AsWhenItReadEverything(string provider)
    {
        _db = _db.On(provider);
        static DateTimeOffset NewYork(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, TimeSpan.FromHours(-4));
        static DateOnly D(int year, int month, int day) => new(year, month, day);
        var cat = new ScorecardCategory { Key = "ransomware", Name = "Ransomware & extortion" };
        var ransomware = new ScorecardTag { Key = "ransomware", Name = "Ransomware", Category = cat };
        var retired = new ScorecardTag { Key = "old-lure", Name = "Old lure", Category = cat, SortOrder = 1, IsRetired = true };
        var fraudCat = new ScorecardCategory { Key = "fraud", Name = "Fraud", SortOrder = 1 };
        var phishing = new ScorecardTag { Key = "phishing", Name = "Phishing", Category = fraudCat };
        var titles = new Dictionary<Guid, string>();
        var articles = new List<Article>();
        var briefs = new Dictionary<DateOnly, Brief>();
        var written = new List<(Guid Id, DateTimeOffset At)>();
        Article Make(string title, DateTimeOffset at, ScorecardTag[] tags, string[] techniques, ArticleStatus status = ArticleStatus.Ready,
            Classification classification = Classification.Informational, params DateOnly[] briefed)
        {
            var a = Article(title, status, classification);
            a.Tags = tags.Select(t => new ArticleTag { ArticleId = a.Id, TagId = t.Id }).ToList();
            a.Techniques = techniques.Select(id => new ArticleTechnique { ArticleId = a.Id, TechniqueId = id }).ToList();
            articles.Add(a);
            titles[a.Id] = title;
            written.Add((a.Id, at));
            foreach (var day in briefed)
            {
                var b = briefs.TryGetValue(day, out var on) ? on : briefs[day] = new Brief { BriefDate = day };
                b.Items.Add(new BriefItem { ArticleId = a.Id, SortOrder = b.Items.Count });
            }
            return a;
        }
        Make("Briefed Mar 31, again Apr 1", NewYork(2026, 3, 30, 9, 0), [ransomware], ["T1566.001"], briefed: [D(2026, 3, 31), D(2026, 4, 1)]);
        Make("Briefed Apr 1", NewYork(2026, 3, 31, 9, 0), [phishing], ["T1566.002"], classification: Classification.Threat, briefed: [D(2026, 4, 1)]);
        Make("Briefed Sep 30, again Oct 1", NewYork(2026, 9, 29, 9, 0), [ransomware, phishing], ["T1078.004", "T1059"], classification: Classification.Threat,
            briefed: [D(2026, 9, 30), D(2026, 10, 1)]);
        Make("First briefed Oct 1", NewYork(2026, 9, 30, 9, 0), [phishing], ["T1566"], briefed: [D(2026, 10, 1)]);
        Make("Written 23:30 Mar 31", NewYork(2026, 3, 31, 23, 30), [ransomware], ["T1486"]);
        Make("Written 00:10 Apr 1", NewYork(2026, 4, 1, 0, 10), [retired], ["T1486"], classification: Classification.Threat);
        Make("Written 23:59 Sep 30", NewYork(2026, 9, 30, 23, 59), [], ["T1190"]);
        Make("Written 00:01 Oct 1", NewYork(2026, 10, 1, 0, 1), [phishing], ["T1190"]);
        Make("Draft written in June", NewYork(2026, 6, 10, 9, 0), [ransomware], ["T1566"], ArticleStatus.Draft);
        Make("Draft briefed Jul 2", NewYork(2026, 6, 30, 9, 0), [phishing], ["T1566.001"], ArticleStatus.Draft, briefed: [D(2026, 7, 2)]);
        Make("Written in January, briefed Jun 15 and Jun 1", NewYork(2026, 1, 5, 9, 0), [ransomware], [], classification: Classification.Threat,
            briefed: [D(2026, 6, 15), D(2026, 6, 1)]);
        Make("Written in February, never briefed", NewYork(2026, 2, 10, 9, 0), [phishing], ["T1078"]);
        Make("Written after the brief it's on", NewYork(2026, 10, 3, 9, 0), [ransomware], ["T1059.001"], briefed: [D(2026, 9, 14)]);
        Make("Briefed a year before", NewYork(2025, 8, 4, 9, 0), [phishing], ["T1566.002"], briefed: [D(2025, 8, 5), D(2026, 5, 5)]);
        Make("Briefed Jul 1 2025", NewYork(2025, 6, 30, 9, 0), [ransomware], ["T1190"], classification: Classification.Threat, briefed: [D(2025, 7, 1)]);
        Make("Briefed Jun 30 2025", NewYork(2025, 6, 29, 9, 0), [ransomware], ["T1190"], briefed: [D(2025, 6, 30)]);
        foreach (var a in articles.Where(a => a.Classification == Classification.Threat && a.Title.Contains("Apr", StringComparison.Ordinal))) a.Priority = Priority.P1;

        var team = new Team { Key = "cyber-defense", Name = "Cyber Defense" };
        var other = new Team { Key = "vm", Name = "Vulnerability Management", IsRetired = true };
        var actions = new List<(ActionItem Action, DateTimeOffset At)>();
        void Act(string title, DateTimeOffset at, ActionKind kind = ActionKind.Block, DateOnly? raisedOn = null, DateOnly? closed = null,
            ActionStatus? status = null, DateOnly? due = null, Priority priority = Priority.P3, Team? by = null)
        {
            var x = new ActionItem
            {
                Number = actions.Count + 1, Title = title, Kind = kind, Team = by ?? team, RaisedOn = raisedOn, ClosedDate = closed, DueDate = due, Priority = priority,
                Status = status ?? (closed is null ? ActionStatus.Open : ActionStatus.Done), Outcome = closed is null ? ActionOutcome.None : ActionOutcome.NoHits,
            };
            actions.Add((x, at));
            titles[x.Id] = title;
        }
        Act("Raised Mar 20, closed Apr 5", NewYork(2026, 3, 20, 9, 0), closed: D(2026, 4, 5), due: D(2026, 4, 1));
        Act("Raised Mar 1, open, due Mar 15", NewYork(2026, 3, 1, 9, 0), due: D(2026, 3, 15), priority: Priority.P1);
        Act("Raised Feb 1, closed Mar 31", NewYork(2026, 2, 1, 9, 0), closed: D(2026, 3, 31));
        Act("Raised 23:30 Sep 30", NewYork(2026, 9, 30, 23, 30), ActionKind.IocSweep);
        Act("Raised 00:30 Oct 1", NewYork(2026, 10, 1, 0, 30), ActionKind.IocSweep);
        Act("For Mar 31's brief, recorded Apr 2", NewYork(2026, 4, 2, 9, 0), raisedOn: D(2026, 3, 31), due: D(2026, 4, 30));
        Act("For Apr 1's brief, recorded Apr 3", NewYork(2026, 4, 3, 9, 0), ActionKind.PatchRemediate, raisedOn: D(2026, 4, 1), closed: D(2026, 4, 20),
            due: D(2026, 4, 10), priority: Priority.P1);
        Act("Raised Sep 10, closed Oct 2", NewYork(2026, 9, 10, 9, 0), closed: D(2026, 10, 2), due: D(2026, 9, 20), priority: Priority.P2);
        Act("Raised Aug 1, done late Sep 30", NewYork(2026, 8, 1, 9, 0), ActionKind.PatchRemediate, closed: D(2026, 9, 30), due: D(2026, 9, 15), priority: Priority.P1);
        Act("Raised Jan 1, closed Oct 5", NewYork(2026, 1, 1, 9, 0), closed: D(2026, 10, 5), by: other);
        Act("Raised Jun 1, cancelled Jun 30", NewYork(2026, 6, 1, 9, 0), closed: D(2026, 6, 30), status: ActionStatus.NotApplicable);
        Act("Raised Jun 30 2025, closed Jul 1 2025", NewYork(2025, 6, 30, 9, 0), closed: D(2025, 7, 1), priority: Priority.P2);
        var swept = actions[3].Action;
        var hunts = new[]
        {
            new HuntQuery { ActionId = swept.Id, Dialect = "kql", Result = HuntResult.Hits, RunAtUtc = NewYork(2026, 3, 31, 23, 0) },
            new HuntQuery { ActionId = swept.Id, Dialect = "kql", Result = HuntResult.NoHits, RunAtUtc = NewYork(2026, 4, 1, 0, 30) },
            new HuntQuery { ActionId = actions[4].Action.Id, Dialect = "kql", Result = HuntResult.Hits, RunAtUtc = NewYork(2026, 9, 30, 23, 59) },
            new HuntQuery { ActionId = actions[4].Action.Id, Dialect = "kql", Result = HuntResult.Hits, RunAtUtc = NewYork(2026, 10, 1, 0, 1) },
        };
        await Add([cat, ransomware, retired, fraudCat, phishing, .. articles, .. briefs.Values, team, other, .. actions.Select(x => x.Action), .. hunts]);
        foreach (var (id, at) in written) await Written(id, at);
        foreach (var (action, at) in actions) await Raised(action.Id, at);

        var midOctober = new OrgClock(Microsoft.Extensions.Options.Options.Create(new OrgOptions()), new TestClock(NewYork(2026, 10, 15, 11, 0)));
        var trends = new TrendsService(_db, _db.User, midOctober);
        var text = new System.Text.StringBuilder();
        foreach (var (end, rolledUp, span) in new (Period, bool, int?)[]
        {
            (Sep, true, null), (Sep, false, null), (Sep, true, 24), (new Period(PeriodKind.Quarter, 2026, 3), true, null),
            (new Period(PeriodKind.Quarter, 2026, 4), true, null), (Period.Of(PeriodKind.Week, D(2026, 4, 1)), true, null),
        })
        {
            var report = await trends.ReportAsync(end, rolledUp, span);
            text.Append("== ").Append(end).Append(rolledUp ? " rolled up" : "").Append(span is { } n ? $" over {n}" : "").Append('\n');
            foreach (var row in report.Articles.Concat(report.Tags).Concat(report.Attack).Concat(report.TacticTechniques).Concat(report.Actions))
            {
                text.Append(row.Key);
                for (var i = 0; i < report.Periods.Count; i++)
                    if (row.Ids[i].Count > 0) text.Append(" | ").Append(report.Periods[i]).Append(": ").AppendJoin("; ", row.Ids[i].Select(id => titles[id]).Order(StringComparer.Ordinal));
                text.Append('\n');
            }
            foreach (var o in report.Outcomes) text.Append(o.Key).Append(": ").Append(o.Current).Append(" / ").Append(o.Previous).Append('\n');
            text.AppendJoin("; ", report.CountedOn.Select(x => $"{titles[x.Key]} {x.Value:yyyy-MM-dd}").Order(StringComparer.Ordinal)).Append('\n');
        }

        text.ToString().Should().Be(EdgesExpected.ReplaceLineEndings("\n"));
    }

    private const string EdgesExpected = """
== 2026-09 rolled up
articles:all | 2026-04: Briefed Apr 1; Written 00:10 Apr 1 | 2026-06: Written in January, briefed Jun 15 and Jun 1 | 2026-07: Draft briefed Jul 2 | 2026-09: Briefed Sep 30, again Oct 1; Written 23:59 Sep 30; Written after the brief it's on
articles:threat | 2026-04: Briefed Apr 1; Written 00:10 Apr 1 | 2026-06: Written in January, briefed Jun 15 and Jun 1 | 2026-09: Briefed Sep 30, again Oct 1
articles:p1-p2 | 2026-04: Briefed Apr 1; Written 00:10 Apr 1 | 2026-06: Written in January, briefed Jun 15 and Jun 1 | 2026-09: Briefed Sep 30, again Oct 1
articles:informational | 2026-07: Draft briefed Jul 2 | 2026-09: Written 23:59 Sep 30; Written after the brief it's on
category:ransomware | 2026-04: Written 00:10 Apr 1 | 2026-06: Written in January, briefed Jun 15 and Jun 1 | 2026-09: Briefed Sep 30, again Oct 1; Written after the brief it's on
tag:ransomware | 2026-06: Written in January, briefed Jun 15 and Jun 1 | 2026-09: Briefed Sep 30, again Oct 1; Written after the brief it's on
tag:old-lure | 2026-04: Written 00:10 Apr 1
category:fraud | 2026-04: Briefed Apr 1 | 2026-07: Draft briefed Jul 2 | 2026-09: Briefed Sep 30, again Oct 1
tag:phishing | 2026-04: Briefed Apr 1 | 2026-07: Draft briefed Jul 2 | 2026-09: Briefed Sep 30, again Oct 1
tag:none | 2026-09: Written 23:59 Sep 30
tactic:TA0043
tactic:TA0042
tactic:TA0001 | 2026-04: Briefed Apr 1 | 2026-07: Draft briefed Jul 2 | 2026-09: Briefed Sep 30, again Oct 1; Written 23:59 Sep 30
tactic:TA0002 | 2026-09: Briefed Sep 30, again Oct 1; Written after the brief it's on
tactic:TA0003 | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0004 | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0005 | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0112
tactic:TA0006
tactic:TA0007
tactic:TA0008
tactic:TA0009
tactic:TA0011
tactic:TA0010
tactic:TA0040 | 2026-04: Written 00:10 Apr 1
technique:T1059 | 2026-09: Briefed Sep 30, again Oct 1; Written after the brief it's on
technique:T1078 | 2026-09: Briefed Sep 30, again Oct 1
technique:T1190 | 2026-09: Written 23:59 Sep 30
technique:T1566 | 2026-04: Briefed Apr 1 | 2026-07: Draft briefed Jul 2
technique:T1486 | 2026-04: Written 00:10 Apr 1
technique:none | 2026-06: Written in January, briefed Jun 15 and Jun 1
tactic:TA0001:T1078 | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0001:T1190 | 2026-09: Written 23:59 Sep 30
tactic:TA0001:T1566 | 2026-04: Briefed Apr 1 | 2026-07: Draft briefed Jul 2
tactic:TA0002:T1059 | 2026-09: Briefed Sep 30, again Oct 1; Written after the brief it's on
tactic:TA0003:T1078 | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0004:T1078 | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0005:T1078 | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0040:T1486 | 2026-04: Written 00:10 Apr 1
actions:raised | 2026-04: For Apr 1's brief, recorded Apr 3 | 2026-06: Raised Jun 1, cancelled Jun 30 | 2026-08: Raised Aug 1, done late Sep 30 | 2026-09: Raised 23:30 Sep 30; Raised Sep 10, closed Oct 2
actions:raised-kind:IocSweep | 2026-09: Raised 23:30 Sep 30
actions:raised-kind:Block | 2026-06: Raised Jun 1, cancelled Jun 30 | 2026-09: Raised Sep 10, closed Oct 2
actions:raised-kind:PatchRemediate | 2026-04: For Apr 1's brief, recorded Apr 3 | 2026-08: Raised Aug 1, done late Sep 30
actions:raised-team:cyber-defense | 2026-04: For Apr 1's brief, recorded Apr 3 | 2026-06: Raised Jun 1, cancelled Jun 30 | 2026-08: Raised Aug 1, done late Sep 30 | 2026-09: Raised 23:30 Sep 30; Raised Sep 10, closed Oct 2
actions:closed | 2026-04: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-06: Raised Jun 1, cancelled Jun 30 | 2026-09: Raised Aug 1, done late Sep 30
actions:done | 2026-04: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-09: Raised Aug 1, done late Sep 30
actions:on-time
actions:late | 2026-04: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-09: Raised Aug 1, done late Sep 30
actions:no-due
actions:cancelled | 2026-06: Raised Jun 1, cancelled Jun 30
actions:outcome:NoHits | 2026-04: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-06: Raised Jun 1, cancelled Jun 30 | 2026-09: Raised Aug 1, done late Sep 30
actions:hunted | 2026-04: Raised 23:30 Sep 30 | 2026-09: Raised 00:30 Oct 1
actions:hits | 2026-09: Raised 00:30 Oct 1
actions:no-hits | 2026-04: Raised 23:30 Sep 30
actions:open-at-end | 2026-04: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-05: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-06: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-07: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-08: For Mar 31's brief, recorded Apr 2; Raised Aug 1, done late Sep 30; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-09: For Mar 31's brief, recorded Apr 2; Raised 23:30 Sep 30; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15; Raised Sep 10, closed Oct 2
actions:overdue-at-end | 2026-04: Raised Mar 1, open, due Mar 15 | 2026-05: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15 | 2026-06: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15 | 2026-07: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15 | 2026-08: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15 | 2026-09: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15; Raised Sep 10, closed Oct 2
median-days:P1: 60 / 
median-days:P2:  / 
Briefed Apr 1 2026-04-01; Briefed Sep 30, again Oct 1 2026-09-30; Draft briefed Jul 2 2026-07-02; Written 00:10 Apr 1 2026-04-01; Written 23:59 Sep 30 2026-09-30; Written after the brief it's on 2026-09-14; Written in January, briefed Jun 15 and Jun 1 2026-06-01
== 2026-09
articles:all | 2026-04: Briefed Apr 1; Written 00:10 Apr 1 | 2026-06: Written in January, briefed Jun 15 and Jun 1 | 2026-07: Draft briefed Jul 2 | 2026-09: Briefed Sep 30, again Oct 1; Written 23:59 Sep 30; Written after the brief it's on
articles:threat | 2026-04: Briefed Apr 1; Written 00:10 Apr 1 | 2026-06: Written in January, briefed Jun 15 and Jun 1 | 2026-09: Briefed Sep 30, again Oct 1
articles:p1-p2 | 2026-04: Briefed Apr 1; Written 00:10 Apr 1 | 2026-06: Written in January, briefed Jun 15 and Jun 1 | 2026-09: Briefed Sep 30, again Oct 1
articles:informational | 2026-07: Draft briefed Jul 2 | 2026-09: Written 23:59 Sep 30; Written after the brief it's on
category:ransomware | 2026-04: Written 00:10 Apr 1 | 2026-06: Written in January, briefed Jun 15 and Jun 1 | 2026-09: Briefed Sep 30, again Oct 1; Written after the brief it's on
tag:ransomware | 2026-06: Written in January, briefed Jun 15 and Jun 1 | 2026-09: Briefed Sep 30, again Oct 1; Written after the brief it's on
tag:old-lure | 2026-04: Written 00:10 Apr 1
category:fraud | 2026-04: Briefed Apr 1 | 2026-07: Draft briefed Jul 2 | 2026-09: Briefed Sep 30, again Oct 1
tag:phishing | 2026-04: Briefed Apr 1 | 2026-07: Draft briefed Jul 2 | 2026-09: Briefed Sep 30, again Oct 1
tag:none | 2026-09: Written 23:59 Sep 30
tactic:TA0043
tactic:TA0042
tactic:TA0001 | 2026-04: Briefed Apr 1 | 2026-07: Draft briefed Jul 2 | 2026-09: Briefed Sep 30, again Oct 1; Written 23:59 Sep 30
tactic:TA0002 | 2026-09: Briefed Sep 30, again Oct 1; Written after the brief it's on
tactic:TA0003 | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0004 | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0005 | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0112
tactic:TA0006
tactic:TA0007
tactic:TA0008
tactic:TA0009
tactic:TA0011
tactic:TA0010
tactic:TA0040 | 2026-04: Written 00:10 Apr 1
technique:T1059 | 2026-09: Briefed Sep 30, again Oct 1
technique:T1059.001 | 2026-09: Written after the brief it's on
technique:T1078.004 | 2026-09: Briefed Sep 30, again Oct 1
technique:T1190 | 2026-09: Written 23:59 Sep 30
technique:T1486 | 2026-04: Written 00:10 Apr 1
technique:T1566.001 | 2026-07: Draft briefed Jul 2
technique:T1566.002 | 2026-04: Briefed Apr 1
technique:none | 2026-06: Written in January, briefed Jun 15 and Jun 1
tactic:TA0001:T1078.004 | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0001:T1190 | 2026-09: Written 23:59 Sep 30
tactic:TA0001:T1566.001 | 2026-07: Draft briefed Jul 2
tactic:TA0001:T1566.002 | 2026-04: Briefed Apr 1
tactic:TA0002:T1059 | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0002:T1059.001 | 2026-09: Written after the brief it's on
tactic:TA0003:T1078.004 | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0004:T1078.004 | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0005:T1078.004 | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0040:T1486 | 2026-04: Written 00:10 Apr 1
actions:raised | 2026-04: For Apr 1's brief, recorded Apr 3 | 2026-06: Raised Jun 1, cancelled Jun 30 | 2026-08: Raised Aug 1, done late Sep 30 | 2026-09: Raised 23:30 Sep 30; Raised Sep 10, closed Oct 2
actions:raised-kind:IocSweep | 2026-09: Raised 23:30 Sep 30
actions:raised-kind:Block | 2026-06: Raised Jun 1, cancelled Jun 30 | 2026-09: Raised Sep 10, closed Oct 2
actions:raised-kind:PatchRemediate | 2026-04: For Apr 1's brief, recorded Apr 3 | 2026-08: Raised Aug 1, done late Sep 30
actions:raised-team:cyber-defense | 2026-04: For Apr 1's brief, recorded Apr 3 | 2026-06: Raised Jun 1, cancelled Jun 30 | 2026-08: Raised Aug 1, done late Sep 30 | 2026-09: Raised 23:30 Sep 30; Raised Sep 10, closed Oct 2
actions:closed | 2026-04: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-06: Raised Jun 1, cancelled Jun 30 | 2026-09: Raised Aug 1, done late Sep 30
actions:done | 2026-04: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-09: Raised Aug 1, done late Sep 30
actions:on-time
actions:late | 2026-04: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-09: Raised Aug 1, done late Sep 30
actions:no-due
actions:cancelled | 2026-06: Raised Jun 1, cancelled Jun 30
actions:outcome:NoHits | 2026-04: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-06: Raised Jun 1, cancelled Jun 30 | 2026-09: Raised Aug 1, done late Sep 30
actions:hunted | 2026-04: Raised 23:30 Sep 30 | 2026-09: Raised 00:30 Oct 1
actions:hits | 2026-09: Raised 00:30 Oct 1
actions:no-hits | 2026-04: Raised 23:30 Sep 30
actions:open-at-end | 2026-04: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-05: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-06: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-07: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-08: For Mar 31's brief, recorded Apr 2; Raised Aug 1, done late Sep 30; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-09: For Mar 31's brief, recorded Apr 2; Raised 23:30 Sep 30; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15; Raised Sep 10, closed Oct 2
actions:overdue-at-end | 2026-04: Raised Mar 1, open, due Mar 15 | 2026-05: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15 | 2026-06: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15 | 2026-07: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15 | 2026-08: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15 | 2026-09: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15; Raised Sep 10, closed Oct 2
median-days:P1: 60 / 
median-days:P2:  / 
Briefed Apr 1 2026-04-01; Briefed Sep 30, again Oct 1 2026-09-30; Draft briefed Jul 2 2026-07-02; Written 00:10 Apr 1 2026-04-01; Written 23:59 Sep 30 2026-09-30; Written after the brief it's on 2026-09-14; Written in January, briefed Jun 15 and Jun 1 2026-06-01
== 2026-09 rolled up over 24
articles:all | 2025-06: Briefed Jun 30 2025 | 2025-07: Briefed Jul 1 2025 | 2025-08: Briefed a year before | 2026-02: Written in February, never briefed | 2026-03: Briefed Mar 31, again Apr 1; Written 23:30 Mar 31 | 2026-04: Briefed Apr 1; Written 00:10 Apr 1 | 2026-06: Written in January, briefed Jun 15 and Jun 1 | 2026-07: Draft briefed Jul 2 | 2026-09: Briefed Sep 30, again Oct 1; Written 23:59 Sep 30; Written after the brief it's on
articles:threat | 2025-07: Briefed Jul 1 2025 | 2026-04: Briefed Apr 1; Written 00:10 Apr 1 | 2026-06: Written in January, briefed Jun 15 and Jun 1 | 2026-09: Briefed Sep 30, again Oct 1
articles:p1-p2 | 2025-07: Briefed Jul 1 2025 | 2026-04: Briefed Apr 1; Written 00:10 Apr 1 | 2026-06: Written in January, briefed Jun 15 and Jun 1 | 2026-09: Briefed Sep 30, again Oct 1
articles:informational | 2025-06: Briefed Jun 30 2025 | 2025-08: Briefed a year before | 2026-02: Written in February, never briefed | 2026-03: Briefed Mar 31, again Apr 1; Written 23:30 Mar 31 | 2026-07: Draft briefed Jul 2 | 2026-09: Written 23:59 Sep 30; Written after the brief it's on
category:ransomware | 2025-06: Briefed Jun 30 2025 | 2025-07: Briefed Jul 1 2025 | 2026-03: Briefed Mar 31, again Apr 1; Written 23:30 Mar 31 | 2026-04: Written 00:10 Apr 1 | 2026-06: Written in January, briefed Jun 15 and Jun 1 | 2026-09: Briefed Sep 30, again Oct 1; Written after the brief it's on
tag:ransomware | 2025-06: Briefed Jun 30 2025 | 2025-07: Briefed Jul 1 2025 | 2026-03: Briefed Mar 31, again Apr 1; Written 23:30 Mar 31 | 2026-06: Written in January, briefed Jun 15 and Jun 1 | 2026-09: Briefed Sep 30, again Oct 1; Written after the brief it's on
tag:old-lure | 2026-04: Written 00:10 Apr 1
category:fraud | 2025-08: Briefed a year before | 2026-02: Written in February, never briefed | 2026-04: Briefed Apr 1 | 2026-07: Draft briefed Jul 2 | 2026-09: Briefed Sep 30, again Oct 1
tag:phishing | 2025-08: Briefed a year before | 2026-02: Written in February, never briefed | 2026-04: Briefed Apr 1 | 2026-07: Draft briefed Jul 2 | 2026-09: Briefed Sep 30, again Oct 1
tag:none | 2026-09: Written 23:59 Sep 30
tactic:TA0043
tactic:TA0042
tactic:TA0001 | 2025-06: Briefed Jun 30 2025 | 2025-07: Briefed Jul 1 2025 | 2025-08: Briefed a year before | 2026-02: Written in February, never briefed | 2026-03: Briefed Mar 31, again Apr 1 | 2026-04: Briefed Apr 1 | 2026-07: Draft briefed Jul 2 | 2026-09: Briefed Sep 30, again Oct 1; Written 23:59 Sep 30
tactic:TA0002 | 2026-09: Briefed Sep 30, again Oct 1; Written after the brief it's on
tactic:TA0003 | 2026-02: Written in February, never briefed | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0004 | 2026-02: Written in February, never briefed | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0005 | 2026-02: Written in February, never briefed | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0112
tactic:TA0006
tactic:TA0007
tactic:TA0008
tactic:TA0009
tactic:TA0011
tactic:TA0010
tactic:TA0040 | 2026-03: Written 23:30 Mar 31 | 2026-04: Written 00:10 Apr 1
technique:T1059 | 2026-09: Briefed Sep 30, again Oct 1; Written after the brief it's on
technique:T1190 | 2025-06: Briefed Jun 30 2025 | 2025-07: Briefed Jul 1 2025 | 2026-09: Written 23:59 Sep 30
technique:T1078 | 2026-02: Written in February, never briefed | 2026-09: Briefed Sep 30, again Oct 1
technique:T1566 | 2025-08: Briefed a year before | 2026-03: Briefed Mar 31, again Apr 1 | 2026-04: Briefed Apr 1 | 2026-07: Draft briefed Jul 2
technique:T1486 | 2026-03: Written 23:30 Mar 31 | 2026-04: Written 00:10 Apr 1
technique:none | 2026-06: Written in January, briefed Jun 15 and Jun 1
tactic:TA0001:T1190 | 2025-06: Briefed Jun 30 2025 | 2025-07: Briefed Jul 1 2025 | 2026-09: Written 23:59 Sep 30
tactic:TA0001:T1078 | 2026-02: Written in February, never briefed | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0001:T1566 | 2025-08: Briefed a year before | 2026-03: Briefed Mar 31, again Apr 1 | 2026-04: Briefed Apr 1 | 2026-07: Draft briefed Jul 2
tactic:TA0002:T1059 | 2026-09: Briefed Sep 30, again Oct 1; Written after the brief it's on
tactic:TA0003:T1078 | 2026-02: Written in February, never briefed | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0004:T1078 | 2026-02: Written in February, never briefed | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0005:T1078 | 2026-02: Written in February, never briefed | 2026-09: Briefed Sep 30, again Oct 1
tactic:TA0040:T1486 | 2026-03: Written 23:30 Mar 31 | 2026-04: Written 00:10 Apr 1
actions:raised | 2025-06: Raised Jun 30 2025, closed Jul 1 2025 | 2026-01: Raised Jan 1, closed Oct 5 | 2026-02: Raised Feb 1, closed Mar 31 | 2026-03: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15; Raised Mar 20, closed Apr 5 | 2026-04: For Apr 1's brief, recorded Apr 3 | 2026-06: Raised Jun 1, cancelled Jun 30 | 2026-08: Raised Aug 1, done late Sep 30 | 2026-09: Raised 23:30 Sep 30; Raised Sep 10, closed Oct 2
actions:raised-kind:IocSweep | 2026-09: Raised 23:30 Sep 30
actions:raised-kind:Block | 2025-06: Raised Jun 30 2025, closed Jul 1 2025 | 2026-01: Raised Jan 1, closed Oct 5 | 2026-02: Raised Feb 1, closed Mar 31 | 2026-03: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15; Raised Mar 20, closed Apr 5 | 2026-06: Raised Jun 1, cancelled Jun 30 | 2026-09: Raised Sep 10, closed Oct 2
actions:raised-kind:PatchRemediate | 2026-04: For Apr 1's brief, recorded Apr 3 | 2026-08: Raised Aug 1, done late Sep 30
actions:raised-team:cyber-defense | 2025-06: Raised Jun 30 2025, closed Jul 1 2025 | 2026-02: Raised Feb 1, closed Mar 31 | 2026-03: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15; Raised Mar 20, closed Apr 5 | 2026-04: For Apr 1's brief, recorded Apr 3 | 2026-06: Raised Jun 1, cancelled Jun 30 | 2026-08: Raised Aug 1, done late Sep 30 | 2026-09: Raised 23:30 Sep 30; Raised Sep 10, closed Oct 2
actions:raised-team:vm | 2026-01: Raised Jan 1, closed Oct 5
actions:closed | 2025-07: Raised Jun 30 2025, closed Jul 1 2025 | 2026-03: Raised Feb 1, closed Mar 31 | 2026-04: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-06: Raised Jun 1, cancelled Jun 30 | 2026-09: Raised Aug 1, done late Sep 30
actions:done | 2025-07: Raised Jun 30 2025, closed Jul 1 2025 | 2026-03: Raised Feb 1, closed Mar 31 | 2026-04: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-09: Raised Aug 1, done late Sep 30
actions:on-time
actions:late | 2026-04: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-09: Raised Aug 1, done late Sep 30
actions:no-due | 2025-07: Raised Jun 30 2025, closed Jul 1 2025 | 2026-03: Raised Feb 1, closed Mar 31
actions:cancelled | 2026-06: Raised Jun 1, cancelled Jun 30
actions:outcome:NoHits | 2025-07: Raised Jun 30 2025, closed Jul 1 2025 | 2026-03: Raised Feb 1, closed Mar 31 | 2026-04: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-06: Raised Jun 1, cancelled Jun 30 | 2026-09: Raised Aug 1, done late Sep 30
actions:hunted | 2026-03: Raised 23:30 Sep 30 | 2026-04: Raised 23:30 Sep 30 | 2026-09: Raised 00:30 Oct 1
actions:hits | 2026-03: Raised 23:30 Sep 30 | 2026-09: Raised 00:30 Oct 1
actions:no-hits | 2026-04: Raised 23:30 Sep 30
actions:open-at-end | 2025-06: Raised Jun 30 2025, closed Jul 1 2025 | 2026-01: Raised Jan 1, closed Oct 5 | 2026-02: Raised Feb 1, closed Mar 31; Raised Jan 1, closed Oct 5 | 2026-03: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15; Raised Mar 20, closed Apr 5 | 2026-04: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-05: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-06: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-07: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-08: For Mar 31's brief, recorded Apr 2; Raised Aug 1, done late Sep 30; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-09: For Mar 31's brief, recorded Apr 2; Raised 23:30 Sep 30; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15; Raised Sep 10, closed Oct 2
actions:overdue-at-end | 2026-03: Raised Mar 1, open, due Mar 15 | 2026-04: Raised Mar 1, open, due Mar 15 | 2026-05: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15 | 2026-06: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15 | 2026-07: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15 | 2026-08: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15 | 2026-09: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15; Raised Sep 10, closed Oct 2
median-days:P1: 60 / 
median-days:P2:  / 
Briefed Apr 1 2026-04-01; Briefed Jul 1 2025 2025-07-01; Briefed Jun 30 2025 2025-06-30; Briefed Mar 31, again Apr 1 2026-03-31; Briefed Sep 30, again Oct 1 2026-09-30; Briefed a year before 2025-08-05; Draft briefed Jul 2 2026-07-02; Written 00:10 Apr 1 2026-04-01; Written 23:30 Mar 31 2026-03-31; Written 23:59 Sep 30 2026-09-30; Written after the brief it's on 2026-09-14; Written in February, never briefed 2026-02-10; Written in January, briefed Jun 15 and Jun 1 2026-06-01
== 2026-Q3 rolled up
articles:all | 2025-Q3: Briefed Jul 1 2025; Briefed a year before | 2026-Q1: Briefed Mar 31, again Apr 1; Written 23:30 Mar 31; Written in February, never briefed | 2026-Q2: Briefed Apr 1; Written 00:10 Apr 1; Written in January, briefed Jun 15 and Jun 1 | 2026-Q3: Briefed Sep 30, again Oct 1; Draft briefed Jul 2; Written 23:59 Sep 30; Written after the brief it's on
articles:threat | 2025-Q3: Briefed Jul 1 2025 | 2026-Q2: Briefed Apr 1; Written 00:10 Apr 1; Written in January, briefed Jun 15 and Jun 1 | 2026-Q3: Briefed Sep 30, again Oct 1
articles:p1-p2 | 2025-Q3: Briefed Jul 1 2025 | 2026-Q2: Briefed Apr 1; Written 00:10 Apr 1; Written in January, briefed Jun 15 and Jun 1 | 2026-Q3: Briefed Sep 30, again Oct 1
articles:informational | 2025-Q3: Briefed a year before | 2026-Q1: Briefed Mar 31, again Apr 1; Written 23:30 Mar 31; Written in February, never briefed | 2026-Q3: Draft briefed Jul 2; Written 23:59 Sep 30; Written after the brief it's on
category:ransomware | 2025-Q3: Briefed Jul 1 2025 | 2026-Q1: Briefed Mar 31, again Apr 1; Written 23:30 Mar 31 | 2026-Q2: Written 00:10 Apr 1; Written in January, briefed Jun 15 and Jun 1 | 2026-Q3: Briefed Sep 30, again Oct 1; Written after the brief it's on
tag:ransomware | 2025-Q3: Briefed Jul 1 2025 | 2026-Q1: Briefed Mar 31, again Apr 1; Written 23:30 Mar 31 | 2026-Q2: Written in January, briefed Jun 15 and Jun 1 | 2026-Q3: Briefed Sep 30, again Oct 1; Written after the brief it's on
tag:old-lure | 2026-Q2: Written 00:10 Apr 1
category:fraud | 2025-Q3: Briefed a year before | 2026-Q1: Written in February, never briefed | 2026-Q2: Briefed Apr 1 | 2026-Q3: Briefed Sep 30, again Oct 1; Draft briefed Jul 2
tag:phishing | 2025-Q3: Briefed a year before | 2026-Q1: Written in February, never briefed | 2026-Q2: Briefed Apr 1 | 2026-Q3: Briefed Sep 30, again Oct 1; Draft briefed Jul 2
tag:none | 2026-Q3: Written 23:59 Sep 30
tactic:TA0043
tactic:TA0042
tactic:TA0001 | 2025-Q3: Briefed Jul 1 2025; Briefed a year before | 2026-Q1: Briefed Mar 31, again Apr 1; Written in February, never briefed | 2026-Q2: Briefed Apr 1 | 2026-Q3: Briefed Sep 30, again Oct 1; Draft briefed Jul 2; Written 23:59 Sep 30
tactic:TA0002 | 2026-Q3: Briefed Sep 30, again Oct 1; Written after the brief it's on
tactic:TA0003 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
tactic:TA0004 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
tactic:TA0005 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
tactic:TA0112
tactic:TA0006
tactic:TA0007
tactic:TA0008
tactic:TA0009
tactic:TA0011
tactic:TA0010
tactic:TA0040 | 2026-Q1: Written 23:30 Mar 31 | 2026-Q2: Written 00:10 Apr 1
technique:T1059 | 2026-Q3: Briefed Sep 30, again Oct 1; Written after the brief it's on
technique:T1566 | 2025-Q3: Briefed a year before | 2026-Q1: Briefed Mar 31, again Apr 1 | 2026-Q2: Briefed Apr 1 | 2026-Q3: Draft briefed Jul 2
technique:T1078 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
technique:T1190 | 2025-Q3: Briefed Jul 1 2025 | 2026-Q3: Written 23:59 Sep 30
technique:T1486 | 2026-Q1: Written 23:30 Mar 31 | 2026-Q2: Written 00:10 Apr 1
technique:none | 2026-Q2: Written in January, briefed Jun 15 and Jun 1
tactic:TA0001:T1566 | 2025-Q3: Briefed a year before | 2026-Q1: Briefed Mar 31, again Apr 1 | 2026-Q2: Briefed Apr 1 | 2026-Q3: Draft briefed Jul 2
tactic:TA0001:T1078 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
tactic:TA0001:T1190 | 2025-Q3: Briefed Jul 1 2025 | 2026-Q3: Written 23:59 Sep 30
tactic:TA0002:T1059 | 2026-Q3: Briefed Sep 30, again Oct 1; Written after the brief it's on
tactic:TA0003:T1078 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
tactic:TA0004:T1078 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
tactic:TA0005:T1078 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
tactic:TA0040:T1486 | 2026-Q1: Written 23:30 Mar 31 | 2026-Q2: Written 00:10 Apr 1
actions:raised | 2026-Q1: For Mar 31's brief, recorded Apr 2; Raised Feb 1, closed Mar 31; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15; Raised Mar 20, closed Apr 5 | 2026-Q2: For Apr 1's brief, recorded Apr 3; Raised Jun 1, cancelled Jun 30 | 2026-Q3: Raised 23:30 Sep 30; Raised Aug 1, done late Sep 30; Raised Sep 10, closed Oct 2
actions:raised-kind:IocSweep | 2026-Q3: Raised 23:30 Sep 30
actions:raised-kind:Block | 2026-Q1: For Mar 31's brief, recorded Apr 2; Raised Feb 1, closed Mar 31; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15; Raised Mar 20, closed Apr 5 | 2026-Q2: Raised Jun 1, cancelled Jun 30 | 2026-Q3: Raised Sep 10, closed Oct 2
actions:raised-kind:PatchRemediate | 2026-Q2: For Apr 1's brief, recorded Apr 3 | 2026-Q3: Raised Aug 1, done late Sep 30
actions:raised-team:cyber-defense | 2026-Q1: For Mar 31's brief, recorded Apr 2; Raised Feb 1, closed Mar 31; Raised Mar 1, open, due Mar 15; Raised Mar 20, closed Apr 5 | 2026-Q2: For Apr 1's brief, recorded Apr 3; Raised Jun 1, cancelled Jun 30 | 2026-Q3: Raised 23:30 Sep 30; Raised Aug 1, done late Sep 30; Raised Sep 10, closed Oct 2
actions:raised-team:vm | 2026-Q1: Raised Jan 1, closed Oct 5
actions:closed | 2025-Q3: Raised Jun 30 2025, closed Jul 1 2025 | 2026-Q1: Raised Feb 1, closed Mar 31 | 2026-Q2: For Apr 1's brief, recorded Apr 3; Raised Jun 1, cancelled Jun 30; Raised Mar 20, closed Apr 5 | 2026-Q3: Raised Aug 1, done late Sep 30
actions:done | 2025-Q3: Raised Jun 30 2025, closed Jul 1 2025 | 2026-Q1: Raised Feb 1, closed Mar 31 | 2026-Q2: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-Q3: Raised Aug 1, done late Sep 30
actions:on-time
actions:late | 2026-Q2: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-Q3: Raised Aug 1, done late Sep 30
actions:no-due | 2025-Q3: Raised Jun 30 2025, closed Jul 1 2025 | 2026-Q1: Raised Feb 1, closed Mar 31
actions:cancelled | 2026-Q2: Raised Jun 1, cancelled Jun 30
actions:outcome:NoHits | 2025-Q3: Raised Jun 30 2025, closed Jul 1 2025 | 2026-Q1: Raised Feb 1, closed Mar 31 | 2026-Q2: For Apr 1's brief, recorded Apr 3; Raised Jun 1, cancelled Jun 30; Raised Mar 20, closed Apr 5 | 2026-Q3: Raised Aug 1, done late Sep 30
actions:hunted | 2026-Q1: Raised 23:30 Sep 30 | 2026-Q2: Raised 23:30 Sep 30 | 2026-Q3: Raised 00:30 Oct 1
actions:hits | 2026-Q1: Raised 23:30 Sep 30 | 2026-Q3: Raised 00:30 Oct 1
actions:no-hits | 2026-Q2: Raised 23:30 Sep 30
actions:open-at-end | 2026-Q1: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15; Raised Mar 20, closed Apr 5 | 2026-Q2: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-Q3: For Mar 31's brief, recorded Apr 2; Raised 23:30 Sep 30; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15; Raised Sep 10, closed Oct 2
actions:overdue-at-end | 2026-Q1: Raised Mar 1, open, due Mar 15 | 2026-Q2: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15 | 2026-Q3: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15; Raised Sep 10, closed Oct 2
median-days:P1: 60 / 19
median-days:P2:  / 
Briefed Apr 1 2026-04-01; Briefed Jul 1 2025 2025-07-01; Briefed Mar 31, again Apr 1 2026-03-31; Briefed Sep 30, again Oct 1 2026-09-30; Briefed a year before 2025-08-05; Draft briefed Jul 2 2026-07-02; Written 00:10 Apr 1 2026-04-01; Written 23:30 Mar 31 2026-03-31; Written 23:59 Sep 30 2026-09-30; Written after the brief it's on 2026-09-14; Written in February, never briefed 2026-02-10; Written in January, briefed Jun 15 and Jun 1 2026-06-01
== 2026-Q4 rolled up
articles:all | 2026-Q1: Briefed Mar 31, again Apr 1; Written 23:30 Mar 31; Written in February, never briefed | 2026-Q2: Briefed Apr 1; Written 00:10 Apr 1; Written in January, briefed Jun 15 and Jun 1 | 2026-Q3: Briefed Sep 30, again Oct 1; Draft briefed Jul 2; Written 23:59 Sep 30; Written after the brief it's on | 2026-Q4: First briefed Oct 1; Written 00:01 Oct 1
articles:threat | 2026-Q2: Briefed Apr 1; Written 00:10 Apr 1; Written in January, briefed Jun 15 and Jun 1 | 2026-Q3: Briefed Sep 30, again Oct 1
articles:p1-p2 | 2026-Q2: Briefed Apr 1; Written 00:10 Apr 1; Written in January, briefed Jun 15 and Jun 1 | 2026-Q3: Briefed Sep 30, again Oct 1
articles:informational | 2026-Q1: Briefed Mar 31, again Apr 1; Written 23:30 Mar 31; Written in February, never briefed | 2026-Q3: Draft briefed Jul 2; Written 23:59 Sep 30; Written after the brief it's on | 2026-Q4: First briefed Oct 1; Written 00:01 Oct 1
category:ransomware | 2026-Q1: Briefed Mar 31, again Apr 1; Written 23:30 Mar 31 | 2026-Q2: Written 00:10 Apr 1; Written in January, briefed Jun 15 and Jun 1 | 2026-Q3: Briefed Sep 30, again Oct 1; Written after the brief it's on
tag:ransomware | 2026-Q1: Briefed Mar 31, again Apr 1; Written 23:30 Mar 31 | 2026-Q2: Written in January, briefed Jun 15 and Jun 1 | 2026-Q3: Briefed Sep 30, again Oct 1; Written after the brief it's on
tag:old-lure | 2026-Q2: Written 00:10 Apr 1
category:fraud | 2026-Q1: Written in February, never briefed | 2026-Q2: Briefed Apr 1 | 2026-Q3: Briefed Sep 30, again Oct 1; Draft briefed Jul 2 | 2026-Q4: First briefed Oct 1; Written 00:01 Oct 1
tag:phishing | 2026-Q1: Written in February, never briefed | 2026-Q2: Briefed Apr 1 | 2026-Q3: Briefed Sep 30, again Oct 1; Draft briefed Jul 2 | 2026-Q4: First briefed Oct 1; Written 00:01 Oct 1
tag:none | 2026-Q3: Written 23:59 Sep 30
tactic:TA0043
tactic:TA0042
tactic:TA0001 | 2026-Q1: Briefed Mar 31, again Apr 1; Written in February, never briefed | 2026-Q2: Briefed Apr 1 | 2026-Q3: Briefed Sep 30, again Oct 1; Draft briefed Jul 2; Written 23:59 Sep 30 | 2026-Q4: First briefed Oct 1; Written 00:01 Oct 1
tactic:TA0002 | 2026-Q3: Briefed Sep 30, again Oct 1; Written after the brief it's on
tactic:TA0003 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
tactic:TA0004 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
tactic:TA0005 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
tactic:TA0112
tactic:TA0006
tactic:TA0007
tactic:TA0008
tactic:TA0009
tactic:TA0011
tactic:TA0010
tactic:TA0040 | 2026-Q1: Written 23:30 Mar 31 | 2026-Q2: Written 00:10 Apr 1
technique:T1566 | 2026-Q1: Briefed Mar 31, again Apr 1 | 2026-Q2: Briefed Apr 1 | 2026-Q3: Draft briefed Jul 2 | 2026-Q4: First briefed Oct 1
technique:T1190 | 2026-Q3: Written 23:59 Sep 30 | 2026-Q4: Written 00:01 Oct 1
technique:T1059 | 2026-Q3: Briefed Sep 30, again Oct 1; Written after the brief it's on
technique:T1078 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
technique:T1486 | 2026-Q1: Written 23:30 Mar 31 | 2026-Q2: Written 00:10 Apr 1
technique:none | 2026-Q2: Written in January, briefed Jun 15 and Jun 1
tactic:TA0001:T1566 | 2026-Q1: Briefed Mar 31, again Apr 1 | 2026-Q2: Briefed Apr 1 | 2026-Q3: Draft briefed Jul 2 | 2026-Q4: First briefed Oct 1
tactic:TA0001:T1190 | 2026-Q3: Written 23:59 Sep 30 | 2026-Q4: Written 00:01 Oct 1
tactic:TA0001:T1078 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
tactic:TA0002:T1059 | 2026-Q3: Briefed Sep 30, again Oct 1; Written after the brief it's on
tactic:TA0003:T1078 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
tactic:TA0004:T1078 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
tactic:TA0005:T1078 | 2026-Q1: Written in February, never briefed | 2026-Q3: Briefed Sep 30, again Oct 1
tactic:TA0040:T1486 | 2026-Q1: Written 23:30 Mar 31 | 2026-Q2: Written 00:10 Apr 1
actions:raised | 2026-Q1: For Mar 31's brief, recorded Apr 2; Raised Feb 1, closed Mar 31; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15; Raised Mar 20, closed Apr 5 | 2026-Q2: For Apr 1's brief, recorded Apr 3; Raised Jun 1, cancelled Jun 30 | 2026-Q3: Raised 23:30 Sep 30; Raised Aug 1, done late Sep 30; Raised Sep 10, closed Oct 2 | 2026-Q4: Raised 00:30 Oct 1
actions:raised-kind:IocSweep | 2026-Q3: Raised 23:30 Sep 30 | 2026-Q4: Raised 00:30 Oct 1
actions:raised-kind:Block | 2026-Q1: For Mar 31's brief, recorded Apr 2; Raised Feb 1, closed Mar 31; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15; Raised Mar 20, closed Apr 5 | 2026-Q2: Raised Jun 1, cancelled Jun 30 | 2026-Q3: Raised Sep 10, closed Oct 2
actions:raised-kind:PatchRemediate | 2026-Q2: For Apr 1's brief, recorded Apr 3 | 2026-Q3: Raised Aug 1, done late Sep 30
actions:raised-team:cyber-defense | 2026-Q1: For Mar 31's brief, recorded Apr 2; Raised Feb 1, closed Mar 31; Raised Mar 1, open, due Mar 15; Raised Mar 20, closed Apr 5 | 2026-Q2: For Apr 1's brief, recorded Apr 3; Raised Jun 1, cancelled Jun 30 | 2026-Q3: Raised 23:30 Sep 30; Raised Aug 1, done late Sep 30; Raised Sep 10, closed Oct 2 | 2026-Q4: Raised 00:30 Oct 1
actions:raised-team:vm | 2026-Q1: Raised Jan 1, closed Oct 5
actions:closed | 2026-Q1: Raised Feb 1, closed Mar 31 | 2026-Q2: For Apr 1's brief, recorded Apr 3; Raised Jun 1, cancelled Jun 30; Raised Mar 20, closed Apr 5 | 2026-Q3: Raised Aug 1, done late Sep 30 | 2026-Q4: Raised Jan 1, closed Oct 5; Raised Sep 10, closed Oct 2
actions:done | 2026-Q1: Raised Feb 1, closed Mar 31 | 2026-Q2: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-Q3: Raised Aug 1, done late Sep 30 | 2026-Q4: Raised Jan 1, closed Oct 5; Raised Sep 10, closed Oct 2
actions:on-time
actions:late | 2026-Q2: For Apr 1's brief, recorded Apr 3; Raised Mar 20, closed Apr 5 | 2026-Q3: Raised Aug 1, done late Sep 30 | 2026-Q4: Raised Sep 10, closed Oct 2
actions:no-due | 2026-Q1: Raised Feb 1, closed Mar 31 | 2026-Q4: Raised Jan 1, closed Oct 5
actions:cancelled | 2026-Q2: Raised Jun 1, cancelled Jun 30
actions:outcome:NoHits | 2026-Q1: Raised Feb 1, closed Mar 31 | 2026-Q2: For Apr 1's brief, recorded Apr 3; Raised Jun 1, cancelled Jun 30; Raised Mar 20, closed Apr 5 | 2026-Q3: Raised Aug 1, done late Sep 30 | 2026-Q4: Raised Jan 1, closed Oct 5; Raised Sep 10, closed Oct 2
actions:hunted | 2026-Q1: Raised 23:30 Sep 30 | 2026-Q2: Raised 23:30 Sep 30 | 2026-Q3: Raised 00:30 Oct 1 | 2026-Q4: Raised 00:30 Oct 1
actions:hits | 2026-Q1: Raised 23:30 Sep 30 | 2026-Q3: Raised 00:30 Oct 1 | 2026-Q4: Raised 00:30 Oct 1
actions:no-hits | 2026-Q2: Raised 23:30 Sep 30
actions:open-at-end | 2026-Q1: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15; Raised Mar 20, closed Apr 5 | 2026-Q2: For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-Q3: For Mar 31's brief, recorded Apr 2; Raised 23:30 Sep 30; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15; Raised Sep 10, closed Oct 2 | 2026-Q4: For Mar 31's brief, recorded Apr 2; Raised 00:30 Oct 1; Raised 23:30 Sep 30; Raised Mar 1, open, due Mar 15
actions:overdue-at-end | 2026-Q1: Raised Mar 1, open, due Mar 15 | 2026-Q2: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15 | 2026-Q3: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15; Raised Sep 10, closed Oct 2 | 2026-Q4: For Mar 31's brief, recorded Apr 2; Raised Mar 1, open, due Mar 15
median-days:P1:  / 60
median-days:P2: 22 / 
Briefed Apr 1 2026-04-01; Briefed Mar 31, again Apr 1 2026-03-31; Briefed Sep 30, again Oct 1 2026-09-30; Draft briefed Jul 2 2026-07-02; First briefed Oct 1 2026-10-01; Written 00:01 Oct 1 2026-10-01; Written 00:10 Apr 1 2026-04-01; Written 23:30 Mar 31 2026-03-31; Written 23:59 Sep 30 2026-09-30; Written after the brief it's on 2026-09-14; Written in February, never briefed 2026-02-10; Written in January, briefed Jun 15 and Jun 1 2026-06-01
== 2026-W14 rolled up
articles:all | 2026-W07: Written in February, never briefed | 2026-W14: Briefed Apr 1; Briefed Mar 31, again Apr 1; Written 00:10 Apr 1; Written 23:30 Mar 31
articles:threat | 2026-W14: Briefed Apr 1; Written 00:10 Apr 1
articles:p1-p2 | 2026-W14: Briefed Apr 1; Written 00:10 Apr 1
articles:informational | 2026-W07: Written in February, never briefed | 2026-W14: Briefed Mar 31, again Apr 1; Written 23:30 Mar 31
category:ransomware | 2026-W14: Briefed Mar 31, again Apr 1; Written 00:10 Apr 1; Written 23:30 Mar 31
tag:ransomware | 2026-W14: Briefed Mar 31, again Apr 1; Written 23:30 Mar 31
tag:old-lure | 2026-W14: Written 00:10 Apr 1
category:fraud | 2026-W07: Written in February, never briefed | 2026-W14: Briefed Apr 1
tag:phishing | 2026-W07: Written in February, never briefed | 2026-W14: Briefed Apr 1
tag:none
tactic:TA0043
tactic:TA0042
tactic:TA0001 | 2026-W07: Written in February, never briefed | 2026-W14: Briefed Apr 1; Briefed Mar 31, again Apr 1
tactic:TA0002
tactic:TA0003 | 2026-W07: Written in February, never briefed
tactic:TA0004 | 2026-W07: Written in February, never briefed
tactic:TA0005 | 2026-W07: Written in February, never briefed
tactic:TA0112
tactic:TA0006
tactic:TA0007
tactic:TA0008
tactic:TA0009
tactic:TA0011
tactic:TA0010
tactic:TA0040 | 2026-W14: Written 00:10 Apr 1; Written 23:30 Mar 31
technique:T1486 | 2026-W14: Written 00:10 Apr 1; Written 23:30 Mar 31
technique:T1566 | 2026-W14: Briefed Apr 1; Briefed Mar 31, again Apr 1
technique:T1078 | 2026-W07: Written in February, never briefed
technique:none
tactic:TA0001:T1566 | 2026-W14: Briefed Apr 1; Briefed Mar 31, again Apr 1
tactic:TA0001:T1078 | 2026-W07: Written in February, never briefed
tactic:TA0003:T1078 | 2026-W07: Written in February, never briefed
tactic:TA0004:T1078 | 2026-W07: Written in February, never briefed
tactic:TA0005:T1078 | 2026-W07: Written in February, never briefed
tactic:TA0040:T1486 | 2026-W14: Written 00:10 Apr 1; Written 23:30 Mar 31
actions:raised | 2026-W09: Raised Mar 1, open, due Mar 15 | 2026-W12: Raised Mar 20, closed Apr 5 | 2026-W14: For Apr 1's brief, recorded Apr 3; For Mar 31's brief, recorded Apr 2
actions:raised-kind:Block | 2026-W09: Raised Mar 1, open, due Mar 15 | 2026-W12: Raised Mar 20, closed Apr 5 | 2026-W14: For Mar 31's brief, recorded Apr 2
actions:raised-kind:PatchRemediate | 2026-W14: For Apr 1's brief, recorded Apr 3
actions:raised-team:cyber-defense | 2026-W09: Raised Mar 1, open, due Mar 15 | 2026-W12: Raised Mar 20, closed Apr 5 | 2026-W14: For Apr 1's brief, recorded Apr 3; For Mar 31's brief, recorded Apr 2
actions:closed | 2026-W14: Raised Feb 1, closed Mar 31; Raised Mar 20, closed Apr 5
actions:done | 2026-W14: Raised Feb 1, closed Mar 31; Raised Mar 20, closed Apr 5
actions:on-time
actions:late | 2026-W14: Raised Mar 20, closed Apr 5
actions:no-due | 2026-W14: Raised Feb 1, closed Mar 31
actions:cancelled
actions:outcome:NoHits | 2026-W14: Raised Feb 1, closed Mar 31; Raised Mar 20, closed Apr 5
actions:hunted | 2026-W14: Raised 23:30 Sep 30
actions:hits | 2026-W14: Raised 23:30 Sep 30
actions:no-hits | 2026-W14: Raised 23:30 Sep 30
actions:open-at-end | 2026-W07: Raised Feb 1, closed Mar 31; Raised Jan 1, closed Oct 5 | 2026-W08: Raised Feb 1, closed Mar 31; Raised Jan 1, closed Oct 5 | 2026-W09: Raised Feb 1, closed Mar 31; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-W10: Raised Feb 1, closed Mar 31; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-W11: Raised Feb 1, closed Mar 31; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15 | 2026-W12: Raised Feb 1, closed Mar 31; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15; Raised Mar 20, closed Apr 5 | 2026-W13: Raised Feb 1, closed Mar 31; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15; Raised Mar 20, closed Apr 5 | 2026-W14: For Apr 1's brief, recorded Apr 3; For Mar 31's brief, recorded Apr 2; Raised Jan 1, closed Oct 5; Raised Mar 1, open, due Mar 15
actions:overdue-at-end | 2026-W12: Raised Mar 1, open, due Mar 15 | 2026-W13: Raised Mar 1, open, due Mar 15 | 2026-W14: Raised Mar 1, open, due Mar 15
median-days:P1:  / 
median-days:P2:  / 
Briefed Apr 1 2026-04-01; Briefed Mar 31, again Apr 1 2026-03-31; Written 00:10 Apr 1 2026-04-01; Written 23:30 Mar 31 2026-03-31; Written in February, never briefed 2026-02-10

""";

    [Fact]
    public async Task Reader_CantSeeTrends()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var act = () => Trends().ReportAsync(Sep);
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task OnTime_LeavesOutActionsWithNoDueDate_AndOutcomesGiveTheMedianDaysToClose()
    {
        var team = new Team { Key = "vm", Name = "Vulnerability Management" };
        ActionItem Done(int n, Priority p, DateOnly? due, DateOnly closed) => new()
        {
            Number = n, Title = $"A{n}", Kind = ActionKind.PatchRemediate, Team = team, Priority = p, DueDate = due,
            Status = ActionStatus.Done, Outcome = ActionOutcome.Remediated, ClosedDate = closed,
        };
        var a = Done(1, Priority.P1, new(2026, 9, 5), new(2026, 9, 4));    // raised Sep 1, 3 days, on time
        var b = Done(2, Priority.P1, new(2026, 9, 5), new(2026, 9, 11));   // raised Sep 1, 10 days, late
        var c = Done(3, Priority.P1, null, new(2026, 9, 6));               // raised Sep 1, 5 days, no due date
        var d = Done(4, Priority.P2, new(2026, 8, 30), new(2026, 8, 28));  // August
        await Add(team, a, b, c, d);
        foreach (var x in new[] { a, b, c }) await Raised(x.Id, Noon(9, 1));
        await Raised(d.Id, Noon(8, 20));

        var report = await Trends().ReportAsync(Sep);

        report.Find("actions:done")!.Current.Should().Be(3);
        report.Find("actions:no-due")!.Ids[^1].Should().Equal(c.Id);
        report.OnTimeRatio.Should().Be(0.5, "one on time of the two with a due date; the one with none is neither");
        report.PreviousOnTimeRatio.Should().Be(1.0);
        var p1 = report.Outcomes.Single(o => o.Key == "median-days:P1");
        p1.Current.Should().Be(5, "3, 5 and 10 days");
        p1.Previous.Should().BeNull("no P1 was done in August");
        report.Outcomes.Single(o => o.Key == "median-days:P2").Previous.Should().Be(8);
    }

    [Fact]
    public async Task AWeek_ReportsEightWeeks_ByDefault()
    {
        var week = Period.Of(PeriodKind.Week, new DateOnly(2026, 9, 16));
        var report = await Trends().ReportAsync(week);
        report.Periods.Should().HaveCount(TrendsService.Weeks);
        report.Periods[^1].Should().Be(week);
        report.Periods[0].Should().Be(week.Shift(-7));
        TrendsService.Spans(PeriodKind.Week).Should().Equal(8, 13, 26);
    }

    [Fact]
    public async Task TheReport_GathersHeadlinesOutcomesMetricsAndTops_FromTheSameCounts_AndExportsThemAsCsv()
    {
        var threat = Article("Edge VPN zero-day", classification: Classification.Threat);
        await Add(threat, Briefed(new DateOnly(2026, 9, 8), threat));
        var group = new MetricGroup { Name = "Alert queue" };
        var metric = new MetricDefinition { Group = group, Key = "older-24h", Name = "Older than 24 hours", Direction = MetricDirection.LowerIsBetter, WarnThreshold = 10 };
        await Add(group, metric,
            new MetricValue { DefinitionId = metric.Id, BriefDate = new(2026, 9, 1), Value = 4, EnteredById = "x", EnteredByName = "X" },
            new MetricValue { DefinitionId = metric.Id, BriefDate = new(2026, 9, 2), Value = 12, EnteredById = "x", EnteredByName = "X" });

        var reports = new ReportService(Trends(), _db.Metrics(), _db.Clock);
        var r = await reports.BuildAsync(Sep);

        r.Headlines.Single(h => h.Name == "Threats").Current.Should().Be(1);
        r.Headlines.Single(h => h.Name == "Articles briefed").Line.Should().Be("articles:all");
        r.Outcomes.Select(o => o.Name).Should().Contain(["Overdue at period end", "Median days to close, P1", "Hunts with hits"]);
        var m = r.Metrics.Single();
        (m.Latest, m.Average, m.Lowest, m.Highest, m.Breaches).Should().Be((12m, 8m, 4m, 12m, 1));
        m.Missing.Should().Be(20, "September 2026 has 22 business days and two have a value");

        var csv = ReportService.ToCsv(r);
        csv.Should().StartWith("section,name,current,previous").And.Contain("Headlines,Threats,1,0").And.Contain("Opening metrics,");
    }

    [Fact]
    public void TheReportCsv_NeutralisesNamesAFormulaCouldStartWith_ButNotNumbers()
    {
        var topics = new[] { new ReportTop("\t=1+1", 2, 1, "t:a"), new ReportTop("=cmd", 3, -1, "t:b") };
        var report = new PeriodReport(Sep, false, [], [], [], topics, [], []);

        var csv = ReportService.ToCsv(report);

        csv.Should().Contain("Topics,'\t=1+1,2,1\n").And.Contain("Topics,'=cmd,3,4\n");
    }

    [Fact]
    public async Task TheReport_OfAPeriodNotStartedYet_HasNoMetricValues_NotTodays()
    {
        // A week that starts after today: its metrics were read from today up to its start, so it showed today's numbers.
        var group = new MetricGroup { Name = "Alert queue" };
        var metric = new MetricDefinition { Group = group, Key = "older-24h", Name = "Older than 24 hours", Direction = MetricDirection.LowerIsBetter, WarnThreshold = 10 };
        await Add(group, metric,
            new MetricValue { DefinitionId = metric.Id, BriefDate = _db.Clock.Today, Value = 12, EnteredById = "x", EnteredByName = "X" });

        var later = Period.Of(PeriodKind.Week, _db.Clock.Today.AddDays(14));
        var r = await new ReportService(Trends(), _db.Metrics(), _db.Clock).BuildAsync(later);

        var m = r.Metrics.Single();
        (m.Latest, m.Average, m.Highest, m.Missing, m.Breaches).Should().Be(((decimal?)null, (decimal?)null, (decimal?)null, 0, 0));
    }
}
