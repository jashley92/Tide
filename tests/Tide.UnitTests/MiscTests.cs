using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Tide.Application.Articles;
using Tide.Application.Content;
using Tide.Application.Org;
using Tide.Application.Scorecard;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

public class OrgClockTests
{
    private static OrgClock At(DateTimeOffset utc) =>
        new(Options.Create(new OrgOptions { TimeZone = "America/New_York", MeetingTime = "10:30" }), new TestClock(utc));

    [Fact]
    public void Today_IsTheLocalDay_NotTheUtcOne()
    {
        // 02:00 UTC on Tuesday is still Monday evening in New York.
        At(new DateTimeOffset(2026, 10, 27, 2, 0, 0, TimeSpan.Zero)).Today.Should().Be(new DateOnly(2026, 10, 26));
    }

    [Fact]
    public void DayOf_IsTheLocalDayAnInstantFallsOn()
    {
        var clock = At(DateTimeOffset.UtcNow);
        clock.DayOf(new DateTimeOffset(2026, 10, 27, 2, 0, 0, TimeSpan.Zero)).Should().Be(new DateOnly(2026, 10, 26), "still Monday evening in New York");
        clock.DayOf(new DateTimeOffset(2026, 10, 27, 5, 0, 0, TimeSpan.Zero)).Should().Be(new DateOnly(2026, 10, 27));
    }

    [Fact]
    public void CurrentBriefDate_OnAWeekend_IsMonday()
    {
        At(new DateTimeOffset(2026, 10, 24, 15, 0, 0, TimeSpan.Zero)).CurrentBriefDate.Should().Be(new DateOnly(2026, 10, 26));
    }

    [Theory]
    // Before and after the US change back to standard time (November 1, 2026): 10:30 local is 14:30 then 15:30 UTC.
    [InlineData(2026, 10, 30, 14)]
    [InlineData(2026, 11, 2, 15)]
    public void MeetingStart_FollowsDaylightSaving(int y, int m, int d, int utcHour)
    {
        var start = At(DateTimeOffset.UtcNow).MeetingStart(new DateOnly(y, m, d));
        start.UtcDateTime.Hour.Should().Be(utcHour);
        start.UtcDateTime.Minute.Should().Be(30);
    }

    [Fact]
    public void BusinessDays_SkipWeekends()
    {
        WorkingDays.Weekdays.Next(new DateOnly(2026, 10, 23)).Should().Be(new DateOnly(2026, 10, 26));
        WorkingDays.Weekdays.Previous(new DateOnly(2026, 10, 26)).Should().Be(new DateOnly(2026, 10, 23));
    }
}

public class MarkdownServiceTests
{
    private readonly MarkdownService _md = new();

    [Fact]
    public void ABareMaliciousAddress_IsNeverALink()
    {
        _md.ToHtml("Payload at https://evil.example.com/stage2").Should().NotContain("<a ");
    }

    [Fact]
    public void AWrittenLink_OpensSafely()
    {
        _md.ToInlineHtml("[advisory](https://vendor.example.com/a)").Should()
            .Contain("href=\"https://vendor.example.com/a\"").And.Contain("rel=\"noopener noreferrer\"");
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("![pixel](https://tracker.example.com/p.gif)")]
    public void UnsafeContent_IsDefused(string markdown)
    {
        var html = _md.ToHtml(markdown);
        html.Should().NotContain("javascript:").And.NotContain("<script").And.NotContain("<img");
    }
}

public class SecurityTests
{
    [Fact]
    public void Admin_HoldsEveryPermission()
    {
        RoleDefinitions.PermissionsFor([AppRole.Admin]).Should().BeEquivalentTo(Enum.GetValues<Permission>());
    }

    [Fact]
    public void Reader_SeesOnlyThePublishedArchive()
    {
        RoleDefinitions.PermissionsFor([AppRole.Reader]).Should().Equal(Permission.ReadPublished);
    }

    [Fact]
    public void EveryPermission_HasAPolicy()
    {
        Policies.Required.Values.Should().BeEquivalentTo(Enum.GetValues<Permission>());
    }

    [Theory]
    [InlineData("Analyst", true)]
    [InlineData("analyst", true)]
    [InlineData("3", false)]
    [InlineData("Analyst,Admin", false)]
    [InlineData(" Admin", false)]
    public void TryParseRole_AcceptsOnlyAName(string name, bool ok) => RoleDefinitions.TryParseRole(name, out _).Should().Be(ok);
}

public class ArticleChecksTests
{
    [Fact]
    public void AThreatWithoutPriority_IsNotReady()
    {
        var a = new Article { Bluf = "x", Classification = Classification.Threat };
        a.Bullets.Add(new ArticleBullet { Text = "y" });
        ArticleChecks.For(a).Should().Contain(c => c.Blocking && c.Message.Contains("priority"));
    }

    [Fact]
    public void ACompleteArticle_HasNoChecks()
    {
        var a = new Article { Bluf = "x", SourceName = "Vendor", Classification = Classification.Informational };
        a.Bullets.Add(new ArticleBullet { Text = "y" });
        a.Tags.Add(new ArticleTag { TagId = Guid.NewGuid() });
        ArticleChecks.For(a).Should().BeEmpty();
    }

    [Fact]
    public void EachCheck_PointsAtThePartThatFixesIt()
    {
        var a = new Article { Classification = Classification.Threat };
        a.Screenshots.Add(new ArticleScreenshot());

        ArticleChecks.For(a).Select(c => c.Part).Should().Equal(
            ArticlePart.BottomLine, ArticlePart.Bullets, ArticlePart.Assessment, ArticlePart.Recommended,
            ArticlePart.Tags, ArticlePart.Assessment, ArticlePart.Screenshots);
    }
}

public class ArticleFormTlpFloorTests
{
    private static ArticleForm Form(TlpLevel article, string? why = "Ours") => new()
    {
        Tlp = article,
        WhyItMatters = why,
        WhyItMattersTlp = TlpLevel.Green,
        Bullets = [new() { Tlp = TlpLevel.Clear }, new() { Tlp = TlpLevel.Red }],
        Screenshots = [new() { Tlp = TlpLevel.Green }],
    };

    [Fact]
    public void RaisingTheArticle_PullsLowerPartsUp_AndCountsThem()
    {
        var form = Form(TlpLevel.Clear);

        form.SetTlp(TlpLevel.Amber).Should().Be(3);

        form.Bullets.Select(b => b.Tlp).Should().Equal(TlpLevel.Amber, TlpLevel.Red);
        form.Screenshots.Single().Tlp.Should().Be(TlpLevel.Amber);
        form.WhyItMattersTlp.Should().Be(TlpLevel.Amber);
    }

    [Fact]
    public void LoweringTheArticle_LeavesThePartsAlone()
    {
        var form = Form(TlpLevel.Clear);
        form.SetTlp(TlpLevel.Amber);

        form.SetTlp(TlpLevel.Clear).Should().Be(0);

        form.Bullets.Select(b => b.Tlp).Should().Equal(TlpLevel.Amber, TlpLevel.Red);
        form.WhyItMattersTlp.Should().Be(TlpLevel.Amber);
    }

    [Fact]
    public void AnEmptyWhyItMatters_IsPulledUp_ButNotCounted()
    {
        var form = Form(TlpLevel.Clear, why: " ");

        form.SetTlp(TlpLevel.Amber).Should().Be(2);
        form.WhyItMattersTlp.Should().Be(TlpLevel.Amber);
    }

    [Fact]
    public void PartsStoredBelowTheArticle_LoadAtTheArticlesTlp()
    {
        var a = new Article { Tlp = TlpLevel.Amber, WhyItMattersTlp = TlpLevel.Clear };
        a.Bullets.Add(new ArticleBullet { Text = "low", Tlp = TlpLevel.Green, SortOrder = 0 });
        a.Bullets.Add(new ArticleBullet { Text = "high", Tlp = TlpLevel.Red, SortOrder = 1 });
        a.Screenshots.Add(new ArticleScreenshot { Tlp = TlpLevel.Clear });

        var form = ArticleForm.From(a);

        form.Bullets.Select(b => b.Tlp).Should().Equal(TlpLevel.Amber, TlpLevel.Red);
        form.Screenshots.Single().Tlp.Should().Be(TlpLevel.Amber);
        form.WhyItMattersTlp.Should().Be(TlpLevel.Amber);
    }
}

public class ScorecardKeyTests
{
    [Theory]
    [InlineData("Third-party & supply chain", "third-party-supply-chain")]
    [InlineData("  Ransomware  ", "ransomware")]
    [InlineData("!!!", "tag")]
    public void KeyFor(string name, string key) => ScorecardService.KeyFor(name).Should().Be(key);
}

public class BulletLinesTests
{
    [Fact]
    public void ListMarkers_AreDropped_ButLeadingBoldIsKept()
    {
        var text = "- first\n* second\n• third\n1. fourth\n2) fifth\n**CVE-2026-0001** is bold\n- **Bold** after a marker\n*emphasis* too\n\n   \n-no space";
        MarkdownService.BulletLines(text).Should().Equal(
            "first", "second", "third", "fourth", "fifth", "**CVE-2026-0001** is bold", "**Bold** after a marker", "*emphasis* too", "-no space");
    }

    [Fact]
    public void Nothing_GivesNoBullets() => MarkdownService.BulletLines(null).Should().BeEmpty();
}

public class AddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", false, "loopback")]
    [InlineData("::1", true, "loopback")]
    [InlineData("::ffff:127.0.0.1", true, "loopback")]
    [InlineData("169.254.169.254", true, "link-local")]
    [InlineData("fe80::1", true, "link-local")]
    [InlineData("0.0.0.0", true, "reserved")]
    [InlineData("224.0.0.1", true, "reserved")]
    [InlineData("255.255.255.255", true, "reserved")]
    [InlineData("10.1.2.3", false, "private")]
    [InlineData("172.16.0.1", false, "private")]
    [InlineData("172.31.255.255", false, "private")]
    [InlineData("192.168.1.1", false, "private")]
    [InlineData("100.64.0.1", false, "private")]
    [InlineData("fd00::1", false, "private")]
    [InlineData("::ffff:10.0.0.1", false, "private")]
    public void Refusal_RefusesWhatNoFeedLivesOn(string address, bool allowPrivate, string why) =>
        Tide.Infrastructure.Notifications.AddressPolicy.Refusal(System.Net.IPAddress.Parse(address), allowPrivate).Should().Contain(why);

    [Theory]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("fd00::1", true)]
    public void Refusal_AllowsPublicAddresses_AndPrivateOnesWhenAllowed(string address, bool allowPrivate) =>
        Tide.Infrastructure.Notifications.AddressPolicy.Refusal(System.Net.IPAddress.Parse(address), allowPrivate).Should().BeNull();
}

public class AttackCatalogTests
{
    [Fact]
    public void Retired_SaysWhereAMergedIdWent_AndKnowsNothingOfLiveOrMadeUpIds()
    {
        var merged = Tide.Application.Mitre.AttackCatalog.Retired("t1574.002")!;
        merged.ReplacedBy!.Id.Should().Be("T1574.001");
        Tide.Application.Mitre.AttackCatalog.RetiredText(merged).Should().Contain("merged into T1574.001");
        Tide.Application.Mitre.AttackCatalog.Retired("T1566").Should().BeNull("it's live");
        Tide.Application.Mitre.AttackCatalog.Retired("T9999").Should().BeNull();
        Tide.Application.Mitre.AttackCatalog.Find("T1574.002").Should().BeNull("the picker offers only live techniques");
    }
}

/// <summary>The reports' one number formatter.</summary>
public class WordsNumTests
{
    [Fact]
    public void Num_ShowsOneDecimalAtMost_InvariantCulture_AndDaysSaysOneDay()
    {
        var was = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Words.Num(2.24).Should().Be("2.2", "a full stop, whatever the server's culture");
            Words.Num(7).Should().Be("7");
            Words.Num(null).Should().BeEmpty();
            Words.Num(null, "—").Should().Be("—");
            Words.Days(1).Should().Be("1 day");
            Words.Days(1.04).Should().Be("1 day", "the shown number decides");
            Words.Days(2.5).Should().Be("2.5 days");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = was; }
    }
}
