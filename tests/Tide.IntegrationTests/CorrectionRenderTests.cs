using System.Net;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>The correction path as an analyst gets it: from the send log and the sent brief, through the form and its
/// confirmation, to the correction under its send in the log.</summary>
public class CorrectionRenderTests(TideApp app) : IClassFixture<TideApp>
{
    private DateOnly Today => TestDb.SendDayFor(app.Services.GetRequiredService<OrgClock>());

    // Today's sample brief sent company-wide, once for the app.
    private async Task<BriefSend> Sent()
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        if (await db.BriefSends.AsNoTracking().FirstOrDefaultAsync(s => s.Edition == Edition.Public && !s.IsTest) is { } done) return done;
        await sp.GetRequiredService<DistributionSettings>().SaveRecipientsAsync(Edition.Public, "everyone@example.org");
        var sends = sp.GetRequiredService<DistributionService>();
        return await sends.SendAsync(await sends.PlanAsync(await Tide.IntegrationTests.Audit.WebSeed.SendableDayAsync(sp), Edition.Public), withoutMeeting: true);
    }

    [Fact]
    public async Task TheSendLog_OffersACorrection_AndOpensTheFormInTheEditionsPlace()
    {
        var send = await Sent();
        await using var r = await RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = Today });

        var html = await r.Html();
        html.Should().Contain(">Send a correction</button>");
        await r.Click(d => { d.StartCorrection(send.Id); return Task.CompletedTask; });
        var form = await r.HtmlOnce("The articles it");
        form.Should().Contain("id=\"correct-h\"").And.Contain("Correct the company-wide edition sent").And.Contain("id=\"kind-slip\"")
            .And.Contain("What changed (required)").And.NotContain("id=\"send-team-h\"", "the form takes the editions' place")
            .And.NotContain(">Send a correction</button>", "one correction at a time");
        r.Component.Correcting.Should().Be(send.Id);
    }

    [Fact]
    public async Task TheForm_ConfirmsWhatGoes_HoldsTheButtonForTheTick_AndTheLogShowsTheCorrectionUnderItsSend()
    {
        var send = await Sent();
        await using var r = await RenderAsync<CorrectionForm>(app, "/", new() { ["SendId"] = send.Id });
        await r.HtmlOnce("The articles it");
        var section = r.Component.Start!.Sections[0];

        await r.Click(f => { f.Pick(section, true); f.Write("A date in the first article was wrong."); return Task.CompletedTask; });
        await r.Click(f => f.Plan());
        var html = await r.Html();
        var plan = r.Component.Confirmation!;
        html.Should().Contain("What goes").And.Contain("To the 1 address the send went to:").And.Contain("everyone@example.org")
            .And.Contain("tide-correction-preview").And.Contain("A date in the first article was wrong.")
            .And.Contain(WebUtility.HtmlEncode(CorrectionForm.CheckedLabel(plan)).Replace("&#39;", "&#x27;", StringComparison.Ordinal))
            .And.Contain("btn btn-primary btn-sm\" disabled").And.NotContain("id=\"correct-recall\"");
        CorrectionForm.Label(plan).Should().Be("Send the correction to 1");

        await r.Click(f => { f.Tick(true); return Task.CompletedTask; });
        (await r.Html()).Should().NotContain("btn btn-primary btn-sm\" disabled");
        Task<bool>? sent = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { sent = r.Pressed(f => f.SendNow(plan)) as Task<bool>; });
        (await sent!).Should().BeTrue();

        await using var log = await RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = Today });
        var logHtml = await log.Html();
        var row = logHtml.IndexOf("tide-log-correction", StringComparison.Ordinal);
        row.Should().BeGreaterThan(logHtml.IndexOf($"sends/{send.Id}/email", StringComparison.Ordinal), "a correction is listed under its send");
        logHtml[row..].Should().Contain(">Correction</span>").And.Contain("corrections/").And.Contain("View as sent");

        var id = logHtml[(logHtml.IndexOf("corrections/", row, StringComparison.Ordinal) + 12)..][..36];
        var res = await app.CreateClient().GetAsync($"/corrections/{id}/email");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        res.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("sandbox");
        (await res.Content.ReadAsStringAsync()).Should().Contain("A date in the first article was wrong.");
    }

    [Fact]
    public async Task ASlip_AsksForTheSecondTick_AndWarnsNotToRestateTheContent()
    {
        var send = await Sent();
        await using var r = await RenderAsync<CorrectionForm>(app, "/", new() { ["SendId"] = send.Id });
        await r.HtmlOnce("The articles it");

        await r.Click(f => { f.SetSlip(true); return Task.CompletedTask; });
        var html = await r.Html();
        html.Should().Contain("A note (optional)").And.Contain("id=\"correct-note-warn\"").And.Contain("incident process").And.Contain("id=\"correct-name\"");
        await r.Click(f => f.Plan());
        html = await r.Html();
        html.Should().Contain("id=\"correct-recall\"").And.Contain("Send the slip notice to 1").And.Contain("btn btn-danger btn-sm\" disabled");
        await r.Click(f => { f.Tick(true); return Task.CompletedTask; });
        (await r.Html()).Should().Contain("btn btn-danger btn-sm\" disabled", "the slip also waits for the second tick");
        await r.Click(f => { f.Tick(true, noRecall: true); return Task.CompletedTask; });
        (await r.Html()).Should().NotContain("btn btn-danger btn-sm\" disabled");
    }

    [Fact]
    public async Task ASentBrief_OffersACorrectionOfEachSend_WhichOpensTheDialogOnIt()
    {
        var send = await Sent();
        await using var r = await RenderAsync<BriefPage>(app, "/", new() { ["Day"] = $"{Today:yyyy-MM-dd}", ["AsToday"] = true });
        (await r.HtmlOnce("This brief has been sent")).Should().Contain("Send a correction of the company-wide edition");

        await r.Press(p => p.AskCorrection(send.Id));
        (await r.HtmlOnce("The articles it")).Should().Contain("role=\"dialog\"").And.Contain("Correct the company-wide edition sent");
    }

    [Fact]
    public async Task ACorrectionWhoseOutcomeIsntKnown_IsMarkedFromTheLog()
    {
        var send = await Sent();
        var id = Guid.NewGuid();
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BriefCorrections.Add(new BriefCorrection
            {
                Id = id, CorrectsSendId = send.Id, BriefId = send.BriefId, BriefDate = send.BriefDate, Edition = send.Edition, Note = "Unsettled.",
                Subject = "Correction: " + send.Subject, Recipients = send.Recipients, RecipientCount = send.RecipientCount, SentAtUtc = DateTimeOffset.UtcNow,
                SentById = "x", SentByName = "X", Status = SendStatus.Unknown, Error = "Sending failed unexpectedly.", EmailHtml = "<p>x</p>", WebHtml = "<p>x</p>",
            });
            await db.SaveChangesAsync();
        }
        await using var r = await RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = send.BriefDate });

        var html = await r.Html();
        var row = html.IndexOf($"corrections/{id}/email", StringComparison.Ordinal);
        row.Should().BePositive();
        html[row..].Should().Contain("It went: mark sent").And.Contain(": mark not sent");

        await r.Click(d => d.SettleCorrection(id, went: false));
        await r.Click(d => d.SettleCorrection(id, went: false));   // again: refused, as a toast, not an error out of the handler
        html = await r.Html();
        html[html.IndexOf($"corrections/{id}/email", StringComparison.Ordinal)..].Should().Contain("checked and marked it not sent");
    }

    [Fact]
    public async Task MarkingASend_IsRefusedAsAToast_WhateverTheReason()
    {
        var send = await Sent();
        // Someone whose sending rights went mid-session: refused (ForbiddenException), as a toast, the circuit kept.
        await using var r = await RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = send.BriefDate },
            new SignIn("dev:settle-owner", "Owner", "ActionOwner"));
        await r.Click(d => d.Settle(send.Id, went: true));
        await r.Click(d => d.SettleCorrection(Guid.NewGuid(), went: true));
        (await r.Html()).Should().Contain("Send log");
    }

    [Fact]
    public async Task TheListNote_SaysHowTheEditionsListDiffers()
    {
        var plan = new CorrectionPlan(new CorrectionRequest(Guid.NewGuid(), false, [], "x"), Today, Edition.Public, TlpLevel.Clear, TlpLevel.Clear,
            "Correction: brief", ["a@example.org", "b@example.org"], 0, 0, [], "", true, "f");
        CorrectionForm.ListNote(plan).Should().BeNull();
        CorrectionForm.ListNote(plan with { NewOnList = 2, OffList = 1 }).Should()
            .Be("The edition's list has changed since: 2 addresses on the list now didn't get the send, so won't get this; 1 that got the send is no longer on the list, and still gets this.");
        await Task.CompletedTask;
    }
}

/// <summary>The send log says plainly which sends a TLP slip withdrew, and how the corrected edition is published.</summary>
public class WithdrawnSendLogTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task ASendTheSlipWithdrew_SaysSoInTheLog_AndTheEditionSentAgainDoesnt()
    {
        DateOnly day;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            day = await Tide.IntegrationTests.Audit.WebSeed.SendableDayAsync(sp);
            await sp.GetRequiredService<DistributionSettings>().SaveRecipientsAsync(Edition.Public, "everyone@example.org");
            var sends = sp.GetRequiredService<DistributionService>();
            var corrections = sp.GetRequiredService<CorrectionService>();
            var send = await sends.SendAsync(await sends.PlanAsync(day, Edition.Public), withoutMeeting: true);
            var first = (await corrections.StartAsync(send.Id)).Sections[0];
            await corrections.SendAsync(await corrections.PlanAsync(new CorrectionRequest(send.Id, true, [new CorrectedSection(first.Number, first.ArticleId!.Value)], null)),
                acknowledgedNoRecall: true);
        }
        await using (var r = await RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = day }))
            (await r.Html()).Should().Contain(DistributePanel.WithdrawnNote);

        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var sends = scope.ServiceProvider.GetRequiredService<DistributionService>();
            await sends.SendAsync(await sends.PlanAsync(day, Edition.Public), withoutMeeting: true);
        }
        await using (var r = await RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = day }))
        {
            var html = await r.Html();
            (html.Length - html.Replace(DistributePanel.WithdrawnNote, "", StringComparison.Ordinal).Length).Should()
                .Be(DistributePanel.WithdrawnNote.Length, "only the send made before the slip is withdrawn");
        }
    }
}

/// <summary>
/// The published briefs as a Reader gets them once a day was corrected and another withdrawn by a TLP slip: the
/// correction above the edition, the slip's notice in place of the withdrawn one, and nothing of it in the list or filter.
/// </summary>
public class CorrectedPublishedTests(ReaderApp app) : IClassFixture<ReaderApp>
{
    private const string Slipped = "Harbour pilots share an amber-only tide table";

    private async Task<(DateOnly Corrected, DateOnly Withdrawn)> Setup()
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var today = await Tide.IntegrationTests.Audit.WebSeed.SendableDayAsync(sp);
        var before = WorkingDays.Weekdays.Previous(today);
        var db = sp.GetRequiredService<AppDbContext>();
        if (await db.BriefCorrections.AnyAsync()) return (today, before);

        await sp.GetRequiredService<DistributionSettings>().SaveRecipientsAsync(Edition.Public, "everyone@example.org");
        var briefs = sp.GetRequiredService<BriefService>();
        var article = await sp.GetRequiredService<ArticleService>().CreateAsync(Slipped, "Vendor", null, TlpLevel.Clear);
        var old = await briefs.GetOrStartAsync(before);
        await briefs.AddArticleAsync(old.Id, article);

        var sends = sp.GetRequiredService<DistributionService>();
        var corrections = sp.GetRequiredService<CorrectionService>();
        var slipped = await sends.SendAsync(await sends.PlanAsync(before, Edition.Public), withoutMeeting: true);
        var slip = await corrections.PlanAsync(new CorrectionRequest(slipped.Id, true, [new CorrectedSection(1, article)], null));
        (await corrections.SendAsync(slip, acknowledgedNoRecall: true)).Status.Should().Be(SendStatus.Sent);

        var send = await sends.SendAsync(await sends.PlanAsync(today, Edition.Public), withoutMeeting: true);
        var first = (await corrections.StartAsync(send.Id)).Sections[0];
        var fix = await corrections.PlanAsync(new CorrectionRequest(send.Id, false, [new CorrectedSection(first.Number, first.ArticleId!.Value)], "A vendor name was misspelled."));
        (await corrections.SendAsync(fix)).Status.Should().Be(SendStatus.Sent);
        return (today, before);
    }

    private async Task<string> Page(string path)
    {
        var res = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(path);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheList_MarksTheCorrectedDay_AndShowsTheWithdrawnOneWithoutItsContent()
    {
        await Setup();

        var html = await Page("/published");

        html.Should().Contain("bi-pencil-square").And.Contain("Corrected ").And.Contain("Withdrawn: something in it went above its audience's marking.")
            .And.NotContain(Slipped);
        (await Page("/published?q=" + Uri.EscapeDataString("misspelled"))).Should().Contain("1 published brief matches.", "a correction's words join its day's");
        (await Page("/published?q=" + Uri.EscapeDataString("Harbour pilots"))).Should().Contain("No matching briefs").And.NotContain(Slipped);
    }

    [Fact]
    public async Task TheCorrectedDay_ShowsTheCorrectionAboveTheEdition_AndTheWithdrawnDayOnlyTheNotice()
    {
        var (corrected, withdrawn) = await Setup();

        var day = await Page($"/published/{corrected:yyyy-MM-dd}");
        var fix = day.IndexOf("tide-published-correction", StringComparison.Ordinal);
        fix.Should().BePositive();
        fix.Should().BeLessThan(day.IndexOf("id=\"edition\"", StringComparison.Ordinal));
        day.Should().Contain("A vendor name was misspelled.").And.Contain("The edition it corrects");

        var gone = await Page($"/published/{withdrawn:yyyy-MM-dd}");
        gone.Should().Contain("tide-published-slip").And.Contain("cannot be recalled").And.NotContain(Slipped).And.NotContain("id=\"edition\"")
            .And.NotContain("The withdrawn send, as it went", "a Reader doesn't get the team's copy");
    }
}
