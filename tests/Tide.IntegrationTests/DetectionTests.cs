using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Adversaries;
using Tide.Application.Api;
using Tide.Application.Articles;
using Tide.Application.Detections;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>EP-79: reading the SOC's rules from Sentinel, Sigma and CSV.</summary>
public class DetectionParserTests
{
    private const string SentinelExport = """
        {
          "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
          "resources": [
            { "type": "Microsoft.OperationalInsights/workspaces/providers/alertRules",
              "name": "[concat(parameters('workspace'),'/Microsoft.SecurityInsights/0b9ae89d-8cad-461c-808f-0494f70ad5c4')]",
              "kind": "Scheduled",
              "properties": { "displayName": "MFA fatigue: many denied pushes", "enabled": true, "tactics": ["CredentialAccess"],
                              "techniques": ["T1621"], "subTechniques": ["T1110.003"] } },
            { "type": "Microsoft.OperationalInsights/workspaces/providers/alertRules", "name": "plain-name", "kind": "NRT",
              "properties": { "displayName": "Old VPN rule", "enabled": false, "techniques": ["T1133", "not-one"] } },
            { "type": "Microsoft.OperationalInsights/workspaces/providers/metadata", "properties": { "displayName": "ignored" } }
          ]
        }
        """;

    private const string SigmaRules = """
        title: Remote access tool installed
        id: 5c5c2d8b-0c3d-4f46-9d0f-1e3c3b8c7d11
        status: test
        logsource:
            product: windows
            category: process_creation
        tags:
            - attack.command-and-control
            - attack.t1219
        ---
        title: 'Old rule'
        id: old-1
        status: deprecated
        tags: [attack.t1566.004, attack.t9]
        logsource:
          product: m365
          service: audit
        """;

    [Fact]
    public void Sentinel_ReadsEachAnalyticsRule_WithItsTechniquesAndState()
    {
        var problems = new List<string>();

        var rules = DetectionParsers.Sentinel("rules.json", SentinelExport, problems);

        rules.Should().BeEquivalentTo(new[]
        {
            new ParsedRule("0b9ae89d-8cad-461c-808f-0494f70ad5c4", "MFA fatigue: many denied pushes", ["T1621", "T1110.003"], "Sentinel Scheduled", true),
            new ParsedRule("plain-name", "Old VPN rule", ["T1133"], "Sentinel NRT", false),
        }, o => o.WithStrictOrdering());
        problems.Should().ContainSingle().Which.Should().Contain("“not-one” isn't a technique id");
        DetectionParsers.Sentinel("x.json", "{ nope", problems).Should().BeEmpty();
        problems.Last().Should().Be("x.json: not JSON.");
        DetectionParsers.Sentinel("one.json", """{ "properties": { "displayName": "Single", "techniques": ["t1078"] } }""", []).Single().Techniques.Should().Equal("T1078");
    }

    [Fact]
    public void Sigma_ReadsTitlesTagsAndStatus_FromEachDocument()
    {
        var problems = new List<string>();

        var rules = DetectionParsers.Sigma("rules.yml", SigmaRules, problems);

        rules.Should().BeEquivalentTo(new[]
        {
            new ParsedRule("5c5c2d8b-0c3d-4f46-9d0f-1e3c3b8c7d11", "Remote access tool installed", ["T1219"], "windows", true),
            new ParsedRule("old-1", "Old rule", ["T1566.004"], "m365 / audit", false),
        }, o => o.WithStrictOrdering());
        DetectionParsers.Sigma("empty.yml", "# nothing", problems).Should().BeEmpty();
        problems.Should().Contain("empty.yml: no Sigma rule (a title) found.");
    }

    [Fact]
    public void Csv_ReadsNamesTechniquesAndEnabled()
    {
        var problems = new List<string>();
        const string csv = "Name,Techniques,Platform,Enabled,Id\r\n\"Help desk reset, then admin\",T1078; T1556,Okta,yes,r-1\r\nNo tags,,,,\r\nOff rule,T1059.001,EDR,maybe,\r\n,T1000,,,\r\n";

        var rules = DetectionParsers.Csv("rules.csv", csv, problems);

        rules.Select(r => (r.ExternalId, r.Name, string.Join(' ', r.Techniques), r.Platform, r.Enabled)).Should().Equal(
            ("r-1", "Help desk reset, then admin", "T1078 T1556", "Okta", true),
            ("No tags", "No tags", "", null, true),
            ("Off rule", "Off rule", "T1059.001", "EDR", true));
        problems.Should().Contain(p => p.Contains("“maybe” isn't yes or no")).And.Contain(p => p.Contains("row 5: no name"));
        FluentActions.Invoking(() => DetectionParsers.Csv("x.csv", "title,tags\r\na,b", problems)).Should().Throw<ArgumentException>().WithMessage("*name and techniques*");
    }

    [Fact]
    public void AZip_IsReadEntryByEntry_AndSizeLimitsHold()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "rules/a.yml", "title: A\ntags:\n  - attack.t1001\n");
            Write(zip, "rules/b.yaml", "title: B\n");
            Write(zip, "README.md", "not a rule");
        }
        var problems = new List<string>();

        var files = DetectionParsers.Files("sigma.zip", ms.ToArray(), [".yml", ".yaml"], problems);

        files.Select(f => f.Name).Should().Equal("rules/a.yml", "rules/b.yaml");
        FluentActions.Invoking(() => DetectionParsers.Files("big.json", new byte[DetectionParsers.MaxUploadBytes + 1], [".json"], problems)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => DetectionParsers.Files("bad.zip", "not a zip"u8.ToArray(), [".json"], problems)).Should().Throw<ArgumentException>().WithMessage("*can't be read*");
        DetectionParsers.Technique(" t1059.001 ").Should().Be("T1059.001");
        DetectionParsers.Technique("T10").Should().BeNull();
    }

    [Theory]
    [InlineData("T1566", "T1566.004", true)]
    [InlineData("T1566.004", "T1566", true)]
    [InlineData("T1566.004", "T1566.004", true)]
    [InlineData("T1566.001", "T1566.004", false)]
    [InlineData("T1078", "T1566", false)]
    public void ARule_CountsForItsTechnique_ItsParent_AndItsSubTechniques(string tag, string technique, bool covers) =>
        DetectionService.Covers(tag, technique).Should().Be(covers);

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(Encoding.UTF8.GetBytes(text));
    }
}

/// <summary>EP-79: storing imports, Coverage, raising a gap, and the Navigator layer's colours.</summary>
public class DetectionServiceTests : IDisposable
{
    private TestDb _db = new();

    public DetectionServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    // The service's clock, which a test moves on between two imports or pushes ("the latest one") instead of waiting for
    // the system clock to tick: a tie would fall either way.
    private readonly TestClock _time = new(DateTimeOffset.UtcNow);

    private DetectionService Detections() => new(_db, _db.User, _db.Clock, _db.Actions(), _time);

    private static byte[] Csv(params string[] rows) => Encoding.UTF8.GetBytes("name,techniques,enabled,id\n" + string.Join('\n', rows));

    private async Task<Guid> Briefed(string title, params string[] techniques)
    {
        var id = await _db.Articles().CreateAsync(title, "Vendor", "https://vendor.example.com/" + Guid.NewGuid().ToString("N"));
        var form = ArticleForm.From((await _db.Articles().GetAsync(id))!);
        form.TechniqueIds = techniques.ToList();
        await _db.Articles().SaveAsync(form);
        var brief = await _db.Briefs().GetOrStartAsync(_db.SendDay);
        await using var ctx = _db.Context();
        ctx.BriefItems.Add(new BriefItem { BriefId = brief.Id, ArticleId = id });
        await ctx.SaveChangesAsync();
        return id;
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AnImport_AddsChangesAndKeeps_AndRulesMissingFromTheNextOneStopCounting(string provider)
    {
        _db = _db.On(provider);
        var first = await Detections().ImportAsync(DetectionSource.Csv, "rules.csv", Csv("MFA push,T1621,yes,r1", "VPN,T1133,no,r2", "Old,T1078,yes,r3"));
        _time.Now += TimeSpan.FromSeconds(1);
        var second = await Detections().ImportAsync(DetectionSource.Csv, "rules.csv", Csv("MFA push,T1621,yes,r1", "VPN,T1133,yes,r2", "New,T1219,yes,r4"));

        first.Should().BeEquivalentTo(new DetectionImport(3, 0, 0, 0, []));
        second.Should().BeEquivalentTo(new DetectionImport(1, 1, 1, 1, []));
        var (rules, imports) = await Detections().ListAsync();
        rules.Select(r => (r.Rule.Name, r.Current)).Should().BeEquivalentTo([("MFA push", true), ("VPN", true), ("New", true), ("Old", false)]);
        imports.Should().ContainSingle().Which.Should().Match<DetectionImportState>(i => i.Source == DetectionSource.Csv && i.Rules == 3 && i.Enabled == 3);
        (await Detections().ListAsync("t1621")).Rules.Should().ContainSingle();
        var counts = (await Detections().ForTechniquesAsync(["T1078", "T1621", "T1621.001"]))!;
        counts["T1078"].Label.Should().Be("no rule", "the rule tagged with it wasn't in the latest import");
        counts["T1621"].Should().BeEquivalentTo(new TechniqueRules(1, 0, ["MFA push"]));
        counts["T1621.001"].Enabled.Should().Be(1, "a rule tagged with the parent counts");

        await FluentActions.Invoking(() => Detections().ImportAsync(DetectionSource.Csv, "empty.csv", Csv())).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => Detections().ImportAsync(DetectionSource.Api, "x.json", [])).Should().ThrowAsync<ArgumentException>();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await FluentActions.Invoking(() => Detections().ImportAsync(DetectionSource.Csv, "r.csv", Csv("a,T1000,yes,a"))).Should().ThrowAsync<ForbiddenException>();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Invoking(() => Detections().CoverageAsync()).Should().ThrowAsync<ForbiddenException>();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Coverage_ListsBriefedTechniques_GapsFirst_WithWatchedAdversaries(string provider)
    {
        _db = _db.On(provider);
        (await Detections().ForTechniquesAsync(["T1621"])).Should().BeNull("no rules have been imported");
        var spider = await _db.Adversaries().SaveAsync(new AdversaryForm { Name = "Scattered Spider", Watched = true });
        var tagged = await Briefed("Help desk calls", "T1621", "T1078");
        await _db.Adversaries().TagArticleAsync(tagged, spider);
        await Briefed("Another MFA story", "T1621");
        await Briefed("Ransomware", "T1486", "T1078");
        var notBriefed = await _db.Articles().CreateAsync("Draft only", "Vendor", "https://vendor.example.com/draft");
        var form = ArticleForm.From((await _db.Articles().GetAsync(notBriefed))!);
        form.TechniqueIds = ["T1190"];
        await _db.Articles().SaveAsync(form);
        await Detections().ImportAsync(DetectionSource.Csv, "rules.csv", Csv("Valid accounts,T1078,yes,a", "Encrypt,T1486,no,b"));

        var report = await Detections().CoverageAsync();

        report.Rows.Select(r => (r.TechniqueId, r.Briefed, string.Join(",", r.Watched), r.Rules.Label)).Should().Equal(
            ("T1621", 2, "Scattered Spider", "no rule"),
            ("T1486", 1, "", "1 disabled"),
            ("T1078", 2, "Scattered Spider", "1 rule"));
        (report.Covered, report.Gaps, report.GapsWatched, report.GapsWithAction).Should().Be((1, 2, 1, 0));
        report.Rows[0].Name.Should().Be("Multi-Factor Authentication Request Generation");
    }

    [Fact]
    public async Task RaisingAGap_CreatesAnActionForTheTeam_AndRemembersIt()
    {
        var team = await _db.Teams().AddAsync("Detection engineering", null, null);
        var article = await Briefed("Help desk calls", "T1621");

        var number = await Detections().RaiseAsync("t1621", team);

        var row = (await Detections().CoverageAsync()).Rows.Single();
        (row.ActionNumber, row.ActionOpen).Should().Be(((int?)number, true));
        var action = await _db.Context().Actions.SingleAsync(a => a.Number == number);
        action.Title.Should().Be("Detection gap: T1621 Multi-Factor Authentication Request Generation");
        (action.Kind, action.TeamId, action.ArticleId, action.Priority).Should().Be((ActionKind.ConfigChange, team, (Guid?)article, Priority.P3));
        action.Description.Should().Contain("Briefed in 1 article").And.Contain("- Help desk calls");
        await FluentActions.Invoking(() => Detections().RaiseAsync("T1621", team)).Should().ThrowAsync<InvalidOperationException>().WithMessage($"A-{number} is already open*");
        await FluentActions.Invoking(() => Detections().RaiseAsync("T1999", team)).Should().ThrowAsync<KeyNotFoundException>();
        await FluentActions.Invoking(() => Detections().RaiseAsync("nope", team)).Should().ThrowAsync<ArgumentException>();

        await using (var ctx = _db.Context())
            await ctx.Actions.Where(a => a.Number == number).ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, ActionStatus.Done));
        var again = await Detections().RaiseAsync("T1621", team);
        again.Should().NotBe(number);
        (await Detections().CoverageAsync()).Rows.Single().ActionNumber.Should().Be(again);
    }

    [Fact]
    public async Task RaisingAGap_ForATeamThatWarnsAtItsLimit_AsksWhy_AndGoesAheadWithTheReason()
    {
        var team = await _db.Teams().AddAsync("Detection engineering", null, null);
        await _db.Teams().SetLimitAsync(team, 1, ActiveLimitMode.Warn);
        await _db.Actions().CreateAsync(new Tide.Application.Actions.ActionForm { Title = "Already on it", TeamId = team });
        await Briefed("Help desk calls", "T1621");

        var ask = await FluentActions.Invoking(() => Detections().RaiseAsync("T1621", team)).Should().ThrowAsync<Tide.Application.Actions.ActiveLimitException>();
        ask.Which.NeedsReason.Should().BeTrue();
        (await _db.Context().Actions.CountAsync()).Should().Be(1);

        var number = await Detections().RaiseAsync("T1621", team, "Rule gap for a watched actor");
        var id = await _db.Context().Actions.Where(a => a.Number == number).Select(a => a.Id).SingleAsync();
        var events = await _db.Context().ActionEvents.Where(e => e.ActionId == id).Select(e => e.Text).ToListAsync();
        events.Should().Contain(e => e.Contains("(2 of 1 active): Rule gap for a watched actor"));
        (await Detections().CoverageAsync()).Rows.Single().ActionNumber.Should().Be(number);
    }

    [Fact]
    public async Task TheNavigatorLayer_IsColouredByCoverage_OnceRulesAreImported()
    {
        var spider = await _db.Adversaries().SaveAsync(new AdversaryForm { Name = "Scattered Spider" });
        var article = await Briefed("Help desk calls", "T1621", "T1078");
        await _db.Adversaries().TagArticleAsync(article, spider);
        var plain = JsonDocument.Parse((await _db.Adversaries().NavigatorLayerAsync(spider))!).RootElement;
        plain.GetProperty("techniques").EnumerateArray().Select(t => t.TryGetProperty("color", out var c)).Should().OnlyContain(has => !has);

        await Detections().ImportAsync(DetectionSource.Csv, "rules.csv", Csv("Valid accounts,T1078,yes,a"));
        var layer = JsonDocument.Parse((await _db.Adversaries().NavigatorLayerAsync(spider))!).RootElement;

        var colours = layer.GetProperty("techniques").EnumerateArray().ToDictionary(t => t.GetProperty("techniqueID").GetString()!, t => (t.GetProperty("color").GetString(), t.GetProperty("comment").GetString()));
        colours["T1078"].Should().Be(("#8fd19e", "1 article; 1 rule tagged"));
        colours["T1621"].Should().Be(("#f4a09a", "1 article; no rule tagged"));
        layer.GetProperty("legendItems").GetArrayLength().Should().Be(2);

        // Someone who reads the team edition but not the rules gets the layer without coverage.
        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        var owners = JsonDocument.Parse((await _db.Adversaries().NavigatorLayerAsync(spider))!).RootElement;
        owners.GetProperty("techniques").EnumerateArray().Select(t => t.TryGetProperty("color", out _)).Should().OnlyContain(has => !has);
        owners.GetProperty("techniques").EnumerateArray().Select(t => t.GetProperty("comment").GetString()).Should().OnlyContain(c => !c!.Contains("rule"));
        owners.GetProperty("legendItems").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task APush_ReplacesTheCallersLastPush(string provider)
    {
        _db = _db.On(provider);
        var first = await Detections().PushAsync([new ParsedRule("", "From the SIEM", ["T1078", "x"], "Splunk", true), new ParsedRule("2", " ", [], null, true)]);
        var second = await Detections().PushAsync([new ParsedRule("b", "Another", ["T1110"], null, false)]);

        first.Should().BeEquivalentTo(new DetectionImport(1, 0, 0, 0, ["From the SIEM: “x” isn't a technique id."]));
        second.NotInImport.Should().Be(1);
        await FluentActions.Invoking(() => Detections().PushAsync([])).Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TwoCallersPushes_AreEachTheirOwnSet(string provider)
    {
        _db = _db.On(provider);
        void As(string id, string name) => (_db.User.UserId, _db.User.DisplayName) = (id, name);
        As("api:0a1b2c3d4e5f4a6b8c7d9e0f1a2b3c4d", "Sentinel sync");
        await Detections().PushAsync([new ParsedRule("1001", "Sentinel MFA", ["T1621"], null, true), new ParsedRule("1002", "Sentinel VPN", ["T1133"], null, true)]);
        _time.Now += TimeSpan.FromSeconds(1);
        As("api:ffffffffffffffffffffffffffffffff", "Splunk sync");
        var splunk = await Detections().PushAsync([new ParsedRule("1001", "Splunk accounts", ["T1078"], null, true)]);
        _time.Now += TimeSpan.FromSeconds(1);
        As("S-1-5-21-TEST-7", "SOAR gMSA");
        await Detections().PushAsync([new ParsedRule("x", "SOAR rule", ["T1110"], null, true)]);

        splunk.Should().BeEquivalentTo(new DetectionImport(1, 0, 0, 0, []), "the same id from another caller is another rule, and nothing of theirs is replaced");
        var (rules, imports) = await Detections().ListAsync();
        rules.Select(r => (r.Rule.Name, r.Current)).Should().BeEquivalentTo([("Sentinel MFA", true), ("Sentinel VPN", true), ("Splunk accounts", true), ("SOAR rule", true)]);
        imports.Should().ContainSingle().Which.Rules.Should().Be(4, "the API's line adds up each caller's latest push");

        // A token issued again under the same name carries on its caller's set.
        As("api:11111111111111111111111111111111", "Sentinel sync");
        _time.Now += TimeSpan.FromSeconds(1);
        var again = await Detections().PushAsync([new ParsedRule("1001", "Sentinel MFA", ["T1621"], null, true)]);
        again.Should().BeEquivalentTo(new DetectionImport(0, 0, 1, 1, []));
        (await Detections().ListAsync()).Rules.Select(r => (r.Rule.Name, r.Current)).Should().BeEquivalentTo(
            [("Sentinel MFA", true), ("Sentinel VPN", false), ("Splunk accounts", true), ("SOAR rule", true)]);
        (await Detections().ForTechniquesAsync(["T1078", "T1133"]))!["T1078"].Enabled.Should().Be(1, "Splunk's rule still counts after Sentinel's push");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task APushFromBeforeCallersWereKept_CountsUntilTheNextPush(string provider)
    {
        _db = _db.On(provider);
        await using (var ctx = _db.Context())
        {
            ctx.DetectionRules.Add(new DetectionRule { Source = DetectionSource.Api, ExternalId = "old-1", Name = "Old push", Techniques = "T1078", LastSeenAtUtc = DateTimeOffset.UtcNow.AddDays(-1) });
            await ctx.SaveChangesAsync();
        }
        (await Detections().ListAsync()).Rules.Single().Current.Should().BeTrue();

        await Detections().PushAsync([new ParsedRule("old-1", "New push", ["T1078"], null, true)]);

        (await Detections().ListAsync()).Rules.Select(r => (r.Rule.Name, r.Current)).Should().BeEquivalentTo([("Old push", false), ("New push", true)]);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AQuietCallersSet_KeepsCounting_OneWhoseTokenIsGoneStops_AndAnAdministratorCanRemoveEither(string provider)
    {
        _db = _db.On(provider);
        void As(string id, string name) => (_db.User.UserId, _db.User.DisplayName) = (id, name);
        var now = DateTimeOffset.UtcNow;
        ApiToken Token(string name, DateTimeOffset expires, DateTimeOffset? revoked = null) =>
            new() { Name = name, Hash = Guid.NewGuid().ToString("N"), Hint = "abcd", Scopes = "detections:write", ExpiresAtUtc = expires, RevokedAtUtc = revoked };
        As("api:0a1b2c3d4e5f4a6b8c7d9e0f1a2b3c4d", "Old sync");
        await Detections().PushAsync([new ParsedRule("1", "Retired rule", ["T1078"], null, true)]);
        As("api:ffffffffffffffffffffffffffffffff", "Splunk sync");
        await Detections().PushAsync([new ParsedRule("1", "Splunk accounts", ["T1110"], null, true), new ParsedRule("2", "Splunk VPN", ["T1133"], null, true)]);
        As("api:22222222222222222222222222222222", "Quiet sync");
        await Detections().PushAsync([new ParsedRule("1", "Quiet rule", ["T1566"], null, true)]);
        await using (var ctx = _db.Context())
        {
            foreach (var r in await ctx.DetectionRules.Where(r => r.Name == "Quiet rule" || r.Name == "Retired rule").ToListAsync())
                r.LastSeenAtUtc = now.AddDays(-(DetectionService.PushQuietDays * 3));
            ctx.ApiTokens.AddRange(Token("Old sync", now.AddDays(300), revoked: now.AddDays(-1)), Token("Splunk sync", now.AddDays(-2)),
                Token("Splunk sync", now.AddDays(88)), Token("Quiet sync", now.AddDays(200)));
            await ctx.SaveChangesAsync();
        }
        As("S-1-5-21-TEST-1", "Test Analyst");

        (await Detections().ListAsync()).Rules.Select(r => (r.Rule.Name, r.Current)).Should().BeEquivalentTo(
            [("Retired rule", false), ("Splunk accounts", true), ("Splunk VPN", true), ("Quiet rule", true)],
            "a pipeline that pushes when its rules change is quiet, not gone; only a set no token can push again stops counting");
        (await Detections().ForTechniquesAsync(["T1078", "T1566"]))!.ToDictionary(t => t.Key, t => t.Value.Label)
            .Should().Equal(new Dictionary<string, string> { ["T1078"] = "no rule", ["T1566"] = "1 rule" });
        var sets = await Detections().CallersAsync();
        sets.Select(s => (s.Name, s.Rules, s.Quiet, s.Counts)).Should().Equal(
            [("the token “splunk sync”", 2, false, true), ("the token “old sync”", 1, true, false), ("the token “quiet sync”", 1, true, true)],
            "Splunk's first token expired, but it was issued again under the same name");

        // A token issued again under the old name brings its set back before it pushes.
        await using (var ctx = _db.Context())
        {
            ctx.ApiTokens.Add(Token("old SYNC", now.AddDays(90)));
            await ctx.SaveChangesAsync();
        }
        (await Detections().ListAsync()).Rules.Single(r => r.Rule.Name == "Retired rule").Current.Should().BeTrue();

        (await Detections().RemoveCallerAsync(sets[0].Caller)).Should().Be(2);
        (await Detections().ListAsync()).Rules.Select(r => r.Rule.Name).Should().BeEquivalentTo(["Retired rule", "Quiet rule"]);
        (await Detections().CallersAsync()).Select(s => s.Caller).Should().Equal([sets[1].Caller, sets[2].Caller]);
        await FluentActions.Invoking(() => Detections().RemoveCallerAsync(sets[0].Caller)).Should().ThrowAsync<KeyNotFoundException>();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await FluentActions.Invoking(() => Detections().CallersAsync()).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => Detections().RemoveCallerAsync(sets[1].Caller)).Should().ThrowAsync<ForbiddenException>();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task EachImportAndPush_IsOneAuditEntry_WithWhoseAndWhatItDid(string provider)
    {
        _db = _db.On(provider);
        await Detections().ImportAsync(DetectionSource.Csv, "rules.csv", Csv("First,T1078,yes,r1", "Second,T1110,yes,r2"));
        await Detections().ImportAsync(DetectionSource.Csv, "rules.csv", Csv("First renamed,T1078,yes,r1", "Third,T1566,yes,r3"));
        (_db.User.UserId, _db.User.DisplayName) = ("api:0a1b2c3d4e5f4a6b8c7d9e0f1a2b3c4d", "Splunk sync");
        await Detections().PushAsync([new ParsedRule("1", "Splunk accounts", ["T1078"], null, true)]);

        await using var ctx = _db.Context();
        var entries = (await ctx.AuditLog.AsNoTracking().Where(e => e.EntityType == nameof(DetectionRule)).ToListAsync())
            .Select(e => (e.UserName, e.Action, Changes: JsonSerializer.Deserialize<Dictionary<string, string?[]>>(e.ChangesJson!)!.ToDictionary(c => c.Key, c => c.Value[1]))).ToList();
        entries.Should().HaveCount(3, "one entry for each import or push, not one for each rule");
        entries.Should().ContainSingle(e => e.Changes.GetValueOrDefault("Not in this import") == "1").Which.Should().Match<(string UserName, string Action, Dictionary<string, string?> Changes)>(e =>
            e.UserName == "Test Analyst" && e.Changes["Name"] == "Rules imported from CSV" && e.Changes["File"] == "rules.csv"
            && e.Changes["Added"] == "1" && e.Changes["Changed"] == "1" && e.Changes["Unchanged"] == "0");
        entries.Should().ContainSingle(e => e.UserName == "Splunk sync").Which.Should().Match<(string UserName, string Action, Dictionary<string, string?> Changes)>(e =>
            e.Action == "Modified" && e.Changes["Name"] == "Rules pushed by the token “splunk sync”" && e.Changes["Added"] == "1" && e.Changes["Not in this push"] == "0");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TwoLongIds_ThatDifferOnlyAtTheEnd_StayTwoRules_AndThePushSaysSo(string provider)
    {
        _db = _db.On(provider);
        var stem = "rules/" + new string('a', 290);
        var result = await Detections().PushAsync([new ParsedRule(stem + "/one", "First long", ["T1078"], null, true), new ParsedRule(stem + "/two", "Second long", ["T1110"], null, true)]);

        result.Added.Should().Be(2);
        result.Problems.Should().HaveCount(2).And.AllSatisfy(p => p.Should().Contain("keeps its start and a hash of the rest"));
        var rules = (await Detections().ListAsync()).Rules;
        rules.Should().HaveCount(2).And.OnlyContain(r => r.Current && r.Rule.ExternalId.Length <= DetectionRule.MaxExternalId);
        (await Detections().PushAsync([new ParsedRule(stem + "/one", "First long", ["T1078"], null, true)])).Unchanged.Should().Be(1, "the same long id is the same rule next time");
    }
}

/// <summary>EP-79's pages and the API push over HTTP.</summary>
public class DetectionPageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task ThePages_Render_AndAToken_PushesRules()
    {
        string token;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            token = (await scope.ServiceProvider.GetRequiredService<ApiTokenService>().IssueAsync("Detections push", [ApiScopes.DetectionsWrite], TlpLevel.Amber, 1)).Token;
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/detections", new { rules = new[] { new { id = "page-1", name = "page-test pushed rule", techniques = new[] { "T1078" } } } });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").GetInt32().Should().Be(1);
        (await client.PostAsJsonAsync("/api/v1/detections", new { rules = Array.Empty<object>() })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/v1/detections", new { rules = new object?[] { null } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var admin = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.Admin.DetectionsAdmin>(app, "/admin/detections");
        (await admin.Html()).Should().Contain("Detection rules").And.Contain("page-test pushed rule").And.Contain("detections:write").And.Contain("Pushed sets");
        await using var coverage = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.CoveragePage>(app, "/coverage");
        (await coverage.Html()).Should().Contain("Coverage").And.Contain("Techniques briefed").And.Contain("API:");
    }

    [Fact]
    public async Task TheSampleCsv_IsOfferedAndServed_AndImportsCleanly()
    {
        await using (var admin = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.Admin.DetectionsAdmin>(app, "/admin/detections"))
            (await admin.Html()).Should().Contain($"href=\"{Tide.Web.Components.Pages.Admin.DetectionsAdmin.SampleCsv}\"").And.Contain("Sample CSV");

        var response = await app.CreateClient().GetAsync("/" + Tide.Web.Components.Pages.Admin.DetectionsAdmin.SampleCsv);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");

        var problems = new List<string>();
        var rules = DetectionParsers.Csv("sample.csv", await response.Content.ReadAsStringAsync(), problems);

        problems.Should().BeEmpty("the sample is what a clean file looks like");
        rules.Should().HaveCount(6).And.OnlyContain(r => r.Techniques.Count > 0);
        rules.Should().Contain(r => r.Name == "Shadow copies deleted, then mass file renames" && r.Techniques.SequenceEqual(new[] { "T1490", "T1486" }), "a quoted name keeps its comma");
        rules.Should().Contain(r => r.ExternalId == "SOC-0005" && !r.Enabled);
        rules.Should().Contain(r => r.ExternalId == "Beaconing to a newly registered domain" && r.Enabled && r.Techniques.Count == 2, "no id: known by its name; blank enabled: yes");
    }
}
