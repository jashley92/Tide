using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Intake;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

public class IntakeServiceTests : IDisposable
{
    private TestDb _db = new(connectionPerContext: true);   // some tests race two promotes
    private const string FeedUrl = "https://news.example.com/feed.xml";

    public IntakeServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private static string Rss(params (string Title, string Link, string? Description)[] items) =>
        "<rss version=\"2.0\"><channel><title>News</title>" +
        string.Concat(items.Select(i => $"<item><title>{i.Title}</title><link>{i.Link}</link><description>{i.Description}</description><pubDate>Mon, 26 Oct 2026 09:00:00 GMT</pubDate></item>")) +
        "</channel></rss>";

    private async Task<Guid> Source(SourceKind kind = SourceKind.Feed, string url = FeedUrl, TlpLevel tlp = TlpLevel.Clear) =>
        await _db.Intake().SaveSourceAsync(new SourceForm { Name = "News " + kind, Kind = kind, Url = url, DefaultTlp = tlp, PollMinutes = 30 });

    [Fact]
    public async Task StarterSources_AreAddedPausedAndClear_AndOnesAlreadyHereAreLeftAlone()
    {
        var intake = _db.Intake();
        // One already here by address under another name, and one by name in another case, retired.
        await intake.SaveSourceAsync(new SourceForm { Name = "Bleeping", Url = "https://www.bleepingcomputer.com/feed/", PollMinutes = 30, Reliability = SourceReliability.A });
        var talos = await intake.SaveSourceAsync(new SourceForm { Name = "cisco talos", Url = "https://talos.example.com/feed", PollMinutes = 30 });
        await using (var ctx = _db.Context())
        {
            var s = await ctx.Sources.SingleAsync(x => x.Id == talos);
            s.IsRetired = true;
            await ctx.SaveChangesAsync();
        }

        var added = await intake.AddStarterSourcesAsync(["BleepingComputer", "Cisco Talos", "The Record", "CISA KEV", "Not on the list"]);

        added.Should().Be(2);
        await using var db = _db.Context();
        var record = await db.Sources.SingleAsync(s => s.Name == "The Record");
        record.Should().Match<Source>(s => !s.Enabled && s.DefaultTlp == TlpLevel.Clear && s.Reliability == SourceReliability.C
            && s.Url == "https://therecord.media/feed" && s.Kind == SourceKind.Feed && s.PollMinutes == 60);
        (await db.Sources.SingleAsync(s => s.Name == "CISA KEV")).Kind.Should().Be(SourceKind.CisaKev);
        (await db.Sources.SingleAsync(s => s.Url == "https://www.bleepingcomputer.com/feed/")).Reliability.Should().Be(SourceReliability.A, "the one already here is untouched");
        (await db.Sources.CountAsync()).Should().Be(4);
        (await intake.AddStarterSourcesAsync(["The Record"])).Should().Be(0, "adding twice adds nothing");
    }

    [Fact]
    public async Task StarterSources_NeedAnAdministrator()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await FluentActions.Invoking(() => _db.Intake().AddStarterSourcesAsync(["The Record"])).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Poll_StoresNewItemsOnce_ByCanonicalAddress_AndFlagsARewordedRepeat()
    {
        var source = await Source();
        _db.Http.Set(FeedUrl, Rss(
            ("Ransomware gang exploits edge VPN zero-day against manufacturers", "https://example.com/a?utm_source=rss", "About CVE-2026-0001."),
            ("Manufacturers hit as ransomware gang exploits edge VPN zero-day", "https://other.example.org/b", null),
            ("Same link, other tracking", "https://example.com/a?utm_campaign=x", null)), etag: "\"v1\"");
        var intake = _db.Intake();

        var first = await intake.PollNowAsync(source);
        first.New.Should().Be(2, "the third item is the first one's address with different tracking");
        (await intake.PollNowAsync(source)).New.Should().Be(0, "nothing is stored twice");
        _db.Http.Requests.Last().Request!.ETag.Should().Be("\"v1\"", "the next poll is conditional");

        var inbox = await intake.InboxAsync(new InboxFilter());
        var repeat = inbox.Single(r => r.Item.Title.StartsWith("Manufacturers", StringComparison.Ordinal));
        repeat.Similar!.Title.Should().StartWith("Ransomware gang");
        inbox.Single(r => r.Item.Title.StartsWith("Ransomware", StringComparison.Ordinal)).Item.CveList.Should().Equal("CVE-2026-0001");
        await using var ctx = _db.Context();
        (await ctx.Sources.SingleAsync()).LastNewItems.Should().Be(0);
    }

    [Fact]
    public async Task Poll_AFailureIsRecorded_AndBacksTheSourceOff()
    {
        var source = await Source();
        var intake = _db.Intake();

        var result = await intake.PollNowAsync(source);

        result.Error.Should().Contain("No canned response");
        await using var ctx = _db.Context();
        var s = await ctx.Sources.SingleAsync();
        s.ConsecutiveFailures.Should().Be(1);
        s.LastError.Should().NotBeNull();
        intake.Interval(s).Should().Be(TimeSpan.FromMinutes(60), "a failure doubles the 30-minute interval");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Poll_WritesItsBookkeeping_WithoutAnAuditRow_ButAnAdministratorsEditIsAudited(string provider)
    {
        _db = _db.On(provider);
        var source = await Source();
        int Trail() { using var ctx = _db.Context(); return ctx.AuditLog.Count(a => a.EntityType == nameof(Tide.Domain.Entities.Source)); }
        var before = Trail();
        var intake = _db.Intake();

        _db.Http.Set(FeedUrl, Rss(("One", "https://example.com/1", null)), etag: "\"v1\"");
        (await intake.PollNowAsync(source)).New.Should().Be(1);
        _db.Http.Responses[FeedUrl] = new HttpFetch(FetchStatus.NotModified);
        (await intake.PollNowAsync(source)).Error.Should().BeNull();
        _db.Http.Responses[FeedUrl] = new HttpFetch(FetchStatus.Failed, Error: "Down.");
        (await intake.PollNowAsync(source)).Error.Should().Be("Down.");
        _db.Http.Throws[FeedUrl] = new InvalidOperationException("Unexpected.");
        (await intake.PollNowAsync(source)).Error.Should().StartWith("The poll failed");

        Trail().Should().Be(before, "a poll is bookkeeping, not a change anyone made");
        await using (var ctx = _db.Context())
        {
            var s = await ctx.Sources.SingleAsync();
            s.Should().Match<Tide.Domain.Entities.Source>(x => x.ETag == "\"v1\"" && x.LastNewItems == 0 && x.ConsecutiveFailures == 2
                && x.LastError!.StartsWith("The poll failed") && x.LastSuccessAtUtc != null && x.LastPolledAtUtc >= x.LastSuccessAtUtc);
        }

        var form = SourceForm.From((await intake.SourcesAsync()).Single());
        form.Name = "Renamed";
        await intake.SaveSourceAsync(form);
        Trail().Should().Be(before + 1);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Poll_DropsAValidatorLongerThanItsColumn_AndStillSucceeds(string provider)
    {
        _db = _db.On(provider);
        // On SQL Server a 201-character ETag would fail the bookkeeping's update after the items were saved.
        var source = await Source();
        var intake = _db.Intake();
        _db.Http.Responses[FeedUrl] = new HttpFetch(FetchStatus.Ok, System.Text.Encoding.UTF8.GetBytes(Rss(("One", "https://example.com/1", null))),
            ETag: "\"" + new string('a', 199) + "\"", LastModified: "Mon, 26 Oct 2026 09:00:00 GMT" + new string(' ', 80));

        (await intake.PollNowAsync(source)).Should().Match<PollResult>(r => r.Error == null && r.New == 1);

        await using (var ctx = _db.Context())
            (await ctx.Sources.SingleAsync()).Should().Match<Tide.Domain.Entities.Source>(s => s.ETag == null && s.LastModified == null && s.ConsecutiveFailures == 0);
        _db.Http.Responses[FeedUrl] = new HttpFetch(FetchStatus.Ok, System.Text.Encoding.UTF8.GetBytes(Rss(("One", "https://example.com/1", null))),
            ETag: "\"" + new string('a', 198) + "\"", LastModified: "Mon, 26 Oct 2026 09:00:00 GMT");
        await intake.PollNowAsync(source);
        _db.Http.Requests.Last().Request.Should().Match<OutboundRequest>(r => r.ETag == null && r.LastModified == null, "the dropped validators aren't sent");
        await using (var ctx = _db.Context())
            (await ctx.Sources.SingleAsync()).Should().Match<Tide.Domain.Entities.Source>(s => s.ETag!.Length == 200 && s.LastModified == "Mon, 26 Oct 2026 09:00:00 GMT");
    }

    [Fact]
    public async Task Poll_ABadFeedIsAnError_NotACrash()
    {
        var source = await Source();
        _db.Http.Set(FeedUrl, "<html>not a feed</html>");
        (await _db.Intake().PollNowAsync(source)).Error.Should().Contain("RSS or Atom");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task PollDue_OnlyWhenIntakeIsOn_AndTheSourceIsDue(string provider)
    {
        _db = _db.On(provider);
        await Source();
        _db.Http.Set(FeedUrl, Rss(("One", "https://example.com/1", null)));

        (await _db.Intake(new IntakeOptions { Enabled = false }).PollDueAsync()).Should().Be(0);
        _db.Http.Requests.Should().BeEmpty("intake is off");

        (await _db.Intake().PollDueAsync()).Should().Be(1);
        (await _db.Intake().PollDueAsync()).Should().Be(0);
        _db.Http.Requests.Should().HaveCount(1, "it was polled a moment ago");
    }

    [Fact]
    public async Task PollDue_ASourceThatBreaksThePoll_IsRecorded_AndTheOthersStillRun()
    {
        // A fault nothing on the way turned into a result.
        var kev = await Source(SourceKind.CisaKev, "https://kev.example.gov/kev.json");
        _db.Http.Throws["https://kev.example.gov/kev.json"] = new InvalidOperationException("Unexpected.");
        await Source();
        _db.Http.Set(FeedUrl, Rss(("One", "https://example.com/1", null)));

        (await _db.Intake().PollDueAsync()).Should().Be(1, "the feed after the broken source is still polled");

        await using var ctx = _db.Context();
        var broken = await ctx.Sources.SingleAsync(s => s.Id == kev);
        broken.LastError.Should().StartWith("The poll failed");
        broken.ConsecutiveFailures.Should().Be(1);
        broken.LastPolledAtUtc.Should().NotBeNull("so it backs off instead of being retried every minute");
        (await _db.Intake().PollDueAsync()).Should().Be(0);

        // A KeyNotFoundException from inside the poll isn't mistaken for the source having been deleted.
        _db.Http.Throws["https://kev.example.gov/kev.json"] = new KeyNotFoundException("a parser's dictionary");
        (await _db.Intake().PollNowAsync(kev)).Error.Should().Contain("a parser's dictionary");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Inbox_FoldsUntouchedItemsIntoOlder_ButNeverAKevOrHighEpssOne(string provider)
    {
        _db = _db.On(provider);
        var source = await Source();
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = _db.Context())
        {
            FeedItem Item(string t, int daysAgo, string? cves = null) => new()
            {
                SourceId = source, Title = t, Url = "https://x.example/" + t.Replace(' ', '-'), UrlHash = t, Cves = cves,
                PublishedAtUtc = now.AddDays(-daysAgo - 30), FetchedAtUtc = now.AddDays(-daysAgo),
            };
            ctx.FeedItems.AddRange(
                Item("fresh story", 1), Item("stale story", 10), Item("stale kev", 10, "CVE-2026-0101"), Item("stale epss", 10, "CVE-2026-0102"),
                Item("stale low epss", 10, "CVE-2026-0103"));
            ctx.CveRecords.AddRange(new CveRecord { CveId = "CVE-2026-0101", IsKev = true }, new CveRecord { CveId = "CVE-2026-0102", EpssScore = 0.42m },
                new CveRecord { CveId = "CVE-2026-0103", EpssScore = 0.01m });
            await ctx.SaveChangesAsync();
        }
        var intake = _db.Intake(new IntakeOptions { Enabled = true, MinPollMinutes = 5, AgeDays = 7 });

        (await intake.InboxAsync(new InboxFilter())).Select(r => r.Item.Title).Should().BeEquivalentTo("fresh story", "stale kev", "stale epss");

        (await intake.InboxAsync(new InboxFilter(Older: true))).Select(r => r.Item.Title).Should().BeEquivalentTo("stale story", "stale low epss");
        (await intake.OlderCountAsync()).Should().Be(2);
        (await intake.CountsAsync())[FeedItemStatus.New].Should().Be(3, "New counts what's still in New");
        (await intake.TallyAsync()).Should().Match<(IReadOnlyDictionary<FeedItemStatus, int> Counts, int Older)>(t => t.Counts[FeedItemStatus.New] == 3 && t.Older == 2,
            "Triage's tabs and its Older fold, from one count");
        (await intake.NewCountAsync()).Should().Be(3, "the sidebar's count too");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Inbox_LeavesOutARetiredSourcesNewItems_AndFiltersSinceAMoment(string provider)
    {
        _db = _db.On(provider);
        var source = await Source();
        await using (var ctx = _db.Context())
        {
            FeedItem Item(string t, DateTimeOffset published, FeedItemStatus status = FeedItemStatus.New) => new()
            {
                SourceId = source, Title = t, Url = "https://x.example/" + t, UrlHash = t, PublishedAtUtc = published, FetchedAtUtc = published, Status = status,
            };
            var now = DateTimeOffset.UtcNow;
            ctx.FeedItems.AddRange(Item("old", now.AddDays(-3)), Item("fresh", now.AddHours(-1)), Item("kept", now.AddDays(-3), FeedItemStatus.Shortlisted));
            await ctx.SaveChangesAsync();
        }
        var intake = _db.Intake();

        (await intake.InboxAsync(new InboxFilter(Since: DateTimeOffset.UtcNow.AddDays(-1)))).Select(r => r.Item.Title).Should().Equal("fresh");
        (await intake.NewCountAsync()).Should().Be(2, "the sidebar's count is the new items, not the shortlisted one");

        await intake.SetSourceRetiredAsync(source, true);
        (await intake.InboxAsync(new InboxFilter())).Should().BeEmpty("a retired source's new items leave the inbox");
        (await intake.CountsAsync())[FeedItemStatus.New].Should().Be(0);
        (await intake.NewCountAsync()).Should().Be(0);
        (await intake.InboxAsync(new InboxFilter(FeedItemStatus.Shortlisted))).Should().ContainSingle("what was kept stays");
    }

    [Fact]
    public async Task LastBriefAt_IsWhenTheLatestMeetingWasHeld()
    {
        (await _db.Intake().LastBriefAtAsync()).Should().BeNull();
        await using (var ctx = _db.Context())
        {
            ctx.Briefs.AddRange(
                new Brief { BriefDate = new(2026, 10, 22), MeetingHeldAtUtc = new DateTimeOffset(2026, 10, 22, 14, 30, 0, TimeSpan.Zero) },
                new Brief { BriefDate = new(2026, 10, 23), PresentedAtUtc = new DateTimeOffset(2026, 10, 23, 14, 45, 0, TimeSpan.Zero) },
                new Brief { BriefDate = new(2026, 10, 26) });
            await ctx.SaveChangesAsync();
        }
        (await _db.Intake().LastBriefAtAsync()).Should().Be(new DateTimeOffset(2026, 10, 23, 14, 45, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task OneStory_ItemsSharingACve_AreGrouped_TheRestDismissed_AndAFollowUpAttached()
    {
        var source = await Source();
        _db.Http.Set(FeedUrl, Rss(
            ("LiteLLM supply chain compromise", "https://a.example/litellm", "CVE-2026-1111 in a popular package."),
            ("LiteLLM maintainers publish an update", "https://b.example/update", "More on CVE-2026-1111."),
            ("PyPI package hijacked (CVE-2026-1111)", "https://c.example/pypi", null),
            ("Unrelated news", "https://d.example/other", null)));
        var intake = _db.Intake();
        await intake.PollNowAsync(source);

        var inbox = await intake.InboxAsync(new InboxFilter());
        var first = inbox.Single(r => r.Item.Url == "https://a.example/litellm");
        first.SameCve.Should().HaveCount(2);
        first.SharedCve.Should().Be("CVE-2026-1111");
        inbox.Single(r => r.Item.Title == "Unrelated news").Should().Match<InboxRow>(r => r.SameCve.Count == 0 && r.SharedCve == null);
        (await intake.InboxAsync(new InboxFilter(Search: first.SharedCve))).Select(r => r.Item.Id).Should()
            .BeEquivalentTo(first.SameCve.Append(first.Item.Id), "searching for the shared CVE shows the items together");

        var articleId = await intake.PromoteAsync(first.Item.Id);
        var update = inbox.Single(r => r.Item.Url == "https://b.example/update").Item;
        await intake.AttachAsync(update.Id, articleId);
        await intake.AttachAsync(update.Id, articleId);   // twice: still one link

        var links = await _db.Articles().LinksAsync(articleId);
        links.Should().ContainSingle().Which.Url.Should().Be("https://b.example/update");
        (await intake.InboxAsync(new InboxFilter(FeedItemStatus.Promoted))).Should().Contain(r => r.Item.Id == update.Id && r.Item.ArticleId == articleId);

        (await intake.DismissSameCveAsync(first.Item.Id, first.SameCve)).Should().Be(1, "only the PyPI item is still new");
        var dismissed = (await intake.InboxAsync(new InboxFilter(FeedItemStatus.Dismissed))).Single().Item;
        dismissed.Url.Should().Be("https://c.example/pypi");
        dismissed.DismissReason.Should().Be("Already covered");
        (await intake.InboxAsync(new InboxFilter())).Select(r => r.Item.Title).Should().Equal("Unrelated news");

        await _db.Articles().RemoveLinkAsync(links[0].Id);
        (await _db.Articles().LinksAsync(articleId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Promote_ByTwoAnalystsAtOnce_MakesOneArticle()
    {
        var source = await Source();
        _db.Http.Set(FeedUrl, Rss(("Vendor fixes exploited bug", "https://example.com/fix", null)));
        await _db.Intake().PollNowAsync(source);
        var item = (await _db.Intake().InboxAsync(new InboxFilter())).Single().Item;

        var results = await Task.WhenAll(Try(), Try());

        await using var ctx = _db.Context();
        (await ctx.Articles.CountAsync()).Should().Be(1);
        var made = (await ctx.Articles.SingleAsync()).Id;
        results.Should().OnlyContain(r => r == made || r == null, "the other analyst is sent to it, or told it's being promoted");
        (await ctx.FeedItems.SingleAsync()).ArticleId.Should().Be(made);

        async Task<Guid?> Try()
        {
            try { return await _db.Intake().PromoteAsync(item.Id); }
            catch (InvalidOperationException) { return null; }
        }
    }

    [Fact]
    public async Task Items_AreAsTheyAreNow_WithWhoSetThem_AndNoneForNoIds()
    {
        var source = await Source();
        _db.Http.Set(FeedUrl, Rss(("Vendor fixes exploited bug", "https://example.com/fix", null), ("Another story", "https://example.com/other", null)));
        await _db.Intake().PollNowAsync(source);
        var inbox = await _db.Intake().InboxAsync(new InboxFilter());
        var dismissed = inbox[0].Item.Id;
        await _db.Intake().DismissAsync(dismissed, "Out of scope");

        var now = await _db.Intake().ItemsAsync([dismissed, Guid.NewGuid()]);

        now.Should().ContainSingle().Which.Should().Match<FeedItem>(i => i.Status == FeedItemStatus.Dismissed && i.StatusByName == "Test Analyst");
        (await _db.Intake().ItemsAsync([])).Should().BeEmpty();
    }

    [Fact]
    public async Task Promote_ThatFailsAfterTheArticleIsMade_LeavesNoArticle_AndTheItemCanBePromotedAgain()
    {
        var source = await Source();
        _db.Http.Set(FeedUrl, Rss(("Vendor fixes exploited bug", "https://example.com/fix", "Fixes CVE-2026-0002.")));
        await _db.Intake().PollNowAsync(source);
        var item = (await _db.Intake().InboxAsync(new InboxFilter())).Single().Item;
        _db.BeforeSave = context =>
        {
            if (context.ChangeTracker.Entries<ArticleCve>().Any(e => e.State == EntityState.Added)) throw new DbUpdateException("database busy");
        };

        var failed = () => _db.Intake().PromoteAsync(item.Id);
        await failed.Should().ThrowAsync<DbUpdateException>();

        await using (var ctx = _db.Context())
        {
            (await ctx.Articles.CountAsync()).Should().Be(0, "the half-made article is removed");
            var row = await ctx.FeedItems.SingleAsync();
            row.Status.Should().Be(FeedItemStatus.New, "the item is given back, not stuck being promoted");
            row.ArticleId.Should().BeNull();
        }
        _db.BeforeSave = null;
        var articleId = await _db.Intake().PromoteAsync(item.Id);
        (await _db.Articles().GetAsync(articleId)).Should().NotBeNull();
    }

    [Fact]
    public async Task Promote_TakesOverAClaimThatNeverFinished_AndAttachRefusesAnItemBeingPromoted()
    {
        var source = await Source();
        _db.Http.Set(FeedUrl, Rss(("Vendor fixes exploited bug", "https://example.com/fix", null)));
        await _db.Intake().PollNowAsync(source);
        var item = (await _db.Intake().InboxAsync(new InboxFilter())).Single().Item;
        var other = await _db.Articles().CreateAsync("Earlier article");
        await using (var ctx = _db.Context())
        {
            var row = await ctx.FeedItems.SingleAsync();
            row.Status = FeedItemStatus.Promoted;   // claimed, then the server stopped
            row.StatusByName = "Robin";
            row.StatusAtUtc = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
        }

        var attach = () => _db.Intake().AttachAsync(item.Id, other);
        await attach.Should().ThrowAsync<InvalidOperationException>().WithMessage("*being promoted*");
        var tooSoon = () => _db.Intake().PromoteAsync(item.Id);
        await tooSoon.Should().ThrowAsync<InvalidOperationException>().WithMessage("Robin is promoting*");

        await using (var ctx = _db.Context())
        {
            (await ctx.FeedItems.SingleAsync()).StatusAtUtc = DateTimeOffset.UtcNow - IntakeService.ClaimTimeout - TimeSpan.FromMinutes(1);
            await ctx.SaveChangesAsync();
        }
        var articleId = await _db.Intake().PromoteAsync(item.Id);
        await using var check = _db.Context();
        (await check.FeedItems.SingleAsync()).ArticleId.Should().Be(articleId);
    }

    // Three reports of the Binance theft from two feeds (the second at AMBER), newest first, and one unrelated item.
    private async Task<(List<Guid> Reports, Guid Other)> BinanceReports()
    {
        var clear = await Source();
        var amber = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "Amber feed", Url = "https://amber.example/feed", DefaultTlp = TlpLevel.Amber, PollMinutes = 30 });
        var now = DateTimeOffset.UtcNow;
        FeedItem Item(Guid source, string title, string url, string? cves, int minutesAgo) => new()
        {
            SourceId = source, Title = title, Url = url, UrlHash = url, Cves = cves, Excerpt = "Excerpt of " + title,
            PublishedAtUtc = now.AddMinutes(-minutesAgo), FetchedAtUtc = now.AddMinutes(-minutesAgo),
        };
        var items = new[]
        {
            Item(clear, "Binance links $6.3 billion crypto theft to compromised DevOps laptop", "https://a.example/devops", "CVE-2026-4001", 10),
            Item(amber, "Binance hit by record $6.3 billion crypto hack", "https://b.example/record", "CVE-2026-4002,CVE-2026-4001", 60),
            Item(clear, "Binance probes potential $5.8 billion crypto-theft hack", "https://c.example/probe", null, 120),
        };
        var other = Item(amber, "Browser vendor ships a fix", "https://d.example/other", null, 5);
        await using var ctx = _db.Context();
        ctx.FeedItems.AddRange(items);
        ctx.FeedItems.Add(other);
        await ctx.SaveChangesAsync();
        return (items.Select(i => i.Id).ToList(), other.Id);
    }

    [Fact]
    public async Task Inbox_SuggestsAGroup_ForReportsThatLookLikeOneEvent_InNewAndShortlistedOnly()
    {
        var (reports, other) = await BinanceReports();
        var inbox = await _db.Intake().InboxAsync(new InboxFilter());

        var group = inbox.Single(r => r.Item.Id == reports[0]).Group!;
        group.Ids.Should().Equal(reports, "newest first");
        group.Sources.Should().Be(2);
        group.Cves.Should().Equal("CVE-2026-4001", "CVE-2026-4002");
        inbox.Where(r => reports.Contains(r.Item.Id)).Should().OnlyContain(r => r.Group == group, "each member's row carries the same group");
        inbox.Single(r => r.Item.Id == other).Group.Should().BeNull();

        await using (var ctx = _db.Context())
        {
            await ctx.FeedItems.Where(i => reports.Contains(i.Id)).ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, FeedItemStatus.Dismissed));
        }
        (await _db.Intake().InboxAsync(new InboxFilter(FeedItemStatus.Dismissed))).Should().OnlyContain(r => r.Group == null, "only items still to decide are grouped");
    }

    [Fact]
    public async Task PromoteAsOne_ClaimsEveryReport_AndTheArticleHasEveryLinkAndCve()
    {
        var (reports, other) = await BinanceReports();
        var intake = _db.Intake();

        var articleId = await intake.PromoteAsOneAsync(reports, new PromoteForm { Classification = Classification.Threat, Priority = Priority.P1 });

        var article = (await _db.Articles().GetAsync(articleId))!;
        article.Title.Should().Be("Binance links $6.3 billion crypto theft to compromised DevOps laptop", "the newest report makes the article");
        article.SourceUrl.Should().Be("https://a.example/devops");
        article.Tlp.Should().Be(TlpLevel.Amber, "the most restrictive of the sources' TLPs");
        article.Priority.Should().Be(Priority.P1);
        await using (var ctx = _db.Context())
        {
            (await ctx.ArticleCves.Where(c => c.ArticleId == articleId).Select(c => c.CveId).ToListAsync())
                .Should().BeEquivalentTo("CVE-2026-4001", "CVE-2026-4002");
            var rows = await ctx.FeedItems.Where(i => reports.Contains(i.Id)).ToListAsync();
            rows.Should().HaveCount(3).And.OnlyContain(i => i.Status == FeedItemStatus.Promoted && i.ArticleId == articleId && i.StatusByName != null);
            (await ctx.FeedItems.SingleAsync(i => i.Id == other)).Status.Should().Be(FeedItemStatus.New, "only the group's reports are claimed");
        }
        var links = await _db.Articles().LinksAsync(articleId);
        links.Select(l => l.Url).Should().Equal("https://b.example/record", "https://c.example/probe");
        links.Select(l => l.SourceName).Should().Equal("Amber feed", "News Feed");
        (await _db.Articles().SourceExcerptAsync(articleId)).Should().Be("Excerpt of " + article.Title, "the editor shows the excerpt of the report it was made from");

        (await intake.PromoteAsOneAsync(reports, new PromoteForm())).Should().Be(articleId, "promoting them again goes to the article they made");
    }

    [Fact]
    public async Task PromoteAsOne_ByTwoAnalystsAtOnce_MakesOneArticle()
    {
        var (reports, _) = await BinanceReports();

        var results = await Task.WhenAll(Try(), Try());

        await using var ctx = _db.Context();
        (await ctx.Articles.CountAsync()).Should().Be(1);
        var made = (await ctx.Articles.SingleAsync()).Id;
        results.Should().OnlyContain(r => r == made || r == null, "the other analyst is sent to it, or told");
        (await ctx.FeedItems.Where(i => reports.Contains(i.Id)).ToListAsync()).Should().OnlyContain(i => i.ArticleId == made);
        (await ctx.ArticleLinks.CountAsync(l => l.ArticleId == made)).Should().Be(2, "each other report once");

        async Task<Guid?> Try()
        {
            try { return await _db.Intake().PromoteAsOneAsync(reports, new PromoteForm()); }
            catch (InvalidOperationException) { return null; }
        }
    }

    [Fact]
    public async Task PromoteAsOne_ClaimsNoneOfThem_WhenOneIsAlreadyTaken()
    {
        var (reports, _) = await BinanceReports();
        var intake = _db.Intake();
        var single = await intake.PromoteAsync(reports[1]);   // another analyst got to one report first

        var group = () => intake.PromoteAsOneAsync(reports, new PromoteForm());
        await group.Should().ThrowAsync<InvalidOperationException>().WithMessage("*none of them was promoted*");

        await using var ctx = _db.Context();
        (await ctx.Articles.Select(a => a.Id).ToListAsync()).Should().Equal(single);
        var rows = await ctx.FeedItems.Where(i => reports.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        rows[reports[0]].Status.Should().Be(FeedItemStatus.New, "the part claim was given back");
        rows[reports[0]].StatusByName.Should().BeNull();
        rows[reports[2]].Status.Should().Be(FeedItemStatus.New);
        rows[reports[1]].ArticleId.Should().Be(single);
    }

    [Fact]
    public async Task PromoteAsOne_ThatFailsPartWay_GivesEveryReportBack_AndLeavesNoArticle()
    {
        var (reports, _) = await BinanceReports();
        _db.BeforeSave = context =>
        {
            if (context.ChangeTracker.Entries<ArticleLink>().Any(e => e.State == EntityState.Added)) throw new DbUpdateException("database busy");
        };

        var failed = () => _db.Intake().PromoteAsOneAsync(reports, new PromoteForm());
        await failed.Should().ThrowAsync<DbUpdateException>();
        _db.BeforeSave = null;

        await using var ctx = _db.Context();
        (await ctx.Articles.CountAsync()).Should().Be(0);
        (await ctx.FeedItems.Where(i => reports.Contains(i.Id)).ToListAsync())
            .Should().OnlyContain(i => i.Status == FeedItemStatus.New && i.ArticleId == null && i.StatusByName == null);
    }

    [Fact]
    public async Task PromoteAsOne_TakesTwoToEightReports_AndRefusesOneGone()
    {
        var (reports, _) = await BinanceReports();
        var intake = _db.Intake();
        var one = () => intake.PromoteAsOneAsync([reports[0], reports[0]], new PromoteForm());
        await one.Should().ThrowAsync<ArgumentException>().WithMessage("Promote 2 to 8*");
        var gone = () => intake.PromoteAsOneAsync([reports[0], Guid.NewGuid()], new PromoteForm());
        await gone.Should().ThrowAsync<KeyNotFoundException>().WithMessage("One of those items is gone.");
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var reader = () => intake.PromoteAsOneAsync(reports, new PromoteForm());
        await reader.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task KevSource_TurnsNewlyListedCvesIntoItems()
    {
        var source = await Source(SourceKind.CisaKev, "https://kev.example.gov/kev.json");
        _db.Http.Set("https://kev.example.gov/kev.json", """
            {"vulnerabilities":[{"cveID":"CVE-2026-0009","vendorProject":"Acme","product":"Router","vulnerabilityName":"Command injection","dateAdded":"2026-10-25","dueDate":"2026-11-15"}]}
            """);
        (await _db.Intake().PollNowAsync(source)).New.Should().Be(1);
        var item = (await _db.Intake().InboxAsync(new InboxFilter())).Single().Item;
        item.Title.Should().StartWith("CVE-2026-0009: Acme Router");
        item.CveList.Should().Equal("CVE-2026-0009");
    }

    [Fact]
    public async Task Inbox_SaysWhenACveWasAlreadyBriefed()
    {
        var articles = _db.Articles();
        var id = await articles.CreateAsync("Earlier VPN article");
        var form = ArticleForm.From((await articles.GetAsync(id))!);
        form.Cves = ["CVE-2026-0001"];
        await articles.SaveAsync(form);
        var brief = await _db.Briefs().GetOrStartAsync(new DateOnly(2026, 10, 14));
        await _db.Briefs().AddArticleAsync(brief.Id, id);

        var source = await Source();
        _db.Http.Set(FeedUrl, Rss(("Follow-up on the VPN bug", "https://example.com/followup", "More on CVE-2026-0001.")));
        await _db.Intake().PollNowAsync(source);

        var coverage = (await _db.Intake().InboxAsync(new InboxFilter())).Single().Coverage.Single();
        coverage.Why.Should().Be("CVE-2026-0001");
        coverage.ArticleTitle.Should().Be("Earlier VPN article");
        coverage.BriefDate.Should().Be(new DateOnly(2026, 10, 14));
    }

    [Fact]
    public async Task Triage_MovesItemsBetweenStates_AndRefusesReaders()
    {
        var source = await Source();
        _db.Http.Set(FeedUrl, Rss(("A one", "https://example.com/1", null), ("B two", "https://example.com/2", null), ("C three", "https://example.com/3", null)));
        var intake = _db.Intake();
        await intake.PollNowAsync(source);
        var ids = (await intake.InboxAsync(new InboxFilter())).Select(r => r.Item.Id).ToList();

        await intake.ShortlistAsync(ids[0]);
        await intake.DismissAsync(ids[1], "Out of scope");
        await intake.MarkDuplicateAsync(ids[2], ids[0]);

        (await intake.CountsAsync())[FeedItemStatus.New].Should().Be(0);
        var dismissed = (await intake.InboxAsync(new InboxFilter(FeedItemStatus.Dismissed))).Single().Item;
        dismissed.DismissReason.Should().Be("Out of scope");
        dismissed.StatusByName.Should().Be(_db.User.DisplayName);
        await intake.RestoreAsync(ids[1]);
        (await intake.CountsAsync())[FeedItemStatus.New].Should().Be(1);

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        var owner = () => _db.Intake().InboxAsync(new InboxFilter());
        await owner.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Promote_MakesADraftArticle_WithTheSourcesTlpAndTheItemsCves_Once()
    {
        var source = await Source(tlp: TlpLevel.Green);
        _db.Http.Set(FeedUrl, Rss(("Vendor fixes exploited bug", "https://example.com/fix?utm_source=rss", "Fixes CVE-2026-0002, exploited in the wild.")));
        var intake = _db.Intake();
        await intake.PollNowAsync(source);
        var item = (await intake.InboxAsync(new InboxFilter())).Single().Item;

        var articleId = await intake.PromoteAsync(item.Id);
        (await intake.PromoteAsync(item.Id)).Should().Be(articleId, "promoting twice gives the same article");

        var a = (await _db.Articles().GetAsync(articleId))!;
        a.Title.Should().Be("Vendor fixes exploited bug");
        a.SourceName.Should().Be("News Feed");
        a.SourceUrl.Should().Be("https://example.com/fix");
        a.Tlp.Should().Be(TlpLevel.Green);
        a.Bluf.Should().BeEmpty("the publisher's excerpt isn't the analyst's bottom line");
        (await _db.Articles().SourceExcerptAsync(articleId)).Should().Be("Fixes CVE-2026-0002, exploited in the wild.", "it's shown beside it to rewrite");
        a.Cves.Select(c => c.CveId).Should().Equal("CVE-2026-0002");
        a.Status.Should().Be(ArticleStatus.Draft);
        (await intake.InboxAsync(new InboxFilter(FeedItemStatus.Promoted))).Single().Item.ArticleId.Should().Be(articleId);
        var dismiss = () => intake.DismissAsync(item.Id, null);
        await dismiss.Should().ThrowAsync<InvalidOperationException>();
    }

    // One new item from a source marked tlp, polled in.
    private async Task<FeedItem> OneItem(TlpLevel tlp = TlpLevel.Clear)
    {
        var source = await Source(tlp: tlp);
        _db.Http.Set(FeedUrl, Rss(("Vendor fixes exploited bug", "https://example.com/fix?utm_source=rss", "Fixes CVE-2026-0002, exploited in the wild.")));
        await _db.Intake().PollNowAsync(source);
        return (await _db.Intake().InboxAsync(new InboxFilter())).Single().Item;
    }

    [Fact]
    public async Task PromoteWithAForm_MakesTheDraftWithEachChosenField_AndKeepsTheItemsSourceLinkAndCves()
    {
        var item = await OneItem(TlpLevel.Green);

        var articleId = await _db.Intake().PromoteAsync(item.Id, new PromoteForm
        {
            Title = "  Edge VPN zero-day: patch now  ", Classification = Classification.Threat, Priority = Priority.P1, Tlp = TlpLevel.Amber,
        });

        var a = (await _db.Articles().GetAsync(articleId))!;
        a.Title.Should().Be("Edge VPN zero-day: patch now", "the title is tidied as a save tidies it");
        a.Classification.Should().Be(Classification.Threat);
        a.Priority.Should().Be(Priority.P1);
        a.Tlp.Should().Be(TlpLevel.Amber, "the analyst's marking, not the source's");
        a.SourceName.Should().Be("News Feed");
        a.SourceUrl.Should().Be("https://example.com/fix");
        a.Cves.Select(c => c.CveId).Should().Equal("CVE-2026-0002");
        a.Status.Should().Be(ArticleStatus.Draft);
        await using var ctx = _db.Context();
        var row = await ctx.FeedItems.SingleAsync();
        row.Status.Should().Be(FeedItemStatus.Promoted);
        row.ArticleId.Should().Be(articleId);
        (await ctx.AuditLog.AnyAsync(e => e.EntityType == "Article" && e.Action == "Added" && e.EntityKey == articleId.ToString()))
            .Should().BeTrue("making the article is in the audit log");
    }

    [Theory]
    [InlineData(Classification.Threat, TlpLevel.Green, TlpLevel.Amber)]
    [InlineData(Classification.Threat, TlpLevel.Red, TlpLevel.Red)]
    [InlineData(Classification.Informational, TlpLevel.Green, TlpLevel.Clear)]
    public async Task Promote_AsAThreat_StartsWhyItMattersAtAmber_OrTheArticlesTlp_AndInformationalKeepsClear(
        Classification classification, TlpLevel tlp, TlpLevel expected)
    {
        var item = await OneItem();

        var articleId = await _db.Intake().PromoteAsync(item.Id, new PromoteForm { Classification = classification, Priority = Priority.P2, Tlp = tlp });

        (await _db.Articles().GetAsync(articleId))!.WhyItMattersTlp.Should().Be(expected);
    }

    [Fact]
    public async Task PromoteWithAForm_TakesTheItemsOwnForWhatsLeftEmpty_AndAnInformationalArticleKeepsNoPriority()
    {
        var item = await OneItem(TlpLevel.AmberStrict);

        var articleId = await _db.Intake().PromoteAsync(item.Id, new PromoteForm { Classification = Classification.Informational, Priority = Priority.P2 });

        var a = (await _db.Articles().GetAsync(articleId))!;
        a.Title.Should().Be("Vendor fixes exploited bug");
        a.Tlp.Should().Be(TlpLevel.AmberStrict, "the source's TLP when none is chosen");
        a.Classification.Should().Be(Classification.Informational);
        a.Priority.Should().BeNull("a priority is for a threat, as the editor saves it");
    }

    [Fact]
    public async Task Promote_WithoutAForm_IsAnInformationalDraft_AtTheSourcesTlp()
    {
        var item = await OneItem(TlpLevel.Green);

        var articleId = await _db.Intake().PromoteAsync(item.Id);

        var a = (await _db.Articles().GetAsync(articleId))!;
        a.Title.Should().Be("Vendor fixes exploited bug");
        a.Classification.Should().Be(Classification.Informational);
        a.Priority.Should().BeNull();
        a.Tlp.Should().Be(TlpLevel.Green);
        a.Cves.Select(c => c.CveId).Should().Equal("CVE-2026-0002");
    }

    [Theory]
    [InlineData("   ", "An article needs a title.")]
    [InlineData(null, "*300 characters*")]
    public async Task PromoteWithAForm_RefusesATitleASaveWouldRefuse_BeforeClaimingTheItem(string? title, string message)
    {
        var item = await OneItem();

        var promote = () => _db.Intake().PromoteAsync(item.Id, new PromoteForm { Title = title ?? new string('a', ArticleService.MaxTitle + 1) });

        await promote.Should().ThrowAsync<ArgumentException>().WithMessage(message);
        await using var ctx = _db.Context();
        (await ctx.Articles.CountAsync()).Should().Be(0);
        (await ctx.FeedItems.SingleAsync()).Status.Should().Be(FeedItemStatus.New, "a refused form never claims the item");
    }

    [Fact]
    public async Task PromoteWithAForm_RefusesValuesThatArentMarkings()
    {
        var item = await OneItem();
        var intake = _db.Intake();

        var kind = () => intake.PromoteAsync(item.Id, new PromoteForm { Classification = (Classification)7 });
        var priority = () => intake.PromoteAsync(item.Id, new PromoteForm { Classification = Classification.Threat, Priority = (Priority)9 });
        var tlp = () => intake.PromoteAsync(item.Id, new PromoteForm { Tlp = (TlpLevel)9 });

        await kind.Should().ThrowAsync<ArgumentException>();
        await priority.Should().ThrowAsync<ArgumentException>();
        await tlp.Should().ThrowAsync<ArgumentException>();
        await using var ctx = _db.Context();
        (await ctx.FeedItems.SingleAsync()).Status.Should().Be(FeedItemStatus.New);
    }

    [Fact]
    public async Task PromoteWithAForm_ByTwoAnalystsAtOnce_MakesOneArticle_AndTheOtherIsSentToIt()
    {
        var item = await OneItem();
        static PromoteForm Form(string title) => new() { Title = title, Classification = Classification.Threat, Priority = Priority.P2 };

        var results = await Task.WhenAll(Try("First analyst's title"), Try("Second analyst's title"));

        await using var ctx = _db.Context();
        var made = await ctx.Articles.SingleAsync();
        made.Title.Should().BeOneOf("First analyst's title", "Second analyst's title");
        results.Should().OnlyContain(r => r == made.Id || r == null, "the other analyst is sent to it, or told it's being promoted");
        (await ctx.FeedItems.SingleAsync()).ArticleId.Should().Be(made.Id);
        (await _db.Intake().PromoteAsync(item.Id, Form("Later"))).Should().Be(made.Id, "an item already promoted gives its article");
        (await _db.Articles().GetAsync(made.Id))!.Title.Should().Be(made.Title, "and the later form changes nothing");

        async Task<Guid?> Try(string title)
        {
            try { return await _db.Intake().PromoteAsync(item.Id, Form(title)); }
            catch (InvalidOperationException) { return null; }
        }
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Prune_RemovesOldUnkeptItems_AndKeepsShortlistedAndPromoted(string provider)
    {
        _db = _db.On(provider);
        var source = await Source();
        await using (var ctx = _db.Context())
        {
            var old = DateTimeOffset.UtcNow.AddDays(-200);
            FeedItem Item(string t, FeedItemStatus s) => new() { SourceId = source, Title = t, Url = "https://x.example/" + t, UrlHash = t, FetchedAtUtc = old, Status = s };
            ctx.FeedItems.AddRange(Item("new", FeedItemStatus.New), Item("dismissed", FeedItemStatus.Dismissed), Item("kept", FeedItemStatus.Shortlisted));
            await ctx.SaveChangesAsync();
        }
        (await _db.Intake().PruneAsync()).Should().Be(2);
        await using var check = _db.Context();
        (await check.FeedItems.SingleAsync()).Title.Should().Be("kept");
    }

    [Fact]
    public async Task Prune_ADismissedItemTheFeedStillLists_DoesntComeBackAsNew()
    {
        const string url = "https://kev.example.gov/kev.json";
        var source = await Source(SourceKind.CisaKev, url);
        _db.Http.Set(url, """{"vulnerabilities":[{"cveID":"CVE-2026-0007","dateAdded":"2026-03-02"}]}""");
        var added = new DateTimeOffset(2026, 3, 3, 12, 0, 0, TimeSpan.Zero);
        var later = added.AddDays(100);   // past the 90-day retention

        (await _db.Intake(time: new TestClock(added)).PollNowAsync(source)).New.Should().Be(1);
        var item = (await _db.Intake(time: new TestClock(added)).InboxAsync(new InboxFilter())).Single().Item;   // read the day it came in, before it ages
        await _db.Intake().DismissAsync(item.Id, null);
        (await _db.Intake(time: new TestClock(later)).PruneAsync()).Should().Be(1);

        _db.Http.Set(url, """{"vulnerabilities":[{"cveID":"CVE-2026-0007","dateAdded":"2026-03-02"}]}""", etag: "\"changed\"");
        (await _db.Intake(time: new TestClock(later)).PollNowAsync(source)).New.Should().Be(0, "it's older than the retention period");
        (await _db.Intake().InboxAsync(new InboxFilter())).Should().BeEmpty();
    }

    [Fact]
    public async Task Sources_AreValidated_AndForAdministrators()
    {
        var tooOften = () => _db.Intake(new IntakeOptions { MinPollMinutes = 15 }).SaveSourceAsync(new SourceForm { Name = "x", Url = FeedUrl, PollMinutes = 1 });
        await tooOften.Should().ThrowAsync<ArgumentException>().WithMessage("*15 minutes*");
        var notWeb = () => _db.Intake().SaveSourceAsync(new SourceForm { Name = "x", Url = "file:///etc/passwd", PollMinutes = 60 });
        await notWeb.Should().ThrowAsync<ArgumentException>();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        var analyst = () => _db.Intake().SaveSourceAsync(new SourceForm { Name = "x", Url = FeedUrl, PollMinutes = 60 });
        await analyst.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ATagFilter_TakesInOnlyMatchingItems_RecordsWhatItSaw_AndAChangedFilterRereadsTheFeed()
    {
        string Tagged(string title, string link, params string[] tags) =>
            $"<item><title>{title}</title><link>{link}</link>{string.Concat(tags.Select(t => $"<category><![CDATA[ {t} ]]></category>"))}</item>";
        var feed = "<rss version=\"2.0\"><channel>" + Tagged("Ransomware hits a hospital", "https://e.com/1", "Security") +
                   Tagged("New phone launched", "https://e.com/2", "Technology") + Tagged("Patch now", "https://e.com/3", "security", "Microsoft") +
                   Tagged("50% off VPNs", "https://e.com/4", "Security", "Deals") + Tagged("Untagged", "https://e.com/5") + "</channel></rss>";
        _db.Http.Set(FeedUrl, feed, etag: "\"v1\"");
        var intake = _db.Intake();
        var id = await intake.SaveSourceAsync(new SourceForm
        {
            Name = "Bleeping", Url = FeedUrl, PollMinutes = 30, OnlyCategories = "Security", SkipCategories = "Deals",
        });

        var result = await intake.PollNowAsync(id);

        result.Should().BeEquivalentTo(new { New = 2, Seen = 5, FilteredOut = 3 });
        (await intake.InboxAsync(new InboxFilter())).Select(r => r.Item.Title).Should().BeEquivalentTo("Ransomware hits a hospital", "Patch now");
        var source = (await intake.SourcesAsync()).Single();
        source.LastFilteredOut.Should().Be(3);
        CategoryFilter.Parse(source.SeenCategories).Should().Equal("Security", "Deals", "Microsoft", "Technology");

        var form = SourceForm.From(source);
        form.OnlyCategories.Should().Be("Security");
        form.OnlyCategories = "Security, Technology";
        await intake.SaveSourceAsync(form);
        (await intake.SourcesAsync()).Single().ETag.Should().BeNull("a changed filter reads the whole feed again");
        (await intake.PollNowAsync(id)).New.Should().Be(1, "the item the old filter left out comes in now");
    }

    [Fact]
    public async Task ATitleFilter_WorksAlongsideTheTagFilter()
    {
        _db.Http.Set(FeedUrl, Rss(
            ("Ransomware gang hits a hospital", "https://e.com/1", null),
            ("New phone launched", "https://e.com/2", null),
            ("Chrome zero-day exploited", "https://e.com/3", null),
            ("Ransomware webinar next week", "https://e.com/4", null)));
        var intake = _db.Intake();
        var id = await intake.SaveSourceAsync(new SourceForm
        {
            Name = "Untagged news", Url = FeedUrl, PollMinutes = 30, OnlyTitleWords = "ransomware, Zero-Day", SkipTitleWords = "webinar",
        });

        (await intake.PollNowAsync(id)).Should().BeEquivalentTo(new { New = 2, FilteredOut = 2 });
        (await intake.InboxAsync(new InboxFilter())).Select(r => r.Item.Title).Should().BeEquivalentTo("Ransomware gang hits a hospital", "Chrome zero-day exploited");
        SourceForm.From((await intake.SourcesAsync()).Single()).OnlyTitleWords.Should().Be("ransomware, Zero-Day");
    }

    [Fact]
    public async Task TheKevCatalog_TakesNoFilter()
    {
        var id = await _db.Intake().SaveSourceAsync(new SourceForm
        {
            Name = "KEV", Kind = SourceKind.CisaKev, Url = "https://kev.example.org/kev.json", PollMinutes = 30, OnlyCategories = "Security",
            OnlyTitleWords = "Microsoft",
        });
        var kev = (await _db.Intake().SourcesAsync()).Single(s => s.Id == id);
        kev.OnlyCategories.Should().BeNull();
        kev.OnlyTitleWords.Should().BeNull();
    }

    [Fact]
    public async Task AttachTo_AnyRecentDraft_CopiesTheItemsCves_AndBumpsTheEditVersion()
    {
        var source = await Source();
        _db.Http.Set(FeedUrl, Rss(("Binance hot wallet drained (CVE-2026-3333, CVE-2026-3334)", "https://news.example/binance", null)));
        await _db.Intake().PollNowAsync(source);
        var item = (await _db.Intake().InboxAsync(new InboxFilter())).Single().Item;
        var draft = await _db.Articles().CreateAsync("Binance theft", "Vendor", "https://first.example/binance");
        var form = ArticleForm.From((await _db.Articles().GetAsync(draft))!);
        form.Cves = ["CVE-2026-3333"];
        await _db.Articles().SaveAsync(form);
        var before = (await _db.Articles().GetAsync(draft))!.EditVersion;

        (await _db.Intake().AttachTargetsAsync(_db.Clock.CurrentBriefDate)).Select(t => t.Id).Should().Contain(draft, "any recent draft, not only coverage");
        await _db.Intake().AttachAsync(item.Id, draft);

        var article = (await _db.Articles().GetAsync(draft))!;
        article.Cves.Select(c => c.CveId).Should().BeEquivalentTo(["CVE-2026-3333", "CVE-2026-3334"], "the missing CVE is added once");
        article.EditVersion.Should().NotBe(before, "an editor open on it now has a stale copy of the CVEs");
        (await _db.Articles().LinksAsync(draft)).Should().ContainSingle().Which.Url.Should().Be("https://news.example/binance");
    }

    [Fact]
    public async Task Attach_RefusesAPresentedOrSentArticle_UnlessAskedOutright_AndThePickerLeavesThemOut()
    {
        var source = await Source();
        _db.Http.Set(FeedUrl, Rss(("Follow-up one", "https://news.example/one", null), ("Follow-up two", "https://news.example/two", null)));
        await _db.Intake().PollNowAsync(source);
        var items = (await _db.Intake().InboxAsync(new InboxFilter())).Select(r => r.Item).ToList();
        var presented = await _db.Articles().CreateAsync("Presented article");
        var sent = await _db.Articles().CreateAsync("Sent article");
        var today = await _db.Articles().CreateAsync("On today's brief");
        var old = await _db.Articles().CreateAsync("An old draft");
        await using (var ctx = _db.Context())
        {
            (await ctx.Articles.SingleAsync(a => a.Id == presented)).Status = ArticleStatus.Presented;
            (await ctx.Articles.SingleAsync(a => a.Id == old)).CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-60);
            (await ctx.Articles.SingleAsync(a => a.Id == today)).CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-60);
            var gone = new Brief { BriefDate = new DateOnly(2026, 10, 1), AnalystName = "Robin", Status = BriefStatus.Sent };
            gone.Items.Add(new BriefItem { ArticleId = sent });
            var current = new Brief { BriefDate = _db.Clock.CurrentBriefDate, AnalystName = "Robin" };
            current.Items.Add(new BriefItem { ArticleId = today });
            ctx.Briefs.AddRange(gone, current);
            await ctx.SaveChangesAsync();
        }

        var targets = await _db.Intake().AttachTargetsAsync(_db.Clock.CurrentBriefDate);
        targets.Select(t => t.Id).Should().Contain(today).And.NotContain([presented, sent, old], "an old draft isn't recent; presented and sent are frozen");
        targets.Single(t => t.Id == today).OnBrief.Should().BeTrue();

        foreach (var frozen in new[] { presented, sent })
        {
            var attach = () => _db.Intake().AttachAsync(items[0].Id, frozen);
            await attach.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already been presented*");
        }
        await _db.Intake().AttachAsync(items[0].Id, presented, evenIfPresented: true);
        (await _db.Articles().LinksAsync(presented)).Should().ContainSingle();
    }

    [Fact]
    public async Task Coverage_CarriesTheOpenActions_AndWhetherItCanStillTakeAReport()
    {
        var source = await Source();
        _db.Http.Set(FeedUrl, Rss(("Fortinet bug exploited", "https://news.example/forti", "CVE-2026-4444"), ("Fortinet bug, day two", "https://news.example/forti2", "CVE-2026-4444")));
        await _db.Intake().PollNowAsync(source);
        var first = (await _db.Intake().InboxAsync(new InboxFilter())).Single(r => r.Item.Url == "https://news.example/forti").Item;
        var article = await _db.Intake().PromoteAsync(first.Id);
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var open = await _db.Actions().CreateAsync(new ActionForm { Title = "Patch", Kind = ActionKind.PatchRemediate, TeamId = team, ArticleId = article });
        var closed = await _db.Actions().CreateAsync(new ActionForm { Title = "Block", Kind = ActionKind.Block, TeamId = team, ArticleId = article });
        await using (var ctx = _db.Context())
        {
            (await ctx.Actions.SingleAsync(a => a.Number == closed)).Status = ActionStatus.Done;
            await ctx.SaveChangesAsync();
        }

        var covered = (await _db.Intake().InboxAsync(new InboxFilter())).Single().Coverage.Single();
        (covered.ArticleId, covered.Attachable).Should().Be((article, true));
        covered.OpenActions.Should().Equal(open);

        await using (var ctx = _db.Context())
        {
            (await ctx.Articles.SingleAsync(a => a.Id == article)).Status = ArticleStatus.Presented;
            await ctx.SaveChangesAsync();
        }
        (await _db.Intake().InboxAsync(new InboxFilter())).Single().Coverage.Single().Attachable.Should().BeFalse();
    }

    [Fact]
    public async Task AddLink_TakesAWebAddress_AndRefusesOthersAndRepeats()
    {
        var id = await _db.Articles().CreateAsync("Okta session theft", "Vendor", "https://vendor.example/okta");

        var link = await _db.Articles().AddLinkAsync(id, " https://other.example/okta-report ", "  Other Outlet ");
        (link.Url, link.Title, link.SourceName, link.AddedByName).Should().Be(("https://other.example/okta-report", "https://other.example/okta-report", "Other Outlet", _db.User.DisplayName));
        (await _db.Articles().LinksAsync(id)).Should().ContainSingle();

        foreach (var bad in new[] { "", "not a link", "ftp://files.example/x", "javascript:alert(1)", "/relative/path" })
        {
            var add = () => _db.Articles().AddLinkAsync(id, bad, null);
            await add.Should().ThrowAsync<ArgumentException>($"“{bad}” isn't an http(s) address");
        }
        foreach (var repeat in new[] { "https://other.example/okta-report?utm_source=x", "https://vendor.example/okta" })
        {
            var add = () => _db.Articles().AddLinkAsync(id, repeat, null);
            await add.Should().ThrowAsync<ArgumentException>().WithMessage("*already on the article*");
        }
        (await _db.Articles().LinksAsync(id)).Should().ContainSingle();
    }
}

public class CveAndStoryTests : IDisposable
{
    private readonly TestDb _db = new();

    public CveAndStoryTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<Guid> ArticleWithCve(string cve)
    {
        var svc = _db.Articles();
        var id = await svc.CreateAsync("About " + cve);
        var form = ArticleForm.From((await svc.GetAsync(id))!);
        form.Cves = [cve];
        await svc.SaveAsync(form);
        return id;
    }

    [Fact]
    public async Task Enrich_FillsKevEpssAndNvd_ForTheCvesOnArticles()
    {
        await ArticleWithCve("CVE-2026-0001");
        var o = new CveOptions { Enabled = true, NvdDelayMs = 0 };
        _db.Http.Set(o.KevUrl, """{"vulnerabilities":[{"cveID":"CVE-2026-0001","vendorProject":"Example","product":"Edge VPN","vulnerabilityName":"x","dateAdded":"2026-10-24","dueDate":"2026-11-14","knownRansomwareCampaignUse":"Known"}]}""");
        _db.Http.Set(o.EpssUrl, """{"data":[{"cve":"CVE-2026-0001","epss":"0.9","percentile":"0.99"}]}""");
        _db.Http.Set(o.NvdUrl, """{"vulnerabilities":[{"cve":{"id":"CVE-2026-0001","descriptions":[{"lang":"en","value":"Bypass."}],"metrics":{"cvssMetricV31":[{"cvssData":{"baseScore":9.8,"baseSeverity":"CRITICAL","vectorString":"v"}}]}}}]}""");

        var run = (await _db.Cves(o).EnrichDueAsync())!;

        run.Error.Should().BeNull();
        (run.Kev, run.Epss, run.Nvd).Should().Be((1, 1, 1));
        var r = (await _db.Cves(o).GetAsync(["cve-2026-0001"]))["CVE-2026-0001"];
        r.IsKev.Should().BeTrue();
        r.KevRansomware.Should().BeTrue();
        r.EpssScore.Should().Be(0.9m);
        r.CvssScore.Should().Be(9.8m);
        r.Description.Should().Be("Bypass.");
        CveService.ThreatHints([r]).Single().Should().Contain("known exploited").And.Contain("ransomware");

        (await _db.Cves(o).EnrichDueAsync()).Should().BeNull("it ran a moment ago");
        (await _db.Cves(new CveOptions { Enabled = false }).EnrichDueAsync()).Should().BeNull("it's off");
    }

    [Fact]
    public async Task Enrich_ACatalogListingACveTwice_IsRead_NotACrash()
    {
        await ArticleWithCve("CVE-2026-0004");
        var o = new CveOptions { Enabled = true, NvdDelayMs = 0 };
        _db.Http.Set(o.KevUrl, """{"vulnerabilities":[{"cveID":"CVE-2026-0004","dateAdded":"2026-10-24"},{"cveID":"CVE-2026-0004","dateAdded":"2026-10-25"}]}""");
        _db.Http.Set(o.EpssUrl, """{"data":[{"cve":"CVE-2026-0004","epss":"0.6"},{"cve":"CVE-2026-0004","epss":"0.7"}]}""");
        _db.Http.Set(o.NvdUrl, """{"vulnerabilities":[]}""");

        var run = (await _db.Cves(o).EnrichNowAsync())!;

        run.Error.Should().BeNull();
        (run.Kev, run.Epss).Should().Be((1, 1));
    }

    [Fact]
    public async Task Enrich_AnNvdAnswerThatCantBeRead_DoesntHoldUpTheOtherCves()
    {
        await ArticleWithCve("CVE-2026-0005");
        await ArticleWithCve("CVE-2026-0006");
        var o = new CveOptions { Enabled = true, NvdDelayMs = 0 };
        _db.Http.Set(o.KevUrl, """{"vulnerabilities":[]}""");
        _db.Http.Set(o.EpssUrl, """{"data":[]}""");
        _db.Http.Set(o.NvdUrl + "?cveId=CVE-2026-0005", """{"vulnerabilities":[{"nocve":true}]}""");
        _db.Http.Set(o.NvdUrl + "?cveId=CVE-2026-0006", """{"vulnerabilities":[{"cve":{"id":"CVE-2026-0006","descriptions":[{"lang":"en","value":"Read."}]}}]}""");

        var run = (await _db.Cves(o).EnrichNowAsync())!;

        run.Error.Should().Contain("CVE-2026-0005");
        run.Nvd.Should().Be(1);
        var records = await _db.Cves(o).GetAsync(["CVE-2026-0005", "CVE-2026-0006"]);
        records["CVE-2026-0006"].Description.Should().Be("Read.");
        records["CVE-2026-0005"].NvdCheckedAtUtc.Should().NotBeNull("it waits its turn like the rest");
    }

    [Fact]
    public async Task Enrich_NvdAnsweringWithAPageThatIsntJson_StopsTheRun_AndMarksNothing()
    {
        await ArticleWithCve("CVE-2026-0005");
        await ArticleWithCve("CVE-2026-0006");
        var o = new CveOptions { Enabled = true, NvdDelayMs = 0 };
        _db.Http.Set(o.KevUrl, """{"vulnerabilities":[]}""");
        _db.Http.Set(o.EpssUrl, """{"data":[]}""");
        _db.Http.Set(o.NvdUrl, "<html>Rate limit exceeded</html>");

        var run = (await _db.Cves(o).EnrichNowAsync())!;

        run.Error.Should().StartWith("NVD");
        _db.Http.Requests.Count(r => r.Url.StartsWith(o.NvdUrl, StringComparison.Ordinal)).Should().Be(1, "the run stops at the first");
        var records = await _db.Cves(o).GetAsync(["CVE-2026-0005", "CVE-2026-0006"]);
        records.Should().HaveCount(2);
        records.Values.Should().OnlyContain(r => r.NvdCheckedAtUtc == null, "they're tried at the next run, not in a week");
    }

    [Fact]
    public async Task Enrich_AnUnreachableServiceIsReported_AndTheOthersStillRun()
    {
        await ArticleWithCve("CVE-2026-0003");
        var o = new CveOptions { Enabled = true, NvdDelayMs = 0 };
        _db.Http.Set(o.EpssUrl, """{"data":[{"cve":"CVE-2026-0003","epss":"0.6","percentile":"0.97"}]}""");
        _db.Http.Set(o.NvdUrl, """{"vulnerabilities":[]}""");

        var run = (await _db.Cves(o).EnrichNowAsync())!;

        run.Error.Should().StartWith("KEV:");
        run.Epss.Should().Be(1);
        var r = (await _db.Cves(o).GetAsync(["CVE-2026-0003"]))["CVE-2026-0003"];
        CveService.ThreatHints([r]).Single().Should().Contain("EPSS");
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        var analyst = () => _db.Cves(o).EnrichNowAsync();
        await analyst.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Stories_LinkArticles_IntoATimeline_AndAreClosedNotDeletedOnceUsed()
    {
        var stories = _db.Stories();
        var story = await stories.CreateAsync("Edge VPN exploitation", "From the zero-day on.");
        var a1 = await ArticleWithCve("CVE-2026-0001");
        var a2 = await ArticleWithCve("CVE-2026-0002");
        await stories.LinkAsync(a1, story);
        await stories.LinkAsync(a2, story);

        var detail = (await stories.GetAsync(story))!;
        detail.Articles.Select(a => a.Id).Should().Equal(a1, a2);
        detail.Cves.Should().Equal("CVE-2026-0001", "CVE-2026-0002");
        (await stories.ListAsync()).Single().Articles.Should().Be(2);

        var delete = () => stories.DeleteAsync(story);
        await delete.Should().ThrowAsync<InvalidOperationException>();
        await stories.LinkAsync(a1, null);
        (await stories.GetAsync(story))!.Articles.Should().ContainSingle();

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        (await _db.Stories().GetAsync(story)).Should().NotBeNull("the team reads stories");
        var change = () => _db.Stories().CreateAsync("x", null);
        await change.Should().ThrowAsync<ForbiddenException>();
    }
}
