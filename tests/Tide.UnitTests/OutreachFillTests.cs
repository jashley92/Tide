using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Application.Org;
using Tide.Application.Operations;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>
/// Outreach templates' placeholders (ADR 0019): which are refused when a template is saved, what each group fills in,
/// the TLP ceiling by the editions' cut, what's said to be left out, the markers left for what had nothing, and the TLP
/// line. Also the old kind name, which still reads as Outreach.
/// </summary>
public class OutreachFillTests
{
    private static readonly DateOnly Today = new(2026, 11, 6);

    private static Article Article(TlpLevel tlp = TlpLevel.Clear) => new()
    {
        Title = "Edge VPN zero-day exploited",
        Bluf = "A pre-auth flaw in the VPN gateway is exploited in the wild.",
        SourceUrl = "https://vendor.example/advisory",
        Tlp = tlp,
        WhyItMatters = "We run two of these gateways.",
        WhyItMattersTlp = TlpLevel.Amber,
        Bullets =
        [
            new ArticleBullet { Text = "Patch is out", Tlp = TlpLevel.Clear, SortOrder = 0 },
            new ArticleBullet { Text = "Our gateways are unpatched", Tlp = TlpLevel.Red, SortOrder = 1 },
        ],
        Indicators =
        [
            new ArticleIndicator { Type = IndicatorType.Domain, Value = "evil.example" },
            new ArticleIndicator { Type = IndicatorType.IpAddress, Value = "198.51.100.7" },
        ],
        Cves = [new ArticleCve { CveId = "CVE-2026-1234" }, new ArticleCve { CveId = "CVE-2026-0002" }],
    };

    private static OutreachFacts Facts(Article? article, TlpLevel ceiling, int ownIndicators = 0, IReadOnlyList<ArticleIndicator>? own = null)
    {
        var cut = OutreachFill.Cut(article, ownIndicators, ceiling);
        var indicators = (cut.Article?.Indicators ?? []).Concat(cut.OwnIndicators ? own ?? [] : []).ToList();
        return new OutreachFacts
        {
            Ceiling = ceiling,
            FromArticle = article is not null,
            Article = cut.Article,
            Cves = new Dictionary<string, CveRecord>
            {
                ["CVE-2026-1234"] = new() { CveId = "CVE-2026-1234", IsKev = true, KevRansomware = true, EpssScore = 0.941m },
            },
            Indicators = indicators,
            IndicatorsTlp = cut.IndicatorsTlp,
            IndicatorsHeldBack = cut.IndicatorsHeldBack,
            Product = new StackProduct
            {
                Name = "Edge VPN", Versions = "22.7R2", ContactName = "Vendor PSIRT", ContactEmail = "psirt@vendor.example", Team = new Team { Name = "Network" },
            },
            ActionNumber = 12,
            ActionLink = "https://tide.example.org/actions/12",
            Due = new DateOnly(2026, 11, 10),
            SenderName = "Robin",
            SenderTeam = "Cyber Threat Intelligence",
            Today = Today,
            LeftOut = cut.LeftOut,
        };
    }

    // --- placeholders refused when saved ----------------------------------------------------------------------------

    [Theory]
    [InlineData("{{article.title}} {{ARTICLE.CVES}} {{ indicators }} {{indicators:sha256}} {{Indicators:Domain}} {{tlp}} {{today}} {{today:+2}} {{Today:+60}} {{today:-3}}")]
    [InlineData("No placeholders at all, and {{ }} isn't one.")]
    public void Check_AcceptsTheNamesTideFills_InAnyCase(string text) => FluentActions.Invoking(() => OutreachFill.Check(text)).Should().NotThrow();

    [Theory]
    [InlineData("{{article.titel}}", "{{article.titel}} isn't a placeholder TIDE fills. Use one of: {{article.title}}*")]
    [InlineData("{{values}}", "{{values}} isn't a placeholder TIDE fills*")]
    [InlineData("{{indicators:sha3}}", "{{indicators:sha3}} isn't a kind of indicator TIDE knows. Use {{indicators}}, or one of: {{indicators:ipv4}}*")]
    [InlineData("{{article.title:domain}}", "{{article.title}} takes nothing after a colon: only {{indicators:…}} and {{today:+2}} do.")]
    [InlineData("{{today:2}}", "{{today:2}} isn't a number of business days. Write it as {{today:+2}} or {{today:-2}}, up to 60.")]
    [InlineData("{{today:-61}}", "{{today:-61}} isn't a number of business days*")]
    [InlineData("{{today:+0}}", "{{today:+0}} isn't a number of business days*")]
    [InlineData("{{today:+61}}", "{{today:+61}} isn't a number of business days*")]
    public void Check_RefusesAnUnknownPlaceholderOrKind_SayingWhatItCanUse(string text, string message) =>
        FluentActions.Invoking(() => OutreachFill.Check(text)).Should().Throw<ArgumentException>().WithMessage(message);

    // A typo that doesn't parse as a placeholder at all was saved, and every email from the template went out with the
    // braces in it: nothing filled it, and no [[…]] marker held the send back.
    [Theory]
    [InlineData("Reply by {{today: +2}}", "{{today: +2}} isn't written as a placeholder TIDE fills*")]
    [InlineData("Reply by {{today :+2}}", "{{today :+2}} isn't written as a placeholder TIDE fills*")]
    [InlineData("{{article_title}}", "{{article_title}} isn't written as a placeholder TIDE fills*")]
    [InlineData("{{product name}}", "{{product name}} isn't written as a placeholder TIDE fills*")]
    [InlineData("{{sender.name}}, {{action.number!}}", "{{action.number!}} isn't written as a placeholder TIDE fills*")]
    public void Check_RefusesBracesThatArentAPlaceholder_ThatWouldGoOutAsTyped(string text, string message) =>
        FluentActions.Invoking(() => OutreachFill.Check(text)).Should().Throw<ArgumentException>().WithMessage(message);

    [Fact]
    public void EveryPlaceholderListed_IsOneTheCheckAccepts() =>
        OutreachFill.All.Should().AllSatisfy(p => FluentActions.Invoking(() => OutreachFill.Check(p.Written)).Should().NotThrow());

    [Fact]
    public void TodayPlusDays_CountsBusinessDays_SkippingWeekendsAndHolidays()
    {
        // Today is Friday, November 6.
        var facts = Facts(Article(), TlpLevel.Clear);
        OutreachFill.Fill("A-12 - action required by {{today:+2}}", "x", facts).Subject
            .Should().Be("A-12 - action required by Tuesday, November 10, 2026", "Monday and Tuesday, past the weekend");
        OutreachFill.Fill("{{today:+1}} {{today}}", "x", facts).Subject.Should().Be("Monday, November 9, 2026 Friday, November 6, 2026");

        var holiday = facts with { Days = new WorkingDays(new Dictionary<DateOnly, string> { [new(2026, 11, 9)] = "Office closed" }) };
        OutreachFill.Fill("{{today:+2}}", "x", holiday).Subject.Should().Be("Wednesday, November 11, 2026", "the holiday doesn't count");

        OutreachFill.Fill("since {{today:-1}}", "x", facts).Subject.Should().Be("since Thursday, November 5, 2026");
        OutreachFill.Fill("{{today:-6}}", "x", facts).Subject.Should().Be("Thursday, October 29, 2026", "back past the weekend");
    }

    // --- each group ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheArticle_FillsItsTitleBottomLineLinkCvesAndTlp_TheCvesAsNumbersOnly()
    {
        var d = OutreachFill.Fill("{{article.title}}", "{{article.bottom-line}}\n{{article.link}}\n{{article.cves}}\n{{article.tlp}}", Facts(Article(TlpLevel.Green), TlpLevel.Green));

        d.Subject.Should().Be("Edge VPN zero-day exploited");
        d.Body.Should().Be("TLP:GREEN: Limited disclosure: may be shared within the community, not publicly.\n\n"
            + "A pre-auth flaw in the VPN gateway is exploited in the wild.\nhttps://vendor.example/advisory\n"
            + "CVE-2026-0002\nCVE-2026-1234\nTLP:GREEN\n", "what TIDE knows of each CVE is {{article.cves-detailed}}'s");
        d.Marking.Should().Be(TlpLevel.Green);
        d.Unfilled.Should().BeEmpty();
        d.LeftOut.Should().BeEmpty("the key point and why it matters held back aren't in this template");
    }

    [Fact]
    public void TheOtherLinks_FollowTheSourceOnlyWhereTheTemplateSays_EachOnce_AndCveLinksPointAtTheNvd()
    {
        var facts = Facts(Article(TlpLevel.Green), TlpLevel.Green) with
        {
            ArticleLinks = ["https://news.example/follow-up", "https://VENDOR.example/advisory", "https://blog.example/analysis", "https://news.example/follow-up"],
        };
        var d = OutreachFill.Fill("x", "{{article.link}}\n\n{{article.other-links}}\n\n{{article.cve-links}}", facts);

        d.Body.Should().EndWith("\n\nhttps://vendor.example/advisory\n\nhttps://news.example/follow-up\nhttps://blog.example/analysis\n\n"
            + "https://nvd.nist.gov/vuln/detail/CVE-2026-0002\nhttps://nvd.nist.gov/vuln/detail/CVE-2026-1234\n",
            "the source isn't listed again among the others");
        OutreachFill.Fill("x", "{{article.link}}", facts).Body.Should().EndWith("\n\nhttps://vendor.example/advisory\n", "the source alone");
        d.Marking.Should().Be(TlpLevel.Green, "the links carry the article's marking");
        d.Unfilled.Should().BeEmpty();

        var noCves = Article();
        noCves.Cves = [];
        var none = OutreachFill.Fill("x", "{{article.cve-links}}", Facts(noCves, TlpLevel.Clear));
        none.Unfilled.Should().Equal("{{article.cve-links}}");
        OutreachFill.Fill("x", "{{article.cve-links}}", Facts(Article(TlpLevel.Amber), TlpLevel.Green)).Body
            .Should().Contain("[[article.cve-links: above TLP:GREEN, left out]]");
    }

    [Fact]
    public void TheCvesDetailed_GiveAParagraphEach_WithWhatTideKnows_AndTheIdAndLinkAlone_ForOneItDoesnt()
    {
        var facts = Facts(Article(TlpLevel.Green), TlpLevel.Green) with
        {
            Cves = new Dictionary<string, CveRecord>
            {
                ["CVE-2026-1234"] = new()
                {
                    CveId = "CVE-2026-1234", CvssScore = 9.8m, CvssSeverity = "CRITICAL", IsKev = true, KevAddedDate = new(2026, 11, 1),
                    KevDueDate = new(2026, 11, 22), KevRansomware = true, EpssScore = 0.941m, Vendor = "Ivanti", Product = "Connect Secure",
                    Description = "A pre-auth flaw\nin the gateway.",
                },
            },
        };
        var d = OutreachFill.Fill("x", "{{article.cves-detailed}}", facts);

        d.Body.Should().EndWith("\n\nCVE-2026-0002\nhttps://nvd.nist.gov/vuln/detail/CVE-2026-0002\n\n"
            + "CVE-2026-1234: CVSS 9.8 critical; on CISA's KEV list since Nov 1, 2026, due Nov 22, 2026, used in ransomware; EPSS 94.1%\n"
            + "Ivanti Connect Secure\nA pre-auth flaw in the gateway.\nhttps://nvd.nist.gov/vuln/detail/CVE-2026-1234\n");
        d.Marking.Should().Be(TlpLevel.Green);
    }

    [Fact]
    public void TheStarterTemplates_WriteAnEmail_ForAnArticleWithNoCvesOrOtherLinks()
    {
        var article = Article();
        article.Cves = [];
        article.SourceUrl = null;
        var facts = Facts(article, TlpLevel.Amber);
        foreach (var s in StarterOutreachTemplates.All)
        {
            OutreachFill.Check(s.Subject);
            OutreachFill.Check(s.Body);
            var d = OutreachFill.Fill(s.Subject, s.Body, facts);
            d.Unfilled.Should().BeEmpty(s.Name);
            d.Body.Should().NotContain("CVEs:", "the heading goes with the empty list").And.NotContain("{{").And.NotContain("\n\n\n", s.Name);
        }
    }

    // --- sections and text for when it's empty ---------------------------------------------------------------------

    private const string CvesSection = "Hello,\n\n{{#article.cves}}\nCVEs:\n{{article.cves}}\n\n{{else}}\nNo CVEs yet.\n\n{{/article.cves}}\nThanks";

    [Fact]
    public void ASection_KeepsItsFirstBranch_WhenThereIsSomethingToFill_AndItsTagLinesLeaveNoGaps()
    {
        var d = OutreachFill.Fill("x", CvesSection, Facts(Article(), TlpLevel.Clear));
        d.Body.Should().EndWith("\n\nHello,\n\nCVEs:\nCVE-2026-0002\nCVE-2026-1234\n\nThanks\n");
    }

    [Fact]
    public void ASection_KeepsWhatFollowsElse_WhenThereIsNothing_OrDropsWhole_WithoutElse()
    {
        var article = Article();
        article.Cves = [];
        var facts = Facts(article, TlpLevel.Clear);
        OutreachFill.Fill("x", CvesSection, facts).Body.Should().EndWith("\n\nHello,\n\nNo CVEs yet.\n\nThanks\n");

        var dropped = OutreachFill.Fill("x", "Hello,\n\n{{#article.cves}}\nCVEs:\n{{article.cves}}\n\n{{/article.cves}}\nThanks", facts);
        dropped.Body.Should().EndWith("\n\nHello,\n\nThanks\n");
        dropped.Unfilled.Should().BeEmpty();

        OutreachFill.Fill("Re: {{#article.cves}}{{article.cves}}{{else}}no CVE{{/article.cves}}", "x", facts).Subject.Should().Be("Re: no CVE", "inline too");
    }

    // A section's tags go as an empty optional placeholder does: blank lines round a section that goes leave one, as
    // round a line of {{x?}}, and a section that goes mid-sentence leaves one space.
    [Fact]
    public void ASectionThatGoes_LeavesTheBlankLinesAndSpaces_AnOptionalPlaceholderWould()
    {
        var article = Article();
        article.Cves = [];
        var facts = Facts(article, TlpLevel.Clear);
        OutreachFill.Fill("x", "Hello,\n\n{{#article.cves}}\nCVEs:\n{{article.cves}}\n{{/article.cves}}\n\nThanks", facts).Body
            .Should().EndWith("\n\nHello,\n\nThanks\n").And.Be(OutreachFill.Fill("x", "Hello,\n\n{{article.cves?}}\n\nThanks", facts).Body);
        OutreachFill.Fill("x", "Hello,\n\n{{#article.cves}}\nCVEs:\n{{article.cves}}\n{{/article.cves}}\n\nThanks", Facts(Article(), TlpLevel.Clear)).Body
            .Should().EndWith("\n\nHello,\n\nCVEs:\nCVE-2026-0002\nCVE-2026-1234\n\nThanks\n");
        OutreachFill.Fill("Patch {{#article.cves}}{{article.cves}} {{/article.cves}}today", "x", facts).Subject.Should().Be("Patch today");
    }

    [Fact]
    public void ASectionHeldBackByTheCeiling_TakesItsElse_AndWhatWasHeldBackIsStillSaid()
    {
        var d = OutreachFill.Fill("x", CvesSection, Facts(Article(TlpLevel.Amber), TlpLevel.Green));
        d.Body.Should().Contain("No CVEs yet.").And.NotContain("CVE-2026");
        d.LeftOut.Should().ContainSingle().Which.Should().StartWith("The article is TLP:AMBER");
        d.Marking.Should().Be(TlpLevel.Clear);
    }

    [Fact]
    public void ASectionKeptForAMarkedPart_CarriesItsMarking_EvenWithoutThePlaceholderInside()
    {
        var d = OutreachFill.Fill("x", "{{#article.cves}}This report names CVEs.{{/article.cves}}", Facts(Article(TlpLevel.Green), TlpLevel.Green));
        d.Body.Should().EndWith("This report names CVEs.\n");
        d.Marking.Should().Be(TlpLevel.Green, "keeping it says the GREEN article has CVEs");
    }

    [Fact]
    public void TextAfterABar_IsWritten_WhenThereIsNothingToFill_AndTheValueOtherwise()
    {
        var facts = Facts(Article(), TlpLevel.Clear);
        var none = Facts(Article(), TlpLevel.Clear);
        none.Product!.ContactName = null;
        OutreachFill.Fill("x", "Hello {{product.contact|there}},", none).Body.Should().EndWith("\n\nHello there,\n");
        OutreachFill.Fill("x", "Hello {{product.contact|there}},", facts).Body.Should().EndWith("\n\nHello Vendor PSIRT,\n");
        OutreachFill.Fill("x", "{{indicators:sha256| none seen }}", facts).Body.Should().EndWith("\n\nnone seen\n");
        OutreachFill.Fill("x", "{{article.title|untitled}}", Facts(Article(TlpLevel.Amber), TlpLevel.Green)).Unfilled.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{{#article.cves}}\nCVEs: {{article.cves}}\n{{else}}\nNone.\n{{/article.cves}}\n{{#indicators:domain}}{{indicators:domain}}{{/Indicators:Domain}}")]
    [InlineData("Hello {{product.contact|there}}, {{today:+2|soon}}")]
    public void Check_AcceptsSectionsAndTextAfterABar(string text) => FluentActions.Invoking(() => OutreachFill.Check(text)).Should().NotThrow();

    [Theory]
    [InlineData("{{#article.cves}}{{#indicators}}x{{/indicators}}{{/article.cves}}", "{{#indicators}} is inside {{#article.cves}}*")]
    [InlineData("{{#article.cves}} x", "{{#article.cves}} is never closed*")]
    [InlineData("{{#article.cves}} x {{/indicators}}", "{{/indicators}} doesn't close {{#article.cves}}: write {{/article.cves}}.")]
    [InlineData("x {{/article.cves}}", "{{/article.cves}} closes a section that isn't open*")]
    [InlineData("x {{else}} y", "{{else}} only goes inside a section*")]
    [InlineData("{{#article.cves}}a{{else}}b{{else}}c{{/article.cves}}", "{{#article.cves}} has two {{else}}s*")]
    [InlineData("{{#tlp}}x{{/tlp}}", "{{#tlp}} can't start a section*")]
    [InlineData("{{#article.cvs}}x{{/article.cvs}}", "{{article.cvs}} isn't a placeholder TIDE fills*")]
    [InlineData("{{article.cves?|none}}", "{{article.cves?|none}} is both optional and has text for when it's empty*")]
    [InlineData("{{article.cves|}}", "{{article.cves|}} has nothing after the |*")]
    [InlineData("{{tlp|none}}", "{{tlp|none}} can't be optional*")]
    [InlineData("{{else|x}}", "{{else|x}} isn't written as a section's {{else}}*")]
    [InlineData("{{#article.cves?}}x{{/article.cves}}", "{{#article.cves?}} is a section's tag: it takes no ? or |. Write it as {{#article.cves}}.")]
    [InlineData("{{#article.cves}}x{{/article.cves|y}}", "{{/article.cves|y}} is a section's tag*")]
    public void Check_RefusesSectionsThatDontPairUp_AndBadBars(string text, string message) =>
        FluentActions.Invoking(() => OutreachFill.Check(text)).Should().Throw<ArgumentException>().WithMessage(message);

    // Spaces after {{ with nothing to close them are read once, not tried every way they could split: a template of them
    // can't hold the server up.
    [Theory]
    [InlineData("{{")]
    [InlineData("{{#")]
    [InlineData("{{article.title|")]
    public void ALongRunOfSpacesAfterBraces_IsReadQuickly(string start)
    {
        var text = start + new string(' ', 30_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        OutreachFill.Check(text);
        OutreachFill.Tokens(text).Should().BeEmpty();
        watch.ElapsedMilliseconds.Should().BeLessThan(200);
    }

    // The one reading of the syntax the check, the fill and the Library rail share.
    [Fact]
    public void Tokens_ReadEachPieceBetweenBraces_AsTheCheckAndTheFillDo()
    {
        var text = "Hi {{ Product.Contact|there }},{{#indicators:Domain}}{{indicators:domain?}}{{else}}none{{/indicators:domain}} {{nope }} {not}";
        OutreachFill.Tokens(text).Select(t => (t.Kind, t.Name, t.Modifier, t.Optional, t.Fallback)).Should().Equal(
            (OutreachTokenKind.Placeholder, "product.contact", null, false, "there "),
            (OutreachTokenKind.Open, "indicators", "Domain", false, null),
            (OutreachTokenKind.Placeholder, "indicators", "domain", true, null),
            (OutreachTokenKind.Else, "else", null, false, null),
            (OutreachTokenKind.Close, "indicators", "domain", false, null),
            (OutreachTokenKind.Placeholder, "nope", null, false, null));
        var first = OutreachFill.Tokens(text)[0];
        (first.Written, first.Index, first.End).Should().Be(("{{ Product.Contact|there }}", 3, 30));
    }

    // --- optional placeholders ----------------------------------------------------------------------------------------

    [Fact]
    public void AnOptionalPlaceholder_WithNothingToFill_IsDropped_WithItsLineWhenAlone_AndIsntUnfilled()
    {
        var facts = Facts(Article(), TlpLevel.Clear);   // no other links
        var d = OutreachFill.Fill("About {{article.other-links?}} {{article.title}}",
            "{{article.link}}\n{{article.other-links?}}\nSee {{indicators:sha256?}} and the rest.\n{{indicators:sha256?}}{{article.other-links?}}\nThanks", facts);

        d.Subject.Should().Be("About Edge VPN zero-day exploited");
        d.Body.Should().EndWith("\n\nhttps://vendor.example/advisory\nSee and the rest.\nThanks\n", "a line left empty goes; one with other text closes the gap");
        d.Unfilled.Should().BeEmpty();
        OutreachFill.HasMarker(d.Body).Should().BeFalse("nothing holds the send back");
    }

    [Fact]
    public void DroppingAnOptionalLine_LeavesOneBlankLine_NotTwo_ButKeepsTheTemplatesOwn()
    {
        var facts = Facts(Article(), TlpLevel.Clear);   // no other links
        OutreachFill.Fill("x", "{{article.title}}\n\n{{article.other-links?}}\n\nThanks", facts).Body
            .Should().EndWith("\n\nEdge VPN zero-day exploited\n\nThanks\n");
        OutreachFill.Fill("x", "{{article.title}}\n{{article.other-links?}}\n\n\nThanks", facts).Body
            .Should().EndWith("\n\nEdge VPN zero-day exploited\n\n\nThanks\n", "the template's own two blank lines aren't the drop's doing");
    }

    [Fact]
    public void AnOptionalPlaceholder_WithAValue_FillsAsUsual()
    {
        var facts = Facts(Article(), TlpLevel.Clear) with { ArticleLinks = ["https://news.example/follow-up"] };
        OutreachFill.Fill("x", "Further:\n{{article.other-links?}}\n{{today:+1?}}", facts).Body
            .Should().EndWith("\n\nFurther:\nhttps://news.example/follow-up\nMonday, November 9, 2026\n");
    }

    [Fact]
    public void AnOptionalPlaceholder_HeldBackByTheCeiling_IsDropped_ButStillSaidToBeLeftOut()
    {
        var d = OutreachFill.Fill("x", "{{article.title?}}\n{{product.name}}", Facts(Article(TlpLevel.Amber), TlpLevel.Green));
        d.Body.Should().EndWith("\n\nEdge VPN\n").And.NotContain("zero-day");
        d.Unfilled.Should().BeEmpty();
        d.LeftOut.Should().ContainSingle().Which.Should().StartWith("The article is TLP:AMBER, above this template's TLP:GREEN");
    }

    [Fact]
    public void Check_AcceptsAClosingQuestionMark() =>
        FluentActions.Invoking(() => OutreachFill.Check("{{article.other-links?}} {{indicators:domain?}} {{today:+2?}} {{Article.CVES?}}")).Should().NotThrow();

    [Theory]
    [InlineData("{{tlp?}}", "{{tlp?}} can't be optional*")]
    [InlineData("{{article.title ?}}", "{{article.title ?}} isn't written as a placeholder TIDE fills*")]
    [InlineData("{{article.titel?}}", "{{article.titel}} isn't a placeholder TIDE fills*")]
    public void Check_RefusesAnOptionalTlp_OrAQuestionMarkThatDoesntParse(string text, string message) =>
        FluentActions.Invoking(() => OutreachFill.Check(text)).Should().Throw<ArgumentException>().WithMessage(message);

    [Fact]
    public void TheIndicators_AreDefanged_AllOfThem_OrOneKindByTheHuntsNames()
    {
        var facts = Facts(Article(), TlpLevel.Clear);
        OutreachFill.Fill("x", "{{indicators}}", facts).Body.Should().EndWith("\n\n198.51.100[.]7\nevil[.]example\n", "a hunt's order: addresses, then domains");
        OutreachFill.Fill("x", "{{indicators:domain}}", facts).Body.Should().EndWith("\n\nevil[.]example\n");
        var none = OutreachFill.Fill("x", "{{indicators:sha256}}", facts);
        none.Body.Should().EndWith("\n\n[[indicators:sha256: nothing to fill]]\n");
        none.Unfilled.Should().Equal("{{indicators:sha256}}");
    }

    [Fact]
    public void TheProductAndTheActionAndSender_FillTheirFacts_WithNoMarking()
    {
        var d = OutreachFill.Fill("{{action.number}}: {{product.name}}",
            "{{product.name}} {{product.versions}}, {{product.team}}, {{product.contact}}\n{{action.link}} due {{action.due}}\n{{sender.name}}, {{sender.team}}, {{today}}",
            Facts(null, TlpLevel.Clear));

        d.Subject.Should().Be("A-12: Edge VPN");
        d.Body.Should().Be("TLP:CLEAR: Disclosure is not limited.\n\nEdge VPN 22.7R2, Network, Vendor PSIRT\nhttps://tide.example.org/actions/12 due Tuesday, November 10, 2026\n"
            + "Robin, Cyber Threat Intelligence, Friday, November 6, 2026\n");
        d.Marking.Should().Be(TlpLevel.Clear);
    }

    [Fact]
    public void APlaceholderWithNothingToFill_IsMarkedInTheText_AndListed_AndTheMarkerIsFoundAgain()
    {
        var facts = Facts(null, TlpLevel.Clear) with { Product = null, ActionLink = null };
        var d = OutreachFill.Fill("About {{article.title}}", "{{product.versions}} {{action.link}} {{product.versions}}", facts);

        d.Subject.Should().Be("About [[article.title: nothing to fill]]", "an action with no article has no title to give");
        d.Body.Should().Contain("[[product.versions: nothing to fill]] [[action.link: nothing to fill]] [[product.versions: nothing to fill]]");
        d.Unfilled.Should().Equal("{{article.title}}", "{{product.versions}}", "{{action.link}}");
        OutreachFill.HasMarker(d.Body).Should().BeTrue();
        OutreachFill.HasMarker("We run 22.7R2 [[note to self]].").Should().BeFalse("only TIDE's own markers count");
    }

    // --- the TLP ceiling ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnArticleAboveTheCeiling_IsLeftOutWhole_IndicatorsWithIt_AndSaidSo()
    {
        var d = OutreachFill.Fill("{{article.title}}", "{{article.bottom-line}}\n{{indicators}}\n{{product.name}}", Facts(Article(TlpLevel.Amber), TlpLevel.Green));

        d.Subject.Should().Be("[[article.title: above TLP:GREEN, left out]]");
        d.Body.Should().Be("TLP:CLEAR: Disclosure is not limited.\n\n[[article.bottom-line: above TLP:GREEN, left out]]\n"
            + "[[indicators: above TLP:GREEN, left out]]\nEdge VPN\n");
        d.LeftOut.Should().Equal("The article is TLP:AMBER, above this template's TLP:GREEN: its title, bottom line, link, CVEs and indicators are left out.");
        d.Marking.Should().Be(TlpLevel.Clear, "nothing marked was filled in");
    }

    [Fact]
    public void APartAboveTheCeiling_IsLeftOutAlone_ByItsOwnTlp_AsTheEditionsCutIt()
    {
        var d = OutreachFill.Fill("x", "{{article.key-points}}\n{{article.why-it-matters}}", Facts(Article(), TlpLevel.Green));

        d.Body.Should().Be("TLP:CLEAR: Disclosure is not limited.\n\n- Patch is out\n[[article.why-it-matters: above TLP:GREEN, left out]]\n");
        d.LeftOut.Should().Equal("1 key point above TLP:GREEN left out.", "Why it matters is TLP:AMBER, above TLP:GREEN: left out.");

        var amber = OutreachFill.Fill("x", "{{article.why-it-matters}}", Facts(Article(), TlpLevel.Amber));
        amber.Body.Should().Be("TLP:AMBER: Limited disclosure: recipients may share with their organization and its clients on a need-to-know basis.\n\nWe run two of these gateways.\n");
        amber.Marking.Should().Be(TlpLevel.Amber, "the email is marked by the highest part it carries");
    }

    [Fact]
    public void TheActionsOwnIndicators_CarryNoMarking_SoCountAsAmber()
    {
        ArticleIndicator[] own = [new() { Type = IndicatorType.Url, Value = "https://evil.example/x" }];
        var clear = OutreachFill.Fill("x", "{{indicators}}", Facts(null, TlpLevel.Clear, 1, own));
        clear.Body.Should().EndWith("[[indicators: above TLP:CLEAR, left out]]\n");
        clear.LeftOut.Should().Equal("The action's own indicators (1) carry no marking, so count as TLP:AMBER, above TLP:CLEAR: left out.");

        var amber = OutreachFill.Fill("x", "{{indicators}}", Facts(null, TlpLevel.Amber, 1, own));
        amber.Body.Should().EndWith("\n\nhxxps://evil[.]example/x\n");
        amber.Marking.Should().Be(TlpLevel.Amber);
    }

    [Fact]
    public void TheTlpLine_GoesWhereTheTemplatePlacesIt_OrFirst_AndTheSubjectTakesTheLabel()
    {
        var facts = Facts(Article(TlpLevel.Green), TlpLevel.Green);
        var placed = OutreachFill.Fill("[{{tlp}}] {{article.title}}", "Hello,\n\n{{article.title}}\n\n{{tlp}}", facts);
        placed.Subject.Should().Be("[TLP:GREEN] Edge VPN zero-day exploited");
        placed.Body.Should().Be("Hello,\n\nEdge VPN zero-day exploited\n\nTLP:GREEN: Limited disclosure: may be shared within the community, not publicly.\n");

        var first = OutreachFill.Fill("x", "\r\nHello", facts);
        first.Body.Should().Be("TLP:CLEAR: Disclosure is not limited.\n\nHello\n", "nothing marked went in, and Windows line ends are read as one");
    }

    [Fact]
    public void AValueThatSaysTlp_IsLeftAsItIs_OnlyTheTemplatesOwnPlaceholderIsFilled()
    {
        var a = Article();
        a.Title = "Why {{tlp}} matters";
        var d = OutreachFill.Fill("{{article.title}}", "{{article.title}}", Facts(a, TlpLevel.Clear));
        d.Subject.Should().Be("Why {{tlp}} matters");
        d.Body.Should().Be("TLP:CLEAR: Disclosure is not limited.\n\nWhy {{tlp}} matters\n");
    }

    // --- the old kind name --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Outreach", ActionKind.Outreach)]
    [InlineData("VendorInquiry", ActionKind.Outreach)]
    [InlineData(" vendorinquiry ", ActionKind.Outreach)]
    [InlineData("iocsweep", ActionKind.IocSweep)]
    [InlineData("6", null)]
    [InlineData("Vendor inquiry", null)]
    [InlineData(null, null)]
    public void KindNamed_ReadsTheCodeName_AndOutreachsOldOne(string? name, ActionKind? kind) => ActionService.KindNamed(name).Should().Be(kind);

    [Fact]
    public void TheKind_KeepsItsStoredNumber_ItsOutcomesAndItsMeetingPriority()
    {
        ((int)ActionKind.Outreach).Should().Be(6, "actions raised as Vendor inquiry are stored as 6");
        ActionService.KindName(ActionKind.Outreach).Should().Be("Outreach");
        ActionService.KindOutcomes(ActionKind.Outreach).Should().Equal(ActionOutcome.Answered, ActionOutcome.NotAffected, ActionOutcome.Other);
        ActionService.QuickPriority(Priority.P1, ActionKind.Outreach).Should().Be(Priority.P2);
        Enum.GetNames<ActionKind>().Should().NotContain(ActionService.FormerOutreachName);
    }

    [Fact]
    public void ASettingKeyFromBeforeTheRename_ReadsAsOutreachs()
    {
        SettingsCatalog.Renamed("actions:sla:kind:vendorinquiry").Should().Be("Actions:Sla:Kind:Outreach");
        SettingsCatalog.Renamed("Actions:Sla:Kind:Block").Should().BeNull();
        SettingsCatalog.IsInApp("Actions:Sla:Kind:Outreach").Should().BeTrue();
    }
}
