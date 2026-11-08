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
    public void TheArticle_FillsItsTitleBottomLineLinkCvesAndTlp_WithKevAndEpssWhereKnown()
    {
        var d = OutreachFill.Fill("{{article.title}}", "{{article.bottom-line}}\n{{article.link}}\n{{article.cves}}\n{{article.tlp}}", Facts(Article(TlpLevel.Green), TlpLevel.Green));

        d.Subject.Should().Be("Edge VPN zero-day exploited");
        d.Body.Should().Be("TLP:GREEN: Limited disclosure: may be shared within the community, not publicly.\n\n"
            + "A pre-auth flaw in the VPN gateway is exploited in the wild.\nhttps://vendor.example/advisory\n"
            + "CVE-2026-0002\nCVE-2026-1234 (on CISA's KEV list, used in ransomware; EPSS 94.1%)\nTLP:GREEN\n");
        d.Marking.Should().Be(TlpLevel.Green);
        d.Unfilled.Should().BeEmpty();
        d.LeftOut.Should().BeEmpty("the key point and why it matters held back aren't in this template");
    }

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
