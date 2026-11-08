using System.Text;
using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Application.Indicators;
using Tide.Application.Intake;
using Tide.Application.Metrics;
using Tide.Domain.Enums;

namespace Tide.UnitTests.Audit;

/// <summary>Pure-logic checks from the backend audit: parsing, time maths and extraction at their edges.</summary>
public class BackendAuditTests
{
    [Theory]
    [InlineData("Mon, 26 Oct 2026 09:00:00 EST", "2026-10-26T14:00:00Z")]
    [InlineData("Mon, 26 Oct 2026 09:00:00 PDT", "2026-10-26T16:00:00Z")]
    [InlineData("2026-10-26T09:00:00+02:00", "2026-10-26T07:00:00Z")]
    [InlineData("2026-10-26T09:00:00", "2026-10-26T09:00:00Z")]
    public void FeedParser_ReadsNamedZones_AndAssumesUtcForNakedTimes(string text, string expectedUtc)
    {
        var feed = $"<rss version=\"2.0\"><channel><item><title>T</title><link>https://e.com/a</link><pubDate>{text}</pubDate></item></channel></rss>";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(feed));

        var item = FeedParser.ParseFeed(ms).Single();

        item.PublishedAtUtc.Should().Be(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void FeedParser_SkipsADoctype_WithoutEntities_AndReadsTheFeed()
    {
        var feed = "<?xml version=\"1.0\"?><!DOCTYPE rss [<!ELEMENT rss ANY>]><rss version=\"2.0\"><channel><item><title>T</title><link>https://e.com/a</link></item></channel></rss>";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(feed));

        FeedParser.ParseFeed(ms).Should().ContainSingle().Which.Title.Should().Be("T");
    }

    // --- A5-R-6: a description nested tens of thousands of elements deep overflowed the stack when its text was read
    // (XElement.Value recurses), which no catch can stop; the reader now refuses a document past MaxDepth first --------

    [Theory]
    [InlineData(50_000)]
    [InlineData(FeedParser.MaxDepth + 1)]
    public void FeedParser_RefusesADocumentNestedTooDeep_EvenOnASmallStack(int depth)
    {
        var xml = "<rss version=\"2.0\"><channel><item><title>deep</title><link>https://e.example/1</link><description>" +
                  string.Concat(Enumerable.Repeat("<a>", depth)) + string.Concat(Enumerable.Repeat("</a>", depth)) + "</description></item></channel></rss>";

        // On a 256 KB stack the walk through 50,000 nested elements overflowed every time; now the document is refused before
        // any tree is built.
        Exception? outcome = null;
        var thread = new Thread(() =>
        {
            try { FeedParser.ParseFeed(new MemoryStream(Encoding.UTF8.GetBytes(xml))); }
            catch (Exception ex) { outcome = ex; }
        }, 256 * 1024);
        thread.Start();
        thread.Join();

        outcome.Should().BeOfType<FormatException>().Which.Message.Should().Contain("nested more than");
    }

    [Fact]
    public void FeedParser_TakesADocumentNestedToTheLimit_AndANonSeekableStream()
    {
        var depth = FeedParser.MaxDepth - 4;   // rss/channel/item/description sit above the nested elements
        var xml = "<rss version=\"2.0\"><channel><item><title>deep</title><link>https://e.example/1</link><description>" +
                  string.Concat(Enumerable.Repeat("<a>", depth)) + "text" + string.Concat(Enumerable.Repeat("</a>", depth)) + "</description></item></channel></rss>";
        var bytes = Encoding.UTF8.GetBytes(xml);

        var items = FeedParser.ParseFeed(new NonSeekable(bytes));

        items.Should().ContainSingle().Which.Summary.Should().Be("text");
    }

    private sealed class NonSeekable(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void FeedParser_CapsItems_AndCategories()
    {
        var items = string.Concat(Enumerable.Range(0, FeedParser.MaxItems + 20).Select(i =>
            $"<item><title>T{i}</title><link>https://e.com/{i}</link>{string.Concat(Enumerable.Range(0, 40).Select(c => $"<category>c{c}</category>"))}</item>"));
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes($"<rss version=\"2.0\"><channel>{items}</channel></rss>"));

        var parsed = FeedParser.ParseFeed(ms);

        parsed.Should().HaveCount(FeedParser.MaxItems);
        parsed[0].Categories.Should().HaveCount(30);
    }

    [Theory]
    [InlineData("HTTP://Example.com:80/a/?utm_source=x&b=2&a=1#frag", "http://example.com/a?a=1&b=2")]
    [InlineData("https://example.com/", "https://example.com")]
    [InlineData("https://example.com:8443/x/", "https://example.com:8443/x")]
    [InlineData("ftp://example.com/x", null)]
    [InlineData("not a url", null)]
    public void UrlCanonicalizer_GivesOneAddressPerStory(string url, string? expected) =>
        UrlCanonicalizer.Canonicalize(url).Should().Be(expected);

    [Fact]
    public void IocExtractor_ReadsDefangedMix_AndSkipsProseDomains()
    {
        var found = IocExtractor.Extract("Contact admin[at]evil[.]com or hxxps://evil[.]com/x?a=1, then 10.0.0[.]1 and 2001:db8::1. "
                                         + "See ASP.NET and U.S. news (example.org). Hash d41d8cd98f00b204e9800998ecf8427e; CVE-2026-0001.");

        found.Indicators.Select(i => (i.Type, i.Value)).Should().BeEquivalentTo(
        [
            (IndicatorType.EmailAddress, "admin@evil.com"),
            (IndicatorType.Url, "https://evil.com/x?a=1"),
            (IndicatorType.IpAddress, "10.0.0.1"),
            (IndicatorType.IpAddress, "2001:db8::1"),
            (IndicatorType.Domain, "example.org"),
            (IndicatorType.FileHash, "d41d8cd98f00b204e9800998ecf8427e"),
        ]);
        found.Cves.Should().Equal("CVE-2026-0001");
    }

    [Fact]
    public void IocExtractor_StopsAtTheCap()
    {
        var text = string.Join(' ', Enumerable.Range(0, IocExtractor.MaxIndicators + 50).Select(i => $"10.{i / 256}.{i % 256}.9"));
        IocExtractor.Extract(text).Indicators.Should().HaveCount(IocExtractor.MaxIndicators);
    }

    [Theory]
    [InlineData("1,204", 1204)]
    [InlineData("97.5%", 97.5)]
    [InlineData("3.5h", 3.5)]
    [InlineData("-2", -2)]
    [InlineData(" 12 ", 12)]
    public void MetricMath_ParsesTypedValues_InvariantOfCulture(string text, decimal expected)
    {
        var was = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            MetricMath.TryParseValue(text, out var value).Should().BeTrue();
            value.Should().Be(expected);
        }
        finally { Thread.CurrentThread.CurrentCulture = was; }
    }

    [Theory]
    [InlineData("1e3")]
    [InlineData("100000000000000")]
    [InlineData("")]
    [InlineData("abc")]
    public void MetricMath_RefusesWhatItCantKeep(string text) => MetricMath.TryParseValue(text, out _).Should().BeFalse();

    [Fact]
    public void ActionSla_DueDate_SkipsWeekends_FromAnyDay()
    {
        var friday = new DateOnly(2026, 10, 30);
        var saturday = friday.AddDays(1);
        ActionSla.DueDate(friday, 1).Should().Be(new DateOnly(2026, 11, 2), "Monday");
        ActionSla.DueDate(saturday, 1).Should().Be(new DateOnly(2026, 11, 2), "a weekend start counts from Monday");
        ActionSla.DueDate(friday, 5).Should().Be(new DateOnly(2026, 11, 6));
        ActionSla.DueDate(friday, 0).Should().BeNull();
        ActionSla.BusinessDaysUntil(new DateOnly(2026, 11, 2), friday).Should().Be(-1, "Friday is one business day before Monday");
        ActionSla.BusinessDaysUntil(friday, new DateOnly(2026, 11, 2)).Should().Be(1);
    }

    // --- Zip entry names come from the stored file's name: only a known image extension is kept, never a path ---------

    [Theory]
    [InlineData("..\\..\\evil.png", "02-login-page.png")]
    [InlineData("C:/x/y.JPG", "02-login-page.jpg")]
    [InlineData("shot.p:ng", "02-login-page.png")]
    [InlineData("shot.exe", "02-login-page.png")]
    [InlineData("", "02-login-page.png")]
    public void RenderImage_FileName_IsNumberCaptionAndAKnownExtension(string stored, string expected)
    {
        var shot = new Tide.Domain.Entities.ArticleScreenshot
        {
            Caption = "Login page / <b>evil</b> ..\\up", File = new Tide.Domain.Entities.StoredFile { FileName = stored },
        };

        var name = new Tide.Application.Distribution.RenderImage(shot, "shot2@tide", 2, null).FileName;

        name.Should().StartWith("02-login-page").And.NotContainAny("/", "\\", ":", "<", "..");
        name.Should().Be(expected.Replace("login-page", "login-page-b-evil-b-up"));
    }

    // --- Rendering: the company-wide edition drops an AMBER article's CLEAR screenshot whole, and a hostile source link can't
    // break out of its attribute ---------------------------------------------------------------------------------------

    [Fact]
    public void Renderer_PublicEdition_CarriesNoScreenshotOfAnArticleAboveTheCeiling_InImagesHtmlOrText()
    {
        var amber = new Tide.Domain.Entities.Article { Title = "Partner article", Tlp = TlpLevel.Amber, Bluf = "Partner bottom line." };
        amber.Screenshots.Add(new Tide.Domain.Entities.ArticleScreenshot { Caption = "partner console", Tlp = TlpLevel.Clear });
        var open = new Tide.Domain.Entities.Article { Title = "Open article", Tlp = TlpLevel.Clear, Bluf = "Open bottom line." };
        open.Screenshots.Add(new Tide.Domain.Entities.ArticleScreenshot { Caption = "open console", Tlp = TlpLevel.AmberStrict });
        var brief = new Tide.Domain.Entities.Brief { BriefDate = new DateOnly(2026, 10, 26), AnalystName = "Analyst" };
        brief.Items.Add(new Tide.Domain.Entities.BriefItem { Article = amber, ArticleId = amber.Id, SortOrder = 0 });
        brief.Items.Add(new Tide.Domain.Entities.BriefItem { Article = open, ArticleId = open.Id, SortOrder = 1 });
        var renderer = new Tide.Application.Distribution.BriefRenderer(new Tide.Application.Content.MarkdownService());

        var r = renderer.Render(Tide.Application.Briefs.BriefEditions.Build(brief, Edition.Public, TlpLevel.Green),
            new Tide.Application.Distribution.BriefRenderContext("CTI", null, null, img => "cid:" + img.ContentId));

        r.Images.Should().BeEmpty();
        foreach (var text in new[] { "partner console", "open console", "Partner article", "Partner bottom line", "cid:" })
        {
            r.EmailHtml.Should().NotContain(text);
            r.Text.Should().NotContain(text);
            r.WebHtml.Should().NotContain(text);
        }
        r.EmailHtml.Should().Contain("1 restricted item was briefed").And.Contain("1 restricted part was left out");
    }

    [Fact]
    public void Renderer_EscapesASourceLink_AndACaption_InAttributes()
    {
        var a = new Tide.Domain.Entities.Article
        {
            Title = "T", Tlp = TlpLevel.Clear, Bluf = "B", SourceUrl = "https://e.com/x?a=\"><script>alert(1)</script>",
        };
        a.Screenshots.Add(new Tide.Domain.Entities.ArticleScreenshot { Caption = "\" onerror=\"alert(1)", Tlp = TlpLevel.Clear });
        var brief = new Tide.Domain.Entities.Brief { BriefDate = new DateOnly(2026, 10, 26), AnalystName = "<b>A</b>" };
        brief.Items.Add(new Tide.Domain.Entities.BriefItem { Article = a, ArticleId = a.Id, SortOrder = 0 });
        var renderer = new Tide.Application.Distribution.BriefRenderer(new Tide.Application.Content.MarkdownService());

        var r = renderer.Render(Tide.Application.Briefs.BriefEditions.Build(brief, Edition.Team, TlpLevel.AmberStrict),
            new Tide.Application.Distribution.BriefRenderContext("CTI", null, "https://tide.example.org", img => "cid:" + img.ContentId));

        foreach (var html in new[] { r.EmailHtml, r.WebHtml })
        {
            html.Should().NotContain("<script>").And.NotContain("onerror=\"alert").And.NotContain("<b>A</b>");
            html.Should().Contain("href=\"https://e.com/x?a=&quot;&gt;&lt;script&gt;");
            html.Should().Contain("alt=\"&quot; onerror=&quot;alert(1)\"");
        }
    }

    // --- A2-21: a zero-padded IPv4 address (how logs print one) was stored as typed, so STIX, hunt queries and the exact
    // sighting lookup never met "10.0.0.1"; a domain kept its case, which DNS ignores but a SIEM's literal match doesn't ---

    [Theory]
    [InlineData("010.000.000.001", "10.0.0.1")]
    [InlineData("10.0.0.1", "10.0.0.1")]
    [InlineData("203.0.113[.]045", "203.0.113.45")]
    [InlineData("::ffff:010.0.0.1", "10.0.0.1")]
    [InlineData("::FFFF:A00:1", "10.0.0.1")]
    [InlineData("2001:0DB8:0000:0000:0000:0000:0000:0001", "2001:db8::1")]
    [InlineData("fe80::1%eth0", "fe80::1")]
    [InlineData("fe80::1%2", "fe80::1%2")]
    [InlineData("10.0.0.0/24", "10.0.0.0/24")]
    [InlineData("0x0A.0.0.1", "0x0A.0.0.1")]
    public void Normalize_IpAddress_HasOneSpelling(string typed, string expected) =>
        Tide.Domain.Observables.IocObservable.Normalize(IndicatorType.IpAddress, typed).Should().Be(expected);

    [Theory]
    [InlineData("EVIL[.]Com.", "evil.com")]
    [InlineData("München.de", "münchen.de")]
    [InlineData("XN--MNCHEN-3YA.de", "xn--mnchen-3ya.de")]
    [InlineData("evil(dot)example(dot)net", "evil.example.net")]
    public void Normalize_Domain_IsLowerCase_Refanged_WithoutTheRootDot(string typed, string expected) =>
        Tide.Domain.Observables.IocObservable.Normalize(IndicatorType.Domain, typed).Should().Be(expected);

    [Fact]
    public void Stix_Pattern_CarriesTheCanonicalAddress()
    {
        var value = Tide.Domain.Observables.IocObservable.Normalize(IndicatorType.IpAddress, "010.000.000.001");
        Tide.Application.Exports.Stix.Pattern(new Tide.Domain.Entities.ArticleIndicator { Type = IndicatorType.IpAddress, Value = value })
            .Should().Be("[ipv4-addr:value = '10.0.0.1']");
    }

    // --- Observables: forms that must classify right, and defang/refang round trips ---------------------------------

    [Theory]
    [InlineData("xn--mnchen-3ya.de", IndicatorType.Domain)]
    [InlineData("münchen.de", IndicatorType.Domain)]
    [InlineData("пример.рф", IndicatorType.Domain)]
    [InlineData("evil.com.", IndicatorType.Domain)]
    [InlineData("my--bank.com", IndicatorType.Domain)]
    [InlineData("evil(dot)com", IndicatorType.Domain)]
    [InlineData("hxxp://evil[.]com/a.exe", IndicatorType.Url)]
    [InlineData("user[at]evil[dot]com", IndicatorType.EmailAddress)]
    [InlineData("first+tag@evil.com", IndicatorType.EmailAddress)]
    [InlineData("2001:db8::1", IndicatorType.IpAddress)]
    [InlineData("::ffff:10.0.0.1", IndicatorType.IpAddress)]
    [InlineData("010.000.000.001", IndicatorType.IpAddress)]
    [InlineData("1.2.3", IndicatorType.Other)]
    [InlineData("256.1.1.1", IndicatorType.Other)]
    [InlineData("0x0A.0.0.1", IndicatorType.Other)]
    [InlineData("1.2.3.4:443", IndicatorType.Other)]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e", IndicatorType.FileHash)]
    [InlineData("invoice.docm", IndicatorType.FileName)]
    [InlineData("CORP\\jsmith", IndicatorType.Account)]
    [InlineData("powershell -enc AAAA", IndicatorType.CommandLine)]
    public void DetectType_ReadsTheForm(string value, IndicatorType expected) =>
        Tide.Domain.Observables.IocObservable.DetectType(value).Should().Be(expected);

    [Theory]
    [InlineData("https://evil.example.com/x?u=a@b&v=1.2")]
    [InlineData("first.last@evil.example.com")]
    [InlineData("10.0.0.1")]
    [InlineData("evil.example.com")]
    [InlineData("ftp://files.evil.net/x.zip")]
    public void Defang_ThenRefang_GivesTheLiveValueBack_AndRefangIsIdempotent(string live)
    {
        var defanged = Tide.Domain.Observables.IocObservable.Defang(live);
        defanged.Should().NotBe(live).And.NotContain("http://").And.NotContain("https://");
        Tide.Domain.Observables.IocObservable.Refang(defanged).Should().Be(live);
        Tide.Domain.Observables.IocObservable.Refang(live).Should().Be(live);
        Tide.Domain.Observables.IocObservable.Refang(Tide.Domain.Observables.IocObservable.Refang(defanged)).Should().Be(live);
    }

    [Theory]
    [InlineData("0x0A.0.0.1", false, true)]
    [InlineData("010.0.0.1", true, false)]
    [InlineData("1.2.3", false, true)]   // the framework would read it as 1.2.0.3
    [InlineData("1.2.3.4.5", false, false)]
    [InlineData("256.1.1.1", false, false)]
    [InlineData("::ffff:0x0A.0.0.1", false, false)]
    [InlineData("[2001:db8::1]", true, false)]
    public void IpText_ReadsDecimalDottedQuads_AndNothingLooser(string text, bool parses, bool nonDecimal)
    {
        Tide.Domain.Observables.IpText.TryParse(text, out _).Should().Be(parses);
        Tide.Domain.Observables.IpText.IsNonDecimalIpv4(text).Should().Be(nonDecimal);
    }

    // --- A3-18: a part stored below its article's marking counts at the article's (max of the two) in every edition,
    // so it can never reach an edition the article can't, and never lowers the edition's marking ------------------------

    [Fact]
    public void Editions_FilterAndMarkParts_AtTheEffectiveTlp_NeverTheStoredOne()
    {
        var amber = new Tide.Domain.Entities.Article { Title = "Amber article", Tlp = TlpLevel.Amber, Bluf = "b", WhyItMatters = "why", WhyItMattersTlp = TlpLevel.Clear };
        amber.Bullets.Add(new Tide.Domain.Entities.ArticleBullet { Text = "stored CLEAR", Tlp = TlpLevel.Clear });
        amber.Screenshots.Add(new Tide.Domain.Entities.ArticleScreenshot { Caption = "stored CLEAR shot", Tlp = TlpLevel.Clear });
        var green = new Tide.Domain.Entities.Article { Title = "Green article", Tlp = TlpLevel.Green, Bluf = "b" };
        green.Bullets.Add(new Tide.Domain.Entities.ArticleBullet { Text = "stored CLEAR under GREEN", Tlp = TlpLevel.Clear });
        var brief = new Tide.Domain.Entities.Brief { BriefDate = new DateOnly(2026, 10, 26), AnalystName = "A" };
        brief.Items.Add(new Tide.Domain.Entities.BriefItem { Article = amber, ArticleId = amber.Id, SortOrder = 0 });
        brief.Items.Add(new Tide.Domain.Entities.BriefItem { Article = green, ArticleId = green.Id, SortOrder = 1 });

        var pub = Tide.Application.Briefs.BriefEditions.Build(brief, Edition.Public, TlpLevel.Green);
        pub.Articles.Select(a => a.Article.Title).Should().Equal(new[] { "Green article" }, "the AMBER article is withheld whole, its CLEAR-stored parts with it");
        pub.WithheldArticles.Should().Be(1);
        pub.Marking.Should().Be(TlpLevel.Green, "a bullet stored CLEAR under a GREEN article counts as GREEN");

        var clear = Tide.Application.Briefs.BriefEditions.Build(brief, Edition.Public, TlpLevel.Clear);
        clear.Articles.Should().BeEmpty("nothing stored CLEAR inside a GREEN or AMBER article reaches a CLEAR ceiling");
        clear.WithheldArticles.Should().Be(2);

        var team = Tide.Application.Briefs.BriefEditions.Build(brief, Edition.Team, TlpLevel.Amber);
        var cut = team.Articles.Single(a => a.Article.Title == "Amber article");
        cut.Bullets.Should().ContainSingle();
        cut.Screenshots.Should().ContainSingle();
        cut.ShowWhy.Should().BeTrue();
        cut.Marking.Should().Be(TlpLevel.Amber);
        team.Marking.Should().Be(TlpLevel.Amber);
    }

    [Fact]
    public void MetricCsv_ReadsQuotedCells_WithCommasAndLineBreaks()
    {
        var csv = "date,metric_key,value,note\r\n2026-10-28,open,\"1,204\",\"a note, with\r\na line break\"\r\n\r\n2026-10-29,open,7,\r\n";

        var file = MetricCsv.Read(Encoding.UTF8.GetBytes(csv));

        file.Refusal.Should().BeNull();
        file.Rows.Should().HaveCount(2);
        file.Rows[0].Value.Should().Be("1,204");
        file.Rows[0].Note.Should().Be("a note, with\r\na line break");
        file.Rows[1].Row.Should().Be(4, "the blank line counts as a row, as a spreadsheet numbers it");
    }
}
