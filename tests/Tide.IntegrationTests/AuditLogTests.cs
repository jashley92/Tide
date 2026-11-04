using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.History;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages.Admin;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Administration › System › Audit log: the whole log, filtered and paged, for administrators only, with secrets
/// masked and the records named in a few queries a page.</summary>
public class AuditLogServiceTests : IDisposable
{
    private TestDb _db = new();

    public AuditLogServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private AuditLogService Audit() => new(_db, _db.User, _db.Clock);

    private async Task SaveSetting(string key, string value)
    {
        await using var ctx = _db.Context();
        var row = await ctx.Settings.FindAsync(key);
        if (row is null) ctx.Settings.Add(new AppSetting { Key = key, Value = value, UpdatedAtUtc = DateTimeOffset.UtcNow, UpdatedBy = _db.User.UserId });
        else row.Value = value;
        await ctx.SaveChangesAsync();
    }

    [Theory]
    [InlineData(AppRole.Analyst)]
    [InlineData(AppRole.ActionOwner)]
    [InlineData(AppRole.Reader)]
    public async Task OnlyAdministrators_ReadTheLog(AppRole role)
    {
        await _db.Articles().CreateAsync("Draft");
        _db.User.Roles = new HashSet<AppRole> { role };

        var page = () => Audit().PageAsync(new AuditFilter());
        await page.Should().ThrowAsync<ForbiddenException>("the log holds text at every marking");
        var export = () => Audit().ExportAsync(new AuditFilter());
        await export.Should().ThrowAsync<ForbiddenException>();
        var people = () => Audit().PeopleAsync();
        await people.Should().ThrowAsync<ForbiddenException>();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheLog_ListsEveryKindOfRecord_NewestFirst_NamedAndLinked(string provider)
    {
        _db = _db.On(provider);
        var article = await _db.Articles().CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);
        await _db.Teams().AddAsync("Vulnerability Management", null, null);
        await SaveSetting("Distribution:PublicRecipients", "all@example.org");
        await using (var ctx = _db.Context())
        {
            ctx.RoleMappings.Add(new AdGroupRoleMapping { AdGroup = @"EXAMPLE\Hunters", RoleName = "Analyst", UpdatedAtUtc = DateTimeOffset.UtcNow, UpdatedBy = "x" });
            await ctx.SaveChangesAsync();
        }

        var page = await Audit().PageAsync(new AuditFilter());

        page.Rows.Select(r => r.AtUtc).Should().BeInDescendingOrder();
        var a = page.Rows.Single(r => r.EntityType == nameof(Article));
        a.Should().Match<AuditRow>(r => r.Kind == "Article" && r.Record == "Edge VPN zero-day" && r.Href == $"articles/{article}"
                                       && r.Action == AuditAction.Created && r.UserName == "Test Analyst");
        a.Changes.Should().Contain(new HistoryChange("Title", null, "Edge VPN zero-day"));
        page.Rows.Single(r => r.EntityType == nameof(Team)).Should().Match<AuditRow>(r => r.Record == "Vulnerability Management" && r.Href == "admin/teams");
        var setting = page.Rows.Single(r => r.EntityType == nameof(AppSetting));
        setting.Should().Match<AuditRow>(r => r.Kind == "Setting" && r.Record == "Distribution: Company-wide edition recipients" && r.Href == "admin/access");
        setting.Changes.Should().ContainSingle().Which.Should().Be(new HistoryChange("Value", null, "all@example.org"), "the stamps are bookkeeping");
        page.Rows.Single(r => r.EntityType == nameof(AdGroupRoleMapping)).Should().Match<AuditRow>(r => r.Kind == "Role mapping" && r.Record == @"EXAMPLE\Hunters as Analyst");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task PushedRules_AndKeptArticleText_AreNotAuditedRowByRow(string provider)
    {
        _db = _db.On(provider);
        var detections = new Tide.Application.Detections.DetectionService(_db, _db.User, _db.Clock, _db.Actions(), TimeProvider.System);
        Tide.Application.Detections.ParsedRule[] rules = [new("1", "MFA push", ["T1621"], null, true), new("2", "VPN", ["T1133"], null, true)];
        await detections.PushAsync(rules);
        await detections.PushAsync(rules);
        await using (var ctx = _db.Context())
        {
            var item = new FeedItem { Source = new Source { Name = "Audit source", Url = "https://audit.example/feed" }, Title = "t", Url = "https://audit.example/a", UrlHash = "audit-a" };
            ctx.Add(item);
            ctx.Users.Add(new AppUser { Sid = _db.User.UserId, DisplayName = "Test Analyst" });
            await ctx.SaveChangesAsync();
            ctx.Add(new FeedItemText { FeedItemId = item.Id, Text = "The publisher's article.", How = "markup", FetchedAtUtc = DateTimeOffset.UtcNow });
            await ctx.SaveChangesAsync();
            ctx.RemoveRange(ctx.FeedItemTexts);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.Context())
            ctx.AuditLog.Where(e => e.EntityType == nameof(FeedItemText)).Should().BeEmpty("kept text is the publisher's");
        var pushes = (await Audit().PageAsync(new AuditFilter { Kind = nameof(DetectionRule) })).Rows;
        pushes.Should().HaveCount(2, "a push sets every rule's LastSeenAtUtc, so it's one entry, not one per rule")
            .And.OnlyContain(r => r.Action == AuditAction.Changed && r.Record == "Rules pushed by Test Analyst");

        (await detections.RemoveCallerAsync((await detections.CallersAsync()).Single().Caller)).Should().Be(2);
        var removed = (await Audit().PageAsync(new AuditFilter { Kind = nameof(DetectionRule) })).Rows.Where(r => r.Action == AuditAction.Deleted).Should()
            .ContainSingle("an administrator's removal is one entry").Subject;
        removed.Should().Match<AuditRow>(r => r.Record == "Rules pushed by Test Analyst");
    }

    [Fact]
    public async Task ARecordThatsGone_IsNamedFromItsOwnRow_WithoutALink()
    {
        var id = await _db.Articles().CreateAsync("Scratch draft");
        await _db.Articles().DeleteAsync(id);

        var rows = (await Audit().PageAsync(new AuditFilter { Kind = nameof(Article), Key = id.ToString() })).Rows;

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Record == "Scratch draft" && r.Href == null);
        rows[0].Action.Should().Be(AuditAction.Deleted);
        rows[0].Changes.Should().Contain(c => c.Field == "Title" && c.Before == "Scratch draft", "a delete shows what it held");
    }

    [Fact]
    public async Task APart_IsNamedByWhatItsPartOf_AndEachAdminRecordLinksToItsPage()
    {
        var team = new Team { Key = "vm", Name = "Vuln Mgmt" };
        var article = new Article { Title = "Parent article" };
        var brief = new Brief { BriefDate = new DateOnly(2026, 10, 26) };
        var action = new ActionItem { Number = 42, Title = "Sweep for the IOCs", TeamId = team.Id };
        var playbook = new Playbook { Name = "Patch playbook" };
        var category = new ScorecardCategory { Key = "c", Name = "Threat type" };
        var group = new MetricGroup { Key = "g", Name = "SOC" };
        var definition = new MetricDefinition { Key = "m", Name = "Open alerts", GroupId = group.Id };
        var story = new Story { Name = "Edge VPN campaign" };
        var source = new Source { Name = "CISA KEV", Url = "https://example.org/kev.json" };
        var rule = new ActionCardRule { Name = "Overdue P1s" };
        var tag = new ScorecardTag { Key = "t", Name = "Ransomware", CategoryId = category.Id };
        // The parts, by their own ids; the audit rows below don't say what they're part of, so it's read from their tables.
        object[] parts =
        [
            new ArticleBullet { ArticleId = article.Id, Text = "b" }, new ArticleIndicator { ArticleId = article.Id, Value = "1.2.3.4" },
            new ArticleLink { ArticleId = article.Id, Url = "https://example.org", Title = "l" }, new BriefItem { BriefId = brief.Id, ArticleId = article.Id },
            new ActionEvent { ActionId = action.Id, Text = "e" }, new ActionComment { ActionId = action.Id, Text = "c" },
            new ActionChecklistItem { ActionId = action.Id, Text = "i" }, new HuntQuery { ActionId = action.Id, Dialect = "kql" },
            new PlaybookStep { PlaybookId = playbook.Id, Text = "s" }, new MetricValue { DefinitionId = definition.Id, BriefDate = brief.BriefDate },
        ];
        await using (var ctx = _db.PlainContext())
        {
            ctx.AddRange(team, article, brief, action, playbook, category, group, definition, story, source, rule, tag);
            ctx.AddRange(parts);
            foreach (var e in parts.Cast<Domain.Common.Entity>().Concat([story, source, rule, tag, playbook, category, group, definition, brief, action]))
                ctx.AuditLog.Add(new AuditLogEntry { AtUtc = DateTimeOffset.UtcNow, UserId = "u", UserName = "U", Action = "Modified",
                    EntityType = e.GetType().Name, EntityKey = e.Id.ToString(), ChangesJson = """{"Text":["a","b"]}""" });
            await ctx.SaveChangesAsync();
        }

        var rows = (await Audit().PageAsync(new AuditFilter())).Rows.ToDictionary(r => r.EntityType);

        foreach (var part in new[] { nameof(ArticleBullet), nameof(ArticleIndicator), nameof(ArticleLink) })
            rows[part].Should().Match<AuditRow>(r => r.Record == "Parent article" && r.Href == $"articles/{article.Id}", part);
        rows[nameof(BriefItem)].Should().Match<AuditRow>(r => r.Record == "Brief for Mon Oct 26, 2026" && r.Href == "briefs/2026-10-26");
        foreach (var part in new[] { nameof(ActionEvent), nameof(ActionComment), nameof(ActionChecklistItem), nameof(HuntQuery), nameof(ActionItem) })
            rows[part].Should().Match<AuditRow>(r => r.Record == "A-42 Sweep for the IOCs" && r.Href == "actions/42", part);
        rows[nameof(PlaybookStep)].Href.Should().Be("admin/playbooks");
        rows[nameof(MetricValue)].Should().Match<AuditRow>(r => r.Record == "Open alerts" && r.Href == "admin/metrics");
        rows[nameof(Story)].Href.Should().Be($"stories/{story.Id}");
        rows[nameof(Source)].Should().Match<AuditRow>(r => r.Record == "CISA KEV" && r.Href == "admin/sources");
        rows[nameof(ActionCardRule)].Href.Should().Be("admin/card-rules");
        rows[nameof(ScorecardTag)].Href.Should().Be("admin/scorecard");
        rows[nameof(ScorecardCategory)].Href.Should().Be("admin/scorecard");
        rows[nameof(MetricGroup)].Record.Should().Be("SOC");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheFilters_NarrowByPersonKindRecordActionDatesAndWords(string provider)
    {
        _db = _db.On(provider);
        var svc = _db.Articles();
        var first = await svc.CreateAsync("Quokka ransomware");
        var second = await svc.CreateAsync("Another story");
        var form = ArticleForm.From((await svc.GetAsync(first))!);
        form.Bluf = "Encrypts O'Brien's shares.";
        await svc.SaveAsync(form);
        _db.User.UserId = "dev:sam";
        _db.User.DisplayName = "Sam";
        await SaveSetting("Org:TeamName", "Blue team");

        var audit = Audit();
        (await audit.PageAsync(new AuditFilter { Person = "dev:sam" })).Rows.Should().ContainSingle().Which.UserName.Should().Be("Sam");
        (await audit.PageAsync(new AuditFilter { Kind = nameof(AppSetting) })).Rows.Should().ContainSingle();
        (await audit.PageAsync(new AuditFilter { Kind = nameof(Article), Key = second.ToString() })).Rows.Should().ContainSingle()
            .Which.Record.Should().Be("Another story");
        (await audit.PageAsync(new AuditFilter { Kind = nameof(Article), Action = AuditAction.Changed })).Rows.Should().ContainSingle()
            .Which.Changes.Should().Contain(new HistoryChange("Bottom line", null, "Encrypts O'Brien's shares."));
        (await audit.PageAsync(new AuditFilter { Search = "o'brien" })).Rows.Should().ContainSingle("the words are matched as the JSON holds them, ignoring case")
            .Which.EntityKey.Should().Be(first.ToString());
        (await audit.PageAsync(new AuditFilter { Search = "100%_off" })).Rows.Should().BeEmpty("% and _ are literal");

        var today = _db.Clock.Today;
        (await audit.PageAsync(new AuditFilter { From = today, To = today })).Total.Should().BeGreaterThan(3);
        (await audit.PageAsync(new AuditFilter { From = today.AddDays(1) })).Rows.Should().BeEmpty();
        (await audit.PageAsync(new AuditFilter { To = today.AddDays(-1) })).Rows.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AFilterWithoutAnIndex_IsReadOverABoundedWindowOfDays(string provider)
    {
        _db = _db.On(provider);
        var audit = Audit();
        var today = _db.Clock.Today;
        var earliest = today.AddDays(-(AuditLogService.UnindexedDays - 1));

        audit.Window(new AuditFilter { Action = AuditAction.Changed }).Should().Be(((DateOnly?)earliest, (DateOnly?)null, (DateOnly?)earliest));
        audit.Window(new AuditFilter { Person = "x" }).Should().Be(((DateOnly?)null, (DateOnly?)null, (DateOnly?)null), "the person has an index");
        audit.Window(new AuditFilter { Person = "x", Search = "y" }).Since.Should().BeNull("the person narrows the words to their rows");
        audit.Window(new AuditFilter { Search = "x", From = today.AddYears(-2) }).Since.Should().Be(earliest, "a longer range is cut");
        audit.Window(new AuditFilter { Action = AuditAction.Deleted, From = today.AddDays(-10) }).Should().Be(((DateOnly?)today.AddDays(-10), (DateOnly?)null, (DateOnly?)null));
        audit.Window(new AuditFilter { Kind = nameof(Article) }).Should().Be(((DateOnly?)null, (DateOnly?)null, (DateOnly?)null), "the kind has an index");

        await using (var ctx = _db.PlainContext())
        {
            ctx.AuditLog.Add(new AuditLogEntry { AtUtc = DateTimeOffset.UtcNow.AddDays(-200), UserId = "dev:old", UserName = "Old", Action = "Added", EntityType = "Story", EntityKey = "k" });
            await ctx.SaveChangesAsync();
        }
        var page = await audit.PageAsync(new AuditFilter { Action = AuditAction.Created });
        page.Rows.Should().NotContain(r => r.UserId == "dev:old", "it's older than the window");
        page.Since.Should().Be(earliest);
        (await audit.PageAsync(new AuditFilter { Action = AuditAction.Created, From = today.AddDays(-210), To = today.AddDays(-190) })).Rows.Should().ContainSingle();
        (await audit.PageAsync(new AuditFilter { Person = "dev:old" })).Rows.Should().ContainSingle("a person is read over any range");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheLog_PagesFiftyAtATime_AndSaysHowManyMatch(string provider)
    {
        _db = _db.On(provider);
        var svc = _db.Articles();
        for (var i = 0; i < AuditLogService.PageSize + 5; i++) await svc.CreateAsync($"Article {i}");

        var first = await Audit().PageAsync(new AuditFilter { Kind = nameof(Article) });
        first.Rows.Should().HaveCount(AuditLogService.PageSize);
        first.Total.Should().Be(AuditLogService.PageSize + 5);
        first.Rows[0].Record.Should().Be($"Article {AuditLogService.PageSize + 4}", "newest first");
        (await Audit().PageAsync(new AuditFilter { Kind = nameof(Article) }, take: 100)).Rows.Should().HaveCount(AuditLogService.PageSize + 5);
    }

    [Fact]
    public async Task TheRecords_AreNamedInAFewQueries_NotOnePerRow()
    {
        async Task<int> QueriesFor(int articles)
        {
            var svc = _db.Articles();
            for (var i = 0; i < articles; i++)
            {
                var id = await svc.CreateAsync($"Story {i}");
                var form = ArticleForm.From((await svc.GetAsync(id))!);
                form.TechniqueIds = ["T1566"];
                await svc.SaveAsync(form);
            }
            _db.Commands.Reset();
            var page = await Audit().PageAsync(new AuditFilter(), take: AuditLogService.MaxPage);
            page.Rows.Where(r => r.EntityType == nameof(ArticleTechnique)).Should().OnlyContain(r => r.Record.StartsWith("Story ") && r.Href != null);
            return _db.Commands.Count;
        }

        var few = await QueriesFor(2);
        var many = await QueriesFor(30);
        many.Should().Be(few, "one query per kind of record on the page, however many rows");
        many.Should().BeLessThan(8);
    }

    [Fact]
    public async Task ASecret_ShowsOnlyAsSetOrChanged_OnThePageAndInTheCsv_AndIsntSearchable()
    {
        await SaveSetting("Email:SmtpPassword", "hunter2-first");
        await SaveSetting("Email:SmtpPassword", "hunter2-second");
        // A row written before the interceptor masked anything: masked when shown regardless.
        await using (var ctx = _db.PlainContext())
        {
            ctx.AuditLog.Add(new AuditLogEntry
            {
                AtUtc = DateTimeOffset.UtcNow, UserId = "dev:old", UserName = "Old", Action = "Modified", EntityType = nameof(AppSetting),
                EntityKey = "Cve:NvdApiKey", ChangesJson = """{"Value":["old-nvd-key-123","new-nvd-key-456"]}""",
            });
            ctx.AuditLog.Add(new AuditLogEntry
            {
                AtUtc = DateTimeOffset.UtcNow, UserId = "dev:old", UserName = "Old", Action = "Modified", EntityType = nameof(Source),
                EntityKey = Guid.NewGuid().ToString(), ChangesJson = """{"Url":["https://feed.example.org/rss","https://feed.example.org/rss?apikey=sekrit789"]}""",
            });
            await ctx.SaveChangesAsync();
        }

        var rows = (await Audit().PageAsync(new AuditFilter())).Rows;

        var password = rows.Where(r => r.EntityKey == "Email:SmtpPassword").ToList();
        password.Should().HaveCount(2);
        password.Single(r => r.Action == AuditAction.Created).Changes.Should().Equal(new HistoryChange("Value", null, HistoryFormatter.SecretSet));
        password.Single(r => r.Action == AuditAction.Changed).Changes.Should().Equal(new HistoryChange("Value", HistoryFormatter.SecretSet, HistoryFormatter.SecretChanged));
        rows.Single(r => r.EntityKey == "Cve:NvdApiKey").Changes.Should().Equal(new HistoryChange("Value", HistoryFormatter.SecretSet, HistoryFormatter.SecretChanged));
        rows.Single(r => r.EntityType == nameof(Source)).Changes.Single().After.Should().NotContain("sekrit789");

        var csv = AuditCsv.Write(await Audit().ExportAsync(new AuditFilter()), _db.Clock);
        csv.Should().NotContain("hunter2").And.NotContain("nvd-key").And.NotContain("sekrit789").And.Contain(HistoryFormatter.SecretChanged);

        (await Audit().PageAsync(new AuditFilter { Search = "nvd-key-456" })).Rows.Should().BeEmpty("a match would tell the secret");
        (await Audit().PageAsync(new AuditFilter { Search = "NvdApiKey" })).Rows.Should().ContainSingle("the key itself can be searched");
    }

    [Fact]
    public async Task People_AreEveryoneWhoSignedIn_AndTheBackgroundJobs()
    {
        await using (var ctx = _db.Context())
        {
            ctx.Users.Add(new AppUser { Sid = "dev:robin", DisplayName = "Robin", UserPrincipalName = "robin@example.org" });
            await ctx.SaveChangesAsync();
        }

        var people = await Audit().PeopleAsync();

        people.Should().Contain(new AuditPerson("dev:robin", "Robin"));
        people[^1].UserId.Should().Be(AuditLogService.SystemUser);
    }
}

/// <summary>The Audit log page: drawn for an administrator, with its filters from the address, and its export.</summary>
public class AuditLogPageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task ThePage_ListsTheChanges_AndItsFiltersAreTheAddresss()
    {
        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Audit page: Wombat campaign");

        await using var r = await RenderAsync<AuditLogAdmin>(app, "/admin/system/audit?kind=Article&q=Wombat");
        var html = await r.HtmlOnce("Wombat campaign");

        html.Should().Contain("<h1 class=\"tide-admin-title\">Audit log</h1>")
            .And.Contain("Audit page: Wombat campaign").And.Contain("Kind: Article").And.Contain("Only this record")
            .And.Contain("admin/system/audit.csv?q=Wombat&amp;kind=Article").And.Contain("Title:");
        html.Should().Contain("Action and word filters cover", "the words are read over a window of days");
    }

    [Fact]
    public async Task TheOverview_AndTheSystemTabs_ListIt()
    {
        var html = await (await app.CreateClient().GetAsync("/admin")).Content.ReadAsStringAsync();
        html.Should().Contain("href=\"admin/system/audit\"");
    }

    [Fact]
    public async Task TheExport_IsCsv_ForTheSameFilters()
    {
        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("=HYPERLINK(\"http://evil\") export check");

        var res = await app.CreateClient().GetAsync("/admin/system/audit.csv?kind=Article&q=export%20check");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        res.Content.Headers.ContentDisposition!.FileNameStar.Should().StartWith("tide-audit-log-");
        var csv = (await res.Content.ReadAsStringAsync()).TrimStart('﻿');
        csv.Should().StartWith(AuditCsv.Header).And.Contain("\"'=HYPERLINK(\"\"http://evil\"\") export check\"", "a formula is neutralised");
    }
}
