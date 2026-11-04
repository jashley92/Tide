using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Intake;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>
/// Related work, drawn: the editor's Related tab (with Mention and Same story), the story picker out of Details, "Add a
/// report link", and Triage's "Attach to…" picker and follow-up line.
/// </summary>
public class RelatedRenderTests(TideApp app) : IClassFixture<TideApp>
{
    // Two articles about one CVE: the earlier one has an open action and a closed one.
    private async Task<(Guid Me, Guid Earlier, int Open)> Seed(string key)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var cve = "CVE-2026-" + Random.Shared.Next(10000, 99999);
        async Task<Guid> Make(string title)
        {
            var id = await articles.CreateAsync(title, "Vendor", null, TlpLevel.Clear);
            var form = ArticleForm.From((await articles.GetAsync(id))!);
            form.Cves = [cve];
            await articles.SaveAsync(form);
            return id;
        }
        var earlier = await Make($"related-{key} earlier article");
        var me = await Make($"related-{key} this article");
        var team = await scope.ServiceProvider.GetRequiredService<TeamService>().AddAsync($"Team {key}", null, null);
        var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
        var open = await actions.CreateAsync(new ActionForm { Title = $"related-{key} patch", Kind = ActionKind.PatchRemediate, TeamId = team, ArticleId = earlier });
        return (me, earlier, open);
    }

    private async Task<ArticleEditorPanel.State> StateOf(Guid id, TlpLevel why = TlpLevel.Amber)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var article = (await scope.ServiceProvider.GetRequiredService<ArticleService>().GetAsync(id))!;
        var form = ArticleForm.From(article);
        form.WhyItMattersTlp = why;
        return new ArticleEditorPanel.State(article, form, [], [], false);
    }

    [Fact]
    public async Task TheEditor_HasARelatedTabWithACount_TheStoryPickerInView_AndAddAReportLink()
    {
        var (me, _, _) = await Seed("page");
        var html = await app.CreateClient().GetStringAsync($"/articles/{me}");

        html.Should().Contain("role=\"group\" aria-label=\"Show\"")
            .And.Contain("title=\"Related articles and their open actions\">Related")
            .And.Contain("<span class=\"tide-seg-count\">1</span>")
            .And.Contain("Add a report link");
        var picker = html.IndexOf("for=\"story-pick\">Threat story", StringComparison.Ordinal);
        picker.Should().BePositive();
        picker.Should().BeLessThan(html.IndexOf("<details class=\"tide-reader-details\"", StringComparison.Ordinal), "the story picker is out of the folded Details");
    }

    [Fact]
    public async Task TheRelatedTab_ListsTheArticle_ItsOpenAction_AndMentionAndSameStory()
    {
        var (me, earlier, open) = await Seed("tab");
        await using var r = await ComponentHarness.RenderAsync<ReaderPreview>(app, $"/articles/{me}", new()
        {
            ["State"] = await StateOf(me),
        });
        (await r.Html()).Should().NotContain("tide-related-list", "the team edition shows first");

        await r.Click(p => { p.ShowRelated(); return Task.CompletedTask; });
        var html = await r.Html();

        html.Should().NotContain("aria-pressed=\"true\" aria-keyshortcuts", "neither edition is shown")
            .And.Contain($"href=\"articles/{earlier}\">related-tab earlier article</a>")
            .And.Contain($"href=\"actions/{open}\"").And.Contain("related-tab patch")
            .And.Contain($"aria-label=\"Mention A-{open} in why it matters\"")
            .And.Contain("Start a story with this article and &#x201C;related-tab earlier article&#x201D;")
            .And.Contain("Not briefed yet")
            .And.NotContain("Ready to brief", "the edition preview gives the tab its place");
        html.Should().NotContain(RelatedPanel.MentionOff[..40]);
    }

    [Fact]
    public async Task Mention_IsOff_WhileWhyItMattersIsBelowAmber()
    {
        var (me, _, _) = await Seed("green");
        await using var r = await ComponentHarness.RenderAsync<ReaderPreview>(app, $"/articles/{me}", new() { ["State"] = await StateOf(me, TlpLevel.Green) });
        await r.Click(p => { p.ShowRelated(); return Task.CompletedTask; });

        var html = await r.Html();
        html.Should().Contain(RelatedPanel.MentionOff[..40]).And.Contain("disabled");
    }

    [Fact]
    public async Task AttachPicker_OpensAComboboxOfRecentArticles_AndEnterPicksTheHighlightedOne()
    {
        await Seed("attach");
        AttachTarget? picked = null;
        await using var r = await ComponentHarness.RenderAsync<AttachPicker>(app, "/triage", new()
        {
            ["OnPick"] = EventCallback.Factory.Create<AttachTarget>(this, t => picked = t),
        });
        var closed = await r.Html();
        closed.Should().Contain("aria-expanded=\"false\"").And.Contain("Attach to&hellip;").And.NotContain("role=\"listbox\"");

        await r.Click(p => p.Toggle());
        var html = await r.Html();
        html.Should().Contain("role=\"combobox\"").And.Contain("role=\"listbox\"").And.Contain("aria-expanded=\"true\"")
            .And.Contain("aria-selected=\"true\"").And.Contain("related-attach this article");

        var first = r.Component.Shown[0];
        await r.Click(p => p.OnKey(new KeyboardEventArgs { Key = "ArrowDown" }));
        await r.Click(p => p.OnKey(new KeyboardEventArgs { Key = "ArrowUp" }));
        await r.Click(p => p.OnKey(new KeyboardEventArgs { Key = "Enter" }));
        picked.Should().Be(first);
        (await r.Html()).Should().NotContain("role=\"listbox\"", "picking closes it");
    }

    [Fact]
    public async Task Triage_OffersAttachTo_OnANewRow()
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            var source = new Source { Name = "Attach source", Url = "https://attach.example/feed", DefaultTlp = TlpLevel.Clear };
            db.Add(source);
            var at = DateTimeOffset.UtcNow.AddMinutes(30);
            db.Add(new FeedItem { Source = source, Title = "attach-row: new outlet on the story", Url = "https://attach.example/1", UrlHash = "attach-row-1", PublishedAtUtc = at, FetchedAtUtc = at });
            await db.SaveChangesAsync();
        }
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage");
        var html = await r.HtmlOnce("attach-row: new outlet");
        html.Should().Contain("class=\"tide-attach\"").And.Contain("Attach to&hellip;");
    }

    [Fact]
    public void FollowUp_NamesTheFirstBriefAndTheOpenActions()
    {
        var item = new FeedItem { Title = "x", Url = "https://x.example/" };
        var source = new Source { Name = "s" };
        var a = Guid.NewGuid();
        var row = new InboxRow(item, source, null, [
            new Coverage("CVE-2026-1", a, "Later", new DateOnly(2026, 10, 27)) { OpenActions = [14] },
            new Coverage("Same link", Guid.NewGuid(), "First", new DateOnly(2026, 10, 22)) { OpenActions = [13, 14] },
        ]);

        Triage.FollowUp(row).Should().Be("Follow-up to Thu Oct 22: A-13, A-14 open");
        Triage.FollowUp(new InboxRow(item, source, null, [new Coverage("CVE-2026-1", a, "Draft", null)])).Should().BeNull("never briefed");
        Triage.FollowUp(new InboxRow(item, source, null, [new Coverage("CVE-2026-1", a, "Done", new DateOnly(2026, 10, 22))]))
            .Should().Be("Follow-up to Thu Oct 22: no actions open");
    }
}
