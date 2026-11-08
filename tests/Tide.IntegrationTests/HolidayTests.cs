using System.Text;
using AwesomeAssertions;
using Tide.Application.Articles;
using Tide.Application.Operations;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>EP-82: the holidays an administrator keeps, put in force at once, and what they change.</summary>
public class HolidayTests : IDisposable
{
    private TestDb _db = new();
    // Past days, so a meeting can be recorded as held: a Wednesday brief, then two holidays before the weekend.
    private static readonly DateOnly Wed = new(2026, 10, 7), Thu = new(2026, 10, 8), Fri = new(2026, 10, 9), Mon = new(2026, 10, 12);

    public HolidayTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private HolidayService Holidays() => new(_db, _db.User, _db.Clock);

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AHoliday_IsInForceAtOnce_AndAWeekendDuplicateOrNonAdminIsRefused(string provider)
    {
        _db = _db.On(provider);
        var added = await Holidays().AddAsync(Thu, "  Founders' Day ");

        added.Name.Should().Be("Founders' Day");
        _db.Clock.Days.IsWorkingDay(Thu).Should().BeFalse("the clock has it at once");
        (await Holidays().ListAsync()).Select(h => (h.Date, h.Name)).Should().Equal((Thu, "Founders' Day"));
        await FluentActions.Invoking(() => Holidays().AddAsync(Thu, "Again")).Should().ThrowAsync<ArgumentException>().WithMessage("*already a holiday (Founders' Day)*");
        await FluentActions.Invoking(() => Holidays().AddAsync(new DateOnly(2026, 10, 10), "Saturday")).Should().ThrowAsync<ArgumentException>().WithMessage("*weekend*");
        await FluentActions.Invoking(() => Holidays().AddAsync(Fri, " ")).Should().ThrowAsync<ArgumentException>().WithMessage("*name*");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await FluentActions.Invoking(() => Holidays().AddAsync(Fri, "Mine")).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => Holidays().RemoveAsync(Thu)).Should().ThrowAsync<ForbiddenException>();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        await Holidays().RemoveAsync(Thu);
        _db.Clock.Days.IsWorkingDay(Thu).Should().BeTrue("removed, it's a working day again");
    }

    [Fact]
    public async Task ACalendarFile_AddsItsDays_AndSkipsWhatsThereOrOnAWeekend()
    {
        await Holidays().AddAsync(Thu, "Kept as it is");
        var ics = "BEGIN:VCALENDAR\nBEGIN:VEVENT\nDTSTART;VALUE=DATE:20261008\nDTEND;VALUE=DATE:20261011\nSUMMARY:Long weekend\nEND:VEVENT\nEND:VCALENDAR\n";

        var result = await Holidays().ImportAsync(Encoding.UTF8.GetBytes(ics));

        result.Added.Select(h => (h.Date, h.Name)).Should().Equal((Fri, "Long weekend"));
        result.Skipped.Should().HaveCount(2).And.Contain(s => s.Contains("already a holiday (Kept as it is)")).And.Contain(s => s.Contains("a weekend"));
        _db.Clock.Days.IsWorkingDay(Fri).Should().BeFalse();
        await FluentActions.Invoking(() => Holidays().ImportAsync(Encoding.UTF8.GetBytes("not a calendar"))).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CopyingAYear_KeepsEachDateAndName_ForTheNextYear()
    {
        await Holidays().AddAsync(new DateOnly(2026, 12, 25), "Christmas Day");
        await Holidays().AddAsync(new DateOnly(2026, 12, 24), "Christmas Eve");

        var result = await Holidays().CopyYearAsync(2026);

        result.Added.Select(h => h.Date).Should().Equal(new DateOnly(2027, 12, 24));
        result.Skipped.Should().ContainSingle().Which.Should().Contain("a weekend", "Dec 25, 2027 is a Saturday");
        await FluentActions.Invoking(() => Holidays().CopyYearAsync(2030)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ABriefIsntHeldOnAHoliday_AndOneEndedBeforeIt_CarriesOverPastIt()
    {
        await Holidays().AddAsync(Thu, "Founders' Day");
        await Holidays().AddAsync(Fri, "Founders' Day, observed");
        var briefs = _db.Briefs();
        await FluentActions.Invoking(() => briefs.GetOrStartAsync(Thu)).Should().ThrowAsync<ArgumentException>()
            .WithMessage("Founders' Day is a holiday, and briefs are held on working days.");

        var article = await _db.Articles().CreateAsync("ran out of time");
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);
        form.Bluf = "bottom line";
        form.Bullets = [new() { Text = "a bullet" }];
        await _db.Articles().SaveAsync(form);
        await _db.Articles().SetStatusAsync(article, ArticleStatus.Ready);
        var brief = await briefs.GetOrStartAsync(Wed);
        await briefs.AddArticleAsync(brief.Id, article);

        (await briefs.MarkPresentedAsync(brief.Id, carry: [article])).Should().Be(Mon, "Thursday and Friday are holidays");
    }

    [Fact]
    public async Task ANewActionsDueDate_SkipsAHoliday()
    {
        var next = _db.Clock.Days.Next(_db.Clock.Today);
        await Holidays().AddAsync(next, "Tomorrow off");

        (await _db.Actions().SuggestedDueAsync(Priority.P1, ActionKind.Block)).Should().Be(_db.Clock.Days.Next(next),
            "one working day, and the holiday isn't one");
    }

    [Fact]
    public async Task TheConfigurationBundle_CarriesTheHolidays_AndAnImportPutsThemInForce()
    {
        await Holidays().AddAsync(Thu, "Founders' Day");
        var bundle = await new ConfigBundleService(_db, _db.User, new CountingRoleDirectory(), TimeProvider.System).ExportAsync();
        bundle.Holidays.Should().Equal(new BundleHoliday(Thu, "Founders' Day"));

        using var other = new TestDb();
        other.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        var import = new ConfigBundleService(other, other.User, new CountingRoleDirectory(), TimeProvider.System, clock: other.Clock);
        var parsed = ConfigBundle.Parse(Encoding.UTF8.GetBytes(bundle.ToJson()));
        (await import.PreviewAsync(parsed)).Changes.Should().Contain(c => c.Area == "Holidays" && c.Kind == BundleChangeKind.Add);
        await import.ImportAsync(parsed);

        other.Clock.Days.HolidayOn(Thu).Should().Be("Founders' Day");
        (await import.ImportAsync(parsed)).Changes.Single(c => c.Area == "Holidays").Kind.Should().Be(BundleChangeKind.Unchanged);
    }

    [Fact]
    public void ABundleFromBeforeHolidays_ReadsWithNone()
    {
        const string old = """{"format":"tide-config-bundle","version":1,"exportedAtUtc":"2026-10-01T00:00:00Z","exportedBy":"x","appVersion":"0.1.0-alpha.9","settings":[],"roleMappings":[],"sources":[],"scorecard":[],"queryTemplates":[]}""";
        ConfigBundle.Parse(Encoding.UTF8.GetBytes(old)).Holidays.Should().BeEmpty();
    }
}
