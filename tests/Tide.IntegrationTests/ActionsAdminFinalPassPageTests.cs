using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages.Admin;
using Tide.Web.Components.Shared;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>Final bug pass, the pages: a cleared meeting time is refused rather than said to be saved; an action's header
/// carries its article's marking; a closed hunt's card offers nothing new and says why.</summary>
public class ActionsAdminFinalPassPageTests(TideApp app) : IClassFixture<TideApp>
{
    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..6]}";

    [Fact]
    public async Task ABlankMeetingTime_IsRefusedOnTheField_AndNothingSaysSaved()
    {
        var was = app.Services.GetRequiredService<OrgClock>().MeetingTime;
        await using var r = await ComponentHarness.RenderAsync<OrganizationAdmin>(app, "/admin/organization");
        var toasts = r.Scope.ServiceProvider.GetRequiredService<ToastService>();

        await r.Click(c => c.MeetingBox!.OnChange(new ChangeEventArgs { Value = "" }));
        await r.Click(c => c.Save());

        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("id=\"org-meeting-error\"").And.Contain("The brief meets at is a 24-hour time such as 10:30.")
            .And.MatchRegex("id=\"org-meeting\"[^>]*aria-invalid=\"true\"|aria-invalid=\"true\"[^>]*id=\"org-meeting\"");
        toasts.Toasts.Should().NotContain(t => !t.IsError, "nothing was saved, so nothing says it was");
        app.Services.GetRequiredService<OrgClock>().MeetingTime.Should().Be(was);

        // Put right, it saves, and the message goes.
        await r.Click(c => c.MeetingBox!.OnChange(new ChangeEventArgs { Value = TimeBox.Format(was) }));
        await r.Click(c => c.Save());
        (await r.Html()).Should().NotContain("id=\"org-meeting-error\"");
        toasts.Toasts.Should().Contain(t => !t.IsError && t.Message == "Saved.");
    }

    [Fact]
    public async Task TheBriefsDeadlines_AreSetOnOrganizationSettings_AndABlankOneIsRefused()
    {
        var clock = app.Services.GetRequiredService<OrgClock>();
        var (sentBy, postedBy) = (clock.SentBy, clock.PostedBy);
        await using var r = await ComponentHarness.RenderAsync<OrganizationAdmin>(app, "/admin/organization");
        try
        {
            await r.Click(c => c.SentByBox!.OnChange(new ChangeEventArgs { Value = "" }));
            await r.Click(c => c.Save());
            (await r.Html()).Should().Contain("id=\"org-sent-by-error\"");
            clock.SentBy.Should().Be(sentBy);

            await r.Click(c => c.SentByBox!.OnChange(new ChangeEventArgs { Value = TimeBox.Format(new TimeOnly(11, 15)) }));
            await r.Click(c => c.PostedByBox!.OnChange(new ChangeEventArgs { Value = TimeBox.Format(new TimeOnly(12, 45)) }));
            await r.Click(c => c.Save());

            (clock.SentBy, clock.PostedBy).Should().Be((new TimeOnly(11, 15), new TimeOnly(12, 45)));
        }
        finally
        {
            await r.Click(c => c.SentByBox!.OnChange(new ChangeEventArgs { Value = TimeBox.Format(sentBy) }));
            await r.Click(c => c.PostedByBox!.OnChange(new ChangeEventArgs { Value = TimeBox.Format(postedBy) }));
            await r.Click(c => c.Save());
        }
    }

    [Fact]
    public async Task TodaysAddArticle_SaysWhatItOffers_InWords()
    {
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(Unique("A fresh draft"), "Vendor");

        var today = WebUtility.HtmlDecode(await app.CreateClient().GetStringAsync("/"));
        today.Should().NotContain("AvailableLabel", "the label is worked out, not written out")
            .And.MatchRegex(@"Add article \((\d+ ready, )?\d+ drafts?\)", "a new draft is on offer");
    }

    [Fact]
    public async Task AHuntCardThatHasntHeardItsActionClosed_SaysWhyEachAddIsRefused()
    {
        int number;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            var team = await sp.GetRequiredService<TeamService>().AddAsync(Unique("Stale hunters"), null, null);
            var actions = sp.GetRequiredService<ActionService>();
            number = await actions.CreateAsync(new ActionForm { Title = "Sweep the proxy", Kind = ActionKind.IocSweep, TeamId = team });
            await actions.AddIndicatorsAsync((await actions.GetAsync(number))!.Action.Id, [new IndicatorForm { Type = IndicatorType.IpAddress, Value = "203.0.113.41" }]);
        }
        ActionItem open;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            open = (await scope.ServiceProvider.GetRequiredService<ActionService>().GetAsync(number))!.Action;
        await using var r = await ComponentHarness.RenderAsync<HuntPanel>(app, "/actions/" + number, new() { ["Action"] = open, ["CanWork"] = true });
        var toasts = r.Scope.ServiceProvider.GetRequiredService<ToastService>();
        (await r.Html()).Should().Contain("Keep this query", "the open sweep's preview is shown");

        // Closed by a colleague; this card hasn't heard.
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<ActionService>().SetStatusAsync(open.Id, ActionStatus.Cancelled, ActionOutcome.None, null);

        var said = $"A-{number} is closed. Reopen it to add to it.";
        await r.Click(c => c.KeepPreview());
        toasts.Toasts.Should().Contain(t => t.IsError && t.Message == said, "Keep this query says why nothing was kept");
        await r.Click(async c => await c.AddIndicators(new IndicatorAdd.Addition([new IndicatorForm { Type = IndicatorType.IpAddress, Value = "203.0.113.42" }], [], false)));
        toasts.Toasts.Count(t => t.IsError && t.Message == said).Should().Be(2, "adding an indicator says so too");
    }

    [Fact]
    public async Task ARefusal_StaysOnScreen_WhenTheColleaguesChangeIsHeardAfterIt()
    {
        int number;
        Guid id;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            var team = await sp.GetRequiredService<TeamService>().AddAsync(Unique("Late hearers"), null, null);
            var actions = sp.GetRequiredService<ActionService>();
            number = await actions.CreateAsync(new ActionForm { Title = "Hunt the loader", Kind = ActionKind.ThreatHunt, TeamId = team });
            id = (await actions.GetAsync(number))!.Action.Id;
        }
        await using var page = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number });
        var toasts = page.Scope.ServiceProvider.GetRequiredService<ToastService>();
        var said = $"A-{number} is closed. Reopen it to add to it.";
        toasts.Error(said);   // as the hunt card says it, refused because a colleague closed the action

        await page.Click(p => p.OnChangedElsewhere([new Tide.Application.Abstractions.Change(Tide.Application.Abstractions.ChangeFeed.Action(id), "dev:robin", "Robin", Guid.NewGuid())]));

        toasts.Toasts.Should().Contain(t => t.IsError && t.Message == said, "news of the colleague's change doesn't clear the refusal")
            .And.Contain(t => !t.IsError && t.Message == "Robin updated this action.");
    }

    [Fact]
    public async Task AnActionsHeader_CarriesItsArticlesMarking_FromAmberUp()
    {
        int red, clear;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            var team = await sp.GetRequiredService<TeamService>().AddAsync(Unique("Marked team"), null, null);
            var articles = sp.GetRequiredService<ArticleService>();
            var actions = sp.GetRequiredService<ActionService>();
            red = await actions.CreateAsync(new ActionForm { Title = "From a partner's report", TeamId = team,
                ArticleId = await articles.CreateAsync(Unique("Partner breach"), "Partner", null, TlpLevel.Red) });
            clear = await actions.CreateAsync(new ActionForm { Title = "From a public report", TeamId = team,
                ArticleId = await articles.CreateAsync(Unique("Public advisory"), "Vendor", null, TlpLevel.Clear) });
        }

        var page = WebUtility.HtmlDecode(await app.CreateClient().GetStringAsync($"/actions/{red}"));
        var head = page[page.IndexOf("tide-action-badges", StringComparison.Ordinal)..page.IndexOf("tide-action-who", StringComparison.Ordinal)];
        head.Should().Contain("title=\"Raised from a TLP:RED article.").And.Contain(">TLP:RED<");
        var plain = WebUtility.HtmlDecode(await app.CreateClient().GetStringAsync($"/actions/{clear}"));
        plain[plain.IndexOf("tide-action-badges", StringComparison.Ordinal)..plain.IndexOf("tide-action-who", StringComparison.Ordinal)]
            .Should().NotContain("TLP:", "CLEAR goes without saying");
    }

    [Fact]
    public async Task AClosedHuntsCard_OffersNoNewQuery_AndSaysWhy()
    {
        int number;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            var team = await sp.GetRequiredService<TeamService>().AddAsync(Unique("Hunters"), null, null);
            var actions = sp.GetRequiredService<ActionService>();
            number = await actions.CreateAsync(new ActionForm { Title = "Hunt for the loader", Kind = ActionKind.ThreatHunt, TeamId = team });
            var id = (await actions.GetAsync(number))!.Action.Id;
            await actions.SetStatusAsync(id, ActionStatus.Done, ActionOutcome.HitsEscalated, "Found on two hosts");
        }

        var page = WebUtility.HtmlDecode(await app.CreateClient().GetStringAsync($"/actions/{number}?tool=hunt"));
        page.Should().Contain(HuntPanel.Closed).And.NotContain("Write one by hand").And.NotContain("Add indicators").And.NotContain("Generate from");
    }
}
