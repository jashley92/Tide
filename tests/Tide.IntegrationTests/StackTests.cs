using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Intake;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Tide.IntegrationTests;

public class StackServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public StackServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AnalystsKeepTheList_NamesAreUnique_AndOwnersCanOnlyRead()
    {
        var stack = _db.Stack();
        var id = await stack.SaveAsync(new StackForm { Name = " GitLab ", OtherNames = "GitLab CE, gitlab; GitLab Runner", Versions = "17.11.3", Notes = "Two hosts.\nThursdays." });

        var p = (await stack.GetAsync(id))!.Product;
        (p.Name, p.OtherNames, p.Versions, p.Notes).Should().Be(("GitLab", "GitLab CE; GitLab Runner", "17.11.3", "Two hosts.\nThursdays."), "the name itself isn't repeated as another name");
        await FluentActions.Invoking(() => stack.SaveAsync(new StackForm { Name = "gitlab" })).Should().ThrowAsync<ArgumentException>().WithMessage("*already on the list*");
        await FluentActions.Invoking(() => stack.SaveAsync(new StackForm { Name = "  " })).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => stack.SaveAsync(new StackForm { Name = "X", OtherNames = "--" })).Should().ThrowAsync<ArgumentException>().WithMessage("*letter or digit*");
        (await stack.AddByNameAsync("GitLab")).Should().Be(id, "adding by name finds what's there");

        await stack.SetRetiredAsync(id, true);
        (await stack.ListAsync()).Should().BeEmpty();
        (await stack.ListAsync(includeRetired: true)).Should().ContainSingle();
        (await stack.AddByNameAsync("GitLab")).Should().Be(id);
        (await stack.ListAsync()).Should().ContainSingle("adding a retired product brings it back");

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        (await stack.ListAsync()).Should().ContainSingle();
        await FluentActions.Invoking(() => stack.SaveAsync(new StackForm { Name = "Pillow" })).Should().ThrowAsync<ForbiddenException>();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Invoking(() => stack.ListAsync()).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public void AProduct_MatchesByAnyOfItsNames_AsWholeWords()
    {
        var entries = new[] { StackService.Entry(new StackProduct { Name = "Ivanti Connect Secure", OtherNames = "Pulse Secure" }), StackService.Entry(new StackProduct { Name = "Go" }) };

        StackService.Match(entries, "Attackers chain three Ivanti Connect-Secure flaws", null)!.Name.Should().Be("Ivanti Connect Secure");
        StackService.Match(entries, "Old VPN bug", "Pulse secure appliances are targeted again")!.Name.Should().Be("Ivanti Connect Secure");
        StackService.Match(entries, "Google patches Chrome", null).Should().BeNull("'Go' isn't the start of 'Google'");
        StackService.Match(entries, "Unrelated", new string('x', StackService.SummaryStart) + " Pulse Secure").Should().BeNull("only the start of a summary is read");
        var match = StackService.Matcher(entries);
        match("Attackers chain three Ivanti Connect-Secure flaws", null)!.Name.Should().Be("Ivanti Connect Secure", "the matcher for many items reads alike");
        match("Old VPN bug", "Pulse secure appliances are targeted again")!.Name.Should().Be("Ivanti Connect Secure");
        match("Google patches Chrome", null).Should().BeNull();
        match("Go 1.26 fixes a flaw", null)!.Name.Should().Be("Go");
        match("Unrelated", new string('x', StackService.SummaryStart) + " Pulse Secure").Should().BeNull();
        StackService.Matcher([])("Go", null).Should().BeNull();
        StackService.SplitNames("a; b,\nB ;; c").Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task AProductsHistory_HasItsTaggedArticles_ThoseNamingIt_AndTheirActions()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };   // the team needs an administrator
        var stack = _db.Stack();
        var gitlab = await stack.SaveAsync(new StackForm { Name = "GitLab" });
        var articles = _db.Articles();
        var named = await articles.CreateAsync("GitLab patches a max-severity RCE", "Vendor", null, TlpLevel.Clear);
        var tagged = await articles.CreateAsync("Runner token leak in CI", "Vendor", null, TlpLevel.Clear);
        var other = await articles.CreateAsync("Pillow heap overflow", "Vendor", null, TlpLevel.Clear);
        await stack.TagArticleAsync(tagged, gitlab);
        var team = await _db.Teams().AddAsync("VM", null, null);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch the hosts", TeamId = team, ArticleId = named });
        await _db.Actions().CreateAsync(new ActionForm { Title = "Rotate GitLab runner tokens", TeamId = team });
        await _db.Actions().CreateAsync(new ActionForm { Title = "Upgrade Pillow", TeamId = team, ArticleId = other });

        var d = (await stack.GetAsync(gitlab))!;

        d.Articles.Select(a => (a.Title, a.Tagged)).Should().BeEquivalentTo([("GitLab patches a max-severity RCE", false), ("Runner token leak in CI", true)]);
        d.Actions.Select(a => a.Title).Should().BeEquivalentTo("Patch the hosts", "Rotate GitLab runner tokens");
        var row = (await stack.ListAsync()).Single();
        (row.Articles, row.OpenActions).Should().Be((2, 2), "the row counts what the product's page lists: one raised from its articles, one naming it with no article");
    }

    [Fact]
    public async Task PresentMode_ReadsEachArticlesProducts_AndAProductsCard_WithItsOtherArticles()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var stack = _db.Stack();
        var team = await _db.Teams().AddAsync("Network", null, null);
        var vpn = await stack.SaveAsync(new StackForm { Name = "Contoso VPN Gateway", Versions = "9.1R18", TeamId = team, CheckedOn = new DateOnly(2026, 10, 1) });
        var proxy = await stack.SaveAsync(new StackForm { Name = "Alpha Proxy" });
        var old = await stack.SaveAsync(new StackForm { Name = "Aardvark Mail" });
        await stack.SetRetiredAsync(old, true);
        var articles = _db.Articles();
        var shown = await articles.CreateAsync("Gateway auth bypass", "Vendor", null, TlpLevel.Clear);
        var bare = await articles.CreateAsync("Nothing we run", "Vendor", null, TlpLevel.Clear);
        foreach (var p in new[] { vpn, proxy, old }) await stack.TagArticleAsync(shown, p);
        for (var i = 0; i < StackService.CardArticles + 1; i++) await articles.CreateAsync($"Contoso VPN Gateway story {i}", "Vendor", null, TlpLevel.Clear);

        var of = await stack.ForArticlesAsync([shown, bare, shown]);
        of.Keys.Should().Equal(shown);
        of[shown].Select(p => p.Name).Should().Equal(["Alpha Proxy", "Contoso VPN Gateway", "Aardvark Mail"], "those in use by name, then the retired");
        (await stack.ForArticlesAsync([])).Should().BeEmpty();

        var card = (await stack.CardAsync(vpn, TlpLevel.AmberStrict, shown))!;
        (card.Product.Name, card.TeamName, card.Product.CheckedOn).Should().Be(("Contoso VPN Gateway", "Network", new DateOnly(2026, 10, 1)));
        card.Others.Should().HaveCount(StackService.CardArticles).And.NotContain(a => a.Id == shown, "not the article on the slide");
        (await stack.CardAsync(proxy, TlpLevel.AmberStrict, shown))!.Others.Should().BeEmpty();
        (await stack.CardAsync(proxy, TlpLevel.AmberStrict))!.Others.Should().ContainSingle(a => a.Id == shown && a.Tagged);
        (await stack.CardAsync(Guid.NewGuid(), TlpLevel.AmberStrict)).Should().BeNull();

        StackService.ChipVersions(" 9.1R18 ").Should().Be("9.1R18");
        StackService.ChipVersions("  ").Should().BeNull();
        StackService.ChipVersions(null).Should().BeNull();
        StackService.ChipVersions(new string('1', StackService.ChipVersionsMax + 1)).Should().BeNull("long ones are on the card");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Invoking(() => stack.ForArticlesAsync([shown])).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => stack.CardAsync(vpn, TlpLevel.AmberStrict)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task APresentModeCard_LeavesOutArticlesAboveTheTeamEditionsCeiling()
    {
        // The card is on the meeting's screen, where the slides leave out an article above the team edition's ceiling:
        // its title was listed among the product's other articles all the same.
        var stack = _db.Stack();
        var vpn = await stack.SaveAsync(new StackForm { Name = "Contoso VPN Gateway" });
        var articles = _db.Articles();
        var open = await articles.CreateAsync("Contoso VPN Gateway patch out", "Vendor", null, TlpLevel.Clear);
        var red = await articles.CreateAsync("Contoso VPN Gateway breach at a partner", "Vendor", null, TlpLevel.Red);
        await stack.TagArticleAsync(red, vpn);

        (await stack.CardAsync(vpn, TlpLevel.AmberStrict))!.Others.Select(a => a.Id).Should().Equal(open);
        (await stack.CardAsync(vpn, TlpLevel.Red))!.Others.Select(a => a.Id).Should().BeEquivalentTo([open, red]);
    }

    [Fact]
    public async Task AnArticle_IsTaggedWithAnyNumberOfProducts_AndOffersTheOnesItNames()
    {
        var stack = _db.Stack();
        var gitlab = await stack.SaveAsync(new StackForm { Name = "GitLab" });
        var fortigate = await stack.SaveAsync(new StackForm { Name = "FortiGate", OtherNames = "FortiOS" });
        var article = await _db.Articles().CreateAsync("FortiOS and GitLab flaws exploited", "Vendor", null, TlpLevel.Clear);

        var before = await stack.ForArticleAsync(article);
        before.Tagged.Should().BeEmpty("tagging is optional; naming a product only suggests it");
        before.Suggested.Select(p => p.Name).Should().Equal("FortiGate", "GitLab");

        await stack.TagArticleAsync(article, gitlab);
        var after = await stack.ForArticleAsync(article);
        after.Tagged.Select(p => p.Name).Should().Equal("GitLab");
        after.Suggested.Select(p => p.Name).Should().Equal("FortiGate");

        await stack.TagArticleAsync(article, fortigate);
        (await stack.ForArticleAsync(article)).Tagged.Should().HaveCount(2);
        await stack.UntagArticleAsync(article, gitlab);
        await stack.UntagArticleAsync(article, fortigate);
        (await stack.ForArticleAsync(article)).Tagged.Should().BeEmpty();
        await stack.UntagArticleAsync(article, fortigate);
        await FluentActions.Invoking(() => stack.TagArticleAsync(article, Guid.NewGuid())).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TwoPeopleTaggingAnArticle_KeepBothTags_AndTaggingTheSameOneTwiceIsFine()
    {
        var stack = _db.Stack();
        var gitlab = await stack.SaveAsync(new StackForm { Name = "GitLab" });
        var okta = await stack.SaveAsync(new StackForm { Name = "Okta" });
        var article = await _db.Articles().CreateAsync("Two tags", "Vendor", null, TlpLevel.Clear);
        // Robin's card and Sam's card both opened on an untagged article.
        (await stack.ForArticleAsync(article)).Tagged.Should().BeEmpty();

        await Task.WhenAll(_db.Stack().TagArticleAsync(article, gitlab), _db.Stack().TagArticleAsync(article, okta));
        await Task.WhenAll(_db.Stack().TagArticleAsync(article, okta), _db.Stack().TagArticleAsync(article, okta));
        await stack.UntagArticleAsync(article, Guid.NewGuid());

        (await stack.ForArticleAsync(article)).Tagged.Select(p => p.Name).Should().BeEquivalentTo(["GitLab", "Okta"], "each tag is its own change, not the list a card loaded");
    }

    [Fact]
    public async Task Triage_LiftsItemsNamingWhatWeRun_SaysWhich_AndNeverAgesThem()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        await _db.Stack().SaveAsync(new StackForm { Name = "GitLab" });
        var source = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "News", Url = "https://news.example.com/feed.xml", PollMinutes = 30 });
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = _db.Context())
        {
            FeedItem Item(string t, int daysAgo) => new()
            {
                SourceId = source, Title = t, Url = "https://x.example/" + t.Replace(' ', '-'), UrlHash = t, PublishedAtUtc = now.AddDays(-daysAgo), FetchedAtUtc = now.AddDays(-daysAgo),
            };
            ctx.FeedItems.AddRange(Item("fresh story", 1), Item("stale story", 10), Item("stale GitLab runner fix", 10), Item("new GitLab advisory", 2));
            await ctx.SaveChangesAsync();
        }
        var intake = _db.Intake(new IntakeOptions { Enabled = true, MinPollMinutes = 5, AgeDays = 7 });

        var inNew = await intake.InboxAsync(new InboxFilter());
        inNew.Select(r => r.Item.Title).Should().BeEquivalentTo("fresh story", "stale GitLab runner fix", "new GitLab advisory");
        inNew.Take(2).Select(r => r.WeRun?.Name).Should().Equal("GitLab", "GitLab");
        inNew[^1].WeRun.Should().BeNull();
        (await intake.OlderCountAsync()).Should().Be(1, "only the stale story aged; the GitLab one never does");
    }

    // Puts an article on the brief of daysAgo days ago, carrying the CVEs given.
    private async Task BriefAsync(Guid article, int daysAgo, params string[] cves)
    {
        await using var ctx = _db.Context();
        var day = _db.Clock.Today.AddDays(-daysAgo);
        var brief = await ctx.Briefs.FirstOrDefaultAsync(b => b.BriefDate == day);
        if (brief is null) ctx.Briefs.Add(brief = new Brief { BriefDate = day, AnalystId = "a", AnalystName = "A" });
        ctx.BriefItems.Add(new BriefItem { BriefId = brief.Id, ArticleId = article });
        ctx.ArticleCves.AddRange(cves.Select(c => new ArticleCve { ArticleId = article, CveId = c }));
        await ctx.SaveChangesAsync();
    }

    private async Task NewItemsAsync(params string[] titles)
    {
        var source = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "News", Url = "https://news.example.com/feed.xml", PollMinutes = 30 });
        await using var ctx = _db.Context();
        var now = DateTimeOffset.UtcNow;
        ctx.FeedItems.AddRange(titles.Select(t => new FeedItem { SourceId = source, Title = t, Url = "https://x.example/" + t.Replace(' ', '-'), UrlHash = t, PublishedAtUtc = now, FetchedAtUtc = now }));
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task TheList_SaysWhatIsHappeningRightNow_AndPutsWhatNeedsALookFirst()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var stack = _db.Stack();
        var today = _db.Clock.Today;
        var team = await _db.Teams().AddAsync("Platform", null, null);
        var gitlab = await stack.SaveAsync(new StackForm { Name = "GitLab", TeamId = team, CheckedOn = today });
        await stack.SaveAsync(new StackForm { Name = "Aardvark Mail", TeamId = team, CheckedOn = today });
        await stack.SaveAsync(new StackForm { Name = "Okta", TeamId = team, CheckedOn = today.AddDays(-StackService.CheckOldAfterDays - 1) });
        await stack.SaveAsync(new StackForm { Name = "Pillow", CheckedOn = today });
        await stack.SaveAsync(new StackForm { Name = "Zebra Proxy", TeamId = team });
        var gone = await stack.SaveAsync(new StackForm { Name = "Beta Old" });
        await stack.SetRetiredAsync(gone, true);

        var articles = _db.Articles();
        var named = await articles.CreateAsync("GitLab patches a max-severity RCE", "Vendor", null, TlpLevel.Clear);
        var tagged = await articles.CreateAsync("Runner token leak in CI", "Vendor", null, TlpLevel.Clear);
        var old = await articles.CreateAsync("GitLab flaw from last quarter", "Vendor", null, TlpLevel.Clear);
        await stack.TagArticleAsync(tagged, gitlab);
        await BriefAsync(named, 1, "CVE-2026-0001", "CVE-2026-0002");
        await BriefAsync(tagged, 2, "CVE-2026-0002", "CVE-2026-0003");
        await BriefAsync(old, StackService.RecentBriefDays + 1, "CVE-2025-9999");
        await using (var ctx = _db.Context())
        {
            ctx.CveRecords.AddRange(new CveRecord { CveId = "CVE-2026-0002", CvssScore = 9.8m, EpssScore = 0.4m },
                new CveRecord { CveId = "CVE-2026-0003", CvssScore = 7.5m, EpssScore = 0.9m, IsKev = true, KevAddedDate = today });
            await ctx.SaveChangesAsync();
        }
        await NewItemsAsync("new GitLab advisory", "GitLab runner fix", "Nothing we run", "Aardvark Mail and GitLab both patched");

        var list = await stack.OverviewAsync(includeRetired: true);

        list.Rows.Select(r => r.Product.Name).Should().Equal(["GitLab", "Aardvark Mail", "Okta", "Pillow", "Zebra Proxy", "Beta Old"],
            "the product with a CVE on KEV, then the one new in triage, then the housekeeping by name; the retired one is last");
        var row = list.Rows.Single(r => r.Product.Id == gitlab);
        row.Now.Cves.Select(c => c.Id).Should().Equal(["CVE-2026-0003", "CVE-2026-0002", "CVE-2026-0001"], "this week's, each once, the worst first; not last quarter's");
        (row.Now.Cves[0].Record!.IsKev, row.Now.Cves[1].Record!.CvssScore, row.Now.Cves[2].Record).Should().Be((true, 9.8m, null));
        (row.Now.TriageItems, row.Articles, row.CheckOld, row.NeedsLook).Should().Be((2, 3, false, true));
        list.Rows.Single(r => r.Product.Name == "Aardvark Mail").Now.TriageItems.Should().Be(1, "an item goes to the first product it names, as its We run mark in triage says");
        list.Rows.Where(r => r.CheckOld).Select(r => r.Product.Name).Should().Equal("Okta", "Zebra Proxy");
        list.Rows.Where(r => r.NoTeam).Select(r => r.Product.Name).Should().Equal("Pillow");
        list.Rows[^1].Should().Match<StackRow>(r => !r.NeedsLook && !r.CheckOld && !r.NoTeam && !r.Now.Any, "a retired product asks for nothing");

        // Once triage is cleared, the one product that's owned, checked and quiet drops below the rest.
        await using (var ctx = _db.Context()) await ctx.FeedItems.ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, FeedItemStatus.Dismissed));
        var after = (await stack.OverviewAsync()).Rows;
        after.Select(r => r.Product.Name).Should().Equal("GitLab", "Okta", "Pillow", "Zebra Proxy", "Aardvark Mail");
        after.Where(r => r.NeedsLook).Should().HaveCount(4).And.BeEquivalentTo(after.Take(4), "the filter's count and the order read one rule");

        // The plain list (the API's) stays by name, and reads no "Right now".
        var plain = await stack.ListAsync();
        plain.Select(r => r.Product.Name).Should().Equal("Aardvark Mail", "GitLab", "Okta", "Pillow", "Zebra Proxy");
        plain.Should().OnlyContain(r => !r.Now.Any && r.Now.TriageItems == null);
        plain.Single(r => r.Product.Id == gitlab).Articles.Should().Be(3);
    }

    [Fact]
    public async Task RightNow_SaysBriefedToday_OrNotWrittenUp_WithinTheTeamEditionsCeiling()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var stack = _db.Stack();
        await stack.SaveAsync(new StackForm { Name = "Halo Router" });
        await stack.SaveAsync(new StackForm { Name = "Halo Keep" });
        await stack.SaveAsync(new StackForm { Name = "Halo Quiet" });
        await stack.SaveAsync(new StackForm { Name = "Halo Idle" });
        var articles = _db.Articles();
        await BriefAsync(await articles.CreateAsync("Halo Router flaw exploited", "Vendor", null, TlpLevel.Clear), 0);
        await BriefAsync(await articles.CreateAsync("Halo Keep breach at a partner", "Vendor", null, TlpLevel.Red), 0);
        await NewItemsAsync("Halo Router advisory", "Halo Quiet patch out");

        var rows = (await stack.OverviewAsync()).Rows.ToDictionary(r => r.Product.Name, r => r.Now);
        (rows["Halo Router"].BriefedToday, rows["Halo Router"].NotWrittenUp).Should().Be((true, false), "an article on today's brief names it");
        rows["Halo Keep"].BriefedToday.Should().BeFalse("its only article on today's brief is above the team edition's ceiling");
        (rows["Halo Quiet"].BriefedToday, rows["Halo Quiet"].NotWrittenUp).Should().Be((false, true), "triage names it and no article does");
        (rows["Halo Idle"].BriefedToday, rows["Halo Idle"].NotWrittenUp).Should().Be((false, false), "nothing is happening to it");

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        (await stack.OverviewAsync()).Rows.Should().OnlyContain(r => !r.Now.NotWrittenUp, "the triage count, and what it says, is only for those who triage");
    }

    [Fact]
    public async Task RightNow_NeverNamesACve_ThatComesOnlyFromAnArticleAboveTheTeamEditionsCeiling()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };   // an administrator moves the ceiling
        var stack = _db.Stack();
        await stack.SaveAsync(new StackForm { Name = "Contoso VPN Gateway" });
        var articles = _db.Articles();
        var open = await articles.CreateAsync("Contoso VPN Gateway patch out", "Vendor", null, TlpLevel.Amber);
        var red = await articles.CreateAsync("Contoso VPN Gateway breach at a partner", "Vendor", null, TlpLevel.Red);
        await BriefAsync(open, 0, "CVE-2026-0001");
        await BriefAsync(red, 0, "CVE-2026-0001", "CVE-2026-0666");
        await using (var ctx = _db.Context())
        {
            ctx.CveRecords.Add(new CveRecord { CveId = "CVE-2026-0666", CvssScore = 10m, IsKev = true });
            await ctx.SaveChangesAsync();
        }

        var list = await stack.OverviewAsync();
        list.Ceiling.Should().Be(TlpLevel.AmberStrict, "the team edition's default ceiling");
        var now = list.Rows.Single().Now;
        now.Cves.Select(c => c.Id).Should().Equal(["CVE-2026-0001"], "the RED article's own CVE isn't named, even to an analyst");
        now.CvesAbove.Should().Be(1, "it's counted");

        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.Red);
        var all = (await stack.OverviewAsync()).Rows.Single().Now;
        (all.Cves.Select(c => c.Id).ToList(), all.CvesAbove).Should().BeEquivalentTo((new[] { "CVE-2026-0666", "CVE-2026-0001" }, 0));
    }

    [Fact]
    public async Task TheTriageCount_IsOnlyForThoseWhoTriage_AndDoesNotOrderTheListForAnyoneElse()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var stack = _db.Stack();
        var team = await _db.Teams().AddAsync("Platform", null, null);
        await stack.SaveAsync(new StackForm { Name = "Aardvark Mail", TeamId = team, CheckedOn = _db.Clock.Today });
        await stack.SaveAsync(new StackForm { Name = "GitLab", TeamId = team, CheckedOn = _db.Clock.Today });
        await NewItemsAsync("new GitLab advisory");

        (await stack.OverviewAsync()).Rows.Select(r => (r.Product.Name, r.Now.TriageItems, r.NeedsLook)).Should().Equal(("GitLab", 1, true), ("Aardvark Mail", 0, false));

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        _db.Commands.Record = true;
        _db.Commands.Reset();
        (await stack.OverviewAsync()).Rows.Select(r => (r.Product.Name, r.Now.TriageItems, r.NeedsLook)).Should().Equal([("Aardvark Mail", null, false), ("GitLab", null, false)],
            "an action owner doesn't see triage, so nothing on the list may tell them what's in it");
        _db.Commands.Sql.Should().NotContain(s => s.Contains("FeedItems", StringComparison.Ordinal), "the items aren't read for them at all");
    }
}

public class StackPageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task OurStack_ListsTheProducts_AndAProductsPageEditsAndShowsItsHistory()
    {
        Guid id;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            id = await scope.ServiceProvider.GetRequiredService<StackService>().SaveAsync(new StackForm { Name = "Page Test Product", Versions = "1.2.3", Notes = "Owned by **ops**." });

        await using (var list = await ComponentHarness.RenderAsync<StackPage>(app, "/stack"))
            (await list.Html()).Should().Contain(">Our stack</h1>").And.Contain($"href=\"stack/{id}\">Page Test Product</a>").And.Contain("1.2.3").And.Contain("Add a product");

        await using var page = await ComponentHarness.RenderAsync<StackPage>(app, $"/stack/{id}", new() { ["Id"] = id });
        var html = await page.Html();
        html.Should().Contain("id=\"f-versions\"").And.Contain("id=\"f-notes\"").And.Contain(">Articles <span class=\"tide-count\">(0)</span>").And.Contain(">Retire</button>");
    }

    private static readonly ComponentHarness.SignIn Owner = new("dev:stack-owner", "Olu Owner", "ActionOwner");

    // Does what a control on the page would, draws the page again as an event would, and reads it.
    private static async Task<string> After(ComponentHarness.Rendered<StackPage> page, Action<StackPage> what)
    {
        await page.Renderer.Dispatcher.InvokeAsync(() =>
        {
            what(page.Component);
            typeof(Microsoft.AspNetCore.Components.ComponentBase).GetMethod("StateHasChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(page.Component, null);
        });
        return await page.Html();
    }

    // The product names in the order the list shows them.
    private static List<string> Names(string html) =>
        [.. System.Text.RegularExpressions.Regex.Matches(html, "<td><a href=\"stack/[0-9a-f-]+\">([^<]+)</a>").Select(m => m.Groups[1].Value)];

    [Fact]
    public async Task TheAddForm_IsBehindItsButton_AndOnlyForThoseWhoKeepTheList()
    {
        await using var page = await ComponentHarness.RenderAsync<StackPage>(app, "/stack");
        var closed = await page.Html();
        closed.Should().Contain("aria-expanded=\"false\">Add a product</button>").And.NotContain("id=\"new-name\"", "the form isn't shown until it's asked for");

        var open = await After(page, p => p.ToggleAdd());
        open.Should().Contain("aria-expanded=\"true\">Add a product</button>").And.Contain("id=\"new-name\"").And.Contain("id=\"new-others\"").And.Contain(">Add</button>");
        (await After(page, p => p.ToggleAdd())).Should().NotContain("id=\"new-name\"");

        await using var owner = await ComponentHarness.RenderAsync<StackPage>(app, "/stack", @as: Owner);
        (await owner.Html()).Should().Contain(">Our stack</h1>").And.NotContain("Add a product");
    }

    [Fact]
    public async Task TheList_PutsWhatNeedsALookFirst_FlagsOldChecks_AndItsFiltersNarrowItWithCounts()
    {
        Guid team;
        DateOnly today;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            today = sp.GetRequiredService<Tide.Application.Org.OrgClock>().Today;
            team = await sp.GetRequiredService<TeamService>().AddAsync("Stack Filter Team", null, null);
            var stack = sp.GetRequiredService<StackService>();
            await stack.SaveAsync(new StackForm { Name = "Sift Alpha Fresh", TeamId = team, CheckedOn = today, Versions = "7.7-sift" });
            await stack.SaveAsync(new StackForm { Name = "Sift Beta Never", TeamId = team, OtherNames = "Siftware" });
            await stack.SaveAsync(new StackForm { Name = "Sift Mid Orphan", CheckedOn = today });
            await stack.SaveAsync(new StackForm { Name = "Sift Zeta Old", TeamId = team, CheckedOn = today.AddYears(-1) });
        }
        await using var page = await ComponentHarness.RenderAsync<StackPage>(app, "/stack");

        var html = await After(page, p => p.SetWords("sift"));
        Names(html).Should().Equal(["Sift Beta Never", "Sift Mid Orphan", "Sift Zeta Old", "Sift Alpha Fresh"], "those that need a look, by name, then the rest");
        html.Should().Contain("<th scope=\"col\">Right now</th>").And.Contain("Never checked").And.Contain($"Over {StackService.CheckOldAfterDays} days ago").And.Contain("Nothing new");
        var lastYear = today.AddYears(-1);
        html.Should().Contain($"{Ui.ShortDay(lastYear)}, {lastYear.Year}", "a check from another year says which").And.NotContain($"{Ui.ShortDay(today)}, {today.Year}", "this year's doesn't");
        Ui.ShortDay(new DateOnly(2025, 9, 16), new DateOnly(2026, 11, 10)).Should().Be("Tue Sep 16, 2025");
        Ui.ShortDay(new DateOnly(2026, 1, 2), new DateOnly(2026, 11, 10)).Should().Be("Fri Jan 2");
        var all = page.Component.List!;
        foreach (var q in StackPage.Quick)
            html.Should().MatchRegex($"aria-pressed=\"false\"[^>]*>{q.Label} \\({all.Count(q.Matches)}\\)</button>", "each quick filter says how many it would leave");
        all.Count(StackPage.Quick[0].Matches).Should().Be(all.Count(r => r.NeedsLook)).And.BeGreaterThanOrEqualTo(3);
        html.Should().Contain($"Showing 4 of {all.Count} products");

        Names(await After(page, p => p.SetWords("7.7-SIFT"))).Should().Equal(["Sift Alpha Fresh"], "a version matches, whatever its case");
        Names(await After(page, p => p.SetWords("siftware"))).Should().Equal(["Sift Beta Never"], "another name matches");

        var old = await After(page, p => { p.SetWords("sift"); p.SetQuick("old", true); });
        Names(old).Should().Equal("Sift Beta Never", "Sift Zeta Old");
        old.Should().MatchRegex("aria-pressed=\"true\"[^>]*><span class=\"bi bi-check2\" aria-hidden=\"true\"></span>Check is old");
        Names(await After(page, p => { p.SetQuick("old", false); p.SetQuick("noteam", true); })).Should().Equal("Sift Mid Orphan");
        Names(await After(page, p => { p.SetQuick("noteam", false); p.SetQuick("look", true); })).Should().Equal("Sift Beta Never", "Sift Mid Orphan", "Sift Zeta Old");
        Names(await After(page, p => { p.SetQuick("look", false); p.SetWords(""); p.SetTeam(team.ToString()); })).Should().Equal("Sift Beta Never", "Sift Zeta Old", "Sift Alpha Fresh");
        (await After(page, p => p.SetWords("no such product"))).Should().Contain("No product matches these filters.").And.Contain("Show retired products");
    }

    [Fact]
    public async Task RightNow_ShowsTheCvesAndTheTriageCount_ButNoTriageCountToSomeoneWhoCannotTriage()
    {
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            var today = sp.GetRequiredService<Tide.Application.Org.OrgClock>().Today;
            var product = await sp.GetRequiredService<StackService>().SaveAsync(new StackForm { Name = "Nowcast Gateway", CheckedOn = today });
            var article = await sp.GetRequiredService<ArticleService>().CreateAsync("Nowcast Gateway flaw exploited", "Vendor", null, TlpLevel.Clear);
            var secret = await sp.GetRequiredService<ArticleService>().CreateAsync("Nowcast Gateway breach at a partner", "Vendor", null, TlpLevel.Red);
            await using var db = await sp.GetRequiredService<Tide.Application.Abstractions.IAppDbContextFactory>().CreateAsync();
            var brief = await db.Briefs.FirstOrDefaultAsync(b => b.BriefDate == today);
            if (brief is null) db.Briefs.Add(brief = new Brief { BriefDate = today, AnalystId = "a", AnalystName = "A" });
            db.BriefItems.AddRange(new BriefItem { BriefId = brief.Id, ArticleId = article, SortOrder = 90 }, new BriefItem { BriefId = brief.Id, ArticleId = secret, SortOrder = 91 });
            db.ArticleCves.AddRange(new ArticleCve { ArticleId = article, CveId = "CVE-2026-4141" }, new ArticleCve { ArticleId = secret, CveId = "CVE-2026-4666" });
            db.CveRecords.Add(new CveRecord { CveId = "CVE-2026-4141", CvssScore = 9.1m, EpssScore = 0.62m, IsKev = true });
            var source = new Source { Name = "Nowcast News", Url = "https://nowcast.example.com/feed.xml" };
            db.Sources.Add(source);
            var now = DateTimeOffset.UtcNow;
            db.FeedItems.Add(new FeedItem { SourceId = source.Id, Title = "Patch out for Nowcast Gateway", Url = "https://nowcast.example.com/1", UrlHash = "nowcast-1", PublishedAtUtc = now, FetchedAtUtc = now });
            await db.SaveChangesAsync();
        }

        await using var analyst = await ComponentHarness.RenderAsync<StackPage>(app, "/stack");
        var seen = await After(analyst, p => p.SetWords("nowcast"));
        seen.Should().Contain("CVE-2026-4141").And.Contain("<span class=\"tide-kev\">KEV</span>").And.Contain(">9.1</span>").And.Contain("62 %")
            .And.Contain("1 CVE above TLP:AMBER&#x2B;STRICT").And.Contain("<a href=\"triage?named=true\">1 new in triage</a>");
        seen.Should().NotContain("CVE-2026-4666", "a CVE seen only on an article above the team edition's ceiling is counted, not named").And.NotContain("breach at a partner");

        await using var owner = await ComponentHarness.RenderAsync<StackPage>(app, "/stack", @as: Owner);
        var theirs = await After(owner, p => p.SetWords("nowcast"));
        theirs.Should().Contain("CVE-2026-4141").And.Contain("1 CVE above TLP:AMBER&#x2B;STRICT").And.NotContain("CVE-2026-4666");
        theirs.Should().NotContain("in triage", "triage is the analysts': its count isn't shown to anyone else").And.NotContain("href=\"triage");
    }

    [Fact]
    public async Task AProductWithNoTeam_ShowsANoTeamFlag_AndRightNowSaysBriefedToday()
    {
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            var today = sp.GetRequiredService<Tide.Application.Org.OrgClock>().Today;
            var team = await sp.GetRequiredService<TeamService>().AddAsync("Flagged Owners", null, null);
            var stack = sp.GetRequiredService<StackService>();
            await stack.SaveAsync(new StackForm { Name = "Flagpole Orphan", CheckedOn = today });
            await stack.SaveAsync(new StackForm { Name = "Flagpole Owned", TeamId = team, CheckedOn = today });
            var article = await sp.GetRequiredService<ArticleService>().CreateAsync("Flagpole Owned flaw exploited", "Vendor", null, TlpLevel.Clear);
            await using var db = await sp.GetRequiredService<Tide.Application.Abstractions.IAppDbContextFactory>().CreateAsync();
            var brief = await db.Briefs.FirstOrDefaultAsync(b => b.BriefDate == today);
            if (brief is null) db.Briefs.Add(brief = new Brief { BriefDate = today, AnalystId = "a", AnalystName = "A" });
            db.BriefItems.Add(new BriefItem { BriefId = brief.Id, ArticleId = article, SortOrder = 95 });
            await db.SaveChangesAsync();
        }

        await using var page = await ComponentHarness.RenderAsync<StackPage>(app, "/stack");
        var html = await After(page, p => p.SetWords("flagpole"));
        var orphan = html[html.IndexOf(">Flagpole Orphan</a>", StringComparison.Ordinal)..];
        orphan = orphan[..orphan.IndexOf("</tr>", StringComparison.Ordinal)];
        orphan.Should().Contain("<span class=\"tide-badge tide-badge-neutral tide-badge-off\">No team</span>", "a product no team owns says so as a flag, as the No owning team filter does")
            .And.NotContain("<td>—</td>");
        var owned = html[html.IndexOf(">Flagpole Owned</a>", StringComparison.Ordinal)..];
        owned = owned[..owned.IndexOf("</tr>", StringComparison.Ordinal)];
        owned.Should().Contain("<td data-label=\"Team\">Flagged Owners</td>").And.Contain("briefed today").And.NotContain("No team");
    }

    // An article's open "we run" card shows a colleague's tag and untag: they're heard on the article's tags topic.
    [Fact]
    public async Task AnOpenCard_ShowsAColleaguesTagAndUntag()
    {
        Guid article, product;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            product = await scope.ServiceProvider.GetRequiredService<StackService>().SaveAsync(new StackForm { Name = "Card Test Gateway" });
            article = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Stack card tagging", "Vendor", null, TlpLevel.Clear);
        }
        await using var robin = await ComponentHarness.RenderAsync<Tide.Web.Components.Shared.ArticleStackCard>(app, $"/articles/{article}",
            new() { ["ArticleId"] = article, ["CanWrite"] = true });
        (await robin.Html()).Should().NotContain("Untag Card Test Gateway");

        await using (var sam = ComponentHarness.SignedIn(app, new ComponentHarness.SignIn("dev:sam", "Sam", "Analyst")))
            await sam.ServiceProvider.GetRequiredService<StackService>().TagArticleAsync(article, product);
        (await robin.HtmlOnce("Untag Card Test Gateway")).Should().Contain("Untag Card Test Gateway");

        await using (var sam = ComponentHarness.SignedIn(app, new ComponentHarness.SignIn("dev:sam", "Sam", "Analyst")))
            await sam.ServiceProvider.GetRequiredService<StackService>().UntagArticleAsync(article, product);
        for (var i = 0; i < 50 && (await robin.Html()).Contains("Untag Card Test Gateway", StringComparison.Ordinal); i++) await Task.Delay(100);
        (await robin.Html()).Should().NotContain("Untag Card Test Gateway");
    }
}
