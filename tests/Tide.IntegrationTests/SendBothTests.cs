using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Distribution;
using Tide.Application.Reviews;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>The Send dialog's Send both: one confirmation for both editions, each still its own send.</summary>
internal static class SendBoth
{
    public const string Button = "id=\"send-both\"";

    // Today's sample brief, sendable now, with two addresses on the team list and one on the company-wide list.
    public static async Task<DateOnly> DayAsync(TideApp app, ReviewRule? rule = null)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var settings = scope.ServiceProvider.GetRequiredService<Tide.Application.Settings.DistributionSettings>();
        await settings.SaveRecipientsAsync(Edition.Team, "cti@example.org; soc@example.org");
        await settings.SaveRecipientsAsync(Edition.Public, "everyone@example.org");
        var day = await Tide.IntegrationTests.Audit.WebSeed.SendableDayAsync(scope.ServiceProvider);
        var briefs = scope.ServiceProvider.GetRequiredService<Tide.Application.Briefs.BriefService>();
        await briefs.MarkPresentedAsync((await briefs.GetOrStartAsync(day)).Id);   // the meeting was held
        if (rule is { } r) await scope.ServiceProvider.GetRequiredService<ReviewService>().SaveRuleAsync(r);
        return day;
    }

    // The button's opening tag, whether it's disabled or not, as it renders.
    public static string Tag(string html)
    {
        var at = html.IndexOf(Button, StringComparison.Ordinal);
        at.Should().BePositive("Send both is offered");
        var start = html.LastIndexOf("<button", at, StringComparison.Ordinal);
        return html[start..html.IndexOf('>', at)];
    }

    public static async Task<IReadOnlyList<BriefSend>> RealSendsAsync<T>(ComponentHarness.Rendered<T> r, DateOnly day)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        (await SendFromTodayTests.SignedIn(r).SendsAsync(day)).Where(s => !s.IsTest).ToList();
}

public class SendBothTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task SendBoth_ShowsBothPlansSideBySide_WaitsForEachTick_AndGoesAsTwoSends()
    {
        var day = await SendBoth.DayAsync(app);
        await using var r = await ComponentHarness.RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = day });
        var distribution = SendFromTodayTests.SignedIn(r);
        var team = await distribution.PlanAsync(day, Edition.Team);
        var company = await distribution.PlanAsync(day, Edition.Public);

        // The two plans, each with its marking, subject and recipient count, above the editions' own confirmations.
        var html = await r.Html();
        var card = html[html.IndexOf("id=\"send-both-h\"", StringComparison.Ordinal)..html.IndexOf("id=\"send-team-h\"", StringComparison.Ordinal)];
        var teamPlan = card[card.IndexOf("id=\"send-both-team-h\"", StringComparison.Ordinal)..card.IndexOf("id=\"send-both-public-h\"", StringComparison.Ordinal)];
        var companyPlan = card[card.IndexOf("id=\"send-both-public-h\"", StringComparison.Ordinal)..];
        teamPlan.Should().Contain("Security team edition</h4>").And.Contain($"Subject: <strong>{System.Net.WebUtility.HtmlEncode(team.Subject)}</strong>")
            .And.Contain("To 2 recipients").And.Contain("Marked <span");
        companyPlan.Should().Contain("Company-wide edition</h4>").And.Contain($"Subject: <strong>{System.Net.WebUtility.HtmlEncode(company.Subject)}</strong>")
            .And.Contain("To 1 recipient<").And.Contain("Marked <span");
        // Each tick is its own labelled checkbox, described by the plan it confirms.
        teamPlan.Should().Contain("id=\"send-both-team-checked\"").And.Contain("aria-describedby=\"send-both-team-plan\"")
            .And.Contain("<label class=\"form-check-label small\" for=\"send-both-team-checked\">Team edition checked</label>");
        companyPlan.Should().Contain("id=\"send-both-public-checked\"").And.Contain("aria-describedby=\"send-both-public-plan\"")
            .And.Contain("<label class=\"form-check-label small\" for=\"send-both-public-checked\">Company-wide checked</label>");
        System.Net.WebUtility.HtmlDecode(card).Should().Contain("Send both (2 + 1)</button>");

        // Disabled until both are ticked: one isn't enough, either way round.
        SendBoth.Tag(html).Should().Contain("disabled");
        await r.Click(d => { d.TickBoth(Edition.Team, true); return Task.CompletedTask; });
        SendBoth.Tag(await r.Html()).Should().Contain("disabled", "the company-wide edition isn't checked");
        await r.Click(d => { d.TickBoth(Edition.Team, false); d.TickBoth(Edition.Public, true); return Task.CompletedTask; });
        SendBoth.Tag(await r.Html()).Should().Contain("disabled", "the team edition isn't checked");
        r.Component.Ticked(Edition.Public).Should().BeFalse("Send both's tick isn't the edition's own");
        Task<bool>? early = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { early = r.Pressed(d => d.SendBoth()) as Task<bool>; });
        (await early!).Should().BeFalse();
        (await SendBoth.RealSendsAsync(r, day)).Should().BeEmpty("a press with one tick sends nothing");

        await r.Click(d => { d.TickBoth(Edition.Team, true); d.TickBoth(Edition.Public, true); return Task.CompletedTask; });
        SendBoth.Tag(await r.Html()).Should().NotContain("disabled");

        Task<bool>? sent = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { sent = r.Pressed(d => d.SendBoth()) as Task<bool>; });
        (await sent!).Should().BeTrue();

        // Two sends, one per edition, each frozen and taken by the relay, the team edition first.
        var sends = await SendBoth.RealSendsAsync(r, day);
        sends.Should().HaveCount(2);
        sends.Select(s => s.Edition).Should().BeEquivalentTo([Edition.Team, Edition.Public]);
        sends.Should().OnlyContain(s => s.Status == SendStatus.Sent);
        sends.Single(s => s.Edition == Edition.Team).SentAtUtc.Should().BeOnOrBefore(sends.Single(s => s.Edition == Edition.Public).SentAtUtc);
        sends.Single(s => s.Edition == Edition.Team).RecipientCount.Should().Be(2);
        sends.Single(s => s.Edition == Edition.Public).RecipientCount.Should().Be(1);

        // The card says so and offers it no more: both went, so each is sent again on its own.
        html = await r.Html();
        html.Should().Contain("The security team edition went to 2 recipients. The company-wide edition went to 1 recipient.")
            .And.NotContain(SendBoth.Button).And.Contain("id=\"send-team-again\"").And.Contain("id=\"send-public-again\"");
        r.Component.BothDue.Should().BeFalse();
    }
}

/// <summary>Send both with review required: the team edition's AMBER article holds it, and nothing goes.</summary>
public class SendBothReviewHoldTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task AReviewHold_KeepsTheButtonDisabled_AndRefusesTheFirst_SoTheSecondIsntAttempted()
    {
        var day = await SendBoth.DayAsync(app, ReviewRule.AmberAndAbove);
        await using var r = await ComponentHarness.RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = day });

        await r.Click(d => { d.TickBoth(Edition.Team, true); d.TickBoth(Edition.Public, true); return Task.CompletedTask; });
        var html = await r.Html();
        html.Should().Contain("An edition holding a required one waits until it");
        SendBoth.Tag(html).Should().Contain("disabled", "the team edition holds an article that needs review");

        // Pressed all the same (a page from before the hold): the service refuses the first, and the second waits.
        Task<bool>? sent = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { sent = r.Pressed(d => d.SendBoth()) as Task<bool>; });
        (await sent!).Should().BeFalse();
        (await SendBoth.RealSendsAsync(r, day)).Should().BeEmpty("neither edition went");
        System.Net.WebUtility.HtmlDecode(await r.Html()).Should().Contain("The security team edition didn't go: Review is required before this edition goes")
            .And.Contain("The company-wide edition wasn't sent.");
    }
}

/// <summary>An app whose relay refuses every email.</summary>
public sealed class RefusingRelayApp : TideApp
{
    public FakeEmailSender Relay { get; } = new() { Result = new EmailSendResult(EmailSendStatus.Failed, "550 Mailbox unavailable") };

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder, s => s.AddSingleton<IEmailSender>(Relay));
    }
}

/// <summary>Send both when the relay refuses the first: the second isn't attempted, and the dialog says which went.</summary>
public class SendBothRelayFailureTests(RefusingRelayApp app) : IClassFixture<RefusingRelayApp>
{
    [Fact]
    public async Task TheFirstFailing_StopsTheSecond_AndTheDialogSaysWhichWentAndWhichDidnt()
    {
        var day = await SendBoth.DayAsync(app);
        await using var r = await ComponentHarness.RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = day });

        await r.Click(d => { d.TickBoth(Edition.Team, true); d.TickBoth(Edition.Public, true); return Task.CompletedTask; });
        Task<bool>? sent = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { sent = r.Pressed(d => d.SendBoth()) as Task<bool>; });
        (await sent!).Should().BeFalse();

        var sends = await SendBoth.RealSendsAsync(r, day);
        sends.Should().ContainSingle("the company-wide edition wasn't attempted").Which.Edition.Should().Be(Edition.Team);
        sends[0].Status.Should().Be(SendStatus.Failed);
        app.Relay.Sent.Should().ContainSingle().Which.To.Should().BeEquivalentTo(["cti@example.org", "soc@example.org"]);

        var html = System.Net.WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("The security team edition didn't go: ").And.Contain("The company-wide edition wasn't sent. Check the send log, then send it on its own below.");
        // A failed send didn't go, so both are still due, offered again with their ticks cleared.
        r.Component.BothDue.Should().BeTrue();
        r.Component.TickedForBoth(Edition.Team).Should().BeFalse();
        r.Component.TickedForBoth(Edition.Public).Should().BeFalse();
    }
}
