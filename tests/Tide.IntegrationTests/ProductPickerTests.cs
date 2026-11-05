using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Stack;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>The article editor's "We run" picker, as StackService searches for it: a few at a time, best first.</summary>
public class ProductPickSearchTests : IDisposable
{
    private TestDb _db = new();

    public ProductPickSearchTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    private Task<Guid> Product(string name, string? otherNames = null, string? versions = null) =>
        _db.Stack().SaveAsync(new StackForm { Name = name, OtherNames = otherNames, Versions = versions });

    private Task<Guid> Article(string title, string? bluf = null) =>
        _db.Articles().CreateAsync(title, "Vendor", "https://vendor.example.com/" + Guid.NewGuid().ToString("N"));

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Typing_ListsExactThenStartingThenHolding_IgnoringCase_AndNamesTheOtherNameThatMatched(string provider)
    {
        _db = _db.On(provider);
        var contains = await Product("Cisco Secure Gateway");
        var otherContains = await Product("Ivanti Connect Secure", "Pulse Gateway Edge; ICS");
        var prefix = await Product("Gateway Manager");
        var otherExact = await Product("FortiGate", "Gateway; FortiOS", "7.4.3");
        await Product("Unrelated Mail");
        var article = await Article("Picker search");

        var picks = await _db.Stack().FindForArticleAsync(article, "  GATEWAY ");

        picks.Found.Select(p => p.Id).Should().Equal(otherExact, prefix, contains, otherContains);
        picks.Found.Select(p => p.OtherName).Should().Equal("Gateway", null, null, "Pulse Gateway Edge");
        picks.Found[0].Versions.Should().Be("7.4.3");
        picks.Found.Should().OnlyContain(p => p.Reason == ProductPickReason.Match && !p.IsRetired);
        picks.Exact.Should().BeTrue("FortiGate goes by Gateway exactly");
        picks.AlreadyTagged.Should().BeNull();

        var partial = await _db.Stack().FindForArticleAsync(article, "gatew");
        partial.Found.Should().HaveCount(4);
        partial.Exact.Should().BeFalse("nothing is called Gatew, so adding it is offered");

        (await _db.Stack().FindForArticleAsync(article, "   ")).Should().Be(ProductPicks.None);
        (await _db.Stack().FindForArticleAsync(article, "%")).Found.Should().BeEmpty("a wildcard is taken as text");
        (await _db.Stack().FindForArticleAsync(article, "fortios")).Found.Single().OtherName.Should().Be("FortiOS");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Results_AreCapped_AndAnExactMatchComesFirstHoweverManyHoldTheWords(string provider)
    {
        _db = _db.On(provider);
        for (var i = 0; i < 60; i++) await Product($"Suite {i:00} Exchange");
        var exchange = await Product("Exchange");
        var article = await Article("Capped search");

        var picks = await _db.Stack().FindForArticleAsync(article, "exchange");

        picks.Found.Should().HaveCount(StackService.PickLimit);
        picks.Found[0].Id.Should().Be(exchange);
        picks.Found.Skip(1).Select(p => p.Name).Should().Equal(Enumerable.Range(0, StackService.PickLimit - 1).Select(i => $"Suite {i:00} Exchange"));
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task RetiredProducts_StillMatch_ButComeLast_AndDontCountAsTheExactName(string provider)
    {
        _db = _db.On(provider);
        var retired = await Product("Old Proxy", "Squid");
        await _db.Stack().SetRetiredAsync(retired, true);
        var live = await Product("Edge Cache", "squid replacement");
        var article = await Article("Retired search");

        var picks = await _db.Stack().FindForArticleAsync(article, "squid");

        picks.Found.Select(p => (p.Id, p.IsRetired)).Should().Equal((live, false), (retired, true));
        picks.Exact.Should().BeFalse("only a retired product goes by Squid");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task WithNothingTyped_ItOffersTheNamedThenRecentlyTagged_NeverOnesAlreadyTaggedOrRetired(string provider)
    {
        _db = _db.On(provider);
        var stack = _db.Stack();
        var named = await Product("GitLab");
        var namedTagged = await Product("Confluence");
        var older = await Product("Okta");
        var newer = await Product("Zscaler");
        var retired = await Product("Old VPN");
        await Product("Never Tagged");
        var article = await Article("GitLab and Confluence flaws");
        await stack.TagArticleAsync(article, namedTagged);

        var elsewhere = await Article("Another report");
        await stack.TagArticleAsync(elsewhere, older);
        await Task.Delay(20);
        await stack.TagArticleAsync(elsewhere, newer);
        await stack.TagArticleAsync(elsewhere, retired);
        await stack.SetRetiredAsync(retired, true);

        var offered = await stack.OfferedAsync(article);

        offered.Select(p => (p.Id, p.Reason)).Should().Equal(
            (named, ProductPickReason.Named), (newer, ProductPickReason.Recent), (older, ProductPickReason.Recent));
        (await stack.OfferedAsync(Guid.NewGuid())).Should().BeEmpty();

        // However many were tagged lately, a few at a time.
        for (var i = 0; i < StackService.PickLimit; i++) await stack.TagArticleAsync(elsewhere, await Product($"Recent {i}"));
        (await stack.OfferedAsync(article)).Should().HaveCount(StackService.PickLimit);
        (await stack.ForArticleCardAsync(article, offer: false)).Offered.Should().BeEmpty("a reader's card doesn't ask");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AProductTheArticleHas_IsLeftOut_AndAnExactNameOfOneSaysSo(string provider)
    {
        _db = _db.On(provider);
        var ics = await Product("Ivanti Connect Secure", "Pulse Secure; ICS");
        var other = await Product("Ivanti EPMM");
        var article = await Article("Tagged already");
        await _db.Stack().TagArticleAsync(article, ics);

        var picks = await _db.Stack().FindForArticleAsync(article, "pulse secure");
        picks.Found.Should().BeEmpty();
        picks.Exact.Should().BeTrue();
        picks.AlreadyTagged.Should().Be("Ivanti Connect Secure");

        (await _db.Stack().FindForArticleAsync(article, "ivanti")).Found.Select(p => p.Id).Should().Equal(other);
    }

    [Fact]
    public async Task OnlyAReaderOfTheStack_MaySearchIt()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Awaiting(() => _db.Stack().FindForArticleAsync(Guid.NewGuid(), "x")).Should().ThrowAsync<Application.Security.ForbiddenException>();
        await FluentActions.Awaiting(() => _db.Stack().OfferedAsync(Guid.NewGuid())).Should().ThrowAsync<Application.Security.ForbiddenException>();
    }

    [Fact]
    public void AMatch_PrefersTheName_ThenTheBestOtherName()
    {
        var p = new Domain.Entities.StackProduct { Name = "Ivanti Connect Secure", OtherNames = "Connect VPN; Pulse Secure" };
        StackService.Matching(p, "connect").Should().Be((1, "Connect VPN"));
        StackService.Matching(p, "ivanti connect secure").Should().Be((0, null));
        StackService.Matching(p, "ulse").Should().Be((2, "Pulse Secure"));
        StackService.Matching(p, "Okta").Rank.Should().Be(3);
    }
}

/// <summary>The "We run" picker on the article, as an analyst (and someone who can't edit) sees it.</summary>
public class ProductPickerRenderTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<(Guid Article, Dictionary<string, Guid> Ids)> Setup(string title, params (string Name, string? Others, bool Retired)[] products)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var svc = scope.ServiceProvider.GetRequiredService<StackService>();
        var ids = new Dictionary<string, Guid>();
        foreach (var (name, others, retired) in products)
        {
            ids[name] = await svc.SaveAsync(new StackForm { Name = name, OtherNames = others, Versions = "1.0" });
            if (retired) await svc.SetRetiredAsync(ids[name], true);
        }
        var article = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(title, "Vendor", null, TlpLevel.Clear);
        return (article, ids);
    }

    private Task<ComponentHarness.Rendered<ArticleStackCard>> Card(Guid article, bool canWrite = true, ComponentHarness.SignIn? @as = null) =>
        ComponentHarness.RenderAsync<ArticleStackCard>(app, $"/articles/{article}", new()
        {
            ["ArticleId"] = article, ["CanWrite"] = canWrite,
            ["OnMention"] = Microsoft.AspNetCore.Components.EventCallback.Factory.Create<string>(this, _ => { }),
        }, @as);

    private static Task Type(ComponentHarness.Rendered<ArticleStackCard> r, string text) => r.Click(c =>
    {
        c._combo!._focused = true;
        c._text = text;
        return c.Find();
    });

    private async Task<IReadOnlyList<string>> Tagged(Guid article)
    {
        await using var check = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        return (await check.ServiceProvider.GetRequiredService<StackService>().ForArticleAsync(article)).Tagged.Select(p => p.Name).ToList();
    }

    [Fact]
    public async Task TypingAnOtherName_ShowsWhichProduct_ThePickTagsIt_TheBoxClears_AndTheKeysWork()
    {
        var (article, ids) = await Setup("We run alias", ("werun-r1 Connect Secure", "werun-r1 Pulse; werun-r1 ICS", false), ("werun-r1 Portal", null, false));
        await using var r = await Card(article);
        (await r.Html()).Should().Contain("role=\"combobox\"").And.Contain("Tag a product by any name, or add one").And.NotContain("<datalist");

        await Type(r, "werun-r1 puls");
        var html = await r.Html();
        html.Should().Contain("role=\"listbox\"").And.Contain("werun-r1 Connect Secure<span class=\"fw-normal tide-muted\"> (also werun-r1 Pulse)</span>")
            .And.Contain("Add “werun-r1 puls” to Our stack").And.Contain("aria-activedescendant=\"stack-pick-" + article + "-opt-0\"");

        await r.Click(c => c.Pick(c.Listed[0]));
        r.Component._text.Should().BeNull("the box clears for another");
        r.Component.Listed.Should().NotContain(p => p.Id == ids["werun-r1 Connect Secure"], "a tagged product isn't offered again");
        (await r.Html()).Should().Contain("Untag werun-r1 Connect Secure").And.Contain("Mention in Why it matters");

        await Type(r, "werun-r1");
        await r.Click(c => c._combo!.OnKey(new KeyboardEventArgs { Key = "ArrowDown" }));
        r.Component._combo!._reveal.Should().BeTrue("the highlighted option is scrolled into view once drawn");
        await r.Click(c => c._combo!.OnKey(new KeyboardEventArgs { Key = "ArrowUp" }));
        await r.Click(c => c._combo!.OnKey(new KeyboardEventArgs { Key = "Enter" }));
        (await Tagged(article)).Should().BeEquivalentTo(["werun-r1 Connect Secure", "werun-r1 Portal"]);

        await r.Click(c => c._combo!.OnKey(new KeyboardEventArgs { Key = "Escape" }));
        r.Component._combo!.Shown.Should().BeFalse("Esc closes the list");
    }

    [Fact]
    public async Task AddToOurStack_IsOfferedUnlessTheNameIsExact_AndAddsAndTagsIt_WithALinkToFillItIn()
    {
        var (article, _) = await Setup("We run add", ("werun-r2 Mail Gateway", null, false));
        await using var r = await Card(article);

        await Type(r, "werun-r2 Mail");
        r.Component.Listed.Select(p => p.Name).Should().Equal("werun-r2 Mail Gateway");
        r.Component.CanAdd.Should().BeTrue();

        await r.Click(c => c.AddNew());
        r.Component._text.Should().BeNull();
        (await Tagged(article)).Should().BeEquivalentTo(["werun-r2 Mail"]);
        await using var check = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var made = await check.ServiceProvider.GetRequiredService<AppDbContext>().StackProducts.SingleAsync(p => p.Name == "werun-r2 Mail");
        (await r.Html()).Should().Contain($"<a href=\"stack/{made.Id}\">werun-r2 Mail</a> to Our stack. Fill in its versions and notes when you can.");

        await Type(r, "WERUN-R2 MAIL GATEWAY");
        r.Component.CanAdd.Should().BeFalse("a product goes by that name");
        await Type(r, "werun-r2 mail");
        r.Component.CanAdd.Should().BeFalse("it's the product just added, already tagged");
        (await r.Html()).Should().Contain("werun-r2 Mail is already tagged.");
    }

    [Fact]
    public async Task ARetiredProduct_IsAskedAboutFirst_AndTaggedOnlyOnceConfirmed()
    {
        var (article, ids) = await Setup("We run retired", ("werun-r3 Old Proxy", null, true), ("werun-r3 Older Proxy", null, true));
        await using var r = await Card(article);

        await Type(r, "werun-r3 old");
        r.Component.Listed.Should().OnlyContain(p => p.IsRetired);
        (await r.Html()).Should().Contain("<span class=\"tide-combo-tag\">retired</span>");

        await r.Press(c => c.Pick(c.Listed.Single(p => p.Id == ids["werun-r3 Old Proxy"])));
        (await r.HtmlOnce("Tag the article with it anyway?")).Should().Contain(">werun-r3 Old Proxy is retired</h2>");
        await r.Renderer.Dispatcher.InvokeAsync(r.Component._confirm!.Cancel);
        (await Tagged(article)).Should().BeEmpty("the analyst said no");

        await r.Press(c => c.Pick(c.Listed.Single(p => p.Id == ids["werun-r3 Older Proxy"])));
        await r.HtmlOnce("werun-r3 Older Proxy is retired");
        await r.Renderer.Dispatcher.InvokeAsync(r.Component._confirm!.Confirm);
        (await r.HtmlOnce("Untag werun-r3 Older Proxy")).Should().Contain("Untag werun-r3 Older Proxy");
    }

    [Fact]
    public async Task WithNothingTyped_TheTextsMatchesAreOffered_AndMentionAddsTheProduct()
    {
        var (article, ids) = await Setup("werun-r5 Gateway flaw exploited", ("werun-r5 Gateway", null, false));
        await using var r = await Card(article);

        r.Component.Listed.Select(p => (p.Name, p.Reason)).Should().Contain(("werun-r5 Gateway", ProductPickReason.Named));
        (await r.Html()).Should().Contain("Names werun-r5 Gateway:").And.Contain("Tag werun-r5 Gateway</button>");

        await r.Click(c => c.Tag(ids["werun-r5 Gateway"]));
        (await r.Html()).Should().Contain("Mention in Why it matters").And.NotContain("Names werun-r5 Gateway:", "a tagged product isn't suggested");
        ArticleStackCard.MentionText(new Domain.Entities.StackProduct { Name = "werun-r5 Gateway", Versions = "1.0" }).Should().Be("We run werun-r5 Gateway (1.0).");
    }

    [Fact]
    public async Task SomeoneWhoCantEdit_SeesTheTags_AndNoPicker()
    {
        var (article, ids) = await Setup("We run reader", ("werun-r4 Portal", null, false));
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<StackService>().TagArticleAsync(article, ids["werun-r4 Portal"]);
        await using var r = await Card(article, canWrite: false, new ComponentHarness.SignIn("dev:owner", "Owner", "ActionOwner"));

        var html = await r.Html();
        html.Should().Contain("werun-r4 Portal").And.Contain("1.0").And.NotContain("role=\"combobox\"").And.NotContain("Untag").And.NotContain("Mention");
    }
}
