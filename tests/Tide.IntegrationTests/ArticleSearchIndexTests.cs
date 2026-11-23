using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Search;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// A text search matches an article by its search text (<see cref="ArticleSearchRow"/>) where that's current, and part by
/// part where it isn't: the same articles in the same order whether no row is built yet, every one is, every one is
/// out of date; an edit is found at once; and the background rebuild brings rows back to current.
/// </summary>
public class ArticleSearchIndexTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    // Each searched in turn; between them they reach every part a search reads, and the ways a match can go wrong.
    private static readonly string[] Queries =
    [
        "ransomware", "ransomware vpn", "éclair", "ÉCLAIR", "evil[.]example.net", "C2", "CVE-2025-0042", "T1566", "phishing",
        "cozy", "\"alpha beta\"", "lure", "pastry", "443", "edge appliances", "fs-isac", "finance",
    ];

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task A_search_finds_the_same_articles_in_the_same_order_built_or_not(string provider)
    {
        using var db = new TestDb(provider: provider);
        var ids = await SeedAsync(db);

        var unbuilt = await AnswersAsync(db);
        await BuildAllAsync(db);
        await using (var ctx = db.Context())
        {
            (await ctx.ArticleSearch.CountAsync(r => !r.Dirty && r.Builder == ArticleSearchIndex.Builder)).Should().Be(ids.Count);
            (await ArticleSearchIndex.LeftAsync(ctx)).Should().Be(0);
        }
        (await AnswersAsync(db)).Should().Equal(unbuilt, "the built text matches as the parts did");

        await using (var ctx = db.Context()) await ctx.ArticleSearch.ExecuteUpdateAsync(s => s.SetProperty(r => r.Dirty, true));
        (await AnswersAsync(db)).Should().Equal(unbuilt, "an out-of-date row is matched part by part");
        await using (var ctx = db.Context()) await ctx.ArticleSearch.ExecuteDeleteAsync();
        (await AnswersAsync(db)).Should().Equal(unbuilt, "an article with no row (added outside the app) is matched part by part");

        // What the answers are: each query against what it should and shouldn't find (the order is checked above).
        HashSet<Guid> Found(string q) => unbuilt.Single(a => a.StartsWith(q + ": ", StringComparison.Ordinal))[(q.Length + 2)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToHashSet();
        HashSet<Guid> Of(params string[] keys) => keys.Select(k => ids[k]).ToHashSet();
        Found("ransomware").Should().BeEquivalentTo(Of("vendor", "roundup"), "a title and a bullet");
        Found("ransomware vpn").Should().BeEquivalentTo(Of("vendor", "roundup"));
        Found("edge appliances").Should().BeEquivalentTo(Of("roundup"), "every term, each anywhere");
        Found("éclair").Should().BeEquivalentTo(Of("eclair"));
        Found("ÉCLAIR").Should().BeEquivalentTo(Of("eclair"), "an accented word in either case");
        Found("evil[.]example.net").Should().BeEquivalentTo(Of("prose", "indicator"), "the live and defanged forms");
        Found("C2").Should().BeEquivalentTo(Of("indicator"), "an indicator's note");
        Found("443").Should().BeEquivalentTo(Of("indicator"));
        Found("CVE-2025-0042").Should().BeEquivalentTo(Of("cve"));
        Found("phishing").Should().BeEquivalentTo(Of("cve", "caption"), "a technique by name, and a caption");
        Found("cozy").Should().BeEquivalentTo(Of("profiled"), "an adversary profile's alias");
        Found("\"alpha beta\"").Should().BeEmpty("a term can't match across two parts");
        Found("pastry").Should().BeEquivalentTo(Of("eclair"));
        Found("fs-isac").Should().BeEquivalentTo(Of("sourced"));
        Found("finance").Should().BeEquivalentTo(Of("sourced"));
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task An_edit_is_found_at_once_and_again_once_its_text_is_rebuilt(string provider)
    {
        using var db = new TestDb(provider: provider);
        var ids = await SeedAsync(db);
        await BuildAllAsync(db);
        var search = new SearchService(db, db.User);
        async Task<bool> Finds(string q, string key) => (await search.SearchAsync(q)).Articles.Items.Any(h => h.Id == ids[key]);

        await using (var ctx = db.Context())
        {
            var a = await ctx.Articles.Include(x => x.Bullets).Include(x => x.Indicators).Include(x => x.Cves).Include(x => x.Techniques)
                .Include(x => x.Screenshots).SingleAsync(x => x.Id == ids["roundup"]);
            a.Title = "Quarterly roundup gamma";
            a.Bullets.Clear();
            a.Indicators.Add(new ArticleIndicator { ArticleId = a.Id, Type = IndicatorType.Domain, Value = "fresh.example.org", Note = "newly seen" });
            a.Cves.Add(new ArticleCve { ArticleId = a.Id, CveId = "CVE-2026-1111" });
            a.Techniques.Add(new ArticleTechnique { ArticleId = a.Id, TechniqueId = "T1190" });
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = db.Context()) (await ctx.ArticleSearch.SingleAsync(r => r.ArticleId == ids["roundup"])).Dirty.Should().BeTrue();
        foreach (var built in new[] { false, true })
        {
            (await Finds("gamma", "roundup")).Should().BeTrue($"a title edit (built: {built})");
            (await Finds("alpha", "roundup")).Should().BeFalse($"the old title (built: {built})");
            (await Finds("ransomware", "roundup")).Should().BeFalse($"a removed bullet (built: {built})");
            (await Finds("newly", "roundup")).Should().BeTrue($"an indicator's note (built: {built})");
            (await Finds("CVE-2026-1111", "roundup")).Should().BeTrue($"a CVE (built: {built})");
            (await Finds("T1190", "roundup")).Should().BeTrue($"a technique (built: {built})");
            await BuildAllAsync(db);
        }
        await using (var ctx = db.Context()) (await ctx.ArticleSearch.SingleAsync(r => r.ArticleId == ids["roundup"])).Dirty.Should().BeFalse();

        // A caption edit marks it too.
        await using (var ctx = db.Context())
        {
            (await ctx.ArticleScreenshots.SingleAsync(s => s.ArticleId == ids["caption"])).Caption = "Credential prompt";
            await ctx.SaveChangesAsync();
        }
        (await Finds("credential", "caption")).Should().BeTrue();
        (await Finds("lure", "caption")).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task A_save_marks_only_what_a_search_reads_and_a_rebuild_never_overwrites_a_newer_mark(string provider)
    {
        using var db = new TestDb(provider: provider);
        var ids = await SeedAsync(db);
        await BuildAllAsync(db);
        Guid stamp;
        await using (var ctx = db.Context())
        {
            stamp = (await ctx.ArticleSearch.AsNoTracking().SingleAsync(r => r.ArticleId == ids["vendor"])).Stamp;
            (await ctx.Articles.SingleAsync(a => a.Id == ids["vendor"])).Status = ArticleStatus.Ready;
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = db.Context())
            (await ctx.ArticleSearch.AsNoTracking().SingleAsync(r => r.ArticleId == ids["vendor"])).Dirty.Should().BeFalse("a status isn't searched");

        await using (var ctx = db.Context())
        {
            (await ctx.Articles.SingleAsync(a => a.Id == ids["vendor"])).Bluf = "Now about wipers.";
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = db.Context())
        {
            var row = await ctx.ArticleSearch.AsNoTracking().SingleAsync(r => r.ArticleId == ids["vendor"]);
            (row.Dirty, row.Stamp == stamp).Should().Be((true, false));
            // A rebuild that read the row before this save would write under the old stamp: it changes nothing.
            (await ctx.ArticleSearch.Where(r => r.ArticleId == ids["vendor"] && r.Stamp == stamp)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Dirty, false))).Should().Be(0);
        }

        // After an upgrade that changes what the text holds, every row is out of date and is rebuilt.
        await using (var ctx = db.Context()) await ctx.ArticleSearch.ExecuteUpdateAsync(s => s.SetProperty(r => r.Builder, ArticleSearchIndex.Builder - 1));
        await using (var ctx = db.Context())
        {
            (await ArticleSearchIndex.LeftAsync(ctx)).Should().Be(1, "only the one a save marked, until the app starts");
            (await ArticleSearchIndex.MarkOlderBuildsAsync(ctx)).Should().Be(ids.Count - 1);
            (await ArticleSearchIndex.LeftAsync(ctx)).Should().Be(ids.Count);
        }
        (await AnswersAsync(db)).Should().NotBeEmpty("an older build's row is matched part by part meanwhile");
        await BuildAllAsync(db);
        await using (var ctx = db.Context())
        {
            (await ArticleSearchIndex.LeftAsync(ctx)).Should().Be(0);
            (await ctx.ArticleSearch.AsNoTracking().SingleAsync(r => r.ArticleId == ids["vendor"])).Text.Should().Contain("Now about wipers.");
        }
    }

    private static async Task BuildAllAsync(TestDb db)
    {
        while (true)
        {
            await using var ctx = db.Context();
            if (await ArticleSearchIndex.RebuildAsync(ctx, batch: 3) < 3) return;
        }
    }

    private static async Task<List<string>> AnswersAsync(TestDb db)
    {
        var search = new SearchService(db, db.User);
        var answers = new List<string>();
        foreach (var q in Queries)
            answers.Add($"{q}: {string.Join(",", (await search.SearchAsync(q)).Articles.Items.Select(h => h.Id))}");
        return answers;
    }

    private static async Task<Dictionary<string, Guid>> SeedAsync(TestDb db)
    {
        db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        var ids = new Dictionary<string, Guid>();
        var at = Now.AddDays(-30);
        Article Made(string key, string title, string bluf = "Summary.")
        {
            var a = new Article { Title = title, Bluf = bluf, AuthorId = "x", AuthorName = "X" };
            ids[key] = a.Id;
            return a;
        }
        var vendor = Made("vendor", "Ransomware hits edge VPN vendor");
        var roundup = Made("roundup", "Quarterly roundup alpha", "beta testers wanted");
        roundup.Bullets.Add(new ArticleBullet { ArticleId = roundup.Id, Text = "The **ransomware** crew moved to edge VPN appliances." });
        var eclair = Made("eclair", "Éclair café incident");
        eclair.WhyItMatters = "It affects every pastry supplier.";
        var prose = Made("prose", "Defanged in prose", "Seen at evil[.]example[.]net last week.");
        var indicator = Made("indicator", "Carries the domain");
        indicator.Indicators.Add(new ArticleIndicator { ArticleId = indicator.Id, Type = IndicatorType.Domain, Value = "evil.example.net", Note = "C2 on port 443" });
        var cve = Made("cve", "A CVE and a technique");
        cve.Cves.Add(new ArticleCve { ArticleId = cve.Id, CveId = "CVE-2025-0042" });
        cve.Techniques.Add(new ArticleTechnique { ArticleId = cve.Id, TechniqueId = "T1566" });
        var caption = Made("caption", "A screenshot");
        var file = new StoredFile { FileName = "lure.png", ContentType = "image/png", SizeBytes = 1, Sha256 = new string('0', 64) };
        caption.Screenshots.Add(new ArticleScreenshot { ArticleId = caption.Id, FileId = file.Id, Caption = "Phishing lure screenshot" });
        var profiled = Made("profiled", "Password spraying");
        var actor = new Adversary { Name = "Midnight Blizzard", Aliases = "APT29, Cozy Bear" };
        var sourced = Made("sourced", "A sector note");
        (sourced.SourceName, sourced.Sectors, sourced.Actors) = ("FS-ISAC", "Finance", "Scattered Spider");
        await using var ctx = db.Context();
        ctx.AddRange(file, actor);
        var n = 0;
        foreach (var a in new[] { vendor, roundup, eclair, prose, indicator, cve, caption, profiled, sourced })
        {
            a.CreatedAtUtc = at.AddHours(n++);
            ctx.Articles.Add(a);
        }
        ctx.ArticleAdversaries.Add(new ArticleAdversary { ArticleId = profiled.Id, AdversaryId = actor.Id });
        await ctx.SaveChangesAsync();
        // The audit stamps the time it was saved; the search orders by it, so each gets its own.
        n = 0;
        foreach (var key in ids.Keys)
        {
            var when = at.AddHours(n++);
            await ctx.Articles.Where(a => a.Id == ids[key]).ExecuteUpdateAsync(s => s.SetProperty(a => a.CreatedAtUtc, when));
        }
        return ids;
    }
}
