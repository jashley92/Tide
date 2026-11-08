using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The team's saved hunt queries: who keeps them, one name per language, the stale edit, retiring and deleting, and
/// adding one to an action with its window filled in.
/// </summary>
public class SavedQueryServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public SavedQueryServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    private const string Shells = "DeviceProcessEvents\n| where Timestamp > ago({{days}}d)\n| where InitiatingProcessFileName =~ \"php-fpm\" and FileName in~ (\"sh\", \"bash\")";

    private Task<Guid> Save(string name, string dialect = "kql", string text = Shells, string? description = null) =>
        _db.SavedQueries().SaveAsync(new SavedQueryForm { Name = name, Dialect = dialect, Text = text, Description = description });

    // An action on a team of its own, raised by an analyst (an administrator adds the team).
    private async Task<ActionItem> Action(Guid? articleId = null)
    {
        var (id, roles) = (_db.User.UserId, _db.User.Roles);
        (_db.User.UserId, _db.User.Roles) = ("S-1-5-21-SETUP", new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin });
        var team = await _db.Teams().AddAsync("Cyber Defense " + Guid.NewGuid().ToString("N")[..4], null, null);
        (_db.User.UserId, _db.User.Roles) = (id, roles);
        var svc = _db.Actions();
        return (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Hunt for web shells", Kind = ActionKind.ThreatHunt, TeamId = team, ArticleId = articleId })))!.Action;
    }

    [Fact]
    public async Task WhoeverWorksActions_KeepsTheLibrary_OneNamePerLanguage_AndAViewerOnlyReadsIt()
    {
        var id = await Save(" PHP-FPM spawning a shell ", description: "Behavioural; no indicators needed.");

        var q = (await _db.SavedQueries().GetAsync(id))!;
        (q.Name, q.Dialect, q.Description, q.CreatedByName, q.CreatedBy).Should().Be(("PHP-FPM spawning a shell", "kql", "Behavioural; no indicators needed.", "Test Analyst", "S-1-5-21-TEST-1"));
        q.ModifiedAtUtc.Should().BeNull();
        await FluentActions.Invoking(() => Save("php-fpm SPAWNING a shell")).Should().ThrowAsync<ArgumentException>().WithMessage("*already a Microsoft Sentinel / Defender (KQL) saved query*");
        await Save("PHP-FPM spawning a shell", "xql", "dataset = xdr_data | filter actor_process_image_name = \"php-fpm\"");
        await FluentActions.Invoking(() => Save("Nothing", text: "  ")).Should().ThrowAsync<ArgumentException>().WithMessage("The query is empty.");
        await FluentActions.Invoking(() => Save("Odd", "spl")).Should().ThrowAsync<ArgumentException>().WithMessage("Choose a query language.");
        await FluentActions.Invoking(() => Save(" ")).Should().ThrowAsync<ArgumentException>().WithMessage("*needs a name*");
        (await _db.SavedQueries().ListAsync()).Select(r => r.Query.Dialect).Should().Equal(["xql", "kql"], "by language, as the languages are listed");

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        await Save("Owner's sweep");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Viewer };
        (await _db.SavedQueries().ListAsync()).Should().HaveCount(3);
        _db.SavedQueries().CanWrite.Should().BeFalse();
        await FluentActions.Invoking(() => Save("Viewer's")).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => _db.SavedQueries().SetRetiredAsync(id, true)).Should().ThrowAsync<ForbiddenException>();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Invoking(() => _db.SavedQueries().ListAsync()).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task AnEdit_BegunBeforeAColleaguesEdit_IsRefused_AndTheirsStands()
    {
        var id = await Save("Encoded PowerShell");
        var q = (await _db.SavedQueries().GetAsync(id))!;
        var mine = SavedQueryForm.From(q);
        var theirs = SavedQueryForm.From(q);

        (_db.User.UserId, _db.User.DisplayName) = ("S-1-5-21-TEST-2", "Robin");
        theirs.Text = Shells + "\n| take 100";
        await _db.SavedQueries().SaveAsync(theirs);

        (_db.User.UserId, _db.User.DisplayName) = ("S-1-5-21-TEST-1", "Test Analyst");
        mine.Name = "Encoded PowerShell (mine)";
        await FluentActions.Invoking(() => _db.SavedQueries().SaveAsync(mine)).Should().ThrowAsync<StaleEditException>().WithMessage("Someone else changed this saved query*");

        var now = (await _db.SavedQueries().GetAsync(id))!;
        (now.Name, now.Text, now.ModifiedByName).Should().Be(("Encoded PowerShell", Shells + "\n| take 100", "Robin"));
        now.ModifiedAtUtc.Should().NotBeNull();
        now.EditVersion.Should().NotBe(q.EditVersion);

        await using var ctx = _db.Context();
        (await ctx.AuditLog.Where(e => e.EntityType == nameof(SavedQuery)).Select(e => e.Action).ToListAsync()).Should().BeEquivalentTo(["Added", "Modified"],
            "each write is in the audit trail, as other team settings are");
        Tide.Infrastructure.Persistence.ChangeInterceptor.TopicsOf(now).Should().Equal(ChangeFeed.SavedQueries);
    }

    [Fact]
    public async Task ARetiredQuery_IsNotOffered_NorAdded_UntilRestored()
    {
        var id = await Save("Old proxy sweep");
        var action = await Action();
        await _db.SavedQueries().SetRetiredAsync(id, true);

        (await _db.SavedQueries().ListAsync()).Should().BeEmpty();
        (await _db.SavedQueries().ListAsync(includeRetired: true)).Should().ContainSingle().Which.Query.IsRetired.Should().BeTrue();
        await FluentActions.Invoking(() => _db.Actions().AddSavedHuntAsync(action.Id, id)).Should().ThrowAsync<InvalidOperationException>().WithMessage("Old proxy sweep is retired*");

        await _db.SavedQueries().SetRetiredAsync(id, false);
        (await _db.Actions().AddSavedHuntAsync(action.Id, id)).SavedQueryId.Should().Be(id);
    }

    [Fact]
    public async Task OnlyWhoeverSavedIt_OrAnAdministrator_DeletesOne_AndHuntsAddedFromItKeepItsName()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        (_db.User.UserId, _db.User.DisplayName) = ("S-1-5-21-OWNER", "Olu Owner");
        var owners = await Save("Owner's sweep");
        var another = await Save("Owner's other sweep");

        (_db.User.UserId, _db.User.DisplayName, _db.User.Roles) = ("S-1-5-21-TEST-1", "Test Analyst", new HashSet<AppRole> { AppRole.Analyst });
        var hunt = await _db.Actions().AddSavedHuntAsync((await Action()).Id, owners);
        _db.SavedQueries().CanDelete((await _db.SavedQueries().GetAsync(owners))!).Should().BeFalse();
        await FluentActions.Invoking(() => _db.SavedQueries().DeleteAsync(owners)).Should().ThrowAsync<ForbiddenException>().WithMessage("*Retire it instead*");

        (_db.User.UserId, _db.User.Roles) = ("S-1-5-21-OWNER", new HashSet<AppRole> { AppRole.ActionOwner });
        await _db.SavedQueries().DeleteAsync(owners);
        (await _db.SavedQueries().GetAsync(owners)).Should().BeNull();
        await using (var ctx = _db.Context())
        {
            var kept = await ctx.HuntQueries.SingleAsync(h => h.Id == hunt.Id);
            (kept.SavedQueryId, kept.SavedQueryName).Should().Be(((Guid?)null, "Owner's sweep"));
        }

        (_db.User.UserId, _db.User.Roles) = ("S-1-5-21-ADMIN", new HashSet<AppRole> { AppRole.Admin });
        await _db.SavedQueries().DeleteAsync(another);
        (await _db.SavedQueries().ListAsync(includeRetired: true)).Should().BeEmpty();
    }

    [Fact]
    public async Task AddingOne_FillsDaysWithTheActionsWindow_SaysSoFirst_AndNamesWhereItCameFrom()
    {
        var article = await _db.Articles().CreateAsync("PHP-FPM shells", "Vendor", null, TlpLevel.Clear);
        await using (var ctx = _db.Context())
        {
            (await ctx.Articles.SingleAsync(x => x.Id == article)).PublishedAtUtc = DateTimeOffset.UtcNow.AddDays(-60);
            await ctx.SaveChangesAsync();
        }
        var action = await Action(article);
        var id = await Save("PHP-FPM spawning a shell");

        var hunt = await _db.Actions().AddSavedHuntAsync(action.Id, id);

        hunt.Text.Should().StartWith("// From the saved query \"PHP-FPM spawning a shell\", looking back 67 days.\nDeviceProcessEvents\n")
            .And.Contain("ago(67d)", "a week before the article, as a generated query looks").And.NotContain(QueryDialects.DaysPlaceholder);
        HuntQueryBuilder.WindowOf(hunt.GeneratedText).Should().Be(67, "the close step reads the window back");
        (hunt.Dialect, hunt.SavedQueryId, hunt.SavedQueryName).Should().Be(("kql", id, "PHP-FPM spawning a shell"));
        (await _db.Actions().GetAsync(action.Number))!.Action.Events.Should().Contain(e => e.Text == "Added the saved Microsoft Sentinel / Defender (KQL) hunt query \"PHP-FPM spawning a shell\".");

        // Without an article the window is the default; without {{days}} the query sets its own range, and no window is claimed.
        var plain = await Action();
        (await _db.Actions().AddSavedHuntAsync(plain.Id, id)).Text.Should().Contain($"ago({HuntQueryBuilder.DefaultDays}d)");
        var ranged = await Save("Last day of logons", text: "SigninLogs\n| where TimeGenerated > ago(1d)");
        var own = await _db.Actions().AddSavedHuntAsync(plain.Id, ranged);
        own.Text.Should().StartWith("// From the saved query \"Last day of logons\".\nSigninLogs");
        HuntQueryBuilder.WindowOf(own.GeneratedText).Should().BeNull();
        await _db.Actions().AddSavedHuntAsync(plain.Id, id);

        var used = (await _db.SavedQueries().ListAsync()).ToDictionary(r => r.Query.Name, r => r.Actions);
        used.Should().Equal(new Dictionary<string, int> { ["Last day of logons"] = 1, ["PHP-FPM spawning a shell"] = 2 }, "each action once, however often it went on");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Viewer };
        await FluentActions.Invoking(() => _db.Actions().AddSavedHuntAsync(plain.Id, id)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task HowOftenEachIsUsed_CountsEachActionOnce_ForTheQueriesListed()
    {
        var a = await ActionWithArticle((IndicatorType.IpAddress, "203.0.113.45"));
        var b = await Action();
        var sweep = await Save("Sweep", text: "x in ({{values}})");
        var shells = await Save("Shells");
        var unused = await Save("Unused");
        await _db.Actions().AddSavedHuntAsync(a.Id, sweep);
        await _db.Actions().AddSavedHuntAsync(a.Id, sweep);
        await _db.Actions().AddSavedHuntAsync(a.Id, shells);
        await _db.Actions().AddSavedHuntAsync(b.Id, shells);
        await _db.Actions().AddHandWrittenHuntAsync(b.Id, "kql", "SigninLogs");
        await _db.SavedQueries().SetRetiredAsync(shells, true);

        (await _db.SavedQueries().ListAsync()).ToDictionary(r => r.Query.Id, r => r.Actions)
            .Should().Equal(new Dictionary<Guid, int> { [sweep] = 1, [unused] = 0 }, "a retired query isn't read, nor counted");
        (await _db.SavedQueries().ListAsync(includeRetired: true)).ToDictionary(r => r.Query.Id, r => r.Actions)
            .Should().Equal(new Dictionary<Guid, int> { [sweep] = 1, [shells] = 2, [unused] = 0 }, "each action once, however often it went on");
        (await _db.SavedQueries().OfferedAsync()).Select(q => q.Id).Should().BeEquivalentTo([sweep, unused], "the card offers the live ones");
    }

    [Fact]
    public async Task SavingAHunt_ForTheTeam_TakesItsLanguageAndText_WithoutTheLineTideWrote()
    {
        var articles = _db.Articles();
        var article = await articles.CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await articles.GetAsync(article))!);
        form.Indicators = [new() { Type = IndicatorType.IpAddress, Value = "203.0.113.45" }];
        await articles.SaveAsync(form);
        var action = await Action(article);
        var generated = await _db.Actions().GenerateHuntAsync(action.Id, "kql");

        var id = await _db.SavedQueries().SaveFromHuntAsync(generated.Id, "VPN IPs", "From A-1", await _db.SavedQueries().DraftTextAsync(generated));

        var q = (await _db.SavedQueries().GetAsync(id))!;
        q.Dialect.Should().Be("kql");
        q.Text.Should().StartWith("// IPv4 addresses (1)").And.Contain("\"203.0.113.45\"").And.NotContain("Generated by TIDE");
        HuntQueryBuilder.WithoutHeader("kql", "// From the saved query \"X\", looking back 30 days.\nSigninLogs").Should().Be("SigninLogs");
        HuntQueryBuilder.WithoutHeader("kql", "// My own note\nSigninLogs").Should().Be("// My own note\nSigninLogs", "only TIDE's own line goes");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Viewer };
        await FluentActions.Invoking(() => _db.SavedQueries().SaveFromHuntAsync(generated.Id, "Mine", null, "x")).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => _db.SavedQueries().DraftTextAsync(generated)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task SavingAHunt_AddedFromASavedQuery_OffersThatQuerysText_PlaceholdersAndAll_UntilItsDeleted()
    {
        var action = await ActionWithArticle((IndicatorType.IpAddress, "203.0.113.45"), (IndicatorType.Domain, "evil.example"));
        const string template = "DeviceNetworkEvents\n| where Timestamp > ago({{days}}d)\n| where RemoteUrl has_any ({{values:domain}})";
        var id = await Save("Their domains", text: template);
        var hunt = await _db.Actions().AddSavedHuntAsync(action.Id, id);
        hunt.Text.Should().Contain("// IPv4 addresses: not in this saved query").And.Contain("\"evil.example\"");

        (await _db.SavedQueries().DraftTextAsync(hunt)).Should().Be(template, "the team's copy keeps {{values}} and {{days}}, not this action's values, window or left-out lines");
        var tuned = await _db.SavedQueries().SaveFromHuntAsync(hunt.Id, "Their domains (tuned)", null, await _db.SavedQueries().DraftTextAsync(hunt) + "\n| take 100");
        (await _db.SavedQueries().GetAsync(tuned))!.Text.Should().Be(template + "\n| take 100");

        await _db.SavedQueries().DeleteAsync(id);
        (await _db.SavedQueries().DraftTextAsync(hunt)).Should().Be(HuntQueryBuilder.WithoutHeader("kql", hunt.Text), "with the saved query gone, the hunt as it stands");
    }

    private async Task<ActionItem> ActionWithArticle(params (IndicatorType Type, string Value)[] indicators)
    {
        var articles = _db.Articles();
        var article = await articles.CreateAsync("Edge VPN zero-day " + Guid.NewGuid().ToString("N")[..4], "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await articles.GetAsync(article))!);
        form.Indicators = [.. indicators.Select(i => new IndicatorForm { Type = i.Type, Value = i.Value })];
        await articles.SaveAsync(form);
        return await Action(article);
    }

    [Fact]
    public async Task ItsValues_AreTheActionsIndicators_WrittenAsTheLanguageWritesThem_AndAKindsPlaceholder_LeavesTheRestOut()
    {
        var action = await ActionWithArticle((IndicatorType.IpAddress, "203.0.113.45"), (IndicatorType.Domain, "evil.example"));
        var all = await Save("Anything they touched", text: "DeviceNetworkEvents\n| where RemoteIP in ({{values}}) or RemoteUrl has_any ({{values}})");
        var domains = await Save("Their domains", text: "DeviceNetworkEvents\n| where RemoteUrl has_any ({{values:Domain}})");

        var every = await _db.Actions().AddSavedHuntAsync(action.Id, all);
        every.Text.Should().StartWith("// From the saved query \"Anything they touched\" with 2 indicator(s).\nDeviceNetworkEvents\n")
            .And.Contain("\"203.0.113.45\"").And.Contain("\"evil.example\"").And.NotContain("{{values");
        HuntQueryBuilder.LeftOut(every.GeneratedText).Should().Be(0, "one {{values}} takes every value");

        var some = await _db.Actions().AddSavedHuntAsync(action.Id, domains);
        some.Text.Should().Contain("has_any (\"evil.example\")").And.NotContain("203.0.113.45")
            .And.Contain("// IPv4 addresses: not in this saved query; 1 value(s) not included.");
        HuntQueryBuilder.LeftOut(some.GeneratedText).Should().Be(1, "No hits, close asks for what it left out, as a generated query's does");
        await _db.Actions().RemoveHuntAsync(every.Id);
        await FluentActions.Invoking(() => _db.Actions().CloseWithNoHitsAsync(some.Id, 30, checkedLeftOut: false)).Should().ThrowAsync<ArgumentException>().WithMessage("The query left out 1 value*");

        HuntQueryBuilder.FromSaved(QueryDialects.Find("kql")!, "Q", "x in ({{values}})", 30, [new ArticleIndicator { Type = IndicatorType.FileName, Value = "a\"b.exe" }])
            .Should().Contain("x in (\"a\\\"b.exe\")", "quoted and escaped as a template's values are");
    }

    [Fact]
    public async Task AQueryNeedingValues_TheActionHasNone_IsRefused_AndAKindTideDoesntKnow_IsCaughtWhenSaved()
    {
        var bare = await Action();
        var ips = await ActionWithArticle((IndicatorType.IpAddress, "203.0.113.45"));
        var all = await Save("Every value", text: "x in ({{values}})");
        var hashes = await Save("Hashes only", text: "x in ({{values:sha256}})");

        await FluentActions.Invoking(() => _db.Actions().AddSavedHuntAsync(bare.Id, all)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*and the action has none*");
        await FluentActions.Invoking(() => _db.Actions().AddSavedHuntAsync(ips.Id, hashes)).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("This saved query needs sha-256 hashes ({{values:sha256}}), and the action has none.");
        await FluentActions.Invoking(() => Save("Typo", text: "x in ({{values:shaa256}})")).Should().ThrowAsync<ArgumentException>().WithMessage("{{values:shaa256}} isn't a kind of indicator TIDE knows*");
        (await _db.Actions().GetAsync(bare.Number))!.Action.Hunts.Should().BeEmpty();
    }

    [Fact]
    public async Task AnActionWithNoArticle_KeepsIndicatorsOfItsOwn_AndGenerateAndValuesUseThem()
    {
        var action = await Action();
        var svc = _db.Actions();

        (await svc.AddIndicatorsAsync(action.Id, [new() { Type = IndicatorType.IpAddress, Value = "203.0.113[.]9" }, new() { Type = IndicatorType.IpAddress, Value = "203.0.113.9" }]))
            .Should().Be(1, "refanged and folded, as an article's are");
        (await svc.AddIndicatorsAsync(action.Id, [new() { Type = IndicatorType.IpAddress, Value = "203.0.113.9" }])).Should().Be(0);
        (await svc.HuntSourcesAsync(action.Id)).Should().Be(new HuntSources(1, 1, 0));

        (await svc.GenerateHuntAsync(action.Id, "kql")).Text.Should().Contain("\"203.0.113.9\"");
        (await svc.AddSavedHuntAsync(action.Id, await Save("Every value", text: "x in ({{values}})"))).Text.Should().Contain("x in (\"203.0.113.9\")");
        var read = (await svc.GetAsync(action.Number))!.Action;
        read.Indicators.Should().ContainSingle().Which.Value.Should().Be("203.0.113.9");
        read.Events.Should().Contain(e => e.Text == "Added 1 indicator.");
        await using (var ctx = _db.Context())
            (await ctx.AuditLog.CountAsync(e => e.EntityType == nameof(ActionIndicator) && e.Action == "Added")).Should().Be(1);
        Tide.Infrastructure.Persistence.ChangeInterceptor.TopicsOf(read.Indicators[0]).Should().Equal(ChangeFeed.Action(action.Id));

        await FluentActions.Invoking(() => svc.AddIndicatorsAsync(action.Id, Enumerable.Range(0, 501).Select(i => new IndicatorForm { Type = IndicatorType.IpAddress, Value = $"10.0.{i / 256}.{i % 256}" })))
            .Should().ThrowAsync<ArgumentException>().WithMessage("An action can carry at most 500 indicators.");
        _db.User.Roles = new HashSet<AppRole> { AppRole.Viewer };
        await FluentActions.Invoking(() => svc.AddIndicatorsAsync(action.Id, [new() { Type = IndicatorType.Domain, Value = "evil.example" }])).Should().ThrowAsync<ForbiddenException>();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await svc.RemoveIndicatorAsync(read.Indicators[0].Id);
        (await svc.HuntSourcesAsync(action.Id)).Indicators.Should().Be(0);
    }

    [Fact]
    public async Task AQueryTideFilledWithManyIndicators_IsAdded_AndItsEditIsSaved_ButAHandWrittenOneIsStillHeldToTheLimit()
    {
        var action = await Action();
        var svc = _db.Actions();
        // 400 SHA-256 hashes: about 27,000 characters once quoted, more than a person may write by hand.
        await svc.AddIndicatorsAsync(action.Id, Enumerable.Range(0, 400).Select(i => new IndicatorForm { Type = IndicatorType.FileHash, Value = i.ToString("x64") }));

        var saved = await svc.AddSavedHuntAsync(action.Id, await Save("Hash sweep", text: "DeviceFileEvents\n| where SHA256 in~ ({{values}})"));
        saved.Text.Length.Should().BeGreaterThan(20000);
        await svc.SaveHuntTextAsync(saved.Id, saved.Text + "\n| take 100");

        var generated = await svc.GenerateHuntAsync(action.Id, "kql");
        generated.Text.Length.Should().BeGreaterThan(20000);
        await svc.SaveHuntTextAsync(generated.Id, generated.Text + "\n| take 100");

        var hunts = (await svc.GetAsync(action.Number))!.Action.Hunts;
        hunts.Should().HaveCount(2).And.OnlyContain(h => h.Text.EndsWith("| take 100"));
        await FluentActions.Invoking(() => svc.AddHandWrittenHuntAsync(action.Id, "kql", new string('x', 20001)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("That query is too long.");
        await FluentActions.Invoking(() => svc.SaveHuntTextAsync(generated.Id, generated.GeneratedText + new string('x', 20001)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("That query is too long.");
    }

    [Fact]
    public async Task AnActionFromAnArticle_AddsToTheArticle_ForAnAnalyst_AndAnOwnerWhoWorksItIsToldToAskOne()
    {
        var action = await ActionWithArticle((IndicatorType.IpAddress, "203.0.113.45"));
        var editorOpen = ArticleForm.From((await _db.Articles().GetAsync(action.ArticleId!.Value))!);

        (await _db.Actions().AddIndicatorsAsync(action.Id, [new() { Type = IndicatorType.Domain, Value = "evil[.]example" }, new() { Type = IndicatorType.IpAddress, Value = "203.0.113.45" }]))
            .Should().Be(1, "the article had the address already");

        var article = (await _db.Articles().GetAsync(action.ArticleId.Value))!;
        article.Indicators.Select(i => i.Value).Should().BeEquivalentTo(["203.0.113.45", "evil.example"]);
        (await _db.Actions().HuntSourcesAsync(action.Id)).Should().Be(new HuntSources(2, 2, 1), "on the article, not the action");
        (await _db.Actions().GetAsync(action.Number))!.Action.Indicators.Should().BeEmpty();
        (await _db.Actions().GetAsync(action.Number))!.Action.Events.Should().Contain(e => e.Text == "Added 1 indicator to the article.");
        editorOpen.Bluf = "Edited meanwhile";
        await FluentActions.Invoking(() => _db.Articles().SaveAsync(editorOpen)).Should().ThrowAsync<StaleEditException>("an editor open on the older copy would drop the new indicator");

        (_db.User.UserId, _db.User.Roles) = ("S-1-5-21-SETUP", new HashSet<AppRole> { AppRole.Admin });
        await _db.Teams().AddMemberAsync(action.TeamId, "S-1-5-21-OWNER", "Olu Owner");
        (_db.User.UserId, _db.User.DisplayName, _db.User.Roles) = ("S-1-5-21-OWNER", "Olu Owner", new HashSet<AppRole> { AppRole.ActionOwner });
        await _db.Actions().TakeAsync(action.Id);   // works the action
        await FluentActions.Invoking(() => _db.Actions().AddIndicatorsAsync(action.Id, [new() { Type = IndicatorType.Domain, Value = "other.example" }]))
            .Should().ThrowAsync<ForbiddenException>().WithMessage(ActionService.OnTheArticle);
    }
}

/// <summary>The saved queries on an action's Hunt queries card, and their own page.</summary>
public class SavedQueryPageTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Viewer = new("dev:viewer-val", "Val Viewer", "Viewer");

    private static IReadOnlyList<ToastService.Toast> Toasts<T>(Rendered<T> r) => r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts;

    private async Task<int> HuntAction(string title)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0].Id;
        return await scope.ServiceProvider.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = title, Kind = ActionKind.ThreatHunt, TeamId = team });
    }

    private async Task<Guid> Saved(string name, string dialect, string text = "DeviceProcessEvents\n| where Timestamp > ago({{days}}d)", string? description = null)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return await scope.ServiceProvider.GetRequiredService<SavedQueryService>().SaveAsync(new SavedQueryForm { Name = name, Dialect = dialect, Text = text, Description = description });
    }

    private async Task<ActionItem> Read(int number)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<ActionService>().GetAsync(number))!.Action;
    }

    [Fact]
    public async Task TheHuntPanel_OffersSavedQueriesInTheChosenLanguage_AndAddsOne()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var xql = await Saved("Office spawning PowerShell " + tag, "xql", "dataset = xdr_data | filter actor_process_image_name = \"winword.exe\"", "Macro droppers");
        await Saved("Sign-ins from new countries " + tag, "kql");
        var number = await HuntAction("Hunt for macro droppers " + tag);

        await using (var r = await RenderAsync<HuntPanel>(app, "/actions/" + number, new() { ["Action"] = await Read(number), ["CanWork"] = true }))
        {
            (await r.Html()).Should().Contain("aria-expanded=\"false\" aria-controls=\"hunt-saved-picker\"").And.Contain(">Add a saved query</button>");
            await r.Click(p => p.TogglePicker());
            var html = await r.Html();
            html.Should().Contain("id=\"hunt-saved-picker\"").And.Contain("Office spawning PowerShell " + tag).And.Contain("Macro droppers")
                .And.Contain("aria-label=\"Add Office spawning PowerShell " + tag + "\"")
                .And.NotContain("Sign-ins from new countries " + tag, "only the chosen language's, until All languages is ticked")
                .And.Contain("href=\"hunts/saved\"", "relative, so it works under a sub-path");
            r.Component.Offered.Should().Contain(q => q.Id == xql);

            await r.Click(p => p.AddSaved(r.Component.Offered.Single(q => q.Id == xql)));
            Toasts(r).Select(t => t.Message).Should().Contain("Office spawning PowerShell " + tag + " added.");
        }

        var action = await Read(number);
        var hunt = action.Hunts.Should().ContainSingle().Subject;
        (hunt.SavedQueryId, hunt.Dialect).Should().Be(((Guid?)xql, "xql"));
        hunt.Text.Should().StartWith("// From the saved query \"Office spawning PowerShell " + tag + "\".\n");
        await using var again = await RenderAsync<HuntPanel>(app, "/actions/" + number, new() { ["Action"] = action, ["CanWork"] = true });
        (await again.Html()).Should().Contain("saved query: Office spawning PowerShell " + tag).And.NotContain(">hand-written<");
    }

    [Fact]
    public async Task SaveForTheTeam_AsksForANameAndDescription_Prefilled_AndSavesTheHuntAsItStands()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var number = await HuntAction("Hunt for web shells " + tag);
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
            await actions.AddHandWrittenHuntAsync((await actions.GetAsync(number))!.Action.Id, "kql", "DeviceProcessEvents\n| where InitiatingProcessFileName =~ \"php-fpm\"");
        }
        var action = await Read(number);

        await using var r = await RenderAsync<HuntPanel>(app, "/actions/" + number, new() { ["Action"] = action, ["CanWork"] = true });
        (await r.Html()).Should().Contain("Save for the team</button>");
        await r.Press(p => p.StartSave(action.Hunts[0]));
        var html = await r.HtmlOnce("for the team</h2>");
        html.Should().Contain("Save the Microsoft Sentinel / Defender (KQL) query for the team</h2>")
            .And.Contain($"id=\"hunt-s-name\" class=\"form-control form-control-sm\" maxlength=\"100\" value=\"Hunt for web shells {tag}\"")
            .And.Contain($"value=\"From A-{number}, Hunt for web shells {tag}\"")
            .And.Contain("<label class=\"tide-field-label\" for=\"hunt-s-text\">Query</label>");

        await r.Click(p => p.SaveForTeam());

        Toasts(r).Select(t => t.Message).Should().Contain($"Saved for the team as Hunt for web shells {tag}.");
        await using var scope2 = SignedIn(app, SignIn.Analyst);
        var saved = (await scope2.ServiceProvider.GetRequiredService<SavedQueryService>().ListAsync()).Single(s => s.Query.Name == "Hunt for web shells " + tag).Query;
        (saved.Dialect, saved.Text, saved.Description).Should().Be(("kql", "DeviceProcessEvents\n| where InitiatingProcessFileName =~ \"php-fpm\"", $"From A-{number}, Hunt for web shells {tag}"));
    }

    [Fact]
    public async Task SaveForTheTeam_OnAHuntFromASavedQuery_OffersThatQuerysText()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var id = await Saved("Sign-ins from new countries " + tag, "kql", "SigninLogs\n| where TimeGenerated > ago({{days}}d)");
        var number = await HuntAction("Hunt for odd sign-ins " + tag);
        var added = await Read(number);
        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<ActionService>().AddSavedHuntAsync(added.Id, id);
        var action = await Read(number);

        await using var r = await RenderAsync<HuntPanel>(app, "/actions/" + number, new() { ["Action"] = action, ["CanWork"] = true });
        await r.Press(p => p.StartSave(action.Hunts[0]));
        var html = await r.HtmlOnce("for the team</h2>");
        html.Should().Contain($"value=\"Sign-ins from new countries {tag}\"")
            .And.Contain("ago({{days}}d)", "the saved query's own text in the box, not this action's window (the hunt above shows that)");
    }

    [Fact]
    public async Task ThePage_ListsThemByLanguage_WithWhoAndHowOftenUsed_AndAViewerOnlyReads()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var id = await Saved("Rare parent processes " + tag, "fql", "#event_simpleName=ProcessRollup2", "Odd parents of cmd.exe");
        var action = await Read(await HuntAction("Hunt for odd parents " + tag));
        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<ActionService>().AddSavedHuntAsync(action.Id, id);

        await using (var r = await RenderAsync<SavedQueriesPage>(app, "/hunts/saved"))
        {
            var html = await r.Html();
            html.Should().Contain("<h1>Saved queries</h1>").And.Contain("<h2 id=\"sq-fql\">CrowdStrike Falcon / Next-Gen SIEM")
                .And.Contain("Rare parent processes " + tag).And.Contain("Odd parents of cmd.exe").And.Contain("Used on 1 action<")
                .And.Contain("Saved by Tester, ").And.Contain("New saved query</button>")
                .And.Contain($"aria-label=\"Edit Rare parent processes {tag}\"").And.Contain($"aria-label=\"Retire Rare parent processes {tag}\"")
                .And.Contain($"aria-label=\"Delete Rare parent processes {tag}\"", "the tester saved it (and is an administrator)")
                .And.NotContain("style=");
            html.Split("<h1").Length.Should().Be(2);
        }

        await using var viewer = await RenderAsync<SavedQueriesPage>(app, "/hunts/saved", @as: Viewer);
        var read = await viewer.Html();
        read.Should().Contain("Rare parent processes " + tag).And.Contain(" Copy</button>")
            .And.NotContain("New saved query").And.NotContain("aria-label=\"Edit ").And.NotContain("aria-label=\"Retire ").And.NotContain("aria-label=\"Delete ");
    }

    [Fact]
    public async Task ThePagesEdit_KeepsWhatsTyped_WhenAColleagueSavedFirst()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var id = await Saved("Beaconing to new domains " + tag, "kql");

        await using var r = await RenderAsync<SavedQueriesPage>(app, "/hunts/saved");
        await r.Click(p => { p.Edit(p.Rows!.Single(x => x.Query.Id == id).Query); return Task.CompletedTask; });
        (await r.Html()).Should().Contain($"Edit Beaconing to new domains {tag}</h3>").And.Contain("id=\"sq-f-text\"");
        r.Component.Form!.Description = "Mine";

        await using (var scope = SignedIn(app, new SignIn("dev:robin", "Robin", "Analyst")))
        {
            var svc = scope.ServiceProvider.GetRequiredService<SavedQueryService>();
            var theirs = SavedQueryForm.From((await svc.GetAsync(id))!);
            theirs.Description = "Theirs";
            await svc.SaveAsync(theirs);
        }

        await r.Click(p => p.Save());
        Toasts(r).Select(t => t.Message).Should().Contain("Someone else changed this saved query since you opened it. Reload to see their changes.");
        r.Component.Form!.Description.Should().Be("Mine", "what was typed stays");
        await using var check = SignedIn(app, SignIn.Analyst);
        (await check.ServiceProvider.GetRequiredService<SavedQueryService>().GetAsync(id))!.Description.Should().Be("Theirs");
    }

    [Fact]
    public async Task TheHuntPanel_AddsIndicatorsToAnActionWithNoArticle_AndItsGenerateButtonFollows_TheActionReadAgain()
    {
        var number = await HuntAction("Hunt for the beacon " + Guid.NewGuid().ToString("N")[..6]);
        await using var r = await RenderAsync<HuntPanel>(app, "/actions/" + number, new() { ["Action"] = await Read(number), ["CanWork"] = true });
        (await r.Html()).Should().NotContain("Generate from").And.Contain("aria-controls=\"hunt-indicators\">Add indicators</button>");

        await r.Click(p => { p.ToggleIndicators(); return Task.CompletedTask; });
        var box = await r.Html();
        box.Should().Contain("id=\"hunt-indicators\"").And.Contain("Kept on this action, for its hunts.").And.Contain("aria-label=\"New indicator\"")
            .And.Contain("Find indicators in pasted text</summary>", "a hunt doesn't search for CVEs");

        await r.Click(p => p.AddIndicators(new IndicatorAdd.Addition([new IndicatorForm { Type = IndicatorType.IpAddress, Value = "198.51.100.7" }], [], false)));

        Toasts(r).Select(t => t.Message).Should().Contain("Added 1 indicator.");
        var action = await Read(number);
        action.Indicators.Should().ContainSingle().Which.Value.Should().Be("198.51.100.7");
        // The page reads the action again (the panel's Changed) and hands it over, and the card reads its counts once, then.
        await using var again = await RenderAsync<HuntPanel>(app, "/actions/" + number, new() { ["Action"] = action, ["CanWork"] = true });
        (await again.Html()).Should().Contain("Generate from 1 indicator</button>");
    }

    [Fact]
    public async Task TheHuntPanel_TellsAnOwnerWhoCantEditTheArticle_WhereIndicatorsGo()
    {
        int number;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var article = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Edge VPN zero-day (owner)", "Vendor", null, TlpLevel.Clear);
            var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0].Id;
            number = await scope.ServiceProvider.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = "Sweep (owner)", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = article });
        }
        await using var r = await RenderAsync<HuntPanel>(app, "/actions/" + number, new() { ["Action"] = await Read(number), ["CanWork"] = true },
            new SignIn("dev:owner-olu", "Olu Owner", "ActionOwner"));
        await r.Click(p => { p.ToggleIndicators(); return Task.CompletedTask; });

        (await r.Html()).Should().Contain("only analysts can add to an article. Ask one to add them there.").And.NotContain("aria-label=\"New indicator\"");
    }

    [Fact]
    public async Task TheIndicatorBox_FindsWhatsNewInPastedText_AndHandsOverThePicked()
    {
        IndicatorAdd.Addition? handed = null;
        await using var r = await RenderAsync<IndicatorAdd>(app, "/", new()
        {
            ["Have"] = (IReadOnlyCollection<IndicatorForm>)[new IndicatorForm { Type = IndicatorType.IpAddress, Value = "203.0.113.45" }],
            ["Cves"] = false,
            ["Adding"] = (Func<IndicatorAdd.Addition, Task<bool>>)(a => { handed = a; return Task.FromResult(true); }),
        });

        await r.Click(p => { p.FindIn("Seen from 203.0.113.45 and 198.51.100.7, beaconing to evil[.]example (CVE-2024-3400)."); return Task.CompletedTask; });
        var html = await r.Html();
        html.Should().Contain("Add 2 selected</button>", "the address already there isn't offered").And.NotContain(">CVE</span>", "CVEs aren't offered");

        await r.Click(p => p.AddFound());
        handed!.Picked.Should().BeTrue();
        handed.Indicators.Select(i => i.Value).Should().BeEquivalentTo(["198.51.100.7", "evil.example"]);
        handed.Cves.Should().BeEmpty();
    }
}
