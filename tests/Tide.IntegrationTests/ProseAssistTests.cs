using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Assist;
using Tide.Application.Security;
using Tide.Application.Trends;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>AI drafts of a report section's assessment and a brief's title slide line (ADR 0004): drafted from what's at
/// or under the AI limit and the team edition's ceiling, checked (numbers included) whether a provider answered or the
/// answer was pasted, recorded as drafts, and refused to whoever can't write the text.</summary>
public class ProseAssistTests : IDisposable
{
    private static readonly Period Sep = new(PeriodKind.Month, 2026, 9);

    private readonly TestDb _db = new();

    public ProseAssistTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    private static Article Article(string title, string bluf, TlpLevel tlp) => new() { Title = title, Bluf = bluf, Tlp = tlp, Status = ArticleStatus.Ready };

    private async Task<Guid> Brief(DateOnly day, BriefStatus status, params Article[] articles)
    {
        var b = new Brief { BriefDate = day, Status = status };
        foreach (var a in articles) b.Items.Add(new BriefItem { ArticleId = a.Id, SortOrder = b.Items.Count });
        await using var db = _db.Context();
        db.AddRange(articles);
        db.Add(b);
        await db.SaveChangesAsync();
        return b.Id;
    }

    private async Task<Guid> ThreeArticleBrief(BriefStatus status = BriefStatus.Draft) => await Brief(new DateOnly(2026, 9, 8), status,
        Article("Ivanti flaws chained", "Attackers chained 2 flaws to reach 3 gateways.", TlpLevel.Clear),
        Article("Okta phishing kit", "A kit targets help desks.", TlpLevel.Green),
        Article("Our VPN gap", "We still run the old build.", TlpLevel.Amber));

    [Fact]
    public async Task ATitleLine_IsDraftedFromTheArticlesUnderTheLimit_AndTakesTheHighestOfTheirTlps()
    {
        var brief = await ThreeArticleBrief();
        _db.Ai.Json = """{"line": "Edge devices under fire: patch 3 gateways and 7 more."}""";

        var draft = await _db.ProseAssist().DraftTitleLineAsync(brief);

        var sent = _db.Ai.Asked.Single();
        sent.Input.Should().Contain("Ivanti flaws chained").And.Contain("Okta phishing kit").And.NotContain("Our VPN gap", "AMBER is above the default GREEN limit");
        sent.Input.Should().NotContain("old build");
        draft.Text.Should().Be("Edge devices under fire: patch 3 gateways and 7 more.");
        draft.UnknownNumbers.Should().Equal("7");
        draft.InputTlp.Should().Be(TlpLevel.Green);
        draft.BasedOn.Should().Be("2 articles' titles and bottom lines on the brief (at or under TLP:GREEN)");

        await using var ctx = _db.Context();
        var stored = await ctx.AiDrafts.SingleAsync();
        (stored.Kind, stored.Subject, stored.ArticleId, stored.InputTlp, stored.Provider, stored.PromptVersion)
            .Should().Be((AiDraftKind.TitleLine, brief.ToString(), (Guid?)null, TlpLevel.Green, AiOptions.AzureOpenAI, ProsePrompt.TitleLineVersion));
    }

    [Fact]
    public async Task TheTeamEditionsCeiling_CutsToo_WhenItsBelowTheAiLimit()
    {
        var brief = await ThreeArticleBrief();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        await _db.Settings().SaveAsync(TlpLevel.Clear, TlpLevel.Clear);
        _db.Ai.Json = """{"line": "Ivanti."}""";

        await _db.ProseAssist(new AiOptions { MaxTlp = TlpLevel.Amber }).DraftTitleLineAsync(brief);

        _db.Ai.Asked.Single().Input.Should().Contain("Ivanti").And.NotContain("Okta").And.NotContain("Our VPN gap");
    }

    [Fact]
    public async Task WithNoProvider_APromptIsCopied_AndThePastedAnswerIsCheckedTheSameWay()
    {
        var brief = await ThreeArticleBrief();
        _db.Ai.Configured = false;
        var assist = _db.ProseAssist();

        assist.Status().ProviderReady.Should().BeFalse();
        var noProvider = () => assist.DraftTitleLineAsync(brief);
        await noProvider.Should().ThrowAsync<InvalidOperationException>().WithMessage("No AI provider*");

        var prompt = await assist.TitleLinePromptAsync(brief);
        prompt.Should().StartWith(ProsePrompt.Instructions(AiDraftKind.TitleLine)).And.Contain("JSON schema").And.Contain("Ivanti flaws chained").And.NotContain("Our VPN gap");

        var pasted = await assist.TitleLineFromPastedAsync(brief, "```json\n{\"line\": \"12 days of edge exploits\"}\n```");
        pasted.Text.Should().Be("12 days of edge exploits");
        pasted.UnknownNumbers.Should().Equal("12");
        await using var ctx = _db.Context();
        (await ctx.AiDrafts.SingleAsync()).Provider.Should().Be(AssistService.Pasted);
        _db.Ai.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task ASentBrief_OrOneWithNothingUnderTheLimit_HasNoLineToSuggest()
    {
        var sent = await ThreeArticleBrief(BriefStatus.Sent);
        var amberOnly = await Brief(new DateOnly(2026, 9, 9), BriefStatus.Draft, Article("Our gap", "Internal.", TlpLevel.Amber));

        var onSent = () => _db.ProseAssist().TitleLinePromptAsync(sent);
        await onSent.Should().ThrowAsync<InvalidOperationException>().WithMessage("*sent*");
        var onAmber = () => _db.ProseAssist().TitleLinePromptAsync(amberOnly);
        await onAmber.Should().ThrowAsync<InvalidOperationException>().WithMessage("No article on this brief is at or under TLP:GREEN*");
    }

    [Fact]
    public async Task AReportAssessment_IsDraftedFromTheSectionsNumbers_AndTheCountedArticlesUnderTheLimit()
    {
        await Brief(new DateOnly(2026, 9, 8), BriefStatus.Sent,
            Article("Ivanti flaws chained", "Exploited at scale.", TlpLevel.Clear),
            Article("Our VPN gap", "We still run the old build.", TlpLevel.Amber));
        _db.Ai.Json = """{"assessment": "We briefed 2 articles, up 2 on August; 40% were threats."}""";

        var draft = await _db.ProseAssist().DraftReportAsync(Sep, "headlines");

        var sent = _db.Ai.Asked.Single();
        sent.SchemaName.Should().Be("tide_report_assessment");
        sent.Input.Should().Contain("Monthly report, Sep 2026").And.Contain("Headlines")
            .And.Contain("- Articles briefed: 2 (the period before: 0; change +2)")
            .And.Contain("Ivanti flaws chained").And.NotContain("Our VPN gap").And.NotContain("old build");
        draft.UnknownNumbers.Should().Equal("40");
        draft.InputTlp.Should().Be(TlpLevel.Clear);
        draft.BasedOn.Should().Be("the Headlines numbers · 1 article's title and bottom line (at or under TLP:GREEN)");

        await using var ctx = _db.Context();
        var stored = await ctx.AiDrafts.SingleAsync();
        (stored.Kind, stored.Subject, stored.Offered).Should().Be((AiDraftKind.ReportAssessment, "2026-09/headlines", 1));
    }

    [Fact]
    public async Task AReportSectionAboutActionsOrMetrics_IsDraftedFromItsNumbersAlone()
    {
        await Brief(new DateOnly(2026, 9, 8), BriefStatus.Sent, Article("Ivanti flaws chained", "Exploited at scale.", TlpLevel.Clear));

        var prompt = await _db.ProseAssist().ReportPromptAsync(Sep, "outcomes");

        prompt.Should().Contain("Overdue at period end: 0").And.NotContain("ARTICLES:").And.NotContain("Ivanti");
        var bad = () => _db.ProseAssist().ReportPromptAsync(Sep, "nonsense");
        await bad.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Decisions_AreCounted_AndOnlyWhoeverCanWriteTheTextMayDraftIt()
    {
        var brief = await ThreeArticleBrief();
        _db.Ai.Json = """{"line": "Edge devices under fire."}""";
        var draft = await _db.ProseAssist().DraftTitleLineAsync(brief);
        await _db.ProseAssist().RecordAsync(draft.DraftId, accepted: true);
        await using (var ctx = _db.Context())
            ((await ctx.AiDrafts.SingleAsync()).Accepted).Should().Be(1);

        _db.User.Roles = new HashSet<AppRole> { AppRole.Manager };
        var line = () => _db.ProseAssist().TitleLinePromptAsync(brief);
        await line.Should().ThrowAsync<ForbiddenException>();
        var report = () => _db.ProseAssist().ReportPromptAsync(Sep, "headlines");
        await report.Should().ThrowAsync<ForbiddenException>("a manager reads the report and doesn't write it");
        var record = () => _db.ProseAssist().RecordAsync(draft.DraftId, accepted: false);
        await record.Should().ThrowAsync<ForbiddenException>();
    }
}
