using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Ending the meeting as a short checklist: the wrap-up is three lists (the recommended lines no action was raised for,
/// who is doing what, the articles not marked discussed), the same for the presenter and everyone following, and only
/// what the edition shows; the open actions slide says how long each has waited and one thing to do about it. Its own
/// app, so the open actions slide holds only this class's.
/// </summary>
public class PresentWrapUpTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:wrap-owner", "Olive Owner", "ActionOwner");

    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private DateOnly DaysBack(int n)
    {
        var day = Today;
        for (var i = 0; i < n; i++) day = WorkingDays.Weekdays.Previous(day);
        return day;
    }

    private sealed record Setup(DateOnly Day, string Team, int Held, int Nobodys, int Hidden);

    // A brief with a CLEAR article recommending two things (one raised, held by the tester; one action raised by hand and
    // left to nobody yet), a second article with nothing raised, and a RED one (above the team edition's ceiling) with an
    // action of its own.
    private async Task<Setup> Brief(int daysBack)
    {
        var day = DaysBack(daysBack);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
        var teamName = $"Wrap team {daysBack}";
        var team = await scope.ServiceProvider.GetRequiredService<TeamService>().AddAsync(teamName, null, null);

        async Task<Guid> Article(string title, TlpLevel tlp, string? recommended)
        {
            var id = await articles.CreateAsync(title, "Vendor", null, tlp);
            var form = ArticleForm.From((await articles.GetAsync(id))!);
            form.Bluf = "Exploited.";
            form.RecommendedActions = recommended;
            await articles.SaveAsync(form);
            return id;
        }
        var first = await Article($"Wrap gateway {daysBack}", TlpLevel.Clear, "- Patch both gateways\n- Ask the vendor for the fixed build");
        var second = await Article($"Wrap phishing {daysBack}", TlpLevel.Clear, null);
        var red = await Article($"Wrap secret {daysBack}", TlpLevel.Red, null);
        var brief = await briefs.GetOrStartAsync(day);
        foreach (var id in new[] { first, second, red }) await briefs.AddArticleAsync(brief.Id, id);

        var held = await actions.QuickAddAsync(first, "Patch the gateways tonight", ActionKind.PatchRemediate, team, null, day, recommendation: "Patch both gateways");
        var nobodys = await actions.CreateAsync(new ActionForm { Title = "Brief the service desk", Kind = ActionKind.Awareness, TeamId = team, ArticleId = first });
        var hidden = await actions.QuickAddAsync(red, "Rotate the secret keys", ActionKind.Other, team, null, day);
        return new(day, teamName, held, (await actions.GetAsync(nobodys))!.Action.Number, hidden);
    }

    /// <summary>The wrap-up's lists, from the first to the end of the slide, as read.</summary>
    private static string Lists(string html)
    {
        var at = html.IndexOf("<div class=\"tide-wrap\"", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, "the wrap-up shows its lists");
        var end = html.IndexOf("<div class=\"tide-slide-dock\"", at, StringComparison.Ordinal);
        return System.Net.WebUtility.HtmlDecode(html[at..end]);
    }

    private static string Section(string lists, string which)
    {
        var at = lists.IndexOf($"data-wrap=\"{which}\"", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, $"the {which} list is shown");
        var end = lists.IndexOf("</section>", at, StringComparison.Ordinal);
        return lists[at..end];
    }

    [Fact]
    public async Task TheWrapUp_IsThreeLists_NotRaised_WhoIsDoingWhat_NotDiscussed()
    {
        var s = await Brief(31);
        await using var pat = await Open(app, s.Day);
        await pat.Click(p => p.OnPresentKey("last"));
        var lists = Lists(await pat.Html());

        Section(lists, "unraised").Should().Contain("Recommended, with no action raised yet (1)")
            .And.Contain("<li>Ask the vendor for the fixed build</li>").And.NotContain("Patch both gateways");
        var owners = Section(lists, "owners");
        owners.Should().Contain("Who is doing what (2)").And.Contain($"Tester · {s.Team}").And.Contain($"{s.Team} · nobody yet")
            .And.Contain($"A-{s.Held}").And.Contain($"A-{s.Nobodys}").And.Contain("Patch the gateways tonight");
        owners.IndexOf($"A-{s.Held}", StringComparison.Ordinal).Should().BeLessThan(owners.IndexOf($"A-{s.Nobodys}", StringComparison.Ordinal),
            "a holder's actions come before the team's nobody has");
        Section(lists, "undiscussed").Should().Contain("Not marked discussed (2)").And.Contain("Wrap gateway 31").And.Contain("Wrap phishing 31");

        lists.Should().NotContain("Wrap secret 31", "the RED article is above the team edition's ceiling")
            .And.NotContain("Rotate the secret keys").And.NotContain($"A-{s.Hidden}");
    }

    [Fact]
    public async Task AFollower_SeesTheSameThreeLists_AndNoneOfTheRail_AndNothingAboveTheCeiling()
    {
        var s = await Brief(32);
        await using var pat = await Open(app, s.Day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(app, s.Day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await pat.Click(p => p.OnPresentKey("last"));
        await Do(owner, p => p.Follow());

        var theirs = await owner.Html();
        theirs.Should().NotContain("present-rail").And.NotContain("Not raised yet").And.NotContain("End the meeting");
        var lists = Lists(theirs);
        Section(lists, "unraised").Should().Contain("<li>Ask the vendor for the fixed build</li>");
        Section(lists, "owners").Should().Contain($"Tester · {s.Team}").And.Contain($"{s.Team} · nobody yet");
        Section(lists, "undiscussed").Should().Contain("Wrap phishing 32");
        var mine = Lists(await pat.Html());
        foreach (var which in new[] { "unraised", "owners", "undiscussed" })
            Section(lists, which).Should().Be(Section(mine, which), "the room sees one wrap-up");
        theirs.Should().NotContain("Wrap secret 32").And.NotContain("Rotate the secret keys");
        EndMeeting(app, s.Day);
    }

    [Fact]
    public async Task TheCompanyWideEdition_HasNoActions_OnItsWrapUp()
    {
        var s = await Brief(33);
        await using var pat = await Open(app, s.Day);
        await Do(pat, p => p.SetEdition(new ChangeEventArgs { Value = Edition.Public.ToString() }));
        await pat.Click(p => p.OnPresentKey("last"));
        var html = await pat.Html();
        html.Should().NotContain("data-wrap=\"owners\"").And.NotContain("Who is doing what").And.NotContain($"A-{s.Held}")
            .And.NotContain("Wrap secret 33");
    }

    [Fact]
    public async Task TheOpenActionsSlide_SaysHowLongEachHasWaited_AndOneThingToDo()
    {
        var day = DaysBack(35);
        Guid id;
        string title = "Wrap overdue sweep " + Guid.NewGuid().ToString("N")[..6];
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var brief = await briefs.GetOrStartAsync(day);
            await briefs.AddArticleAsync(brief.Id, await articles.CreateAsync("Wrap open 35", "Vendor", null, TlpLevel.Clear));
            var team = await scope.ServiceProvider.GetRequiredService<TeamService>().AddAsync("Wrap open team", null, null);
            var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
            var n = await actions.CreateAsync(new ActionForm { Title = title, Kind = ActionKind.IocSweep, Priority = Priority.P1, TeamId = team, DueDate = day.AddDays(-3) });
            id = (await actions.GetAsync(n))!.Action.Id;
            // Held by someone not on its team (it has no members): theirs all the same, as the action page says.
            var heldNumber = await actions.CreateAsync(new ActionForm { Title = "Wrap held sweep", Kind = ActionKind.IocSweep, Priority = Priority.P1, TeamId = team,
                AssigneeId = "dev:tester", AssigneeName = "Tester", DueDate = day.AddDays(-2) });
            var heldDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await heldDb.Actions.Where(a => a.Number == heldNumber).ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, ActionStatus.Blocked));
            // Overdue on a RED article, above the team edition's ceiling: neither shown nor counted.
            var red = await articles.CreateAsync("Wrap RED partner report", "Partner", null, TlpLevel.Red);
            await actions.CreateAsync(new ActionForm { Title = "Wrap red rotation", TeamId = team, ArticleId = red, DueDate = day.AddDays(-4) });

            // Nothing has happened to it for six days before the brief's.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var then = new DateTimeOffset(day.AddDays(-6).ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero);
            await db.Actions.Where(a => a.Id == id).ExecuteUpdateAsync(u => u.SetProperty(a => a.CreatedAtUtc, then));
            await db.Set<ActionEvent>().Where(e => e.ActionId == id).ExecuteUpdateAsync(u => u.SetProperty(e => e.AtUtc, then));
        }
        OutstandingAction mine;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            mine = (await scope.ServiceProvider.GetRequiredService<ActionService>().OutstandingAsync(day, TlpLevel.AmberStrict)).Rows.Single(o => o.Row.Id == id);
        var clock = app.Services.GetRequiredService<OrgClock>();
        mine.Row.QuietDays(day, clock.DayOf).Should().Be(6);

        await using var pat = await Open(app, day);
        string html = "";
        for (var i = 0; i < 6 && !html.Contains("<table class=\"tide-slide-open\"", StringComparison.Ordinal); i++)
        {
            if (i > 0) await pat.Click(p => p.OnPresentKey("next"));
            html = await pat.Html();
        }
        var at = html.IndexOf("<table class=\"tide-slide-open\"", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, "the open actions slide");
        var table = System.Net.WebUtility.HtmlDecode(html[at..html.IndexOf("</table>", at, StringComparison.Ordinal)]);
        table.Should().Contain("<th scope=\"col\">Waited</th>").And.Contain("<th scope=\"col\">Next</th>");
        var row = table[table.IndexOf(title, StringComparison.Ordinal)..];
        row = row[..row.IndexOf("</tr>", StringComparison.Ordinal)];
        row.Should().Contain("3 days late").And.Contain("no update for 6 days").And.Contain(mine.NextInTheRoom(6))
            .And.Contain("no one: the team has no members").And.Contain("Reassign to a team with members");

        var held = table[table.IndexOf("Wrap held sweep", StringComparison.Ordinal)..];
        held = held[..held.IndexOf("</tr>", StringComparison.Ordinal)];
        held.Should().Contain("· Tester").And.Contain("Ask what it's waiting on").And.NotContain("no members").And.NotContain("Reassign");

        html.Should().NotContain("Wrap red rotation").And.Contain("2 of 2 overdue shown. <a", "the RED one isn't counted, and the sentences are apart");
    }

    [Fact]
    public async Task TheRailsChecklist_SaysWhenAListIsEmpty()
    {
        var day = DaysBack(36);
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var brief = await briefs.GetOrStartAsync(day);
            await briefs.AddArticleAsync(brief.Id, await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Wrap empty 36", "Vendor", null, TlpLevel.Clear));
        }
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await pat.Click(p => p.OnPresentKey("last"));
        await pat.Click(p => p.OnPresentKey("prev"));
        await pat.Click(p => p.OnPresentKey("discussed"));
        await pat.Click(p => p.OnPresentKey("last"));
        var html = await pat.Html();
        var at = html.IndexOf("<aside id=\"present-rail\"", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0);
        var rail = html[at..html.IndexOf("</aside>", at, StringComparison.Ordinal)];
        rail.Should().Contain("Not raised yet (0)").And.Contain("Nothing left to raise.")
            .And.Contain("Not discussed (0)").And.Contain("Every article was discussed.");
        EndMeeting(app, day);
    }
}
