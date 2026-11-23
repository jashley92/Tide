using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Security;
using Tide.Application.Trends;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>The report's assessments: saved per period and section, with who and when, a TLP each, refused when stale or
/// when the writer can't write them, heard live, and carried in the CSV at the highest marking.</summary>
public class ReportAssessmentTests : IDisposable
{
    private static readonly Period Oct = new(PeriodKind.Month, 2026, 10);
    private static readonly Period Q4 = new(PeriodKind.Quarter, 2026, 4);
    private static readonly Period W44 = new(PeriodKind.Week, 2026, 44);

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AnAssessment_IsSavedWithItsPeriod_AndReadBackOnlyForIt()
    {
        _db.User.DisplayName = "Pat";
        var saved = await _db.Reports().SaveAssessmentAsync(Oct, "headlines", "  October was quiet.  ", TlpLevel.Green, null);
        await _db.Reports().SaveAssessmentAsync(Q4, "headlines", "The quarter picked up.", TlpLevel.Amber, null);
        await _db.Reports().SaveAssessmentAsync(W44, "teams", "Cyber Defense carried the week.", TlpLevel.Amber, null);

        var october = await _db.Reports().AssessmentsAsync(Oct);
        var a = october.Should().ContainSingle().Subject;
        (a.PeriodKey, a.Section, a.Text, a.Tlp, a.UpdatedByName).Should().Be(("2026-10", "headlines", "October was quiet.", TlpLevel.Green, "Pat"));
        a.EditVersion.Should().Be(saved!.EditVersion);
        a.UpdatedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        (await _db.Reports().AssessmentsAsync(Q4)).Single().Text.Should().Be("The quarter picked up.");
        (await _db.Reports().AssessmentsAsync(W44)).Single().Section.Should().Be("teams");
        (await _db.Reports().AssessmentsAsync(Oct.Next)).Should().BeEmpty("November has none of October's");
    }

    [Fact]
    public async Task ASecondSave_ReplacesTheFirst_AndClearingRemovesIt()
    {
        var first = await _db.Reports().SaveAssessmentAsync(Oct, "outcomes", "Slow to close.", TlpLevel.Amber, null);
        _db.User.DisplayName = "Sam";
        var second = await _db.Reports().SaveAssessmentAsync(Oct, "outcomes", "Slow to close, P1s on time.", TlpLevel.Amber, first!.EditVersion);

        second!.EditVersion.Should().NotBe(first.EditVersion);
        var row = (await _db.Reports().AssessmentsAsync(Oct)).Single();
        (row.Text, row.UpdatedByName).Should().Be(("Slow to close, P1s on time.", "Sam"));

        (await _db.Reports().SaveAssessmentAsync(Oct, "outcomes", "   ", TlpLevel.Amber, second.EditVersion)).Should().BeNull();
        (await _db.Reports().AssessmentsAsync(Oct)).Should().BeEmpty();
        await using var ctx = _db.Context();
        (await ctx.AuditLog.CountAsync(e => e.EntityType == nameof(ReportAssessment))).Should().Be(3, "added, changed and deleted are each in the audit trail");
    }

    [Fact]
    public async Task ASaveFromAnOlderRead_IsRefused_AndTheColleaguesStands()
    {
        // Two leads opened October's report with no assessment; Pat saved first.
        _db.User.DisplayName = "Pat";
        var pats = await _db.Reports().SaveAssessmentAsync(Oct, "metrics", "Queue under control.", TlpLevel.Amber, null);

        _db.User.DisplayName = "Sam";
        var firstSave = () => _db.Reports().SaveAssessmentAsync(Oct, "metrics", "Queue grew.", TlpLevel.Amber, null);
        (await firstSave.Should().ThrowAsync<StaleEditException>()).WithMessage("Someone else saved this assessment*");

        // Sam reloaded, then Pat saved again before Sam did.
        var seen = pats!.EditVersion;
        await _db.Reports().SaveAssessmentAsync(Oct, "metrics", "Queue under control again.", TlpLevel.Amber, seen);
        var stale = () => _db.Reports().SaveAssessmentAsync(Oct, "metrics", "Queue grew.", TlpLevel.Amber, seen);
        await stale.Should().ThrowAsync<StaleEditException>();
        var clear = () => _db.Reports().SaveAssessmentAsync(Oct, "metrics", "", TlpLevel.Amber, seen);
        await clear.Should().ThrowAsync<StaleEditException>("clearing from an older read would drop theirs unseen too");

        (await _db.Reports().AssessmentsAsync(Oct)).Single().Text.Should().Be("Queue under control again.");
    }

    [Theory]
    [InlineData(AppRole.Manager)]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.ActionOwner)]
    public async Task OnlyWhoeverEntersNumbersOrWritesArticles_CanWriteThem_ButTheTeamReadsThem(AppRole role)
    {
        await _db.Reports().SaveAssessmentAsync(Oct, "topics", "Ransomware led.", TlpLevel.Amber, null);
        _db.User.Roles = new HashSet<AppRole> { role };

        _db.Reports().CanAssess.Should().BeFalse();
        var save = () => _db.Reports().SaveAssessmentAsync(Oct, "teams", "Mine.", TlpLevel.Amber, null);
        await save.Should().ThrowAsync<ForbiddenException>();
        (await _db.Reports().AssessmentsAsync(Oct)).Should().ContainSingle("the team edition reads the report, its assessments with it");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var read = () => _db.Reports().AssessmentsAsync(Oct);
        await read.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ASectionNotOnTheReport_OrTextTooLong_IsRefused()
    {
        var section = () => _db.Reports().SaveAssessmentAsync(Oct, "elsewhere", "Text.", TlpLevel.Amber, null);
        await section.Should().ThrowAsync<ArgumentException>();
        var tooLong = () => _db.Reports().SaveAssessmentAsync(Oct, "headlines", new string('x', ReportAssessment.MaxText + 1), TlpLevel.Amber, null);
        await tooLong.Should().ThrowAsync<ArgumentException>();
        var badTlp = () => _db.Reports().SaveAssessmentAsync(Oct, "headlines", "Text.", (TlpLevel)42, null);
        await badTlp.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ASave_IsPublishedOnItsPeriodsTopic()
    {
        var heard = new List<string>();
        _db.Feed.Changed += c => { lock (heard) heard.Add(c.Topic); };

        await _db.Reports().SaveAssessmentAsync(W44, "headlines", "A busy week.", TlpLevel.Amber, null);

        lock (heard) heard.Should().Contain(ChangeFeed.Report("2026-W44")).And.NotContain(ChangeFeed.Report("2026-10"));
    }

    [Fact]
    public async Task TheCsv_CarriesEachAssessmentWithItsTlp_AndIsMarkedAtTheHighest()
    {
        await _db.Reports().SaveAssessmentAsync(Oct, "headlines", "Quiet, \"mostly\".", TlpLevel.Green, null);
        var report = await _db.Reports().BuildAsync(Oct);

        var amber = await _db.Reports().AssessmentsAsync(Oct);
        ReportService.Marking(amber).Should().Be(TlpLevel.Amber, "the numbers are AMBER, and a GREEN assessment doesn't lower that");
        var csv = ReportService.ToCsv(report, amber);
        csv.Should().StartWith("section,name,current,previous,tlp,text\n")
            .And.Contain("Headlines,Articles briefed,0,0,TLP:AMBER,\n")
            .And.MatchRegex(@"Headlines,""Assessment \(edited by Test Analyst, \w+ \d+, \d{4}\)"",,,TLP:GREEN,""Quiet, """"mostly"""".""\n");

        await _db.Reports().SaveAssessmentAsync(Oct, "teams", "We missed the patch window.", TlpLevel.Red, null);
        var red = await _db.Reports().AssessmentsAsync(Oct);
        ReportService.Marking(red).Should().Be(TlpLevel.Red);
        ReportService.ToCsv(report, red).Should().EndWith(",,,TLP:RED,We missed the patch window.\n", "the teams' assessment follows their numbers, last");
    }
}
