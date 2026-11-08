using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>Triage's promote form: P (or the button) opens it in the selected row, prefilled; Esc or Cancel closes it.</summary>
public class TriagePromoteTests(TideApp app) : IClassFixture<TideApp>
{
    // The newest new item, so it leads the inbox and is selected: from an AMBER source, naming a CVE.
    private async Task Seed(string key, int minutesAhead, FeedItemStatus status = FeedItemStatus.New)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        var source = new Source { Name = "Promote source " + key, Url = "https://promote.example/feed/" + key, DefaultTlp = TlpLevel.Amber };
        db.Add(source);
        var at = DateTimeOffset.UtcNow.AddMinutes(minutesAhead);
        db.Add(new FeedItem
        {
            // A CVE of its own, so the items of the tests before it don't group with it ("Looks like one event").
            Source = source, Title = TitleOf(key), Url = "https://promote.example/" + key, UrlHash = "promote-form-" + key, Cves = $"CVE-2026-{1100 + minutesAhead}",
            PublishedAtUtc = at, FetchedAtUtc = at, Status = status,
        });
        await db.SaveChangesAsync();
    }

    private static string TitleOf(string key) => "promote-form " + key + ": vendor fixes an exploited VPN bug";

    [Fact]
    public async Task Promote_OpensAPrefilledFormInTheRow_AndCancelClosesIt()
    {
        const string key = "cancel";
        var title = TitleOf(key);
        await Seed(key, 10);
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage");
        var before = await r.HtmlOnce(title);
        before.Should().NotContain("tide-promote-form").And.Contain("Promote <span class=\"tide-kbd\">P</span>");

        await r.Click(t => t.OnTriageKey("promote"));
        var html = await r.Html();

        var form = html[html.IndexOf("<form class=\"tide-promote-form", StringComparison.Ordinal)..];
        form = form[..form.IndexOf("</form>", StringComparison.Ordinal)];
        form.Should().Contain("aria-label=\"Promote to an article\"")
            .And.Contain("id=\"promote-title\"").And.Contain($"value=\"{title}\"").And.Contain("required")
            .And.Contain("for=\"promote-threat\"").And.Contain("for=\"promote-info\"")
            .And.Contain("id=\"promote-priority\"").And.Contain("<option value=\"P3\" selected")
            .And.Contain("<option value=\"Amber\" selected").And.Contain("From the source: TLP:AMBER")
            .And.Contain("type=\"submit\" class=\"btn btn-primary btn-sm\"").And.Contain("Promote and write")
            .And.Contain(">Cancel</button>").And.Contain("Enter promotes, Esc cancels");
        html.Should().NotContain("Shortlist <span class=\"tide-kbd\">S</span>", "the form takes the action buttons' place");

        await r.Click(t => t.OnTriageKey("dismiss"));
        (await r.Html()).Should().Contain("tide-promote-form", "the letter keys do nothing while the form is open");

        await r.Click(t => t.OnTriageKey("cancel"));
        var after = await r.Html();
        after.Should().NotContain("tide-promote-form").And.Contain("Shortlist <span class=\"tide-kbd\">S</span>");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        db.FeedItems.Single(i => i.UrlHash == "promote-form-" + key).Status.Should().Be(FeedItemStatus.New, "nothing was promoted or dismissed");
    }

    [Fact]
    public async Task PromoteAndWrite_MakesTheDraftWithTheFormsValues()
    {
        const string key = "submit";
        await Seed(key, 20);
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage");
        await r.HtmlOnce(TitleOf(key));

        await r.Click(t => t.OnTriageKey("promote"));
        await r.Click(t => t.SubmitPromote());

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        var item = db.FeedItems.Single(i => i.UrlHash == "promote-form-" + key);
        item.Status.Should().Be(FeedItemStatus.Promoted);
        var article = db.Articles.Single(a => a.Id == item.ArticleId);
        article.Title.Should().Be(TitleOf(key));
        article.Classification.Should().Be(Classification.Threat, "the item names a CVE");
        article.Priority.Should().Be(Priority.P3);
        article.Tlp.Should().Be(TlpLevel.Amber, "the source's TLP");
    }

    // A render after a call that, in the browser, an event handler would re-render for: "prev" on the first row moves
    // nothing (and, with the promote form open, is ignored) but renders.
    private static Task Rendered(Triage t) => t.OnTriageKey("prev");

    [Fact]
    public async Task ATitle_OpensItsRowsPromoteForm_AndTheSourceOpensFromItsOwnIcon()
    {
        const string key = "title";
        var title = TitleOf(key);
        await Seed(key, 30);
        try { await TitleOpensTheForm(key, title); }
        finally
        {
            // Set aside, so the other tests' newer-first items still lead the inbox whatever order they run in.
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            db.FeedItems.Single(i => i.UrlHash == "promote-form-" + key).Status = FeedItemStatus.Duplicate;
            await db.SaveChangesAsync();
        }
    }

    private async Task TitleOpensTheForm(string key, string title)
    {
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage");
        var html = await r.HtmlOnce(title);

        html.Should().MatchRegex($"<button type=\"button\" class=\"tide-triage-title tide-triage-title-btn\" title=\"Promote to an article\"[^>]*>{title}</button>")
            .And.NotContain($"href=\"https://promote.example/{key}\">{title}", "the title no longer opens the source");
        html.Should().Contain($"<a class=\"tide-triage-link tide-triage-source\" href=\"https://promote.example/{key}\" target=\"_blank\" rel=\"noopener noreferrer\" title=\"Open the source in a new tab\" aria-label=\"Open the source in a new tab\"")
            .And.Contain("bi-box-arrow-up-right");

        await r.Click(t =>
        {
            t.PromoteFromTitle(0);
            return Rendered(t);
        });
        var form = await r.Html();
        form.Should().Contain("<form class=\"tide-promote-form").And.Contain($"value=\"{title}\"");

        await r.Click(t =>
        {
            t.PromoteFromTitle(0);   // again: the form stays open as it was
            return Rendered(t);
        });
        (await r.Html()).Split("<form class=\"tide-promote-form").Length.Should().Be(2);
    }

    [Fact]
    public async Task APromotedItemsTitle_GoesToItsArticle()
    {
        const string key = "promoted-title";
        var title = TitleOf(key);
        await Seed(key, 40);
        await using (var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage"))
        {
            await r.HtmlOnce(title);
            await r.Click(t => t.OnTriageKey("promote"));
            await r.Click(t => t.SubmitPromote());
        }
        Guid articleId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            articleId = db.FeedItems.Single(i => i.UrlHash == "promote-form-" + key).ArticleId!.Value;
        }

        await using var promoted = await ComponentHarness.RenderAsync<Triage>(app, "/triage");
        await promoted.Click(async t =>
        {
            await t.SetStatus(FeedItemStatus.Promoted);
            await Rendered(t);
        });
        var html = await promoted.HtmlOnce(title);
        html.Should().MatchRegex($"<a class=\"tide-triage-title\" href=\"articles/{articleId}\" title=\"Open the article\"[^>]*>{title}</a>")
            .And.Contain($"href=\"https://promote.example/{key}\" target=\"_blank\"", "its source still opens from the icon")
            .And.NotContain("tide-triage-title-btn", "a promoted item has nothing left to promote");
    }

    [Fact]
    public async Task ADismissedItemsTitle_IsPlainText_AndItKeepsPutBack()
    {
        const string key = "dismissed-title";
        var title = TitleOf(key);
        await Seed(key, 50, FeedItemStatus.Dismissed);
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/triage");
        await r.Click(async t =>
        {
            await t.SetStatus(FeedItemStatus.Dismissed);
            await Rendered(t);
        });
        var html = await r.HtmlOnce(title);
        html.Should().Contain($"<span class=\"tide-triage-title\">{title}</span>").And.NotContain("tide-triage-title-btn")
            .And.Contain("Put back <span class=\"tide-kbd\">R</span>");

        await r.Click(t =>
        {
            t.PromoteFromTitle(0);   // a title the page doesn't offer as a button does nothing
            return Rendered(t);
        });
        (await r.Html()).Should().NotContain("tide-promote-form");
    }

    [Theory]
    [InlineData(FeedItemStatus.New, true)]
    [InlineData(FeedItemStatus.Shortlisted, true)]
    [InlineData(FeedItemStatus.Promoted, false)]
    [InlineData(FeedItemStatus.Dismissed, false)]
    [InlineData(FeedItemStatus.Duplicate, false)]
    public void OnlyTheInboxesStillToDecide_PromoteFromTheTitle(FeedItemStatus status, bool expected) =>
        Triage.Promotable(status).Should().Be(expected);

    // An older item (so it doesn't lead the inbox), its id; its CVE's record when given.
    private async Task<Guid> SeedAt(string key, FeedItemStatus status, string cve, CveRecord? record = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        var source = new Source { Name = "Promote source " + key, Url = "https://promote.example/feed/" + key, DefaultTlp = TlpLevel.Green };
        var at = DateTimeOffset.UtcNow.AddDays(-2);
        var item = new FeedItem
        {
            Source = source, Title = TitleOf(key), Url = "https://promote.example/" + key, UrlHash = "promote-form-" + key, Cves = cve,
            PublishedAtUtc = at, FetchedAtUtc = at, Status = status,
        };
        db.AddRange(source, item);
        if (record is not null) db.Add(record);
        await db.SaveChangesAsync();
        return item.Id;
    }

    [Fact]
    public async Task TheAddress_OpensItsRowsPromoteForm_WithThePrioritySuggestedFromKev()
    {
        const string key = "kev";
        var id = await SeedAt(key, FeedItemStatus.Shortlisted, "CVE-2026-7777",
            new CveRecord { CveId = "CVE-2026-7777", IsKev = true, KevAddedDate = DateOnly.FromDateTime(DateTime.UtcNow), CvssScore = 10m });

        // As the brief's Add article picker links it: the Shortlisted tab, at this item, its form open.
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, "/" + AddArticlesDialog.PromoteHref(id));
        var html = await r.HtmlOnce("tide-promote-form");

        html.Should().Contain("aria-current=\"true\" aria-label=\"" + TitleOf(key) + "\"", "the row from the address is selected");
        var form = html[html.IndexOf("<form class=\"tide-promote-form", StringComparison.Ordinal)..];
        form = form[..form.IndexOf("</form>", StringComparison.Ordinal)];
        form.Should().Contain($"value=\"{TitleOf(key)}\"").And.Contain("<option value=\"P1\" selected")
            .And.Contain("aria-describedby=\"promote-suggested\"")
            .And.Contain("<span class=\"form-text m-0\" id=\"promote-suggested\">Suggested P1: on KEV, CVSS 10</span>");

        await r.Click(t => t.SubmitPromote());
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        var item = db.FeedItems.Single(i => i.Id == id);
        item.Status.Should().Be(FeedItemStatus.Promoted);
        db.Articles.Single(a => a.Id == item.ArticleId).Priority.Should().Be(Priority.P1, "the suggestion was kept");
    }

    [Fact]
    public async Task BackAtTheAddress_SelectsTheRow_OrTheOneInItsPlace_WithNoFormOpen()
    {
        const string key = "back";
        var id = await SeedAt(key, FeedItemStatus.Dismissed, "CVE-2026-7778");
        await SeedAt(key + "-2", FeedItemStatus.Dismissed, "CVE-2026-7779");   // so the tab has a second row

        await using (var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?tab=Dismissed&at={id}&promote=true"))
        {
            var html = await r.HtmlOnce(TitleOf(key));
            html.Should().Contain("aria-current=\"true\" aria-label=\"" + TitleOf(key) + "\"")
                .And.NotContain("tide-promote-form", "a dismissed item has nothing to promote")
                .And.Contain("Put back <span class=\"tide-kbd\">R</span>");
        }

        // The item has left the tab (promoted, say): the row now in its place is selected, not the first.
        await using var gone = await ComponentHarness.RenderAsync<Triage>(app, "/" + Triage.Address(FeedItemStatus.Dismissed, null, null, false, Guid.NewGuid(), 1));
        var rows = (await gone.HtmlOnce("tide-triage-row")).Split("<li class=\"tide-triage-row").Skip(1).ToList();
        rows.Count.Should().BeGreaterThan(1);
        rows[1].Should().StartWith(" selected", "row 1 is selected");
    }

    [Fact]
    public void TheAddress_HoldsTheTabFiltersAndRow()
    {
        var source = Guid.NewGuid();
        var item = Guid.NewGuid();
        Triage.Address(FeedItemStatus.Shortlisted, source, " vpn & ssl ", sinceLast: true, item, 3)
            .Should().Be($"triage?tab=shortlisted&source={source}&q=vpn%20%26%20ssl&since=true&at={item}&row=3");
        Triage.Address(FeedItemStatus.New, null, null, sinceLast: false, null, 0).Should().Be("triage?tab=new");
        AddArticlesDialog.PromoteHref(item).Should().Be($"triage?tab=shortlisted&at={item}&promote=true");
    }

    [Fact]
    public async Task TheAddArticlePicker_ListsTheShortlistFirst_AndHidesBriefedArticlesUntilAsked()
    {
        var shortId = await SeedAt("picker", FeedItemStatus.Shortlisted, "CVE-2026-7780");
        var source = new Source { Name = "Picker source", DefaultTlp = TlpLevel.Green };
        var item = new FeedItem { Id = shortId, Source = source, Title = "Shortlisted story", StatusByName = "Robin" };
        var now = DateTimeOffset.UtcNow;
        Tide.Application.Briefs.BriefCandidate Article(string title, ArticleStatus status) =>
            new(Guid.NewGuid(), title, Classification.Threat, Priority.P2, TlpLevel.Green, status, "Robin", now, null);
        await using var r = await ComponentHarness.RenderAsync<AddArticlesDialog>(app, "/briefs/today", new()
        {
            ["Candidates"] = (IReadOnlyList<Tide.Application.Briefs.BriefCandidate>)
                [Article("ready one", ArticleStatus.Ready), Article("presented one", ArticleStatus.Presented), Article("draft one", ArticleStatus.Draft)],
            ["Shortlist"] = (IReadOnlyList<Tide.Application.Intake.InboxRow>)[new(item, source, null, [])],
        });
        await r.Press(d => d.PickAsync());
        var html = await r.Html();

        html.Should().Contain(">Shortlisted in triage</h3>").And.Contain("Shortlisted story").And.Contain("shortlisted by Robin")
            .And.Contain($"href=\"triage?tab=shortlisted&amp;at={shortId}&amp;promote=true\" aria-label=\"Promote &#x201C;Shortlisted story&#x201D;\">Promote</a>")
            .And.Contain("for=\"add-briefed\">Show articles already briefed</label>")
            .And.Contain("drafts from the last 14 days and any carried").And.Contain("2 shown.")
            .And.Contain("ready one").And.Contain("draft one").And.NotContain("presented one", "presented articles wait behind the toggle");
        html.IndexOf("Shortlisted story", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("ready one", StringComparison.Ordinal));
    }
}
