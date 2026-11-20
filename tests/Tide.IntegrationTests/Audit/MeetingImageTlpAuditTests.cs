using AwesomeAssertions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Files;
using Tide.Domain.Enums;
using Tide.Web.Services;

namespace Tide.IntegrationTests.Audit;

/// <summary>
/// A Visitor seated in a live meeting loads a slide image only as the meeting's slides show it: the article and the
/// screenshot both within the team edition's ceiling. An image the slides leave out isn't served by its id either.
/// </summary>
public sealed class MeetingImageTlpAuditTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 1);
    private readonly TestDb _db = new();
    private readonly LiveMeetings _meetings = new(new TestClock(new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero)));

    public MeetingImageTlpAuditTests() => _db.Live = _meetings;

    public void Dispose()
    {
        _meetings.Dispose();
        _db.Dispose();
    }

    private void As(string id, string name, params AppRole[] roles)
    {
        _db.User.UserId = id;
        _db.User.DisplayName = name;
        _db.User.Roles = roles.ToHashSet();
    }

    private void AsAdmin() => As("S-1-5-21-TEST-1", "Test Analyst", AppRole.Analyst, AppRole.Admin);

    private async Task<Guid> ShotOn(Guid article, string name, TlpLevel? tlp = null)
    {
        var shot = (await _db.Screenshots().AddAsync(article, [new ScreenshotUpload(name, Samples.Png)])).Single();
        if (tlp is { } marking)
        {
            var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);
            form.Screenshots.Single(s => s.Id == shot.Id).Tlp = marking;
            await _db.Articles().SaveAsync(form);
        }
        return shot.FileId;
    }

    [Fact]
    public async Task AVisitor_IsRefusedASlideImage_AboveTheTeamCeiling_ButStillLoadsOneWithinIt()
    {
        AsAdmin();
        var amber = await _db.Articles().CreateAsync("Within the ceiling", tlp: TlpLevel.Amber);
        var red = await _db.Articles().CreateAsync("Partner breach", tlp: TlpLevel.Red);
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, amber);
        await _db.Briefs().AddArticleAsync(brief.Id, red);
        var allowed = await ShotOn(amber, "console.png");
        var onRed = await ShotOn(red, "partner.png");
        var markedRed = await ShotOn(amber, "internal.png", TlpLevel.Red);

        using var presenter = _meetings.Start(Day, brief.Id, "S-1-5-21-TEST-1", "Test Analyst", "c-presenter", Guid.NewGuid(), 0);
        As("S-1-5-21-TEST-9", "Vic Visitor", AppRole.Visitor);
        using var seat = _meetings.Join(Day, _db.User.UserId, _db.User.DisplayName, false, "c-visitor", Guid.NewGuid(), 0)!;

        var open = await _db.Screenshots().OpenAsync(allowed);
        open.Should().NotBeNull("the article and the screenshot are within AMBER+STRICT, so the slide shows it");
        await open!.Content!.DisposeAsync();
        (await _db.Screenshots().OpenAsync(onRed)).Should().BeNull("a RED article is left out of the meeting's slides");
        (await _db.Screenshots().OpenAsync(markedRed)).Should().BeNull("a screenshot marked RED is left out of its slide");

        AsAdmin();
        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.Green);
        As("S-1-5-21-TEST-9", "Vic Visitor", AppRole.Visitor);
        (await _db.Screenshots().OpenAsync(allowed)).Should().BeNull("the team ceiling is now GREEN, below the AMBER article");
    }

    [Fact]
    public async Task AnAmberStrictSlideImage_LoadsAtAnAmberStrictCeiling_AndIsRefusedAtAmber()
    {
        AsAdmin();
        var strict = await _db.Articles().CreateAsync("Within our walls", tlp: TlpLevel.AmberStrict);
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, strict);
        var shot = await ShotOn(strict, "console.png", TlpLevel.AmberStrict);
        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.AmberStrict);

        using var presenter = _meetings.Start(Day, brief.Id, "S-1-5-21-TEST-1", "Test Analyst", "c-presenter", Guid.NewGuid(), 0);
        As("S-1-5-21-TEST-9", "Vic Visitor", AppRole.Visitor);
        using var seat = _meetings.Join(Day, _db.User.UserId, _db.User.DisplayName, false, "c-visitor", Guid.NewGuid(), 0)!;

        var open = await _db.Screenshots().OpenAsync(shot);
        open.Should().NotBeNull("AMBER+STRICT is at the ceiling, not above it");
        await open!.Content!.DisposeAsync();

        AsAdmin();
        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.Amber);
        As("S-1-5-21-TEST-9", "Vic Visitor", AppRole.Visitor);
        (await _db.Screenshots().OpenAsync(shot)).Should().BeNull("AMBER+STRICT is above an AMBER ceiling");
    }
}
