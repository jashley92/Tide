using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Exports;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Search's context rail on an indicator or CVE lookup: the hunt query, who is sweeping for it (with Take it where the
/// reader may), and the export are drawn in the rail, not in the page; the page keeps the answer (the first line, the
/// articles, the actions raised and "Seen with"). A search for words has no rail.
/// </summary>
public partial class SearchRailTests(TideApp app) : IClassFixture<TideApp>
{
    private const string Rail = "<aside id=\"tide-rail\"";

    private static SignIn Person(string role) => new($"dev:search-rail-{Guid.NewGuid():N}", $"Search rail {role}", role);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    private async Task<Guid> Article(string title, IEnumerable<(IndicatorType Type, string Value)> indicators, IEnumerable<string>? cves = null,
        TlpLevel tlp = TlpLevel.Amber)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var svc = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var id = await svc.CreateAsync(title, "Vendor", null, tlp);
        var form = ArticleForm.From((await svc.GetAsync(id))!);
        form.Bluf = "Summary.";
        form.Indicators = indicators.Select(i => new IndicatorForm { Type = i.Type, Value = i.Value }).ToList();
        form.Cves = (cves ?? []).ToList();
        await svc.SaveAsync(form);
        return id;
    }

    // A sweep raised from the article, for a new team of its own (with `member` in it, when given); its number.
    private async Task<int> Sweep(string title, Guid articleId, SignIn? member = null)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var teams = scope.ServiceProvider.GetRequiredService<TeamService>();
        var team = await teams.AddAsync(Unique("Rail team"), null, null);
        if (member is not null) await teams.AddMemberAsync(team, member.UserId, member.Name);
        return await scope.ServiceProvider.GetRequiredService<ActionService>().CreateAsync(new ActionForm
        {
            Title = title, Kind = ActionKind.IocSweep, TeamId = team, ArticleId = articleId,
        });
    }

    private Task<Rendered<Search>> Open(string q, SignIn who) => RenderAsync<Search>(app, "/search?q=" + Uri.EscapeDataString(q), @as: who);

    /// <summary>The page's markup and its rail's, split where MainLayout draws the rail (none: the whole page, and "").</summary>
    private static (string Page, string Rail) Split(string html)
    {
        var at = html.IndexOf(Rail, StringComparison.Ordinal);
        return at < 0 ? (html, "") : (html[..at], html[at..]);
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    private static string Words(string html) => Regex.Replace(WebUtility.HtmlDecode(Tags().Replace(html, " ")), @"\s+", " ").Trim();

    [Theory]
    [InlineData("Analyst")]
    [InlineData("ActionOwner")]
    [InlineData("Viewer")]
    public async Task AnIndicatorLookup_PutsTheHuntQueryTheSweepsAndTheExportInTheRail_AndTheAnswerInThePage(string role)
    {
        var domain = Unique("rail") + ".example.net";
        var other = Unique("travels") + ".example.net";
        var id = await Article("Rail lookup article", [(IndicatorType.Domain, domain), (IndicatorType.Domain, other)]);
        var sweep = await Sweep("Rail lookup sweep", id);

        await using var r = await Open(domain, Person(role));
        var (page, rail) = Split(await r.Html());

        rail.Should().StartWith(Rail + " class=\"tide-rail\" aria-label=\"Do something with it\"");
        rail.Should().Contain("id=\"lookup-query-h\"").And.Contain("id=\"lookup-query\"").And.Contain("aria-label=\"Query language\"")
            .And.Contain("data-copy-target=\"lookup-query\"").And.Contain("id=\"lookup-travels\"")
            .And.Contain("id=\"lookup-sweeps-h\"").And.Contain("id=\"lookup-sweeping\"").And.Contain("Rail lookup sweep").And.Contain($"href=\"actions/{sweep}\"")
            .And.Contain("id=\"lookup-export-h\"").And.Contain($"exports/lookup/{ExportService.LookupKey(domain)}?format=stix")
            .And.Contain($"exports/lookup/{ExportService.LookupKey(domain)}?format=csv").And.Contain("aria-label=\"Highest TLP to include\"");

        page.Should().Contain("id=\"lookup-summary\"").And.Contain("tide-lookup-articles").And.Contain("id=\"lookup-actions\"").And.Contain("id=\"lookup-seen-with\"");
        page.Should().NotContain("id=\"lookup-query\"").And.NotContain("lookup-query-tools").And.NotContain("lookup-sweeping")
            .And.NotContain("lookup-export").And.NotContain("exports/lookup").And.NotContain("tide-lookup\"", "the page is one column beside the rail");
    }

    [Theory]
    [InlineData("Analyst")]
    [InlineData("Viewer")]
    public async Task TheExport_StartsAtTheLeastSensitiveCeiling_SoAWiderOneIsChosen(string role)
    {
        var domain = Unique("ceiling") + ".example.net";
        await Article("Rail ceiling article", [(IndicatorType.Domain, domain)], tlp: TlpLevel.Red);

        await using var r = await Open(domain, Person(role));
        var rail = Split(await r.Html()).Rail;

        rail.Should().MatchRegex("<select[^>]*aria-label=\"Highest TLP to include\"[^>]*value=\"Clear\"|<select[^>]*value=\"Clear\"[^>]*aria-label=\"Highest TLP to include\"",
            "the ceiling starts at TLP:CLEAR");
        var key = ExportService.LookupKey(domain);
        rail.Should().Contain($"exports/lookup/{key}?format=stix&amp;tlp=clear").And.Contain($"exports/lookup/{key}?format=csv&amp;tlp=clear")
            .And.NotContain("tlp=red").And.NotContain("tlp=amber");
    }

    [Fact]
    public async Task ACveLookup_PutsTheSweepsOfItsArticlesAndTheExportInTheRail_AndHasNoHuntQuery()
    {
        var cve = $"CVE-2032-{Random.Shared.Next(100000, 999999)}";
        var id = await Article("Rail CVE article", [(IndicatorType.Domain, Unique("railcve") + ".example.net")], [cve]);
        await Sweep("Rail CVE sweep", id);

        await using var r = await Open(cve, Person("Analyst"));
        var (page, rail) = Split(await r.Html());

        rail.Should().Contain("id=\"lookup-swept\"").And.Contain("Rail CVE sweep").And.Contain("A hunt doesn't search for a CVE")
            .And.Contain($"exports/lookup/{ExportService.LookupKey(cve)}?format=stix").And.NotContain("lookup-query");
        page.Should().Contain("id=\"lookup-summary\"").And.Contain("tide-lookup-articles").And.NotContain("lookup-swept").And.NotContain("exports/lookup");
    }

    [Fact]
    public async Task AnUnseenValue_SaysNobodyHasSweptForIt_AndOffersNoExport()
    {
        await using var r = await Open(Unique("unseen") + ".example.net", Person("Analyst"));
        var (_, rail) = Split(await r.Html());
        Words(rail).Should().Contain("Nobody has swept for it yet").And.Contain("Hunt query");
        rail.Should().NotContain("lookup-export-h", "there's nothing to export");
    }

    [Fact]
    public async Task ASearchForWords_HasNoRail()
    {
        await Article($"Rail words {Unique("quillfeather")}", [(IndicatorType.Domain, Unique("words") + ".example.net")]);
        await using var r = await Open("quillfeather", Person("Analyst"));
        (await r.Html()).Should().NotContain(Rail).And.NotContain("tide-rail-toggle");
    }

    [Fact]
    public async Task IncludeWhatItTravelsWith_AddsTheIndicatorsSeenWithIt_ToTheQuery()
    {
        var domain = Unique("carry") + ".example.net";
        var other = Unique("companion") + ".example.net";
        await Article("Rail travels article", [(IndicatorType.Domain, domain), (IndicatorType.Domain, other)]);

        await using var r = await Open(domain, Person("Viewer"));
        var before = await r.Html();
        Inside(before, "lookup-query").Should().Contain(domain).And.NotContain(other);
        before.Should().MatchRegex("id=\"lookup-travels\"[^>]*aria-pressed=\"false\"");

        await r.Click(async p =>
        {
            await p.IncludeTravels(true);
            await p.SetParametersAsync(ParameterView.Empty);
        });

        var after = await r.Html();
        Inside(after, "lookup-query").Should().Contain(domain).And.Contain(other);
        after.Should().MatchRegex("id=\"lookup-travels\"[^>]*aria-pressed=\"true\"");
    }

    private static string Inside(string html, string id)
    {
        var m = Regex.Match(html, $"<(\\w+)[^>]*id=\"{id}\"[^>]*>(.*?)</\\1>", RegexOptions.Singleline);
        m.Success.Should().BeTrue($"the page has #{id}");
        return m.Groups[2].Value;
    }

    [Fact]
    public async Task TakeIt_IsOfferedToWhoeverMayWorkTheSweep_AndTakesItThroughTheCheckedPath()
    {
        var owner = Person("ActionOwner");
        var domain = Unique("take") + ".example.net";
        var id = await Article("Rail take article", [(IndicatorType.Domain, domain)]);
        var number = await Sweep("Rail take sweep", id, member: owner);

        static bool Offers(string rail) => rail.Contains("tide-lookup-take", StringComparison.Ordinal);
        await using (var viewer = await Open(domain, Person("Viewer"))) Offers(Split(await viewer.Html()).Rail).Should().BeFalse("a viewer changes nothing");
        await using (var stranger = await Open(domain, Person("ActionOwner"))) Offers(Split(await stranger.Html()).Rail).Should().BeFalse("not their team's");

        await using var r = await Open(domain, owner);
        var rail = Split(await r.Html()).Rail;
        Offers(rail).Should().BeTrue("a member of the sweep's team may take it on");
        Words(rail).Should().Contain("Nobody has swept for it yet").And.Contain($"Open A-{number}");

        await r.Click(p => p.Take(p.Sweeps.Single(a => a.Number == number)));

        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var detail = (await scope.ServiceProvider.GetRequiredService<ActionService>().GetAsync(number))!;
            detail.Action.AssigneeId.Should().Be(owner.UserId);
            detail.Action.Status.Should().Be(ActionStatus.InProgress);
            await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
            (await db.ActionEvents.CountAsync(e => e.ActionId == detail.Action.Id && e.Text.StartsWith("Took it on"))).Should().Be(1, "taking it is in the action's history");
        }
        var taken = Split(await r.Html()).Rail;
        Offers(taken).Should().BeFalse("it's theirs now");
        Words(taken).Should().Contain("Who is sweeping for it").And.Contain(owner.Name);
    }

    [Fact]
    public async Task TakeIt_OnASweepSomeoneElseTookMeanwhile_IsRefused_AndTheyKeepIt()
    {
        var owner = Person("ActionOwner");
        var domain = Unique("stale") + ".example.net";
        var id = await Article("Rail stale article", [(IndicatorType.Domain, domain)]);
        var number = await Sweep("Rail stale sweep", id, member: owner);

        await using var r = await Open(domain, owner);
        var sweep = r.Component.Sweeps.Single(a => a.Number == number);
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
            // A colleague on the team takes it first.
            await scope.ServiceProvider.GetRequiredService<TeamService>()
                .AddMemberAsync((await actions.GetAsync(number))!.Action.TeamId, SignIn.Analyst.UserId, SignIn.Analyst.Name);
            await actions.TakeAsync(sweep.Id);
        }

        await r.Click(p => p.Take(sweep));

        await using (var scope = SignedIn(app, SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<ActionService>().GetAsync(number))!.Action.AssigneeId.Should().Be(SignIn.Analyst.UserId);
        Split(await r.Html()).Rail.Should().NotContain("tide-lookup-take", "the page has read it again: it's someone's now");
    }
}
