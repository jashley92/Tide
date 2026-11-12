using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// An external team's action nobody in TIDE holds is named by its ticket (linked to an http or https address), or says it
/// has no ticket yet, on the board, the list, the open actions slide, the wrap-up and the team email; an internal team's
/// reads as before, and counting and filtering still go by the assignee. Its own app, so the open actions slide holds only
/// this class's.
/// </summary>
public class ExternalTicketHolderTests(TideApp app) : IClassFixture<TideApp>
{
    private const string Url = "https://itsm.example.org/INC-4411";
    private const string Link = $"<a class=\"tide-holder\" href=\"{Url}\" target=\"_blank\" rel=\"noopener noreferrer\" draggable=\"false\">INC-4411</a>";

    private DateOnly DaysBack(int n)
    {
        var day = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        for (var i = 0; i < n; i++) day = WorkingDays.Weekdays.Previous(day);
        return day;
    }

    private sealed record Seeded(string Tag, string External, ActionRow Ticketed, ActionRow Bare, ActionRow Inside);

    // An external team's two actions (one with a ticket, one without) and an internal team's, with a ticket of its own;
    // due <paramref name="due"/>, and raised on <paramref name="article"/> when there is one.
    private async Task<Seeded> Seed(string tag, DateOnly? due = null, Guid? article = null)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var teams = scope.ServiceProvider.GetRequiredService<TeamService>();
        var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
        var external = $"Hosting {tag}";
        var outside = await teams.AddExternalAsync(external, "noc@provider.example", confirmOutside: true);
        var inside = await teams.AddAsync($"Network {tag}", null, null);
        await actions.CreateAsync(new ActionForm { Title = $"Patch the hosted edge {tag}", TeamId = outside, DueDate = due, ArticleId = article,
            TicketRef = "INC-4411", TicketUrl = Url });
        await actions.CreateAsync(new ActionForm { Title = $"Rotate the hosted keys {tag}", TeamId = outside, DueDate = due, ArticleId = article });
        await actions.CreateAsync(new ActionForm { Title = $"Sweep the logs {tag}", TeamId = inside, DueDate = due, ArticleId = article, TicketRef = "CHG-1" });
        var rows = await actions.ListAsync(new ActionFilter(ActionView.Open) { Search = tag });
        ActionRow Titled(string start) => rows.Single(r => r.Title.StartsWith(start, StringComparison.Ordinal));
        return new(tag, external, Titled("Patch"), Titled("Rotate"), Titled("Sweep"));
    }

    private static string Card(string html, string title)
    {
        var at = html.IndexOf(title, StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, $"{title} is shown");
        var end = html.IndexOf("</article>", at, StringComparison.Ordinal);
        return html[at..(end < 0 ? html.IndexOf("</tr>", at, StringComparison.Ordinal) : end)];
    }

    [Fact]
    public async Task TheRows_CarryTheTicket_AndTheNoOwnerFilter_StillGoesByTheAssignee()
    {
        var s = await Seed("rows");
        (s.Ticketed.TicketRef, s.Ticketed.TicketUrl, s.Ticketed.TeamExternal).Should().Be(("INC-4411", (string?)Url, true));
        s.Ticketed.Holder("Unassigned").Text.Should().Be("INC-4411");

        await using var scope = SignedIn(app, SignIn.Analyst);
        var nobodys = await scope.ServiceProvider.GetRequiredService<ActionService>()
            .ListAsync(new ActionFilter(ActionView.Open, AssigneeId: ActionFilter.Unassigned) { Search = s.Tag });
        nobodys.Select(r => r.Id).Should().BeEquivalentTo([s.Ticketed.Id, s.Bare.Id, s.Inside.Id], "nobody in TIDE holds any of them");
    }

    [Fact]
    public async Task TheBoard_NamesAnExternalTeamsAction_ByItsTicket_Linked()
    {
        var s = await Seed("board");
        await using var r = await RenderAsync<ActionKanban>(app, "/actions?layout=board", new()
        {
            ["Board"] = new ActionBoard([s.Ticketed, s.Bare, s.Inside], true, new HashSet<Guid>()), ["Today"] = DaysBack(0),
        });
        var html = await r.Html();

        Card(html, s.Ticketed.Title).Should().Contain(Link).And.NotContain("Unassigned");
        Card(html, s.Bare.Title).Should().Contain("<span class=\"tide-holder\">no ticket yet</span>").And.Contain(s.External).And.NotContain("Unassigned");
        Card(html, s.Inside.Title).Should().Contain("aria-hidden=\"true\"></span> Unassigned <span").And.NotContain("tide-holder")
            .And.NotContain("CHG-1", "an internal team's is unchanged");
    }

    [Fact]
    public async Task TheList_NamesAnExternalTeamsAction_ByItsTicket_Linked()
    {
        var s = await Seed("list");
        await using var r = await RenderAsync<ActionTable>(app, "/actions", new()
        {
            ["Rows"] = (IReadOnlyList<ActionRow>)[s.Ticketed, s.Bare, s.Inside], ["Today"] = DaysBack(0),
        });
        var html = await r.Html();

        Card(html, s.Ticketed.Title).Should().Contain(Link).And.NotContain("Unassigned");
        Card(html, s.Bare.Title).Should().Contain("no ticket yet").And.NotContain("Unassigned");
        Card(html, s.Inside.Title).Should().Contain("Unassigned").And.NotContain("CHG-1");
    }

    [Fact]
    public async Task AHolderWithLinksOff_IsWordsOnly()
    {
        var label = HolderLabel.Of(null, null, true, "Hosting", "INC-4411", Url, "Unassigned");
        await using var r = await RenderAsync<HolderText>(app, "/", new() { ["Label"] = label, ["Links"] = false });
        (await r.Html()).Should().Contain("<span class=\"tide-holder\">INC-4411</span>").And.NotContain("href");
    }

    [Fact]
    public async Task TheOpenActionsSlide_AndTheWrapUp_NameTheTicket_AndTheRoomAsksThereForAnUpdate()
    {
        var day = DaysBack(31);
        Guid article;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var brief = await briefs.GetOrStartAsync(day);
            article = await articles.CreateAsync("Hosted edge zero-day", "Vendor", null, TlpLevel.Clear);
            await briefs.AddArticleAsync(brief.Id, article);
        }
        var s = await Seed("slide", day.AddDays(-3), article);
        // Nothing has happened to them for six days before the brief's.
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = new[] { s.Ticketed.Id, s.Bare.Id, s.Inside.Id };
            var then = new DateTimeOffset(day.AddDays(-6).ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero);
            await db.Actions.Where(a => ids.Contains(a.Id)).ExecuteUpdateAsync(u => u.SetProperty(a => a.CreatedAtUtc, then));
            await db.Set<ActionEvent>().Where(e => ids.Contains(e.ActionId)).ExecuteUpdateAsync(u => u.SetProperty(e => e.AtUtc, then));
        }

        await using var pat = await Open(app, day);
        var html = "";
        for (var i = 0; i < 6 && !html.Contains("<table class=\"tide-slide-open\"", StringComparison.Ordinal); i++)
        {
            if (i > 0) await pat.Click(p => p.OnPresentKey("next"));
            html = await pat.Html();
        }
        var at = html.IndexOf("<table class=\"tide-slide-open\"", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, "the open actions slide");
        var table = html[at..html.IndexOf("</table>", at, StringComparison.Ordinal)];
        string Row(string title)
        {
            var row = table[table.IndexOf(title, StringComparison.Ordinal)..];
            return System.Net.WebUtility.HtmlDecode(row[..row.IndexOf("</tr>", StringComparison.Ordinal)]);
        }
        Row(s.Ticketed.Title).Should().Contain(System.Net.WebUtility.HtmlDecode(Link)).And.Contain("Ask for an update on its ticket")
            .And.NotContain("nobody yet").And.NotContain("Reassign");
        Row(s.Bare.Title).Should().Contain($"<td>{s.External} <span class=\"tide-present-help\">· <span class=\"tide-holder\">no ticket yet</span>").And.Contain("Ask the team for an update").And.NotContain("Reassign");
        Row(s.Inside.Title).Should().Contain("no one: the team has no members").And.Contain("Reassign to a team with members").And.NotContain("CHG-1");

        await pat.Click(p => p.OnPresentKey("last"));
        var wrap = System.Net.WebUtility.HtmlDecode(await pat.Html());
        wrap = wrap[wrap.IndexOf("data-wrap=\"owners\"", StringComparison.Ordinal)..];
        wrap = wrap[..wrap.IndexOf("</section>", StringComparison.Ordinal)];
        wrap.Should().Contain(System.Net.WebUtility.HtmlDecode(Link) + " · " + s.External).And.Contain($"With {s.External} · no ticket yet")
            .And.Contain($"Network slide · nobody yet");
    }

    [Fact]
    public async Task TheActionOverASlide_NamesTheHolderBesideItsTeam_AndLinksOnlyWhereItMay()
    {
        var s = await Seed("card");
        ActionDetail ticketed, bare;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
            (ticketed, bare) = ((await actions.GetAsync(s.Ticketed.Number))!, (await actions.GetAsync(s.Bare.Number))!);
        }
        await using (var r = await RenderAsync<PresentAction>(app, "/present", new() { ["Detail"] = bare, ["Links"] = true }))
            (await r.Html()).Should().Contain("<dt>Assignee</dt><dd><span class=\"tide-holder\">no ticket yet</span></dd>", "the Team fact beside it names the team");
        await using (var r = await RenderAsync<PresentAction>(app, "/present", new() { ["Detail"] = ticketed, ["Links"] = true }))
            (await r.Html()).Should().Contain(Link);
        await using (var r = await RenderAsync<PresentAction>(app, "/present", new() { ["Detail"] = ticketed, ["Links"] = false }))
            (await r.Html()).Should().Contain("<span class=\"tide-holder\">INC-4411</span>").And.NotContain(Url, "a Visitor gets no links");
    }

    [Fact]
    public async Task TheTicketFact_LinksOnlyAnHttpOrHttpsAddress()
    {
        var a = new ActionItem { Number = 9101, Title = "Rotate", TicketRef = "INC-9", TicketUrl = "javascript:alert(1)", Team = new Team { Name = "Hosting" } };
        await using (var r = await RenderAsync<Tide.Web.Components.ActionRail.ActionFactsSection>(app, "/actions/9101", new() { ["A"] = a }))
            (await r.Html()).Should().Contain("<dt>Ticket</dt>").And.Contain("INC-9").And.NotContain("javascript:");
        a.TicketUrl = Url;
        await using (var r = await RenderAsync<Tide.Web.Components.ActionRail.ActionFactsSection>(app, "/actions/9101", new() { ["A"] = a }))
            (await r.Html()).Should().Contain($"href=\"{Url}\"");
    }

    [Fact]
    public async Task ThePresentersRail_NamesAnExternalTeamsAction_ByItsTicket_AndOffersNoOneToAssign()
    {
        var day = DaysBack(35);
        Guid article;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var brief = await briefs.GetOrStartAsync(day);
            article = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Hosted DNS hijack", "Vendor", null, TlpLevel.Clear);
            await briefs.AddArticleAsync(brief.Id, article);
        }
        var s = await Seed("rail", day.AddDays(5), article);

        await using var pat = await Open(app, day);
        var html = "";
        for (var i = 0; i < 8 && !html.Contains("id=\"rail-actions-h\"", StringComparison.Ordinal); i++)
        {
            if (i > 0) await pat.Click(p => p.OnPresentKey("next"));
            html = await pat.Html();
        }
        var at = html.IndexOf("id=\"rail-actions-h\"", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, "the presenter's rail lists the article's actions");
        var rail = html[at..html.IndexOf("</section>", at, StringComparison.Ordinal)];
        string Item(int number)
        {
            var from = rail.IndexOf($">A-{number}<", StringComparison.Ordinal);
            from.Should().BeGreaterThan(0, $"A-{number} is listed");
            var next = rail.IndexOf("class=\"tide-present-rail-action\"", from, StringComparison.Ordinal);
            return rail[from..(next < 0 ? rail.Length : next)];
        }

        Item(s.Ticketed.Number).Should().Contain(Link).And.NotContain($"Assign A-{s.Ticketed.Number}").And.NotContain("No one is on")
            .And.NotContain("Take it");
        Item(s.Bare.Number).Should().Contain("With " + s.External + " &#xB7; no ticket yet").And.NotContain($"Assign A-{s.Bare.Number}")
            .And.NotContain("No one is on");
        Item(s.Inside.Number).Should().Contain($"Assign A-{s.Inside.Number}").And.Contain("No one is on Network rail yet", "an internal team's is unchanged");
    }

    [Fact]
    public async Task TheTeamEmail_NamesTheTicket_InWordsOnly()
    {
        var day = DaysBack(33);
        Guid article;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var brief = await briefs.GetOrStartAsync(day);
            article = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Hosted mail flaw", "Vendor", null, TlpLevel.Clear);
            await briefs.AddArticleAsync(brief.Id, article);
        }
        var s = await Seed("email", day.AddDays(-2), article);

        await using var reader = SignedIn(app, SignIn.Analyst);
        var html = await reader.ServiceProvider.GetRequiredService<DistributionService>().PreviewEmailAsync(day, Edition.Team, id => $"/files/{id}");
        html.Should().Contain($"{s.External} &middot; INC-4411</span>").And.Contain($"{s.External} &middot; no ticket yet</span>")
            .And.NotContain("itsm.example.org", "an email names the ticket, it doesn't link it")
            .And.Contain("Network email &middot; no one: the team has no members</span>").And.NotContain("CHG-1");
    }
}
