using AwesomeAssertions;
using Tide.Application.Briefs;
using Tide.Application.Content;
using Tide.Application.Distribution;
using Tide.Application.Files;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>What leaves TIDE by email and by SharePoint paste never carries anything above the edition's ceiling.</summary>
public class BriefRendererTests
{
    private readonly BriefRenderer _renderer = new(new MarkdownService());
    private static readonly Guid OpenShot = Guid.NewGuid(), SecretShot = Guid.NewGuid();

    // One open article with a restricted bullet and screenshot; one AMBER article; an article left at CLEAR with an AMBER
    // article TLP would be covered by the edition tests.
    private static Brief Brief()
    {
        var open = new Article
        {
            Title = "Open article", Tlp = TlpLevel.Clear, Bluf = "Everyone may read this.", Classification = Classification.Threat, Priority = Priority.P2,
            WhyItMatters = "We run it.",
        };
        open.Bullets.Add(new ArticleBullet { Text = "public **bullet**", Tlp = TlpLevel.Clear, SortOrder = 0 });
        open.Bullets.Add(new ArticleBullet { Text = "ISAC-only bullet", Tlp = TlpLevel.Amber, SortOrder = 1 });
        open.Screenshots.Add(new ArticleScreenshot { FileId = OpenShot, Caption = "open caption", Tlp = TlpLevel.Clear, SortOrder = 0 });
        open.Screenshots.Add(new ArticleScreenshot { FileId = SecretShot, Caption = "secret caption", Tlp = TlpLevel.Red, SortOrder = 1 });
        var amber = new Article { Title = "Partner-only article", Tlp = TlpLevel.Amber, Bluf = "Shared by a partner.", RecommendedActions = "Call them." };
        amber.Bullets.Add(new ArticleBullet { Text = "partner detail", Tlp = TlpLevel.Amber });
        var brief = new Brief { BriefDate = new DateOnly(2026, 10, 26), AnalystName = "Analyst" };
        brief.Items.Add(new BriefItem { Article = open, ArticleId = open.Id, SortOrder = 0 });
        brief.Items.Add(new BriefItem { Article = amber, ArticleId = amber.Id, SortOrder = 1 });
        return brief;
    }

    private static MetricDay Metrics()
    {
        var group = new MetricGroup { Name = "Alert queue" };
        var metric = new MetricDefinition { Name = "Older than 24 hours", Direction = MetricDirection.LowerIsBetter, WarnThreshold = 1 };
        var tile = new MetricTile(metric, 3m, null, new MetricComparison(new DateOnly(2026, 10, 23), 5m, -2m, MetricTrend.Better), MetricLevel.Warn, [5m, 3m]);
        return new MetricDay(new DateOnly(2026, 10, 26), [new MetricGroupDay(group, [tile])]);
    }

    [Fact]
    public void Email_NamesTheDayComparedWithByTheOrgsWorkingDays_SoAHolidayBetweenKeepsTheWeekday()
    {
        var tue = new DateOnly(2026, 10, 27);
        var group = new MetricGroup { Name = "Alert queue" };
        var tile = new MetricTile(new MetricDefinition { Name = "Open" }, 3m, null, new MetricComparison(new DateOnly(2026, 10, 23), 5m, -2m, MetricTrend.Better), MetricLevel.Normal, [5m, 3m]);
        var holidays = new WorkingDays(new Dictionary<DateOnly, string> { [new DateOnly(2026, 10, 26)] = "Founders' Day" });
        var day = new MetricDay(tue, [new MetricGroupDay(group, [tile])]) { Days = holidays };

        var team = _renderer.Render(BriefEditions.Build(Brief(), Edition.Team, TlpLevel.AmberStrict), new("CTI", day, null, img => "cid:" + img.ContentId));

        team.EmailHtml.Should().Contain("vs Fri").And.NotContain("vs Oct 23");
    }

    private BriefRenderContext Context() => new("CTI", Metrics(), "https://tide.example.org", img => "cid:" + img.ContentId);

    private static readonly string[] Restricted = ["ISAC-only bullet", "secret caption", "Partner-only article", "partner detail", "Call them.", "Shared by a partner."];

    [Fact]
    public void Email_Public_CarriesNothingAboveTheCeiling()
    {
        var edition = BriefEditions.Build(Brief(), Edition.Public, TlpLevel.Green);

        var r = _renderer.Render(edition, Context());

        foreach (var text in Restricted)
        {
            r.EmailHtml.Should().NotContain(text);
            r.Text.Should().NotContain(text);
            r.WebHtml.Should().NotContain(text);
        }
        r.EmailHtml.Should().Contain("Open article").And.Contain("open caption").And.Contain("<strong>bullet</strong>");
        r.Images.Select(i => i.Shot.FileId).Should().Equal(OpenShot);
        r.EmailHtml.Should().Contain("cid:shot1@tide").And.NotContain("shot2@tide");
        r.EmailHtml.Should().Contain("1 restricted item was briefed to the security team");
    }

    [Fact]
    public void Email_Public_NeverShowsMetrics_TeamShowsThemFirst()
    {
        var pub = _renderer.Render(BriefEditions.Build(Brief(), Edition.Public, TlpLevel.Green), Context());
        pub.EmailHtml.Should().NotContain("The day's numbers").And.NotContain("Older than 24 hours");

        var team = _renderer.Render(BriefEditions.Build(Brief(), Edition.Team, TlpLevel.AmberStrict), Context());
        team.EmailHtml.Should().Contain("The day's numbers").And.Contain("Older than 24 hours").And.Contain("▼");
        team.EmailHtml.IndexOf("The day's numbers", StringComparison.Ordinal).Should().BeLessThan(team.EmailHtml.IndexOf("Open article", StringComparison.Ordinal));
        team.EmailHtml.Should().Contain("Partner-only article").And.Contain("ISAC-only bullet").And.NotContain("secret caption", "RED is above the team ceiling");
    }

    [Fact]
    public void Email_AnOpeningNoteAboveTheCeiling_IsNotInTheEmailTextOrSharePointCopy()
    {
        var brief = Brief();
        brief.Intro = "Partner briefing at 2pm.";
        brief.IntroTlp = TlpLevel.Amber;

        var pub = BriefEditions.Build(brief, Edition.Public, TlpLevel.Green);
        var rendered = _renderer.Render(pub, Context());
        rendered.EmailHtml.Should().NotContain("Partner briefing");
        rendered.Text.Should().NotContain("Partner briefing");
        rendered.WebHtml.Should().NotContain("Partner briefing");

        _renderer.Render(BriefEditions.Build(brief, Edition.Team, TlpLevel.AmberStrict), Context()).EmailHtml.Should().Contain("Partner briefing");
    }

    [Fact]
    public void AnEditionLeftWithNoArticles_SaysSo_InTheEmailAndTheSharePointCopy()
    {
        // Every article is above the company-wide ceiling: that edition has none, and says so rather than going out blank.
        var brief = Brief();
        brief.Items.RemoveAll(i => i.Article!.Tlp == TlpLevel.Clear);
        var edition = BriefEditions.Build(brief, Edition.Public, TlpLevel.Green);
        edition.Articles.Should().BeEmpty();

        var email = _renderer.Render(edition, Context()).EmailHtml;
        var page = _renderer.Semantic(edition, Context() with { ImageSrc = img => "files/" + img.Shot.FileId }, null, img => img.FileName);

        email.Should().Contain("Nothing in this edition today.").And.NotContain("Partner-only article");
        page.Should().Contain("<p>Nothing in this edition today.</p>").And.NotContain("Partner-only article");
    }

    [Fact]
    public void SharePoint_Public_IsPlainSemanticHtml_WithNothingRestricted()
    {
        var edition = BriefEditions.Build(Brief(), Edition.Public, TlpLevel.Green);

        var html = _renderer.Semantic(edition, Context() with { ImageSrc = img => "files/" + img.Shot.FileId }, null, img => img.FileName);

        foreach (var text in Restricted) html.Should().NotContain(text);
        html.Should().NotContain("style=").And.NotContain("<table").And.NotContain("target=");
        html.Should().Contain($"<h2 data-article=\"{edition.Articles[0].Article.Id}\">1. Open article</h2>").And.Contain("<figure><img src=\"files/" + OpenShot + "\"")
            .And.Contain("Figure 1 (01-open-caption.png)");
    }

    [Fact]
    public void PublishedContent_ReadsTheHeadlinesAndWords_OfTheFrozenCompanyWideEdition_AndNothingLeftOut()
    {
        var html = _renderer.Semantic(BriefEditions.Build(Brief(), Edition.Public, TlpLevel.Green), Context());

        var content = PublishedContent.Parse(html);

        content.Headlines.Should().Equal("Open article");
        content.Matches("everyone READ").Should().BeTrue("every word, in any case");
        content.Matches("\"open article\"").Should().BeTrue();
        content.Matches("everyone partner").Should().BeFalse("the AMBER article was left out of this edition");
        foreach (var text in Restricted) content.Matches(text).Should().BeFalse();
        content.Matches("  ").Should().BeTrue("no words matches every brief");
    }

    [Fact]
    public void PublishedContent_DecodesEntities_DropsTags_AndKeepsAnUnnumberedHeading()
    {
        var content = PublishedContent.Parse("<h1>CTI</h1>\n<h2>2. Fish &amp; <em>chips</em></h2><p>a<br>b</p><h2>Plain</h2><h2>2024. Year</h2><h2></h2>");

        content.Headlines.Should().Equal("Fish & chips", "Plain", "Year");
        content.Text.Should().Be("CTI 2. Fish & chips a b Plain 2024. Year");
        PublishedContent.Parse(null).Should().BeSameAs(PublishedContent.Empty);
        PublishedContent.Parse("<h2>Unclosed").Headlines.Should().BeEmpty();
    }

    [Fact]
    public void PublishedContentCache_ParsesASendOnce()
    {
        var cache = new PublishedContentCache();
        var id = Guid.NewGuid();
        cache.TryGet(id, out _).Should().BeFalse();

        var first = cache.Add(id, "<h2>1. First</h2>");
        cache.Add(id, "<h2>1. Changed</h2>").Should().BeSameAs(first, "a send is frozen, so it's parsed once");

        cache.TryGet(id, out var got).Should().BeTrue();
        got.Headlines.Should().Equal("First");
    }

    [Fact]
    public void Email_IsOutlookShaped_AndMarkedAtTopAndBottom()
    {
        var edition = BriefEditions.Build(Brief(), Edition.Public, TlpLevel.Green);
        var sizes = new Dictionary<Guid, (int, int)> { [OpenShot] = (1200, 600) };

        var html = _renderer.Email(edition, Context() with { Sizes = sizes });

        html.Should().NotContain("<style").And.NotContain("var(--").And.NotContain("<script");
        html.Should().Contain($"width=\"{BriefRenderer.ImageMaxWidth}\" height=\"296\"", "a wide image is drawn at the email's width, in proportion");
        html.Split("TLP:CLEAR").Length.Should().BeGreaterThanOrEqualTo(3, "TLP:CLEAR is shown at least twice");
        BriefRenderer.Subject(edition, "CTI").Should().Be("CTI daily brief, Mon Oct 26 [TLP:CLEAR]");
    }

    [Fact]
    public void Email_EscapesWhatAnalystsTyped()
    {
        var brief = Brief();
        brief.Items[0].Article!.Title = "<img src=x onerror=alert(1)>";
        brief.Items[0].Article!.Bullets[0].Text = "<script>alert(1)</script>";

        var html = _renderer.Email(BriefEditions.Build(brief, Edition.Team, TlpLevel.AmberStrict), Context());

        html.Should().NotContain("<img src=x").And.NotContain("<script>alert");
    }

    [Fact]
    public void Dimensions_ReadsPngAndGif()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        ImageSniffer.Dimensions(png).Should().Be((1, 1));
        byte[] gif = [.. "GIF89a"u8, 0x20, 0x03, 0x58, 0x02, 0, 0];
        ImageSniffer.Dimensions(gif).Should().Be((800, 600));
        ImageSniffer.Dimensions("not an image"u8).Should().BeNull();
    }

    [Fact]
    public void Composer_MakesOneLineSubjects_AndReadableText()
    {
        EmailComposer.SingleLine("a\r\nb\tc").Should().Be("a b c");
        EmailComposer.SingleLine("a\u2028b \u2029 c").Should().Be("a b c", "Unicode line and paragraph separators break a header line too");
        EmailComposer.ToText("<h2>Title</h2><ul><li>one</li><li>two &amp; three</li></ul>").Should().Be("Title\n\n- one\n- two & three");
        EmailComposer.ToText("<html><head><title>Heading</title></head><body><ul><li>patching.</li></ul><div>Why it matters</div></body></html>")
            .Should().Be("- patching.\nWhy it matters", "the head's title isn't read, and a list ends its line");
    }

    [Fact]
    public void TheTeamEmail_ListsOverdueActionsAfterTheNumbers_TheCompanyWideOneNever()
    {
        var row = new Tide.Application.Actions.ActionRow(Guid.NewGuid(), 13, "Patch self-managed GitLab", ActionKind.PatchRemediate, Priority.P1,
            Guid.NewGuid(), "Vulnerability Management", "u1", "Robin", new DateOnly(2026, 10, 16), ActionStatus.Open, ActionOutcome.None,
            null, null, null, null, 0, DateTimeOffset.UtcNow);
        var iam = row with { Number = 19, Title = "Reset CI keys", Priority = Priority.P2, TeamName = "IAM", AssigneeId = null, AssigneeName = null };
        var open = new Tide.Application.Actions.OpenActionsReview(
            [new(row, 10, false), new(iam, 6, true)], 28);
        var context = new BriefRenderContext("CTI", Metrics(), "https://tide.example.org", img => "cid:" + img.ContentId, null, open);

        var team = _renderer.Render(BriefEditions.Build(Brief(), Edition.Team, TlpLevel.AmberStrict), context);
        team.EmailHtml.Should().Contain("Still open from earlier briefs").And.Contain("https://tide.example.org/actions/13")
            .And.Contain("10 days late").And.Contain("no one: the team has no members").And.Contain("28 overdue in all");
        team.EmailHtml.IndexOf("The day's numbers", StringComparison.Ordinal).Should()
            .BeLessThan(team.EmailHtml.IndexOf("Still open from earlier briefs", StringComparison.Ordinal), "the numbers come first");
        team.Text.Should().Contain("P1").And.Contain("Patch self-managed GitLab");

        var pub = _renderer.Render(BriefEditions.Build(Brief(), Edition.Public, TlpLevel.Green), context);
        pub.EmailHtml.Should().NotContain("Still open").And.NotContain("Patch self-managed GitLab");
        pub.Text.Should().NotContain("Reset CI keys");
    }

    [Fact]
    public void TheTeamEmail_NamesAnExternalTeamsAction_ByItsTicket_InWordsOnly()
    {
        var row = new Tide.Application.Actions.ActionRow(Guid.NewGuid(), 31, "Patch the hosted edge", ActionKind.PatchRemediate, Priority.P2,
            Guid.NewGuid(), "Hosting Provider", null, null, new DateOnly(2026, 10, 30), ActionStatus.Tracking, ActionOutcome.None,
            null, null, null, null, 0, DateTimeOffset.UtcNow, TeamExternal: true)
        { TicketRef = "INC-4411", TicketUrl = "https://itsm.example.org/INC-4411" };
        var none = row with { Number = 32, Title = "Rotate the hosted keys", TicketRef = null, TicketUrl = null };
        var inside = row with { Number = 33, Title = "Sweep the logs", TeamName = "Network", TeamExternal = false };
        var open = new Tide.Application.Actions.OpenActionsReview([new(row, 3, false), new(none, 3, false), new(inside, 3, false)], 3);
        var context = new BriefRenderContext("CTI", Metrics(), "https://tide.example.org", img => "cid:" + img.ContentId, null, open);

        var html = _renderer.Render(BriefEditions.Build(Brief(), Edition.Team, TlpLevel.AmberStrict), context).EmailHtml;
        html.Should().Contain("Hosting Provider &middot; INC-4411</span>").And.NotContain("itsm.example.org", "an email names the ticket, it doesn't link it")
            .And.Contain("Hosting Provider &middot; no ticket yet</span>").And.Contain("Network &middot; unassigned</span>");
    }

    [Fact]
    public void TheTeamEmail_MarksAnEscalatedAction_EvenOneNotYetDue()
    {
        var row = new Tide.Application.Actions.ActionRow(Guid.NewGuid(), 21, "Patch the VPN appliances", ActionKind.PatchRemediate, Priority.P2,
            Guid.NewGuid(), "Network Engineering", null, null, new DateOnly(2026, 10, 30), ActionStatus.Tracking, ActionOutcome.None,
            null, null, null, null, 0, DateTimeOffset.UtcNow, EscalatedForDue: new DateOnly(2026, 10, 30), EscalatedAtUtc: DateTimeOffset.UtcNow);
        var open = new Tide.Application.Actions.OpenActionsReview([new(row, 0, false)], 1);
        var context = new BriefRenderContext("CTI", Metrics(), "https://tide.example.org", img => "cid:" + img.ContentId, null, open);

        var team = _renderer.Render(BriefEditions.Build(Brief(), Edition.Team, TlpLevel.AmberStrict), context);
        team.EmailHtml.Should().Contain("Patch the VPN appliances <strong").And.Contain(">Escalated</strong>").And.Contain("not yet due");
        var quiet = _renderer.Render(BriefEditions.Build(Brief(), Edition.Team, TlpLevel.AmberStrict),
            context with { Open = new([new(row with { EscalatedForDue = null }, 3, false)], 1) });
        quiet.EmailHtml.Should().NotContain("Escalated</strong>").And.Contain("3 days late");
    }

    [Fact]
    public void TheTeamEmail_NamesTheDayEachNumberIsComparedWith_AndAThresholdInWords()
    {
        var group = new MetricGroup { Name = "Alert queue" };
        var metric = new MetricDefinition { Name = "Older than 24 hours", Direction = MetricDirection.LowerIsBetter, WarnThreshold = 1 };
        // Monday Oct 26, compared with Wednesday Oct 21 (no value since): "vs Wed" would read as the coming Wednesday.
        var tile = new MetricTile(metric, 3m, null, new MetricComparison(new DateOnly(2026, 10, 21), 5m, -2m, MetricTrend.Better), MetricLevel.Warn, [5m, 3m]);
        var critical = tile with { Metric = new MetricDefinition { Name = "Past SLA" }, Level = MetricLevel.Critical };
        var normal = tile with { Metric = new MetricDefinition { Name = "Within SLA" }, Level = MetricLevel.Normal };
        var day = new MetricDay(new DateOnly(2026, 10, 26), [new MetricGroupDay(group, [tile, critical, normal])]);

        var team = _renderer.Render(BriefEditions.Build(Brief(), Edition.Team, TlpLevel.AmberStrict), new("CTI", day, "https://tide.example.org", img => "cid:" + img.ContentId));

        team.EmailHtml.Should().Contain("vs Oct 21").And.NotContain("vs Wed");
        team.Text.Should().Contain("⚠ warning", "a plain-text or colour-blind reader can't see the colour").And.Contain("⚠ critical");
        team.Text.Split('\n').Single(l => l.Contains("Within SLA", StringComparison.Ordinal)).Should().NotContain("⚠", "a normal value has no mark");
        _renderer.Render(BriefEditions.Build(Brief(), Edition.Team, TlpLevel.AmberStrict), Context()).EmailHtml
            .Should().Contain("vs Fri", "the previous business day keeps its weekday");
    }

    [Fact]
    public void TheTeamEmail_ListsIndicatorsDefanged_AndFollowUpActions_ThePublicOneNeither()
    {
        var brief = Brief();
        var open = brief.Items[0].Article!;
        open.Indicators.Add(new ArticleIndicator { Type = IndicatorType.Domain, Value = "evil.example.com" });
        var actions = new[] { (open.Id, new EditionAction(7, "Block the domain", "Cyber Defense", new DateOnly(2026, 10, 28), ActionStatus.Open)) }
            .ToLookup(x => x.Item1, x => x.Item2);

        var team = _renderer.Render(BriefEditions.Build(brief, Edition.Team, TlpLevel.AmberStrict, actions), Context());
        var pub = _renderer.Render(BriefEditions.Build(brief, Edition.Public, TlpLevel.Green, actions), Context());

        team.EmailHtml.Should().Contain("evil[.]example[.]com").And.NotContain("evil.example.com").And.Contain("A-7").And.Contain("due Oct 28");
        team.WebHtml.Should().Contain("Follow-up actions (1)");
        pub.EmailHtml.Should().NotContain("evil[.]example").And.NotContain("A-7");
        pub.WebHtml.Should().NotContain("Indicators (");
    }

    [Fact]
    public void ARestrictedWhyItMatters_IsLeftOutOfThePublicEdition()
    {
        var brief = Brief();
        brief.Items[0].Article!.WhyItMattersTlp = TlpLevel.Amber;

        var pub = _renderer.Render(BriefEditions.Build(brief, Edition.Public, TlpLevel.Green), Context());
        var team = _renderer.Render(BriefEditions.Build(brief, Edition.Team, TlpLevel.AmberStrict), Context());

        pub.EmailHtml.Should().NotContain("We run it.");
        pub.WebHtml.Should().NotContain("We run it.");
        team.EmailHtml.Should().Contain("We run it.");
    }
}

