using System.Text;
using AwesomeAssertions;
using Tide.Application.Adversaries;
using Tide.Application.Org;
using Tide.Application.Rotation;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>Bug pass 5: the editor's light name lists, and holidays meeting briefs, rota requests and calendar files.</summary>
public class BugPass5Tests : IDisposable
{
    private readonly TestDb _db = new();
    private const string Me = "S-1-5-21-TEST-1", Robin = "dev:robin";

    public BugPass5Tests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private HolidayService Holidays() => new(_db, _db.User, _db.Clock);

    [Fact]
    public async Task TheEditorsPickers_ListLiveNames_WithoutMatchingArticles()
    {
        await _db.Adversaries().SaveAsync(new AdversaryForm { Name = "Scattered Spider", Aliases = "Octo Tempest" });
        var gone = await _db.Adversaries().SaveAsync(new AdversaryForm { Name = "Old Crew" });
        await _db.Adversaries().SetRetiredAsync(gone, true);
        await _db.Stack().SaveAsync(new StackForm { Name = "Ivanti Connect Secure", OtherNames = "Pulse Secure" });

        (await _db.Adversaries().ProfilesAsync()).Select(p => p.Name).Should().Equal("Scattered Spider");
        var products = (await _db.Stack().ListAsync()).Select(r => r.Product).ToList();
        products.Select(p => p.Name).Should().Equal("Ivanti Connect Secure");
        StackService.Names(StackService.Entry(products[0]), StackService.Words("Pulse-Secure flaw exploited", null))
            .Should().BeTrue("each name's match words are worked out once and still match");
    }

    [Fact]
    public async Task ADayWithABrief_CantBecomeAHoliday_ByHandOrByImport()
    {
        var day = new DateOnly(2026, 10, 7);
        await _db.Briefs().GetOrStartAsync(day);

        await FluentActions.Invoking(() => Holidays().AddAsync(day, "Founders' Day")).Should().ThrowAsync<ArgumentException>().WithMessage("*has a brief already*");
        var ics = "BEGIN:VCALENDAR\nBEGIN:VEVENT\nDTSTART;VALUE=DATE:20261007\nSUMMARY:Founders' Day\nEND:VEVENT\nEND:VCALENDAR\n";
        var result = await Holidays().ImportAsync(Encoding.UTF8.GetBytes(ics));
        result.Added.Should().BeEmpty();
        result.Skipped.Should().ContainSingle().Which.Should().Contain("has a brief already");
    }

    [Fact]
    public async Task AnImportsSkippedReasons_AreCapped()
    {
        var sb = new StringBuilder("BEGIN:VCALENDAR\n");
        for (var i = 0; i < 80; i++) sb.Append("BEGIN:VEVENT\nSUMMARY:No date\nEND:VEVENT\n");
        sb.Append("END:VCALENDAR\n");

        var result = await Holidays().ImportAsync(Encoding.UTF8.GetBytes(sb.ToString()));

        result.Skipped.Should().HaveCount(HolidayService.MaxSkippedShown + 1);
        result.Skipped[^1].Should().Be("…and 50 more.");
    }

    [Fact]
    public async Task ARotaRequestWhoseDaysAllBecameHolidays_IsRefusedOnApproval_NotCrashed()
    {
        var r = _db.Rotation();
        await r.AddMemberAsync(Me, "Test Analyst");
        await r.AddMemberAsync(Robin, "Robin");
        var week = RotationService.WeekStart(_db.Clock.Today).AddDays(7);
        await r.SetWeekAsync(week, Me);
        var change = await r.RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = week, GiveTo = week });

        _db.Clock.UseHolidays(new Dictionary<DateOnly, string> { [week] = "A holiday since" });
        (_db.User.UserId, _db.User.Roles) = (Robin, new HashSet<AppRole> { AppRole.Analyst });

        await FluentActions.Invoking(() => _db.Rotation().ApproveAsync(change.Id)).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The days in this request are now holidays*");
    }
}
