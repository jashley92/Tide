using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Trends;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>Reports and the Trends tiles, as the pages render them (EP-22).</summary>
public class ReportsPageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task Reports_ShowsEachSection_WithAssessmentsAndLinksIntoTrends()
    {
        await using var r = await ComponentHarness.RenderAsync<Reports>(app, "/reports?p=2026-10");
        var html = System.Net.WebUtility.HtmlDecode(await r.Html());

        html.Should().Contain("Monthly report · October 2026").And.Contain("Compared with Sep 2026.")
            .And.Contain("1 · Headlines").And.Contain("2 · Outcomes").And.Contain("3 · Opening metrics").And.Contain("4 · Topics and techniques")
            .And.Contain("5 · Actions raised by team").And.Contain("id=\"assess-headlines\"").And.Contain("id=\"assess-teams\"")
            .And.Contain("href=\"trends?p=2026-10&view=tags&line=articles%3Aall&at=2026-10\"").And.Contain("href=\"reports/export.csv?p=2026-10\"");
    }

    [Fact]
    public async Task Reports_ByWeek_AndItsCsv()
    {
        await using (var r = await ComponentHarness.RenderAsync<Reports>(app, "/reports?p=2026-W44"))
            System.Net.WebUtility.HtmlDecode(await r.Html()).Should().Contain("Weekly report · Week of Oct 26").And.Contain("btn-primary\" aria-pressed");

        var csv = await app.CreateClient().GetStringAsync("/reports/export.csv?p=2026-Q4");
        csv.Should().StartWith("section,name,current,previous").And.Contain("Headlines,Articles briefed,");
    }

    [Fact]
    public async Task Reports_SwitchingKind_FromAPeriodToDate_GoesToTheOneHoldingToday_NotOneNotStarted()
    {
        // This month to date, switched to Week, went to the week holding the month's last day: one that hasn't started.
        var clock = app.Services.GetRequiredService<Tide.Application.Org.OrgClock>();
        var month = Tide.Application.Trends.Period.Of(Tide.Application.Trends.PeriodKind.Month, clock.Today);
        await using var r = await ComponentHarness.RenderAsync<Reports>(app, $"/reports?p={month.Key}");

        var week = await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Switched(Tide.Application.Trends.PeriodKind.Week));
        week.Should().Be(Tide.Application.Trends.Period.Of(Tide.Application.Trends.PeriodKind.Week, clock.Today));
        var past = new Tide.Application.Trends.Period(Tide.Application.Trends.PeriodKind.Month, 2026, 9);
        await using var september = await ComponentHarness.RenderAsync<Reports>(app, $"/reports?p={past.Key}");
        (await september.Renderer.Dispatcher.InvokeAsync(() => september.Component.Switched(Tide.Application.Trends.PeriodKind.Week)))
            .Should().Be(Tide.Application.Trends.Period.Of(Tide.Application.Trends.PeriodKind.Week, past.End));
    }

    [Fact]
    public async Task AnAssessment_StaysWithItsPeriod_AndTheNextPeriodStartsEmpty()
    {
        // Earlier and Later keep the same page and change only ?p=; October's assessment showed, and printed, under November.
        await using var r = await ComponentHarness.RenderAsync<Reports>(app, "/reports?p=2026-10");
        await r.Click(c =>
        {
            c.Note("headlines", "October was quiet");
            return c.SetParametersAsync(Microsoft.AspNetCore.Components.ParameterView.Empty);
        });
        (await r.Html()).Should().Contain("October was quiet", "the same period drawn again keeps what the lead wrote");

        await r.Click(c =>
        {
            c.PeriodParam = "2026-11";
            return c.SetParametersAsync(Microsoft.AspNetCore.Components.ParameterView.Empty);
        });
        var november = System.Net.WebUtility.HtmlDecode(await r.Html());
        november.Should().Contain("Monthly report · November 2026").And.NotContain("October was quiet");
    }

    [Fact]
    public async Task EachAssessment_OffersADraft_AndAUsedDraftFillsTheBoxUnsaved()
    {
        // No AI provider in the test app: copying a prompt is offered, and a used draft is an ordinary unsaved edit.
        await using var r = await ComponentHarness.RenderAsync<Reports>(app, "/reports?p=2026-10");
        System.Net.WebUtility.HtmlDecode(await r.Html()).Should().Contain("Draft an assessment: copy a prompt");

        await r.Click(c =>
        {
            c.UseDraft("headlines", new Tide.Application.Assist.ProseDraft(Guid.NewGuid(), "A quiet October.", [], TlpLevel.Amber, "the Headlines numbers"));
            return c.SetParametersAsync(Microsoft.AspNetCore.Components.ParameterView.Empty);
        });

        (await r.Html()).Should().Contain("A quiet October.");
        r.Component.Dirty("headlines").Should().BeTrue("nothing is saved without the lead");
    }

    private async Task SaveAs(string name, Period period, string section, string text, TlpLevel tlp)
    {
        await using var scope = ComponentHarness.SignedIn(app, new ComponentHarness.SignIn("dev:" + name.ToLowerInvariant(), name, "Analyst"));
        var reports = scope.ServiceProvider.GetRequiredService<ReportService>();
        var version = (await reports.AssessmentsAsync(period)).FirstOrDefault(a => a.Section == section)?.EditVersion;
        await reports.SaveAssessmentAsync(period, section, text, tlp, version);
    }

    [Fact]
    public async Task ASavedAssessment_IsThereOnReload_WithWhoEditedIt_AndAColleaguesSaveShowsLive()
    {
        var march = new Period(PeriodKind.Month, 2025, 3);
        await using (var r = await ComponentHarness.RenderAsync<Reports>(app, "/reports?p=2025-03"))
        {
            System.Net.WebUtility.HtmlDecode(await r.Html()).Should().NotContain("aren't saved");
            await r.Click(c =>
            {
                c.Note("headlines", "March was steady");
                return c.Save("headlines");
            });
        }

        await using var again = await ComponentHarness.RenderAsync<Reports>(app, "/reports?p=2025-03");
        var html = System.Net.WebUtility.HtmlDecode(await again.Html());
        html.Should().Contain("March was steady").And.Contain("Edited by Tester, ").And.Contain("maxlength=\"4000\"");

        // Robin saves another section while the page is open: it shows without a reload, and the report's marking follows.
        await SaveAs("Robin", march, "outcomes", "Outcomes held up", TlpLevel.Red);
        var live = System.Net.WebUtility.HtmlDecode(await again.HtmlOnce("Outcomes held up"));
        live.Should().Contain("Edited by Robin, ");
        live.Should().MatchRegex("tide-report-title[^>]*>[^<]*<span class=\"tide-tlp tide-tlp-red\"", "a TLP:RED assessment marks the whole report");
    }

    [Fact]
    public async Task AColleaguesSave_ToASectionBeingEdited_KeepsWhatWasTyped_AndRefusesTheSave()
    {
        var april = new Period(PeriodKind.Month, 2025, 4);
        await using var r = await ComponentHarness.RenderAsync<Reports>(app, "/reports?p=2025-04");
        await r.Click(c =>
        {
            c.Note("metrics", "My draft");
            return Task.CompletedTask;
        });

        await SaveAs("Robin", april, "metrics", "Robin got there first", TlpLevel.Amber);
        var html = System.Net.WebUtility.HtmlDecode(await r.HtmlOnce("Robin saved this assessment"));
        html.Should().Contain("Robin saved this assessment while you were editing").And.Contain("My draft");

        await r.Click(c => c.Save("metrics"));
        (await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Dirty("metrics"))).Should().BeTrue("the refused save keeps what was typed");
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        (await scope.ServiceProvider.GetRequiredService<ReportService>().AssessmentsAsync(april)).Single().Text.Should().Be("Robin got there first");
    }

    [Fact]
    public async Task TheCsv_IsMarkedAtTheHighestTlpItHolds()
    {
        var may = new Period(PeriodKind.Month, 2025, 5);
        var client = app.CreateClient();
        static string Name(HttpResponseMessage res) => res.Content.Headers.ContentDisposition!.FileNameStar ?? res.Content.Headers.ContentDisposition.FileName!.Trim('"');

        using (var plain = await client.GetAsync("/reports/export.csv?p=2025-05"))
            Name(plain).Should().Be("tide-report-2025-05-amber.csv", "the numbers alone are AMBER");

        await SaveAs("Robin", may, "teams", "Patching lagged", TlpLevel.Red);
        using var marked = await client.GetAsync("/reports/export.csv?p=2025-05");
        Name(marked).Should().Be("tide-report-2025-05-red.csv");
        (await marked.Content.ReadAsStringAsync()).Should().Contain(",,,TLP:RED,Patching lagged");
    }

    [Fact]
    public async Task TrendsTiles_OpenWhatTheyCount_AndTheOutcomesRowFollows()
    {
        await using var r = await ComponentHarness.RenderAsync<Trends>(app, "/trends?p=2026-W44");
        var html = System.Net.WebUtility.HtmlDecode(await r.Html());

        html.Should().Contain(">Weekly</button>").And.Contain("Week of Oct 26").And.Contain("weeks</button>")
            .And.Contain("line=actions%3Aon-time").And.Contain(">Outcomes</h2>").And.Contain("Median days to close, P1").And.Contain("Hunts with hits");
    }
}
