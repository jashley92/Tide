using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Adversaries;
using Tide.Application.Articles;
using Tide.Application.Search;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>The article editor's adversary picker, as AdversaryService searches for it: a few at a time, best first.</summary>
public class AdversaryPickSearchTests : IDisposable
{
    private TestDb _db = new();

    public AdversaryPickSearchTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    private Task<Guid> Profile(string name, string? aliases = null, string? mitre = null, bool watched = false) =>
        _db.Adversaries().SaveAsync(new AdversaryForm { Name = name, Aliases = aliases, MitreId = mitre, Watched = watched });

    private async Task<Guid> Article(string title, string? actors = null)
    {
        var id = await _db.Articles().CreateAsync(title, "Vendor", "https://vendor.example.com/" + Guid.NewGuid().ToString("N"));
        if (actors is null) return id;
        await _db.SetEarlierActorsAsync(id, actors);
        return id;
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Typing_ListsExactThenStartingThenHolding_IgnoringCase_AndNamesTheAliasThatMatched(string provider)
    {
        _db = _db.On(provider);
        var contains = await Profile("Wizard Spider");
        var aliasContains = await Profile("Ember Group", "Ember Spider; UAC-0056");
        var prefix = await Profile("Spider Kitten");
        var aliasExact = await Profile("Octo Group", "Octo Tempest; Spider; 0ktapus");
        await Profile("Unrelated Bear");
        var article = await Article("Picker search");

        var picks = await _db.Adversaries().FindForArticleAsync(article, "  SPIDER ");

        picks.Found.Select(p => p.Id).Should().Equal(aliasExact, prefix, aliasContains, contains);
        picks.Found.Select(p => p.Other).Should().Equal("Spider", null, "Ember Spider", null);
        picks.Found.Should().OnlyContain(p => p.Reason == PickReason.Match && !p.IsRetired);
        picks.Exact.Should().BeTrue("Octo Group goes by Spider exactly");
        picks.AlreadyTagged.Should().BeNull();

        var partial = await _db.Adversaries().FindForArticleAsync(article, "spid");
        partial.Found.Should().HaveCount(4);
        partial.Exact.Should().BeFalse("no profile is called Spid, so adding one is offered");

        (await _db.Adversaries().FindForArticleAsync(article, "   ")).Should().Be(TagPicks.None);
        (await _db.Adversaries().FindForArticleAsync(article, "%")).Found.Should().BeEmpty("a wildcard is taken as text");
        (await _db.Adversaries().FindForArticleAsync(article, "Spider " + new string('x', 200))).Found.Should().BeEmpty("it's cut to a name's length, and nothing's called that");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AMitreGroupId_FindsItsProfile(string provider)
    {
        _db = _db.On(provider);
        var apt29 = await Profile("APT29", "Cozy Bear; The Dukes; NOBELIUM", "G0016");
        var apt28 = await Profile("APT28", "Fancy Bear", "G0007");
        var article = await Article("MITRE id search");

        var exact = await _db.Adversaries().FindForArticleAsync(article, "g0016");
        exact.Found.Select(p => p.Id).Should().Equal(apt29);
        exact.Found[0].Should().Be(new TagPick(apt29, "APT29", "G0016", false, null, PickReason.Match));
        exact.Exact.Should().BeTrue("the MITRE id names the group exactly");

        (await _db.Adversaries().FindForArticleAsync(article, "G00")).Found.Select(p => p.Id).Should().Equal(apt28, apt29);
        (await _db.Adversaries().FindForArticleAsync(article, "cozy")).Found.Single().Other.Should().Be("Cozy Bear");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Results_AreCapped_AndAnExactMatchComesFirstHoweverManyHoldTheWords(string provider)
    {
        _db = _db.On(provider);
        // More profiles that hold "zeta" than the search reads, and the one called Zeta added last of all.
        for (var i = 0; i < 60; i++) await Profile($"Group {i:00} Zeta");
        var zeta = await Profile("Zeta");
        var article = await Article("Capped search");

        var picks = await _db.Adversaries().FindForArticleAsync(article, "zeta");

        picks.Found.Should().HaveCount(AdversaryService.PickLimit);
        picks.Found[0].Id.Should().Be(zeta);
        picks.Found.Skip(1).Select(p => p.Name).Should().Equal(Enumerable.Range(0, AdversaryService.PickLimit - 1).Select(i => $"Group {i:00} Zeta"));
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task RetiredProfiles_StillMatch_ButComeLast_AndDontCountAsTheExactName(string provider)
    {
        _db = _db.On(provider);
        var retired = await Profile("Lapsus Group", "Lapsus");
        await _db.Adversaries().SetRetiredAsync(retired, true);
        var live = await Profile("Strawberry Tempest", "DEV-0537 lapsus copycat");
        var article = await Article("Retired search");

        var picks = await _db.Adversaries().FindForArticleAsync(article, "lapsus");

        picks.Found.Select(p => (p.Id, p.IsRetired)).Should().Equal((live, false), (retired, true));
        picks.Exact.Should().BeFalse("only a retired profile goes by Lapsus, and a new live one may take the name");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task WithNothingTyped_ItOffersTheNamedThenWatchedThenRecentlyTagged_NeverOnesAlreadyTagged(string provider)
    {
        _db = _db.On(provider);
        var adversaries = _db.Adversaries();
        var named = await Profile("APT29", "Cozy Bear");
        var namedTagged = await Profile("Sandworm Team", watched: true);
        var watched = await Profile("Volt Typhoon", watched: true);
        var older = await Profile("Kimsuky");
        var newer = await Profile("Lazarus Group");
        var retired = await Profile("FIN7");
        await adversaries.SetRetiredAsync(retired, true);
        await Profile("Never Tagged Group");
        var article = await Article("Cozy Bear and Sandworm Team phishing");
        await adversaries.TagArticleAsync(article, namedTagged);

        var elsewhere = await Article("Another report");
        await adversaries.TagArticleAsync(elsewhere, older);
        await TaggedEarlierAsync(nameof(Tide.Domain.Entities.ArticleAdversary), older);
        await adversaries.TagArticleAsync(elsewhere, newer);
        await adversaries.TagArticleAsync(elsewhere, watched);
        await adversaries.TagArticleAsync(elsewhere, retired);

        var offered = await adversaries.OfferedAsync(article);

        offered.Select(p => (p.Id, p.Reason)).Should().Equal(
            (named, PickReason.Named), (watched, PickReason.Watched),
            (newer, PickReason.Recent), (older, PickReason.Recent));
        (await adversaries.OfferedAsync(Guid.NewGuid())).Should().BeEmpty();

        // However many are watched, a few at a time.
        for (var i = 0; i < AdversaryService.PickLimit; i++) await Profile($"Watched {i}", watched: true);
        (await adversaries.OfferedAsync(article)).Should().HaveCount(AdversaryService.PickLimit).And.NotContain(p => p.Reason == PickReason.Recent);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AProfileTheArticleHas_IsLeftOut_AndAnExactNameOfOneSaysSo(string provider)
    {
        _db = _db.On(provider);
        var spider = await Profile("Scattered Spider", "UNC3944; Octo Tempest");
        var other = await Profile("Scattered Canary");
        var article = await Article("Tagged already");
        await _db.Adversaries().TagArticleAsync(article, spider);

        var picks = await _db.Adversaries().FindForArticleAsync(article, "octo tempest");
        picks.Found.Should().BeEmpty();
        picks.Exact.Should().BeTrue();
        picks.AlreadyTagged.Should().Be("Scattered Spider");

        (await _db.Adversaries().FindForArticleAsync(article, "scattered")).Found.Select(p => p.Id).Should().Equal(other);
    }

    [Fact]
    public async Task OnlyAReaderOfTheProfiles_MaySearchThem()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Awaiting(() => _db.Adversaries().FindForArticleAsync(Guid.NewGuid(), "x")).Should().ThrowAsync<Application.Security.ForbiddenException>();
        await FluentActions.Awaiting(() => _db.Adversaries().OfferedAsync(Guid.NewGuid())).Should().ThrowAsync<Application.Security.ForbiddenException>();
    }

    [Fact]
    public void AMatch_PrefersTheName_ThenTheMitreId_ThenTheBestAlias()
    {
        var a = new Domain.Entities.Adversary { Name = "Midnight Blizzard", Aliases = "Blizzard Unit; APT29", MitreId = "G0016" };
        AdversaryService.Matching(a, "blizzard").Should().Be((1, "Blizzard Unit"));
        AdversaryService.Matching(a, "midnight blizzard").Should().Be((0, null));
        AdversaryService.Matching(a, "g0016").Should().Be((0, null));
        AdversaryService.Matching(a, "pt2").Should().Be((2, "APT29"));
        AdversaryService.Matching(a, "Cozy").Rank.Should().Be(3);
    }

    // The "recently tagged" order reads the audit trail's times, and a tie there falls either way: move a tag back a
    // minute, rather than wait for the clock to tick between two tags.
    private async Task TaggedEarlierAsync(string entityType, Guid id)
    {
        await using var ctx = _db.PlainContext();
        var key = id.ToString();
        var added = await ctx.AuditLog.Where(e => e.EntityType == entityType && e.Action == "Added" && e.EntityKey.EndsWith(key)).SingleAsync();
        added.AtUtc = added.AtUtc.AddMinutes(-1);
        await ctx.SaveChangesAsync();
    }
}

/// <summary>The picker on the article's adversary card, as an analyst (and a reader) sees it.</summary>
public class AdversaryPickerRenderTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<(Guid Article, Dictionary<string, Guid> Ids)> Setup(string title, params (string Name, string? Aliases, bool Retired)[] profiles)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var svc = scope.ServiceProvider.GetRequiredService<AdversaryService>();
        var ids = new Dictionary<string, Guid>();
        foreach (var (name, aliases, retired) in profiles)
        {
            ids[name] = await svc.SaveAsync(new AdversaryForm { Name = name, Aliases = aliases });
            if (retired) await svc.SetRetiredAsync(ids[name], true);
        }
        var article = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(title, "Vendor", null, TlpLevel.Clear);
        return (article, ids);
    }

    private Task<ComponentHarness.Rendered<ArticleAdversaryCard>> Card(Guid article, bool canWrite = true, ComponentHarness.SignIn? @as = null) =>
        ComponentHarness.RenderAsync<ArticleAdversaryCard>(app, $"/articles/{article}", new() { ["ArticleId"] = article, ["CanWrite"] = canWrite }, @as);

    private static Task Type(ComponentHarness.Rendered<ArticleAdversaryCard> r, string text) => r.Click(c =>
    {
        c._combo!._focused = true;
        c._text = text;
        return c.Find();
    });

    private async Task<IReadOnlyList<string>> Tagged(Guid article)
    {
        await using var check = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        return (await check.ServiceProvider.GetRequiredService<AdversaryService>().ForArticleAsync(article)).Tagged.Select(a => a.Name).ToList();
    }

    [Fact]
    public async Task TypingAnAlias_ShowsWhichProfile_ThePickTagsIt_AndTheBoxClearsForTheNext()
    {
        var (article, ids) = await Setup("Picker alias", ("pick-r1 Midnight", "pick-r1 Cozy; pick-r1 Dukes", false), ("pick-r1 Kitten", null, false));
        await using var r = await Card(article);
        (await r.Html()).Should().Contain("role=\"combobox\"").And.Contain("Tag an adversary by any name, or add one").And.NotContain("<datalist");

        await Type(r, "pick-r1 coz");
        var html = await r.Html();
        html.Should().Contain("role=\"listbox\"").And.Contain("pick-r1 Midnight<span class=\"fw-normal tide-muted\"> (also pick-r1 Cozy)</span>")
            .And.Contain("Add “pick-r1 coz” as a new adversary").And.Contain("aria-activedescendant=\"adv-pick-" + article + "-opt-0\"");

        await r.Click(c => c.Pick(c.Listed[0]));
        r.Component._text.Should().BeNull("the box clears for another");
        r.Component.Listed.Should().NotContain(p => p.Id == ids["pick-r1 Midnight"], "a tagged profile isn't offered again");
        (await r.Html()).Should().Contain("Untag pick-r1 Midnight");

        // The keyboard: down to the second result, Enter tags it.
        await Type(r, "pick-r1");
        r.Component.Listed.Select(p => p.Name).Should().Equal("pick-r1 Kitten");
        await r.Click(c => c._combo!.OnKey(new KeyboardEventArgs { Key = "ArrowDown" }));
        await r.Click(c => c._combo!.OnKey(new KeyboardEventArgs { Key = "ArrowUp" }));
        await r.Click(c => c._combo!.OnKey(new KeyboardEventArgs { Key = "Enter" }));
        (await Tagged(article)).Should().BeEquivalentTo(["pick-r1 Midnight", "pick-r1 Kitten"]);

        await r.Click(c => c._combo!.OnKey(new KeyboardEventArgs { Key = "Escape" }));
        r.Component._combo!.Shown.Should().BeFalse("Esc closes the list");
    }

    [Fact]
    public async Task AddNew_IsOfferedBesideMatchesThatArentExact_AndMakesAndTagsTheProfile_WithALinkToFillItIn()
    {
        var (article, _) = await Setup("Picker add", ("pick-r2 Storm Group", null, false));
        await using var r = await Card(article);

        await Type(r, "pick-r2 Storm");
        r.Component.Listed.Select(p => p.Name).Should().Equal("pick-r2 Storm Group");
        r.Component.CanAdd.Should().BeTrue("the only match isn't called that exactly");

        await r.Click(c => c.AddNew());
        r.Component._text.Should().BeNull();
        (await Tagged(article)).Should().BeEquivalentTo(["pick-r2 Storm"]);
        await using var check = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var made = await check.ServiceProvider.GetRequiredService<AppDbContext>().Adversaries.SingleAsync(a => a.Name == "pick-r2 Storm");
        (await r.Html()).Should().Contain($"<a href=\"adversaries/{made.Id}\">pick-r2 Storm</a> as a new profile. Fill it in when you can.");

        await Type(r, "PICK-R2 STORM GROUP");
        r.Component.CanAdd.Should().BeFalse("a profile goes by that name");
        await Type(r, "pick-r2 storm");
        r.Component.CanAdd.Should().BeFalse("it's the profile just added, already tagged");
        (await r.Html()).Should().Contain("pick-r2 Storm is already tagged.");
    }

    [Fact]
    public async Task ARetiredProfile_IsAskedAboutFirst_AndTaggedOnlyOnceConfirmed()
    {
        var (article, ids) = await Setup("Picker retired", ("pick-r3 Old Crew", null, true), ("pick-r3 Older Crew", null, true));
        await using var r = await Card(article);

        await Type(r, "pick-r3 old");
        r.Component.Listed.Should().OnlyContain(p => p.IsRetired);
        (await r.Html()).Should().Contain("<span class=\"tide-combo-tag\">retired</span>");

        await r.Press(c => c.Pick(c.Listed.Single(p => p.Id == ids["pick-r3 Old Crew"])));
        (await r.HtmlOnce("Tag the article with it anyway?")).Should().Contain(">pick-r3 Old Crew is retired</h2>");
        await r.Renderer.Dispatcher.InvokeAsync(r.Component._confirm!.Cancel);
        (await Tagged(article)).Should().BeEmpty("the analyst said no");

        await r.Press(c => c.Pick(c.Listed.Single(p => p.Id == ids["pick-r3 Older Crew"])));
        await r.HtmlOnce("pick-r3 Older Crew is retired");
        await r.Renderer.Dispatcher.InvokeAsync(r.Component._confirm!.Confirm);
        (await r.HtmlOnce("Untag pick-r3 Older Crew")).Should().Contain("Untag pick-r3 Older Crew");
        r.Component._text.Should().BeNull();
    }

    [Fact]
    public async Task AReader_GetsNoPicker()
    {
        var (article, ids) = await Setup("Picker reader", ("pick-r4 Group", null, false));
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<AdversaryService>().TagArticleAsync(article, ids["pick-r4 Group"]);
        await using var r = await Card(article, canWrite: false, new ComponentHarness.SignIn("dev:owner", "Owner", "ActionOwner"));

        var html = await r.Html();
        html.Should().Contain("pick-r4 Group").And.NotContain("role=\"combobox\"").And.NotContain("Untag");
    }
}
