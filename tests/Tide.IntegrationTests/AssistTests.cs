using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Assist;
using Tide.Application.Intake;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Assist;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

public class AssistServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private const string Link = "https://news.example.com/ivanti-flaws";

    private static readonly string Page = "<html><body><article>" + string.Concat(Enumerable.Range(0, 6).Select(i =>
        $"<p>Attackers chained two Ivanti Connect Secure flaws, CVE-2026-1281 among them, reaching 185.220.101.47 from gateway {i}, and the vendor shipped fixes.</p>")) +
        "</article></body></html>";

    public AssistServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    // An article promoted from a feed item of a source with the given marking and fetching rule.
    private async Task<(Guid Article, Guid Source)> Promoted(TlpLevel tlp = TlpLevel.Clear, ArticleFetch fetch = ArticleFetch.TideOnly, string? selector = null)
    {
        var source = await _db.Intake().SaveSourceAsync(new SourceForm
        {
            Name = "News", Url = "https://news.example.com/feed.xml", PollMinutes = 30, DefaultTlp = tlp, ArticleFetch = fetch, ArticleBodySelector = selector,
        });
        var article = await _db.Articles().CreateAsync("Ivanti flaws chained", "News", Link, tlp);
        await using var ctx = _db.Context();
        ctx.FeedItems.Add(new FeedItem
        {
            SourceId = source, Title = "Ivanti flaws chained", Url = Link, UrlHash = "h", ArticleId = article, Excerpt = "Two Ivanti flaws are being chained.",
            FetchedAtUtc = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();
        return (article, source);
    }

    private async Task<ArticleForm> Form(Guid id) => ArticleForm.From((await _db.Articles().GetAsync(id))!);

    [Fact]
    public async Task Fetching_ReadsThePageThroughTheGuardedClient_AndAppliesTheSourcesSelector()
    {
        var (article, _) = await Promoted(selector: "article");
        _db.Http.Set(Link, Page);

        var got = await _db.Assist().FetchAsync(article, Link, "Ivanti flaws chained", TlpLevel.Clear);

        got.Text.Should().Contain("gateway 5");
        got.Origin.Should().Be("fetched by TIDE: the source's selector");
        _db.Http.Requests.Select(r => r.Url).Should().Equal(Link);
    }

    [Fact]
    public async Task ANeverSource_IsntFetched_AndAFailedFetchSaysToPaste()
    {
        var (never, _) = await Promoted(fetch: ArticleFetch.Never);
        (await _db.Assist().FetchAsync(never, Link, null, TlpLevel.Clear)).Problem.Should().Contain("aren't fetched");
        _db.Http.Requests.Should().BeEmpty();

        var (tideOnly, _) = await PromotedAgain(ArticleFetch.TideOnly);
        var failed = await _db.Assist(new AiOptions { WebGrounding = true }).FetchAsync(tideOnly, Link, null, TlpLevel.Clear);
        failed.Problem.Should().Contain("No canned response").And.EndWith("Paste the article instead.");
        _db.Ai.LookedUp.Should().BeEmpty("only an Open source is looked up by the provider");
        (await _db.Assist().FetchAsync(tideOnly, "ftp://x", null, TlpLevel.Clear)).Problem.Should().Contain("no http or https link");
    }

    // A second promoted article under another source name.
    private async Task<(Guid, Guid)> PromotedAgain(ArticleFetch fetch)
    {
        var source = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "Other", Url = "https://other.example.com/feed.xml", PollMinutes = 30, ArticleFetch = fetch });
        var article = await _db.Articles().CreateAsync("Another", "Other", Link);
        await using var ctx = _db.Context();
        ctx.FeedItems.Add(new FeedItem { SourceId = source, Title = "Another", Url = Link, UrlHash = "h2", ArticleId = article, FetchedAtUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();
        return (article, source);
    }

    [Fact]
    public async Task AnOpenSource_FallsBackToTheProvidersWebSearch_OnlyForClearAndWhenGroundingIsOn()
    {
        var (article, _) = await Promoted(fetch: ArticleFetch.Open);
        _db.Http.Set(Link, "<html><body><div id='app'></div></body></html>");
        _db.Ai.Found = string.Concat(Enumerable.Repeat("The full article as published. ", 30));

        (await _db.Assist().FetchAsync(article, Link, null, TlpLevel.Clear)).Problem.Should().Contain("built by script", "grounding is off");
        var found = await _db.Assist(new AiOptions { WebGrounding = true }).FetchAsync(article, Link, "Ivanti flaws chained", TlpLevel.Clear);
        found.Origin.Should().Be("found by the AI provider's web search");
        _db.Ai.LookedUp.Should().Equal(Link);

        (await _db.Assist(new AiOptions { WebGrounding = true, MaxTlp = TlpLevel.Amber }).FetchAsync(article, Link, null, TlpLevel.Green)).Text.Should().BeNull("only TLP:CLEAR is looked up");
        _db.Ai.LookedUp.Should().HaveCount(1);
        _db.Ai.Found = null;
        (await _db.Assist(new AiOptions { WebGrounding = true }).FetchAsync(article, Link, null, TlpLevel.Clear)).Problem.Should().Contain("didn't find it either (not found).");
    }

    [Fact]
    public async Task ADraft_IsChecked_Stored_AndCounted()
    {
        var (article, _) = await Promoted();
        var category = await _db.Scorecard().AddCategoryAsync("Threats", null);
        var edge = await _db.Scorecard().AddTagAsync(category, "Edge devices", null);
        _db.Ai.Json = """
            { "bottomLine": "Attackers chain two Ivanti flaws.", "bullets": ["Exploited since Oct 24."], "whyItMatters": "", "recommendedActions": "- Patch",
              "techniques": [{ "id": "T1190", "reason": "VPN" }, { "id": "T0000", "reason": "no" }], "tags": ["Edge devices"],
              "indicators": [{ "type": "IpAddress", "value": "185.220.101[.]47" }], "cves": ["CVE-2026-1281"] }
            """;
        var form = await Form(article);

        var draft = await _db.Assist().DraftAsync(article, form, "Pasted text naming 185.220.101.47.", null);

        draft.Suggestions.Bluf.Should().Be("Attackers chain two Ivanti flaws.");
        draft.Suggestions.Tags.Should().Equal(new SuggestedTag(edge, "Edge devices"));
        draft.Suggestions.Indicators.Single().InSource.Should().BeTrue();
        draft.Suggestions.DroppedTechniques.Should().Be(1);
        draft.BasedOn.Should().Be("article text, 34 chars (pasted) · feed summary · not our notes (TLP:AMBER, above the limit)");
        draft.Model.Should().Be("fake-model");
        var asked = _db.Ai.Asked.Single();
        asked.Input.Should().Contain("Pasted text naming").And.Contain("Two Ivanti flaws are being chained.").And.Contain("SCORECARD TAGS: Edge devices");

        await _db.Assist().RecordAsync(draft.DraftId, accepted: true, 3);
        await _db.Assist().RecordAsync(draft.DraftId, accepted: false);
        await using var ctx = _db.Context();
        var stored = await ctx.AiDrafts.SingleAsync();
        (stored.ArticleId, stored.Provider, stored.Model, stored.PromptVersion, stored.InputTlp).Should().Be((article, "AzureOpenAI", "fake-model", DraftPrompt.Version, TlpLevel.Clear));
        stored.InputHash.Should().HaveLength(64);
        (stored.Offered, stored.Accepted, stored.Rejected).Should().Be((7, 3, 1));
        stored.Suggestions.Should().Contain("Attackers chain two Ivanti flaws.");
    }

    [Fact]
    public async Task OurNotes_GoOnlyWhenTheLimitAllowsAmber_AndThenTheInputCountsAsAmber()
    {
        var (article, _) = await Promoted();
        var product = await _db.Stack().SaveAsync(new StackForm { Name = "Ivanti Connect Secure", Versions = "22.7R2.4", Notes = "Two gateways." });
        await _db.Stack().TagArticleAsync(article, product);
        _db.Ai.Json = """{ "whyItMatters": "Both gateways are on 22.7R2.4." }""";

        var green = await _db.Assist().DraftAsync(article, await Form(article), null, null);
        green.Suggestions.WhyItMatters.Should().BeNull();
        _db.Ai.Asked[^1].Input.Should().NotContain("Two gateways");

        var amber = await _db.Assist(new AiOptions { MaxTlp = TlpLevel.Amber }).DraftAsync(article, await Form(article), null, null);
        amber.Suggestions.WhyItMatters.Should().Be("Both gateways are on 22.7R2.4.");
        amber.BasedOn.Should().EndWith("our notes on Ivanti Connect Secure");
        _db.Ai.Asked[^1].Input.Should().Contain("We run Ivanti Connect Secure, versions: 22.7R2.4.");
        await using var ctx = _db.Context();
        (await ctx.AiDrafts.OrderBy(d => d.CreatedAtUtc).Select(d => d.InputTlp).ToListAsync()).Should().Equal(TlpLevel.Clear, TlpLevel.Amber);
    }

    [Fact]
    public async Task AboveTheLimit_NothingIsSent_NorCopied()
    {
        var (article, _) = await Promoted(TlpLevel.Amber);
        var assist = _db.Assist();

        var status = await assist.StatusAsync(article, TlpLevel.Clear);
        status.Blocked.Should().Be("This article is TLP:AMBER and the AI limit is TLP:GREEN, so nothing is sent.", "the source's marking counts too");
        status.CanDraft.Should().BeFalse();
        await FluentActions.Invoking(async () => await assist.DraftAsync(article, await Form(article), "x", null)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*nothing is sent*");
        await FluentActions.Invoking(async () => await assist.PromptAsync(article, await Form(article), "x", null)).Should().ThrowAsync<InvalidOperationException>();
        _db.Ai.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task WithNoProvider_APromptIsCopied_AndThePastedAnswerGoesThroughTheSameChecks()
    {
        var (article, _) = await Promoted();
        _db.Ai.Configured = false;
        var assist = _db.Assist();
        (await assist.StatusAsync(article, TlpLevel.Clear)).ProviderReady.Should().BeFalse();
        await FluentActions.Invoking(async () => await assist.DraftAsync(article, await Form(article), null, null)).Should().ThrowAsync<InvalidOperationException>().WithMessage("No AI provider*");

        var prompt = await assist.PromptAsync(article, await Form(article), "The text.", "fetched by TIDE: x");
        prompt.Should().StartWith(DraftPrompt.Instructions).And.Contain("SOURCE 1 (News, fetched by TIDE: x):");

        var pasted = await assist.FromPastedAsync(article, await Form(article), "The text.", null, "```json\n{ \"bottomLine\": \"Pasted.\", \"techniques\": [\"T1190\"] }\n```");
        pasted.Suggestions.Bluf.Should().Be("Pasted.");
        pasted.Suggestions.Techniques.Single().Id.Should().Be("T1190");
        await using var ctx = _db.Context();
        (await ctx.AiDrafts.SingleAsync()).Provider.Should().Be("Pasted");
        await FluentActions.Invoking(async () => await assist.FromPastedAsync(article, await Form(article), null, null, "  ")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task OnlyAnalystsDraft_AndOnlyAdministratorsTestASourcesFetch()
    {
        var (article, _) = await Promoted();
        _db.Http.Set(Link, Page);
        (await _db.Assist().TestFetchAsync(Link, "article")).Usable.Should().BeTrue();
        await FluentActions.Invoking(() => _db.Assist().TestFetchAsync(Link, "div[")).Should().ThrowAsync<ArgumentException>();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await FluentActions.Invoking(() => _db.Assist().TestFetchAsync(Link, null)).Should().ThrowAsync<ForbiddenException>();
        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        await FluentActions.Invoking(() => _db.Assist().StatusAsync(article, TlpLevel.Clear)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => _db.Assist().FetchAsync(article, Link, null, TlpLevel.Clear)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ASourcesFetchSettings_AreSaved_AndABadSelectorIsRefused()
    {
        var (_, source) = await Promoted(fetch: ArticleFetch.Open, selector: " div.article-content ");
        await using (var ctx = _db.Context())
            (await ctx.Sources.SingleAsync(s => s.Id == source)).Should().Match<Source>(s => s.ArticleFetch == ArticleFetch.Open && s.ArticleBodySelector == "div.article-content");

        await FluentActions.Invoking(() => _db.Intake().SaveSourceAsync(new SourceForm { Name = "Bad", Url = "https://bad.example.com/feed", PollMinutes = 30, ArticleBodySelector = "div[" }))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*isn't a CSS selector*");
    }

    [Fact]
    public async Task Fetching_TakesOutTheAdverts_AndWhatTheSourceSaysToRemove_AndTheTestUsesTheSelectorAsTyped()
    {
        var (article, source) = await Promoted();
        var page = Page.Replace("</article>", "<div class=\"inline-ad\"><p>Half-price firewalls this week</p></div>" +
            "<div class=\"x-cta\"><p>Join 40,000 readers of our weekly roundup</p></div><p>Advertisement</p></article>", StringComparison.Ordinal);
        _db.Http.Set(Link, page);

        (await _db.Assist().FetchAsync(article, Link, null, TlpLevel.Clear)).Text.Should()
            .Contain("gateway 5").And.NotContain("Half-price").And.NotContain("Advertisement").And.Contain("weekly roundup");
        (await _db.Assist().TestFetchAsync(Link, null, ".x-cta")).Text.Should().NotContain("weekly roundup", "the Test uses the remove selector as typed");

        await using (var ctx = _db.Context())
        {
            var row = await ctx.Sources.SingleAsync(s => s.Id == source);
            var form = SourceForm.From(row);
            form.ArticleRemoveSelector = " .x-cta ";
            await _db.Intake().SaveSourceAsync(form);
        }
        (await _db.Assist().FetchAsync(article, Link, null, TlpLevel.Clear)).Text.Should().NotContain("weekly roundup");
        await using (var ctx = _db.Context())
            (await ctx.Sources.SingleAsync(s => s.Id == source)).ArticleRemoveSelector.Should().Be(".x-cta");

        await FluentActions.Invoking(() => _db.Assist().TestFetchAsync(Link, null, "div[")).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => _db.Intake().SaveSourceAsync(new SourceForm { Name = "Bad", Url = "https://bad.example.com/feed", PollMinutes = 30, ArticleRemoveSelector = "div[" }))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*isn't a CSS selector*");
    }
}

public class AzureOpenAiAssistantTests
{
    private sealed class Answering(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add((request, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly AiOptions Options = new() { Provider = "AzureOpenAI", Endpoint = "https://contoso.openai.azure.com/", Deployment = "gpt-5-mini", ApiKey = "secret" };

    private static AzureOpenAiAssistant Assistant(Answering handler, AiOptions? options = null) =>
        new(new StaticOptionsMonitor<AiOptions>(options ?? Options), NullLogger<AzureOpenAiAssistant>.Instance, handler);

    [Fact]
    public async Task ADraft_IsOneStrictSchemaRequest_WithNoTools_AndNothingStored()
    {
        var handler = new Answering(HttpStatusCode.OK, """
            { "status": "completed", "output": [ { "type": "reasoning" }, { "type": "message", "content": [ { "type": "output_text", "text": "{\"bottomLine\":\"x\"}" } ] } ] }
            """);

        var answer = await Assistant(handler).AnswerAsync(new AssistRequest("Do it.", "Material.", "tide_article_draft", DraftPrompt.Schema));

        answer.Json.Should().Be("""{"bottomLine":"x"}""");
        var (request, body) = handler.Sent.Single();
        request.RequestUri!.AbsoluteUri.Should().Be("https://contoso.openai.azure.com/openai/v1/responses");
        request.Headers.GetValues("api-key").Should().Equal("secret");
        var json = JsonNode.Parse(body)!;
        ((string?)json["model"], (bool?)json["store"], (string?)json["instructions"], (string?)json["input"]).Should().Be(("gpt-5-mini", false, "Do it.", "Material."));
        ((string?)json["text"]!["format"]!["type"], (bool?)json["text"]!["format"]!["strict"]).Should().Be(("json_schema", true));
        json["tools"].Should().BeNull("a draft can't reach the web");
    }

    [Fact]
    public async Task FindingAnArticle_SearchesOnlyItsSite_AndNeedsACitationOfThatPage()
    {
        const string cited = """
            { "status": "completed", "output": [
              { "type": "web_search_call", "action": { "type": "search", "sources": [ { "type": "url", "url": "https://www.news.example.com/ivanti-flaws/?utm_source=x" } ] } },
              { "type": "message", "content": [ { "type": "output_text", "text": "The article.", "annotations": [] } ] } ] }
            """;
        var handler = new Answering(HttpStatusCode.OK, cited);

        var found = await Assistant(handler).FindArticleAsync("https://news.example.com/ivanti-flaws", "Ivanti flaws");

        found.Text.Should().Be("The article.");
        var json = JsonNode.Parse(handler.Sent.Single().Body)!;
        ((string?)json["tools"]![0]!["type"], (string?)json["tools"]![0]!["filters"]!["allowed_domains"]![0]).Should().Be(("web_search", "news.example.com"));
        ((string?)json["input"]).Should().Be("Address: https://news.example.com/ivanti-flaws\nTitle: Ivanti flaws");

        var elsewhere = cited.Replace("ivanti-flaws/?utm_source=x", "another-story", StringComparison.Ordinal);
        (await Assistant(new Answering(HttpStatusCode.OK, elsewhere)).FindArticleAsync("https://news.example.com/ivanti-flaws", null))
            .Should().Be(new FoundArticle(null, "it didn't cite that page"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "*refused the key*")]
    [InlineData(HttpStatusCode.NotFound, "*no deployment*")]
    [InlineData(HttpStatusCode.TooManyRequests, "*busy*")]
    [InlineData(HttpStatusCode.InternalServerError, "*HTTP 500*")]
    public async Task Failures_AreSaid_NotThrown(HttpStatusCode status, string message)
    {
        var answer = await Assistant(new Answering(status, "{}")).AnswerAsync(new AssistRequest("i", "m", "s", "{}"));
        answer.Json.Should().BeNull();
        answer.Error.Should().Match(message);
    }

    [Fact]
    public async Task ARefusal_AnIncompleteAnswer_AndNoProvider_AreSaid()
    {
        (await Assistant(new Answering(HttpStatusCode.OK, """{ "status": "completed", "output": [ { "type": "message", "content": [ { "type": "refusal", "refusal": "No." } ] } ] }"""))
            .AnswerAsync(new AssistRequest("i", "m", "s", "{}"))).Error.Should().Be("The model declined to answer: No.");
        (await Assistant(new Answering(HttpStatusCode.OK, """{ "status": "incomplete", "output": [] }""")).AnswerAsync(new AssistRequest("i", "m", "s", "{}")))
            .Error.Should().Contain("stopped before finishing");
        var none = new Answering(HttpStatusCode.OK, "{}");
        (await Assistant(none, new AiOptions()).AnswerAsync(new AssistRequest("i", "m", "s", "{}"))).Error.Should().Be("No AI provider is set up.");
        none.Sent.Should().BeEmpty();
    }
}

public class DraftAssistCardTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TheEditorOffersDrafting_AndWithNoProvider_ACopiedPrompt()
    {
        Guid id;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            id = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Copilot card test", "Vendor", "https://vendor.example.com/a");

        await using var page = await ComponentHarness.RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id });
        var html = await page.Html();

        html.Should().Contain("Draft from the sources").And.Contain("id=\"draft-text\"").And.Contain("Fetch the article").And.Contain("Copy a prompt")
            .And.Contain("No AI provider is set up.");
        html.Should().NotContain(">Draft from the sources</button>", "with no provider there's nothing to draft with");
        html.Should().Contain("Other reports").And.Contain("Add another report");
    }

    [Fact]
    public async Task AGroupsOtherReports_AreListedAsSources()
    {
        Guid id;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            var source = new Source { Name = "Card group source", Url = "https://cardgroup.example/feed" };
            var a = new FeedItem { Source = source, Title = "card-group A", Url = "https://cardgroup.example/a", UrlHash = "card-group-a", FetchedAtUtc = DateTimeOffset.UtcNow };
            var b = new FeedItem { Source = source, Title = "card-group B", Url = "https://cardgroup.example/b", UrlHash = "card-group-b", FetchedAtUtc = DateTimeOffset.UtcNow };
            db.AddRange(a, b);
            await db.SaveChangesAsync();
            id = await scope.ServiceProvider.GetRequiredService<IntakeService>().PromoteAsOneAsync([a.Id, b.Id],
                new PromoteForm { Title = "card-group", Classification = Classification.Informational });
        }

        await using var page = await ComponentHarness.RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id });
        var html = await page.Html();

        html.Should().Contain("Other reports (1)").And.Contain("Fetch it").And.Contain("every claim says which it came from");
    }
}
