using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Tide.Application.Content;
using Tide.Application.Distribution;
using Tide.Application.Files;
using Tide.Application.Indicators;
using Tide.Application.Intake;
using Tide.Application.Metrics;
using Tide.Domain.Enums;
using Tide.Domain.Observables;

namespace Tide.UnitTests.Audit;

// Agent 5 (independent QA / adversarial review): hostile input against the pure parsing and rendering code.

public partial class MarkdownInjectionTests
{
    [System.Text.RegularExpressions.GeneratedRegex("<[^>]*>")]
    private static partial System.Text.RegularExpressions.Regex AnyTag();

    [System.Text.RegularExpressions.GeneratedRegex("^</?([a-zA-Z][a-zA-Z0-9]*)")]
    private static partial System.Text.RegularExpressions.Regex TagName();

    private static readonly MarkdownService Md = new();

    public static TheoryData<string> Payloads =>
    [
        "<script>alert(1)</script>",
        "<img src=x onerror=alert(1)>",
        "<svg onload=alert(1)><script>alert(1)</script></svg>",
        "<iframe src=\"javascript:alert(1)\"></iframe>",
        "<a href=\"javascript:alert(1)\">x</a>",
        "[x](javascript:alert(1))",
        "[x](JavaScript:alert(1))",
        "[x](  javascript:alert(1))",
        "[x](\tjavascript:alert(1))",
        "[x](javascript&#58;alert(1))",
        "[x](&#106;avascript:alert(1))",
        "[x](&#x6A;avascript:alert(1))",
        "[x](java\u0000script:alert(1))",
        "[x](javascript:alert(1) \"title\")",
        "[x](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)",
        "[x](vbscript:msgbox(1))",
        "[x](file:///etc/passwd)",
        "[x](\\javascript:alert(1))",
        "[x]: javascript:alert(1)\n\n[x]",
        "[x]: data:text/html,<script>alert(1)</script>\n\n[x][x]",
        "<javascript:alert(1)>",
        "<data:text/html,x>",
        "![x](javascript:alert(1))",
        "![x](https://evil.example/pixel.gif)",
        "[x](https://example.com/\" onmouseover=\"alert(1))",
        "[x](#\" onclick=\"alert(1))",
        "[x](/\"><script>alert(1)</script>)",
        "<details open ontoggle=alert(1)>",
        "<math><mtext></mtext><script>alert(1)</script></math>",
        "<style>@import 'https://evil.example/x.css';</style>",
        "<?php echo 1 ?>",
        "<!-- --><script>alert(1)</script>",
        "<![CDATA[<script>alert(1)</script>]]>",
        "<![CDATA[]]><script>alert(1)</script>",
        "&lt;script&gt;alert(1)&lt;/script&gt;",
        "**bold**<script>alert(1)</script>",
        "    <script>alert(1)</script>",
        "```html\n<script>alert(1)</script>\n```",
        "<a href='javascript:alert(1)'>x</a>",
    ];

    // The tags the renderer may emit: what Markdig produces for the syntax TIDE allows, none of them active content.
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "ul", "ol", "li", "a", "em", "strong", "code", "pre", "blockquote", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "span", "del",
        "table", "thead", "tbody", "tr", "th", "td",
    };

    [Theory]
    [MemberData(nameof(Payloads))]
    public void RenderedMarkdown_CarriesNoScriptNoEventHandlerNoDangerousScheme(string markdown)
    {
        foreach (var html in new[] { Md.ToHtml(markdown), Md.ToInlineHtml(markdown), Md.ToHtml("- " + markdown + "\n- two") })
        {
            // Every tag in the output (text between tags is escaped, so a literal "<script>" there reads as "&lt;script&gt;").
            var tags = AnyTag().Matches(html).Select(m => m.Value).ToList();
            html.Should().NotContain("<!--", markdown).And.NotContain("<![CDATA[", markdown).And.NotContain("<?", markdown);
            foreach (var tag in tags)
            {
                var name = TagName().Match(tag).Groups[1].Value;
                AllowedTags.Should().Contain(name, $"{markdown} produced {tag}");
                tag.Should().NotMatchRegex("(?i)\\son[a-z]+\\s*=", $"{markdown} produced {tag}");
                tag.Should().NotMatchRegex("(?i)(href|src)\\s*=\\s*\"\\s*(javascript|vbscript|data|file):", $"{markdown} produced {tag}");
                tag.Should().NotMatchRegex("(?i)(href|src)\\s*=\\s*\"\\s*&#", $"{markdown} produced {tag} (an entity-encoded scheme)");
            }
            // A raw tag from the input only ever survives as escaped text.
            html.Should().NotContainEquivalentOf("<script").And.NotContainEquivalentOf("<img").And.NotContainEquivalentOf("<svg")
                .And.NotContainEquivalentOf("<iframe").And.NotContainEquivalentOf("<style").And.NotContainEquivalentOf("<details").And.NotContainEquivalentOf("<math");
        }
    }

    [Fact]
    public void OnlyHttpHttpsAndMailtoLinks_KeepTheirAddress_AndOpenWithoutOpener()
    {
        Md.ToHtml("[a](https://example.com/x?y=1&z=2) [b](http://example.com) [c](mailto:x@example.com) [d](/local) [e](#top)")
            .Should().Contain("href=\"https://example.com/x?y=1&amp;z=2\"").And.Contain("href=\"http://example.com\"").And.Contain("href=\"mailto:x@example.com\"")
            .And.Contain("href=\"/local\"").And.Contain("href=\"#top\"");
        Md.ToHtml("[a](https://example.com)").Should().Contain("target=\"_blank\"").And.Contain("rel=\"noopener noreferrer\"");
        Md.ToHtml("[a](ftp://example.com) [b](ssh://x) [c](ms-msdt:/id)").Should().NotContain("ftp://").And.NotContain("ssh://").And.NotContain("ms-msdt:");
    }

    [Fact]
    public void BareAddresses_AreNeverLinked_AndImagesNeverFetched()
    {
        var html = Md.ToHtml("See https://evil.example/malware and evil.example and 203.0.113.5 ![pixel](https://evil.example/p.gif)");
        html.Should().NotContain("<a ").And.NotContain("<img");
        html.Should().Contain("https://evil.example/malware", "the text stays as typed, as inert text");
    }

    [Fact]
    public void LargeFlatMarkdown_RendersInBoundedTime()
    {
        var sw = Stopwatch.StartNew();
        Md.ToHtml(string.Concat(Enumerable.Repeat("- [x](https://e.example/a?b=c) **bold** _it_ `code` text\n", 20_000)));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        sw.Restart();
        Md.ToHtml(new string('`', 200_000) + "a");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        sw.Restart();
        Md.ToHtml(string.Concat(Enumerable.Repeat("[x](", 500)) + "https://e.example" + new string(')', 500));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    private static string Shape(string shape) => shape switch
    {
        "[ x200" => new string('[', 200) + "x",
        "* x200" => new string('*', 200) + "x" + new string('*', 200),
        "_ x200" => new string('_', 200) + "x" + new string('_', 200),
        "` x1000" => new string('`', 1000) + "x",
        _ => string.Concat(Enumerable.Repeat("> ", 200)) + "x",
    };

    [Theory]
    [InlineData("[ x200")]
    [InlineData("> x200")]
    public void StoredMarkdown_WithinTheColumnLimit_AlwaysRenders(string shape)
    {
        var text = Shape(shape);
        text.Length.Should().BeLessThan(2000, "it fits in a bullet");
        FluentActions.Invoking(() => Md.ToHtml(text)).Should().NotThrow();
        FluentActions.Invoking(() => Md.ToInlineHtml(text)).Should().NotThrow();
        FluentActions.Invoking(() => Md.ToPlainText(text)).Should().NotThrow();
    }

    [Theory]
    [InlineData("* x200")]
    [InlineData("_ x200")]
    [InlineData("` x1000")]
    public void RunsOfEmphasisAndCodeMarkers_Render(string shape)
    {
        var text = Shape(shape);
        FluentActions.Invoking(() => Md.ToHtml(text)).Should().NotThrow();
        FluentActions.Invoking(() => Md.ToInlineHtml(text)).Should().NotThrow();
    }
}

public class CsvInjectionTests
{
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\t=1", "'\t=1")]
    [InlineData("\r=1", "\"'\r=1\"")]
    [InlineData("=cmd|' /C calc'!A0", "'=cmd|' /C calc'!A0")]
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")", "\"'=HYPERLINK(\"\"http://evil\"\",\"\"x\"\")\"")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("a\nb", "\"a\nb\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ACell_IsGuardedAgainstFormulasAndQuotedWhenItNeedsToBe(string? text, string expected) =>
        Csv.Cell(text).Should().Be(expected);

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+cmd")]
    [InlineData("-cmd")]
    [InlineData("@cmd")]
    [InlineData("\t=1")]
    [InlineData("\r=1")]
    public void AnImportedNote_IsUnguarded_SoAnExportReadsBackAsItWas(string original)
    {
        var csv = $"date,metric_key,value,note\n2026-10-01,k,1,{Csv.Cell(original)}\n";
        var file = MetricCsv.Read(Encoding.UTF8.GetBytes(csv));
        file.Refusal.Should().BeNull();
        file.Rows.Single().Note.Should().Be(original);
    }
}

public class IndicatorExtractionTimingTests
{
    private static TimeSpan Time(Action act)
    {
        var sw = Stopwatch.StartNew();
        act();
        return sw.Elapsed;
    }

    public static TheoryData<string, string> HostileTexts => new()
    {
        { "email with many dots", "eve@" + string.Concat(Enumerable.Repeat(".a", 60_000)) + " " },
        { "email with no final dot", "a@" + new string('a', 200_000) + "!" },
        { "domain-like with many labels", string.Concat(Enumerable.Repeat("a.", 100_000)) + "a" },
        { "url-like", "hxxp://" + string.Concat(Enumerable.Repeat("x[.]", 50_000)) + "com/" + new string('a', 50_000) },
        { "defang brackets", string.Concat(Enumerable.Repeat("[.]", 100_000)) },
        { "open brackets", string.Concat(Enumerable.Repeat("[", 200_000)) },
        { "command line shaped", "powershell " + string.Concat(Enumerable.Repeat("a.a ", 50_000)) + ".exe" },
        { "exe-ish long token", string.Concat(Enumerable.Repeat("a.", 100_000)) + "exe x" },
        { "hex", new string('f', 200_000) },
        { "cves", string.Concat(Enumerable.Repeat("CVE-2026-", 50_000)) },
        { "ipv6-ish", string.Concat(Enumerable.Repeat(":", 100_000)) + "1.2.3.4" },
        { "down-level logon", "CORP\\" + new string('a', 200_000) },
        { "registry", "HKLM\\" + string.Concat(Enumerable.Repeat("a\\", 100_000)) },
        { "wallet", "bc1" + new string('q', 200_000) },
        { "mixed separators", string.Concat(Enumerable.Repeat("a, b; (c) [d] {e} \"f\" 'g' <h> |i| `j`\n", 10_000)) },
        { "unicode letters", string.Concat(Enumerable.Repeat("é.", 100_000)) + "com" },
    };

    [Theory]
    [MemberData(nameof(HostileTexts))]
    public void Extraction_OnAdversarialText_IsLinearEnough(string name, string text)
    {
        var extract = Time(() => IocExtractor.Extract(text));
        extract.Should().BeLessThan(TimeSpan.FromSeconds(3), $"{name}: Extract");
        var detect = Time(() => IocObservable.DetectType(text));
        detect.Should().BeLessThan(TimeSpan.FromSeconds(3), $"{name}: DetectType on the whole text as one token");
        var parse = Time(() => IocObservable.ParseIndicators(text));
        parse.Should().BeLessThan(TimeSpan.FromSeconds(3), $"{name}: ParseIndicators");
        var refang = Time(() => IocObservable.Refang(text));
        refang.Should().BeLessThan(TimeSpan.FromSeconds(3), $"{name}: Refang");
    }

    [Fact]
    public void Extraction_IsCappedAtTheMaximumNumberOfIndicators()
    {
        var text = string.Join(' ', Enumerable.Range(0, 2000).Select(i => $"203.0.{i / 256}.{i % 256}"));
        IocExtractor.Extract(text).Indicators.Should().HaveCount(IocExtractor.MaxIndicators);
    }

    [Fact]
    public void TitleKeys_AndFeedPlainText_OnHugeInput_StayBounded()
    {
        // The regexes carry 1 s match timeouts, so a pathological input throws; the wall-clock bound here only has to
        // catch a runaway, and tolerates a loaded test run. The inputs are sized so a linear pass takes tens of
        // milliseconds (a busy machine stays well inside the 1 s timeout) while a backtracking one would still blow it.
        var bound = TimeSpan.FromSeconds(10);
        var title = string.Concat(Enumerable.Repeat("<b>word</b> ", 20_000));
        Time(() => TitleMatch.Key(title)).Should().BeLessThan(bound);
        Time(() => FeedParser.PlainText(title, 500)).Should().BeLessThan(bound);
        var unclosed = "<script" + new string(' ', 100_000) + "<style" + new string('<', 20_000);
        Time(() => FeedParser.PlainText(unclosed, 500)).Should().BeLessThan(bound);
        Time(() => EmailComposer.ToText(unclosed)).Should().BeLessThan(bound);
        Time(() => EmailComposer.SingleLine(new string('\n', 100_000) + new string(' ', 100_000))).Should().BeLessThan(bound);
    }
}

public class FeedParserHostileXmlTests
{
    private static Stream Xml(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("<!DOCTYPE rss [<!ENTITY xxe SYSTEM \"file:///C:/Windows/win.ini\">]><rss version=\"2.0\"><channel><item><title>&xxe;</title><link>https://e.example/1</link></item></channel></rss>")]
    [InlineData("<!DOCTYPE rss [<!ENTITY xxe SYSTEM \"http://169.254.169.254/latest/meta-data/\">]><rss version=\"2.0\"><channel><item><title>&xxe;</title><link>https://e.example/1</link></item></channel></rss>")]
    [InlineData("<!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol1 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\"><!ENTITY lol2 \"&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;\"><!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\"><!ENTITY lol4 \"&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;\"><!ENTITY lol5 \"&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;\"><!ENTITY lol6 \"&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;\"><!ENTITY lol7 \"&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;\"><!ENTITY lol8 \"&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;\"><!ENTITY lol9 \"&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;\">]><rss version=\"2.0\"><channel><item><title>&lol9;</title><link>https://e.example/1</link></item></channel></rss>")]
    public void AnEntityADoctypeDeclares_IsRefused_NotExpanded(string xml)
    {
        var sw = Stopwatch.StartNew();
        var act = () => FeedParser.ParseFeed(Xml(xml));
        act.Should().Throw<FormatException>().WithMessage("*well-formed XML*");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    // A DOCTYPE is skipped unread, as some publishers' feeds start with one: an external DTD or parameter entity isn't
    // fetched, and the feed's items are read as usual.
    [Theory]
    [InlineData("<!DOCTYPE rss [<!ENTITY % ext SYSTEM \"http://evil.example/evil.dtd\"> %ext;]><rss version=\"2.0\"><channel><item><title>T</title><link>https://e.example/1</link></item></channel></rss>")]
    [InlineData("<!DOCTYPE rss SYSTEM \"http://evil.example/rss.dtd\"><rss version=\"2.0\"><channel><item><title>T</title><link>https://e.example/1</link></item></channel></rss>")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE rss PUBLIC \"-//Netscape Communications//DTD RSS 0.91//EN\" \"http://my.netscape.com/publish/formats/rss-0.91.dtd\"><rss version=\"2.0\"><channel><item><title>T</title><link>https://e.example/1</link></item></channel></rss>")]
    public void ADoctype_IsSkipped_AndTheFeedRead(string xml)
    {
        var items = FeedParser.ParseFeed(Xml(xml));
        items.Should().ContainSingle().Which.Title.Should().Be("T");
    }

    [Fact]
    public void AnXIncludeOrProcessingInstruction_IsInertText()
    {
        var items = FeedParser.ParseFeed(Xml("<rss version=\"2.0\"><channel><item><title>T<?php echo 1 ?></title>" +
            "<link>https://e.example/1</link><description><![CDATA[<xi:include href=\"file:///etc/passwd\" xmlns:xi=\"http://www.w3.org/2001/XInclude\"/>" +
            "<script>steal()</script>Body]]></description></item></channel></rss>"));
        items.Should().ContainSingle();
        items[0].Title.Should().Be("T");
        items[0].Summary.Should().Be("Body");
    }

    [Fact]
    public void ADeeplyNestedDocument_DoesNotOverflowTheStack()
    {
        var depth = 50_000;
        var xml = "<rss version=\"2.0\"><channel><item><title>deep</title><link>https://e.example/1</link><description>" +
                  string.Concat(Enumerable.Repeat("<a>", depth)) + string.Concat(Enumerable.Repeat("</a>", depth)) + "</description></item></channel></rss>";
        // On a 256 KB stack the recursive read of the description's text overflowed every time (a crash no catch can
        // stop); the parser now refuses the document by depth before any tree is built. Agent 2, round 9 (A5-R-6).
        Exception? outcome = null;
        var thread = new Thread(() =>
        {
            try { FeedParser.ParseFeed(Xml(xml)); }
            catch (Exception ex) { outcome = ex; }
        }, 256 * 1024);
        thread.Start();
        thread.Join();
        outcome.Should().BeOfType<FormatException>().Which.Message.Should().Contain("nested more than");
    }

    [Fact]
    public void AnOversizedDocument_IsRefusedByTheCharacterLimit()
    {
        var sb = new StringBuilder("<rss version=\"2.0\"><channel>");
        var item = "<item><title>t</title><link>https://e.example/1</link><description>" + new string('d', 100_000) + "</description></item>";
        for (var i = 0; i < 210; i++) sb.Append(item);
        sb.Append("</channel></rss>");
        var act = () => FeedParser.ParseFeed(Xml(sb.ToString()));
        act.Should().Throw<FormatException>("21 million characters is over the 20 million limit");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/x")]
    [InlineData("//example.com/x")]
    [InlineData("example.com/x")]
    [InlineData("HTTPS://EXAMPLE.COM/../../x?utm_source=1")]
    [InlineData("https://example.com\\@evil.example/")]
    public void OnlyAbsoluteHttpAddresses_Canonicalize(string url)
    {
        var canonical = UrlCanonicalizer.Canonicalize(url);
        if (canonical is not null) canonical.Should().StartWith("https://").And.NotContain("utm_").And.NotContain("\\");
        // A backslash in the authority isn't an address (iter7's parser refuses it outright, which is the safer answer).
        else url.Should().Match(u => !u.StartsWith("https://", StringComparison.Ordinal) || u.Contains('\\'), "an https address canonicalizes unless its authority is malformed");
    }

    [Fact]
    public void AnAddressOverTheLimit_IsDropped() =>
        UrlCanonicalizer.Canonicalize("https://example.com/" + new string('a', 3000)).Should().BeNull();
}

public class HostileNumbersAndUploadsTests
{
    [Theory]
    [InlineData("1e308", false)]
    [InlineData("1E5", false)]
    [InlineData("NaN", false)]
    [InlineData("Infinity", false)]
    [InlineData("∞", false)]
    [InlineData("0x10", false)]
    [InlineData("99999999999999999999999999999", false)]
    [InlineData("100000000000000", false)]
    [InlineData("-100000000000000", false)]
    [InlineData("99999999999999", true)]
    [InlineData("-99999999999999", true)]
    [InlineData("-5", true)]
    [InlineData("1,000", true)]
    [InlineData("97.5%", true)]
    [InlineData("3.5h", true)]
    [InlineData("(5)", false)]
    [InlineData("5 5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("\u2212" + "5", false)]
    public void AMetricValue_IsANumberWithinBounds_OrNothing(string? text, bool accepted)
    {
        MetricMath.TryParseValue(text, out var value).Should().Be(accepted, text);
        if (accepted) Math.Abs(value).Should().BeLessThan(MetricMath.MaxValue);
    }

    [Fact]
    public void AMetricsCsv_OverTheRowOrByteLimit_OrWithABinaryBody_IsRefusedWhole()
    {
        var rows = new StringBuilder("date,metric_key,value\n");
        for (var i = 0; i < MetricCsv.MaxRows + 2; i++) rows.Append("2026-10-01,k,1\n");
        MetricCsv.Read(Encoding.UTF8.GetBytes(rows.ToString())).Refusal.Should().Contain("more than");
        MetricCsv.Read(new byte[MetricCsv.MaxBytes + 1]).Refusal.Should().Contain("larger than");
        MetricCsv.Read("date,metric_key,value\n2026-10-01,k,\0"u8.ToArray()).Refusal.Should().Contain("isn't text");
        MetricCsv.Read("date,metric_key,value\n\"unterminated,k,1\n"u8.ToArray()).Refusal.Should().Contain("isn't closed");
        MetricCsv.Read([0xC3, 0x28, (byte)'\n']).Refusal.Should().NotBeNull("invalid UTF-8 falls back to Latin-1, then has no header");
    }

    [Fact]
    public void AMetricsCsv_WithAHugeCell_IsReadWithoutQuadraticCost()
    {
        var csv = "date,metric_key,value,note\n2026-10-01,k,1," + new string('n', 900_000) + "\n";
        var sw = Stopwatch.StartNew();
        var file = MetricCsv.Read(Encoding.UTF8.GetBytes(csv));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        file.Rows.Single().Note!.Length.Should().Be(900_000);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<?xml version=\"1.0\"?><svg/>")]
    [InlineData("<html><script>alert(1)</script></html>")]
    [InlineData("%PDF-1.7")]
    [InlineData("MZ\u0090\u0000")]
    [InlineData("PK\u0003\u0004")]
    [InlineData("GIF89")]
    [InlineData("GIF88a")]
    [InlineData("\u0089PNG\r\n\u001a")]
    [InlineData("RIFF....WAVE")]
    [InlineData("RIFF....WEBX")]
    [InlineData("BM")]
    [InlineData("")]
    public void OnlyRealRasterHeaders_AreImages(string header) =>
        ImageSniffer.RasterContentType(Encoding.Latin1.GetBytes(header)).Should().BeNull(header);

    [Fact]
    public void Polyglots_AreTheirRasterType_AndHostileDimensionsDoNotCrash()
    {
        ImageSniffer.RasterContentType("GIF89a/*<script>alert(1)</script>*/"u8).Should().Be("image/gif");
        var jpegSvg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.Concat("<svg onload=alert(1)>"u8.ToArray()).ToArray();
        ImageSniffer.RasterContentType(jpegSvg).Should().Be("image/jpeg");
        // A PNG claiming 2^32-1 pixels a side, a GIF with a zero size, a JPEG whose segments run off the end.
        var png = new byte[32];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        for (var i = 16; i < 24; i++) png[i] = 0xFF;
        var act = () => ImageSniffer.Dimensions(png);
        act.Should().NotThrow();
        act().Should().NotBeNull();
        ImageSniffer.Dimensions("GIF89a\0\0\0\0"u8).Should().Be((0, 0));
        var jpeg = new byte[64];
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0xFF }.CopyTo(jpeg, 0);   // a segment longer than the file
        FluentActions.Invoking(() => ImageSniffer.Dimensions(jpeg)).Should().NotThrow();
        var looping = new byte[64];
        looping[0] = 0xFF; looping[1] = 0xD8;
        for (var i = 2; i < 64; i += 2) { looping[i] = 0xFF; looping[i + 1] = 0x01; }   // marker after marker, no length
        var sw = Stopwatch.StartNew();
        FluentActions.Invoking(() => ImageSniffer.Dimensions(looping)).Should().NotThrow();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        var zeroLength = new byte[64];
        zeroLength[0] = 0xFF; zeroLength[1] = 0xD8;
        for (var i = 2; i + 3 < 64; i += 4) { zeroLength[i] = 0xFF; zeroLength[i + 1] = 0xE1; zeroLength[i + 2] = 0; zeroLength[i + 3] = 1; }   // length 1 (< 2)
        FluentActions.Invoking(() => ImageSniffer.Dimensions(zeroLength)).Should().NotThrow();
        FluentActions.Invoking(() => ImageSniffer.Dimensions("RIFF\0\0\0\0WEBPVP8X"u8)).Should().NotThrow();
    }
}

public class EmailHeaderInjectionTests
{
    [Theory]
    [InlineData("Brief\r\nBcc: eve@evil.example", "Brief Bcc: eve@evil.example")]
    [InlineData("Brief\nBcc: x", "Brief Bcc: x")]
    [InlineData("Brief\rBcc: x", "Brief Bcc: x")]
    [InlineData("Brief\u2028Bcc: x", "Brief Bcc: x")]
    [InlineData("Brief\u0000\u0001\u001f\u007fX", "Brief X")]
    [InlineData("  Brief \t\n ", "Brief")]
    [InlineData("\r\n\r\n", "")]
    public void ASubject_IsOneLine(string subject, string expected)
    {
        var folded = EmailComposer.SingleLine(subject);
        folded.Should().Be(expected);
        folded.Should().NotContainAny("\r", "\n");
        folded.Any(char.IsControl).Should().BeFalse();
    }

    [Fact]
    public void BodyText_IsAlwaysEscaped()
    {
        EmailComposer.Esc("<script>alert(1)</script>\"'&").Should().Be("&lt;script&gt;alert(1)&lt;/script&gt;&quot;&#39;&amp;");
        EmailComposer.Shell("<t>", "<s>", "body", "<f>").Should().NotContain("<t>").And.NotContain("<s>").And.NotContain("<f>");
    }
}

// =====================================================================================================================
// Phase 2: challenging the merged fixes (A4-2 embedded IPv4, A1-1 protocol-relative links, A4-14 release builds)
// =====================================================================================================================

public class AddressPolicyChallengeTests
{
    private static string? Refusal(string address, bool allowPrivate = false) =>
        Tide.Infrastructure.Notifications.AddressPolicy.Refusal(System.Net.IPAddress.Parse(address), allowPrivate);

    [Theory]
    // NAT64 (64:ff9b::/96) carrying the metadata address, loopback and a private address
    [InlineData("64:ff9b::a9fe:a9fe", "link-local")]
    [InlineData("64:ff9b::7f00:1", "loopback")]
    [InlineData("64:ff9b::c0a8:101", "private")]
    [InlineData("64:ff9b::", "reserved")]
    // 6to4 (2002:V4V4::/48), the IPv4 in bytes 2..5, never anywhere else
    [InlineData("2002:a9fe:a9fe::", "link-local")]
    [InlineData("2002:7f00:1::", "loopback")]
    [InlineData("2002:0a00:1::", "private")]
    [InlineData("2002:a9fe:a9fe:a9fe:a9fe:a9fe:a9fe:a9fe", "link-local")]
    [InlineData("2002::", "reserved")]
    // IPv4-compatible (::/96) and IPv4-mapped (::ffff:0:0/96)
    [InlineData("::a9fe:a9fe", "link-local")]
    [InlineData("::7f00:1", "loopback")]
    [InlineData("::ffff:169.254.169.254", "link-local")]
    [InlineData("::ffff:127.0.0.1", "loopback")]
    [InlineData("::ffff:10.0.0.1", "private")]
    [InlineData("::1", "loopback")]
    [InlineData("::", "reserved")]
    [InlineData("fe80::5efe:a9fe:a9fe", "link-local")]   // ISATAP on the link-local prefix
    [InlineData("ff02::1", "reserved")]
    [InlineData("fc00::1", "private")]
    public void AnIPv6Form_ThatReachesARefusedIPv4_IsRefused(string address, string why) =>
        Refusal(address).Should().NotBeNull(address).And.Contain(why, address);

    [Theory]
    [InlineData("64:ff9b::808:808")]                              // NAT64 to 8.8.8.8
    [InlineData("2002:808:808::1")]                               // 6to4 of 8.8.8.8
    [InlineData("2002:c633:6401::")]                              // 6to4 of 198.51.100.1
    [InlineData("::808:808")]                                     // IPv4-compatible 8.8.8.8
    [InlineData("2001:db8::1")]                                   // a plain global address
    [InlineData("2003:a9fe:a9fe::1")]                             // 2003::/16 is not 6to4, whatever its bytes look like
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]          // Teredo: the client address is obfuscated, the server public
    [InlineData("2001:db8:a9fe:a9fe::1")]                         // 169.254 in the middle of an ordinary address isn't embedded
    [InlineData("64:ff9b::1:a9fe:a9fe")]                          // 64:ff9b::/96 only when bits 32..95 are zero
    [InlineData("2a00:1450:4009:81f::200e")]
    public void AGlobalIPv6Address_IsNotMisreadAsEmbeddingAnIPv4(string address) =>
        Refusal(address).Should().BeNull(address);

    [Theory]
    [InlineData("64:ff9b::c0a8:101")]
    [InlineData("2002:c0a8:101::")]
    [InlineData("::ffff:192.168.1.1")]
    public void AnEmbeddedPrivateIPv4_IsAllowedOnlyWhenPrivateNetworksAre(string address)
    {
        Refusal(address, allowPrivate: false).Should().Contain("private");
        Refusal(address, allowPrivate: true).Should().BeNull();
    }

    [Theory]
    [InlineData("64:ff9b:1::a9fe:a9fe")]
    [InlineData("64:ff9b:1:ffff:ffff:ffff:a9fe:a9fe")]
    [InlineData("::ffff:0:a9fe:a9fe")]
    [InlineData("::ffff:0:7f00:1")]
    [InlineData("2001:db8::5efe:a9fe:a9fe")]
    [InlineData("2001:db8::200:5efe:7f00:1")]
    public void OtherTransitionForms_ThatCarryARefusedIPv4_AreRefusedToo(string address) =>
        Refusal(address).Should().NotBeNull(address);
}

public partial class ProtocolRelativeLinkChallengeTests
{
    [System.Text.RegularExpressions.GeneratedRegex("href=\"([^\"]*)\"")]
    private static partial System.Text.RegularExpressions.Regex Href();

    private static readonly MarkdownService Md = new();

    private static string Href(string url)
    {
        var html = Md.ToHtml($"[x]({url})");
        var m = Href().Match(html);
        return m.Success ? m.Groups[1].Value : "(no link)";
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("//evil.example/path")]
    [InlineData("///evil.example")]
    [InlineData("  //evil.example")]
    [InlineData("//evil.example:443/")]
    [InlineData("//@evil.example")]
    public void AProtocolRelativeLink_IsNeutralised(string url) => Href(url).Should().Be("#", url);

    [Theory]
    [InlineData("/local/path")]
    [InlineData("/")]
    [InlineData("#top")]
    [InlineData("./relative")]
    [InlineData("https://example.com/x")]
    public void ALocalOrAllowedLink_KeepsItsAddress(string url) => Href(url).Should().NotBe("#", url);

    [Theory]
    [InlineData("/\\evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("/\\/evil.example")]
    [InlineData("\\/evil.example")]
    public void ABackslashSpellingOfAProtocolRelativeLink_IsNeutralisedToo(string url) => Href(url).Should().Be("#", url);
}

public class ReleaseBuildChallengeTests
{
    [Fact]
    public void OnlyTheReleaseWorkflow_TurnsAnUnversionedBuildIntoARelease()
    {
        Tide.Application.Operations.AppVersionInfo.ReleaseOf("1.0.0+abcdef1", releaseBuild: false).Should().BeNull();
        Tide.Application.Operations.AppVersionInfo.ReleaseOf("1.0.0+abcdef1", releaseBuild: true).Should().NotBeNull();
        Tide.Application.Operations.AppVersionInfo.ReleaseOf("1.0.0", releaseBuild: true)!.ToString().Should().Be("1.0.0");
        Tide.Application.Operations.AppVersionInfo.ReleaseOf("0.1.0-alpha.2+sha", releaseBuild: false).Should().NotBeNull("a stamped pre-release is a release whoever built it");
        Tide.Application.Operations.AppVersionInfo.ReleaseOf("not-a-version", releaseBuild: true).Should().BeNull("the flag doesn't invent a version");
        Tide.Application.Operations.AppVersionInfo.ReleaseOf("", releaseBuild: true).Should().BeNull();
        Tide.Application.Operations.AppVersionInfo.DisplayOf("1.0.0+abcdef1", releaseBuild: false).Should().StartWith("development build");
        Tide.Application.Operations.AppVersionInfo.DisplayOf("1.0.0+abcdef1", releaseBuild: true).Should().StartWith("1.0.0");
        Tide.Application.Operations.AppVersionInfo.IsReleaseBuild.Should().BeFalse("the tests are a local build");
        Tide.Application.Operations.AppVersionInfo.Current.Should().BeNull();
    }
}

// =====================================================================================================================
// Phase 3: IOC canonicalisation (A2 round 4) must touch only what it means to
// =====================================================================================================================

public class IocCanonicalisationChallengeTests
{
    [Theory]
    [InlineData(IndicatorType.Domain, "EVIL.Example.", "evil.example")]
    [InlineData(IndicatorType.Domain, "Evil[.]Example", "evil.example")]
    [InlineData(IndicatorType.IpAddress, "010.000.000.001", "10.0.0.1")]
    [InlineData(IndicatorType.IpAddress, "203.0.113.005", "203.0.113.5")]
    [InlineData(IndicatorType.IpAddress, "::ffff:010.0.0.1", "10.0.0.1")]
    [InlineData(IndicatorType.IpAddress, "2001:0DB8:0000:0000:0000:0000:0000:0001", "2001:db8::1")]
    public void WhatItMeansToCanonicalise_IsCanonicalised(IndicatorType type, string typed, string canonical) =>
        IocObservable.Normalize(type, typed).Should().Be(canonical);

    [Theory]
    // A range isn't an address: left as written, padded or not.
    [InlineData(IndicatorType.IpAddress, "10.0.0.0/8")]
    [InlineData(IndicatorType.IpAddress, "010.0.0.0/8")]
    [InlineData(IndicatorType.IpAddress, "203.0.113.0-203.0.113.255")]
    [InlineData(IndicatorType.IpAddress, "2001:db8::/32")]
    // Hex is case-preserved (a hash isn't refangable).
    [InlineData(IndicatorType.FileHash, "ABCDEF0123456789ABCDEF0123456789")]
    [InlineData(IndicatorType.FileHash, "d41d8cd98f00b204e9800998ecf8427e")]
    // A URL keeps its path and query exactly (paths are case-sensitive), and its host as typed.
    [InlineData(IndicatorType.Url, "https://Evil.Example/Path/To/File.PHP?Q=Value&X=1")]
    [InlineData(IndicatorType.Url, "https://evil.example/010.000.000.001/index.html")]
    // An email keeps its local part exactly.
    [InlineData(IndicatorType.EmailAddress, "John.Doe@Evil.Example")]
    [InlineData(IndicatorType.Account, "CORP\\JDoe")]
    [InlineData(IndicatorType.FileName, "Invoice.DOCM")]
    [InlineData(IndicatorType.RegistryKey, "HKLM\\Software\\Evil")]
    [InlineData(IndicatorType.CommandLine, "powershell -enc AAAA")]
    [InlineData(IndicatorType.Other, "Mixed Case Thing 010.0.0.1")]
    public void WhatItMustNotTouch_IsLeftAsTyped(IndicatorType type, string typed) =>
        IocObservable.Normalize(type, typed).Should().Be(typed);

    [Fact]
    public void ParseIndicators_DedupesTheOldAndTheNewSpelling_ToOne()
    {
        var parsed = IocObservable.ParseIndicators("EVIL.Example\nevil.example\nEvil[.]Example\n010.0.0.1\n10.0.0.1");
        parsed.Where(p => p.Type == IndicatorType.Domain).Should().ContainSingle().Which.Value.Should().Be("evil.example");
        parsed.Where(p => p.Type == IndicatorType.IpAddress).Should().ContainSingle().Which.Value.Should().Be("10.0.0.1");
    }

    [Fact]
    public void Extraction_FindsTheCanonicalForm_OnceEach()
    {
        var found = IocExtractor.Extract("Seen at EVIL.example and evil[.]EXAMPLE from 010.000.000.001 and 10.0.0.1.");
        found.Indicators.Where(i => i.Type == IndicatorType.Domain).Select(i => i.Value).Should().Equal("evil.example");
        found.Indicators.Where(i => i.Type == IndicatorType.IpAddress).Select(i => i.Value).Should().Equal("10.0.0.1");
    }

    [Fact]
    public void Defang_OfTheCanonicalForm_RoundTrips()
    {
        foreach (var v in new[] { "evil.example", "10.0.0.1", "2001:db8::1", "https://evil.example/Path", "john.doe@evil.example" })
        {
            var type = IocObservable.DetectType(v);
            IocObservable.Normalize(type, IocObservable.Defang(type, v)).Should().Be(v, v);
        }
    }
}

// =====================================================================================================================
// Phase 4: A4 round 5 (bundle warnings, recipient cap) and round 7 (install answers)
// =====================================================================================================================

public class BundleRiskChallengeTests
{
    private static string? Risk(string group, AppRole role) => Tide.Application.Operations.ConfigBundleService.RiskOf(group, role);

    [Theory]
    [InlineData("Everyone")]
    [InlineData("EVERYONE")]
    [InlineData(" everyone ")]
    [InlineData("Authenticated Users")]
    [InlineData("NT AUTHORITY\\Authenticated Users")]
    [InlineData("nt authority\\authenticated users")]
    [InlineData("Domain Users")]
    [InlineData("CORP\\Domain Users")]
    [InlineData("TRUST\\domain users")]
    [InlineData("corp.example.com\\Domain Users\t")]
    [InlineData("Users")]
    [InlineData("BUILTIN\\Users")]
    [InlineData("builtin\\users")]
    [InlineData("S-1-1-0")]
    [InlineData("s-1-1-0")]
    [InlineData("S-1-5-11")]
    [InlineData("S-1-5-32-545")]
    [InlineData("s-1-5-32-545")]
    [InlineData("S-1-5-21-3623811015-3361044348-30300820-513")]
    [InlineData("s-1-5-21-3623811015-3361044348-30300820-513")]
    public void AGroupEveryoneIsIn_HoweverSpelled_IsFlagged_ForAnyRole(string group)
    {
        foreach (var role in new[] { AppRole.Reader, AppRole.Analyst, AppRole.ActionOwner, AppRole.Admin })
            Risk(group, role).Should().NotBeNull($"{group} → {role}").And.Contain("everyone", $"{group} → {role}");
    }

    [Theory]
    [InlineData("SOC-Analysts", AppRole.Analyst)]
    [InlineData("CORP\\CTI-Readers", AppRole.Reader)]
    [InlineData("Domain Admins", AppRole.Reader)]
    [InlineData("Domain Users Ltd", AppRole.Reader)]
    [InlineData("S-1-5-21-3623811015-3361044348-30300820-1105", AppRole.Reader)]
    [InlineData("S-1-5-21-3623811015-3361044348-30300820-5130", AppRole.Reader)]
    [InlineData("Users-of-TIDE", AppRole.Reader)]
    public void AnOrdinaryGroup_WithANonAdminRole_IsNotFlagged(string group, AppRole role) => Risk(group, role).Should().BeNull();

    [Theory]
    [InlineData("SOC-Admins")]
    [InlineData("Domain Admins")]
    [InlineData("S-1-5-21-3623811015-3361044348-30300820-512")]
    public void AnyGroup_GivenAdmin_IsFlagged(string group) => Risk(group, AppRole.Admin).Should().Contain("administer");

    [Theory] // A5-R-8, fixed by agent 4 round 8
    [InlineData("NT AUTHORITY\\NETWORK")]
    [InlineData("S-1-5-2")]
    [InlineData("NT AUTHORITY\\INTERACTIVE")]
    [InlineData("S-1-5-4")]
    [InlineData("NT AUTHORITY\\This Organization")]
    [InlineData("S-1-5-15")]
    [InlineData("S-1-18-1")]
    [InlineData("BUILTIN\\Pre-Windows 2000 Compatible Access")]
    [InlineData("S-1-5-32-554")]
    public void TheOtherEveryoneIdentities_OfAWindowsToken_AreFlaggedToo(string group) =>
        Risk(group, AppRole.Analyst).Should().NotBeNull(group).And.Contain("everyone", group);
}

public class InstallAnswersRoleCaseTests
{
    [Fact]
    public void Write_ListsAGroupUnderItsRole_AndEscapesEveryQuote()
    {
        var facts = new Tide.Application.Operations.InstallFacts
        {
            Groups = new Dictionary<AppRole, IReadOnlyList<string>> { [AppRole.Admin] = ["SOC-Admins", "CORP\\Robin’s team"], [AppRole.Reader] = ["All 'Staff'"] },
        };
        var text = Tide.Application.Operations.InstallAnswers.Write(facts);
        text.Should().Contain("SOC-Admins").And.Contain("Robin’’s team").And.Contain("All ''Staff''");
        text.Should().NotContain("’s team'", "a typographic quote would end the string");
    }
}
