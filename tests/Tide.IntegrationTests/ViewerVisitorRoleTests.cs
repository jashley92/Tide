using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Files;
using Tide.Application.Metrics;
using Tide.Application.Rotation;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Enums;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>
/// The Viewer and Visitor roles, permission by permission, through the services: a Viewer reads everything and writes
/// nothing but meeting notes; a Visitor reads the team's content only from a seat in a live meeting, and only its brief.
/// </summary>
public class ViewerVisitorRoleTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 1);
    private static readonly DateOnly OtherDay = new(2026, 9, 2);
    private readonly TestDb _db = new();
    private readonly LiveMeetings _meetings = new(new TestClock(new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero)));

    private sealed record Setup(Guid Brief, Guid Article, Guid Screenshot, Guid OtherBrief, Guid OtherArticle, Guid OtherScreenshot,
        int Action, Guid ActionId, Guid Product, Guid Metric, Guid Team, Guid Checklist);

    public ViewerVisitorRoleTests()
    {
        _db.Live = _meetings;
        AsAnalyst();
    }

    public void Dispose()
    {
        _meetings.Dispose();
        _db.Dispose();
    }

    private void As(AppRole role, string id = "S-1-5-21-TEST-9", string name = "Vic Visitor")
    {
        _db.User.UserId = id;
        _db.User.DisplayName = name;
        _db.User.Roles = new HashSet<AppRole> { role };
    }

    private void AsAnalyst() => As(AppRole.Analyst, "S-1-5-21-TEST-1", "Test Analyst");
    private void AsAdmin() => (_db.User.UserId, _db.User.DisplayName, _db.User.Roles) = ("S-1-5-21-TEST-1", "Test Analyst", new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin });

    // Two briefs (today's meeting's and another day's), each with an article and a screenshot; an action on the first
    // article with a checklist item, a product tagged on it, and a metric with a value for the day.
    private async Task<Setup> Seed()
    {
        AsAdmin();
        var article = await _db.Articles().CreateAsync("Edge VPN zero-day");
        var other = await _db.Articles().CreateAsync("Another day's story");
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, article);
        var otherBrief = await _db.Briefs().GetOrStartAsync(OtherDay);
        await _db.Briefs().AddArticleAsync(otherBrief.Id, other);
        var shot = (await _db.Screenshots().AddAsync(article, [new ScreenshotUpload("console.png", Samples.Png)])).Single().FileId;
        var otherShot = (await _db.Screenshots().AddAsync(other, [new ScreenshotUpload("other.png", Samples.Png)])).Single().FileId;
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep for the loader", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = article });
        var action = (await _db.Actions().GetAsync(number))!.Action;
        await _db.Actions().AddChecklistItemAsync(action.Id, "Check the proxy logs");
        var item = (await _db.Actions().ChecklistAsync(action.Id)).Single().Id;
        var product = await _db.Stack().SaveAsync(new StackForm { Name = "Northwind Edge", Versions = "4.2" });
        await _db.Stack().TagArticleAsync(article, product);
        var group = await _db.Metrics().AddGroupAsync("Email");
        var metric = await _db.Metrics().SaveMetricAsync(new MetricForm { GroupId = group, Name = "Phish reported" });
        await _db.Metrics().SaveValueAsync(metric, Day, 7, null);
        return new Setup(brief.Id, article, shot, otherBrief.Id, other, otherShot, number, action.Id, product, metric, team, item);
    }

    // An analyst presenting the day's meeting; a seat for the one signed in, as their present-mode page takes.
    private IMeetingSeat Present(Guid brief) => _meetings.Start(Day, brief, "S-1-5-21-TEST-1", "Test Analyst", "c-presenter", Guid.NewGuid(), 0);

    private IMeetingSeat Sit(bool canPresent = false) =>
        _meetings.Join(Day, _db.User.UserId, _db.User.DisplayName, canPresent, "c-" + _db.User.UserId, Guid.NewGuid(), 0)!;

    private static Task Refused(Func<Task> call) => call.Should().ThrowAsync<ForbiddenException>();

    // --- Viewer ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Viewer_HoldsTheTeamEditionAndReadActions_AndNeitherWorkActionsNorWriteArticles()
    {
        var p = RoleDefinitions.SystemRoles[AppRole.Viewer];
        p.Should().BeEquivalentTo([Permission.ReadPublished, Permission.ReadTeamEdition, Permission.ReadActions, Permission.MeetingNotes, Permission.JoinMeetings]);
        p.Should().NotContain([Permission.WorkActions, Permission.WriteArticles, Permission.PresentBriefs, Permission.EnterMetrics, Permission.Administer]);
    }

    [Fact]
    public async Task Viewer_ReadsEachPagesData_EveryActionIncluded()
    {
        var s = await Seed();
        As(AppRole.Viewer, "S-1-5-21-TEST-5", "Val Viewer");

        (await _db.Briefs().GetAsync(Day))!.Id.Should().Be(s.Brief);
        (await _db.Briefs().ListAsync()).Should().HaveCount(2);
        (await _db.Articles().GetAsync(s.Article)).Should().NotBeNull();
        (await _db.Actions().ListAsync(new ActionFilter(ActionView.Open))).Should().ContainSingle(r => r.Number == s.Action);
        (await _db.Actions().BoardAsync(new ActionFilter(ActionView.Open))).Cards.Should().ContainSingle();
        (await _db.Actions().WorkloadAsync()).Should().NotBeEmpty();
        var detail = (await _db.Actions().GetAsync(s.Action))!;
        (detail.CanWork, detail.CanComment, detail.CanEditAll).Should().Be((false, false, false), "the page shows it read-only");
        (await _db.Stack().ListAsync()).Should().ContainSingle();
        (await _db.Adversaries().ListAsync()).Should().NotBeNull();
        (await _db.Stories().ListAsync()).Should().NotBeNull();
        (await _db.Metrics().HistoryAsync(Day, Day)).Should().NotBeNull();
        (await _db.Rotation().CalendarAsync(Day, Day)).Should().NotBeNull();
        (await _db.Screenshots().OpenAsync(s.Screenshot)).Should().NotBeNull();
        (await _db.Meetings().GetAsync(s.Brief)).Should().NotBeNull();
    }

    [Fact]
    public async Task Viewer_IsRefusedEveryWrite()
    {
        var s = await Seed();
        As(AppRole.Viewer, "S-1-5-21-TEST-5", "Val Viewer");
        var actions = _db.Actions();

        await Refused(() => actions.AddCommentAsync(s.ActionId, "Looks done to me"));
        await Refused(() => _db.ActionFiles().AddAsync(s.ActionId, [new ActionFileUpload("proxy.log", Samples.Text("log"), "text/plain")]));
        await Refused(() => actions.SetStatusAsync(s.ActionId, ActionStatus.InProgress, ActionOutcome.None, null));
        await Refused(() => actions.TakeAsync(s.ActionId));
        await Refused(() => actions.SetChecklistItemAsync(s.Checklist, true));
        await Refused(() => actions.CreateAsync(new ActionForm { Title = "Mine", Kind = ActionKind.Other, TeamId = s.Team }));
        await Refused(() => actions.QuickAddAsync(s.Article, "Block it", ActionKind.Block, s.Team, null));
        await Refused(() => _db.Metrics().SaveValueAsync(s.Metric, Day, 9, null));
        await Refused(() => _db.Intake().DismissManyAsync([Guid.NewGuid()], null));
        await Refused(() => _db.Articles().CreateAsync("A new story"));
        await Refused(() => _db.Briefs().SetTeamNoteAsync(s.Brief, "Morning all"));
        await Refused(() => _db.Briefs().ReopenAsync(s.Brief));
        await Refused(() => _db.Distribution().FinishNotSentAsync(Day, "No meeting"));
        await Refused(() => _db.Rotation().RequestAsync(new RotationRequest { PartnerId = "S-1-5-21-TEST-1", GiveFrom = Day, GiveTo = Day }));
        await Refused(() => _db.Stack().SaveAsync(new StackForm { Name = "Contoso Mail" }));
        await Refused(() => _db.Meetings().AddAttendeeAsync(s.Brief, "Someone"));
        await Refused(() => _db.Teams().AddAsync("Their own team", null, null));
    }

    [Fact]
    public async Task Viewer_CanRemoveNoFile_ThoughTheyMayRead_AndCantDescribeOne()
    {
        var s = await Seed();
        var file = (await _db.ActionFiles().AddAsync(s.ActionId, [new ActionFileUpload("proxy.log", Samples.Text("log"), "text/plain")])).Single();
        As(AppRole.Viewer, "S-1-5-21-TEST-5", "Val Viewer");

        (await _db.Actions().GetAsync(s.Action))!.Files.Live.Should().ContainSingle(f => !f.CanRemove);
        await Refused(() => _db.ActionFiles().RemoveAsync(file.FileId, "Not needed"));
        await Refused(() => _db.ActionFiles().DescribeAsync(file.FileId, "Proxy extract"));
    }

    [Fact]
    public async Task SomeoneMovedToViewer_CanNoLongerRemoveTheFileTheyAddedBefore()
    {
        var s = await Seed();
        As(AppRole.Analyst, "S-1-5-21-TEST-6", "Ana Analyst");
        var file = (await _db.ActionFiles().AddAsync(s.ActionId, [new ActionFileUpload("proxy.log", Samples.Text("log"), "text/plain")])).Single();
        (await _db.Actions().GetAsync(s.Action))!.Files.Live.Should().ContainSingle(f => f.CanRemove);

        As(AppRole.Viewer, "S-1-5-21-TEST-6", "Ana Analyst");

        (await _db.Actions().GetAsync(s.Action))!.Files.Live.Should().ContainSingle(f => !f.CanRemove);
        await Refused(() => _db.ActionFiles().RemoveAsync(file.FileId, "Changed my mind"));
        As(AppRole.Reader, "S-1-5-21-TEST-6", "Ana Analyst");
        await Refused(() => _db.ActionFiles().RemoveAsync(file.FileId, "Changed my mind"));
    }

    [Fact]
    public async Task Viewer_AddsAMeetingNote_AndRaisesAHand()
    {
        var s = await Seed();
        using var presenter = Present(s.Brief);
        As(AppRole.Viewer, "S-1-5-21-TEST-5", "Val Viewer");
        var meetings = _db.Meetings();
        meetings.CanNote.Should().BeTrue();

        var note = await meetings.AddNoteAsync(s.Brief, s.Article, "Ask Cyber Defense about the branch routers");
        await meetings.EditNoteAsync(note, "Ask Cyber Defense about the branch offices");
        (await meetings.GetAsync(s.Brief)).Notes.Should().ContainSingle(n => n.Text.EndsWith("offices") && n.AuthorId == "S-1-5-21-TEST-5");

        using var seat = Sit();
        _meetings.Hand(seat, up: true, s.Article);
        _meetings.Get(Day)!.Hands.Should().ContainSingle(h => h.UserId == "S-1-5-21-TEST-5" && h.ArticleId == s.Article);
        _meetings.TakeOver(seat).Should().BeFalse("a Viewer can't present");
    }

    [Fact]
    public async Task ExistingRoles_KeepCommentingAndNotes()
    {
        var s = await Seed();
        As(AppRole.ActionOwner, "S-1-5-21-TEST-6", "Olive Owner");
        await _db.Actions().AddCommentAsync(s.ActionId, "On it from our side");
        _db.Meetings().CanNote.Should().BeTrue();
        As(AppRole.Reader, "S-1-5-21-TEST-7", "Rae Reader");
        _db.Meetings().CanNote.Should().BeFalse();
        await Refused(() => _db.Actions().GetAsync(s.Action));
    }

    // --- Visitor ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Visitor_HoldsThePublishedArchiveAndJoinMeetings_Only()
    {
        RoleDefinitions.SystemRoles[AppRole.Visitor].Should().BeEquivalentTo([Permission.ReadPublished, Permission.JoinMeetings]);
    }

    [Fact]
    public async Task Visitor_OutsideAMeeting_IsRefusedEveryTeamRead()
    {
        var s = await Seed();
        As(AppRole.Visitor);

        await Refused(() => _db.Briefs().GetAsync(Day));
        await Refused(() => _db.Briefs().ListAsync());
        await Refused(() => _db.Articles().GetAsync(s.Article));
        await Refused(() => _db.Actions().ListAsync(new ActionFilter()));
        await Refused(() => _db.Actions().GetAsync(s.Action));
        await Refused(() => _db.Actions().OutstandingAsync(Day, TlpLevel.AmberStrict));
        await Refused(() => _db.Metrics().GetDayAsync(Day));
        await Refused(() => _db.Metrics().HistoryAsync(Day, Day));
        await Refused(() => _db.Stack().ListAsync());
        await Refused(() => _db.Stack().ForArticlesAsync([s.Article]));
        await Refused(() => _db.Stack().CardAsync(s.Product, TlpLevel.AmberStrict));
        await Refused(() => _db.Settings().GetAsync());
        await Refused(() => _db.Cves().GetAsync(["CVE-2026-0001"]));
        await Refused(() => _db.Meetings().GetAsync(s.Brief));
        await Refused(() => _db.Meetings().JoinedAsync(s.Brief));
        await Refused(() => _db.Adversaries().ListAsync());
        await Refused(() => _db.Stories().ListAsync());
        await Refused(() => _db.Search().SearchAsync("vpn", Tide.Application.Search.SearchPages.First));
        await Refused(() => _db.Rotation().CalendarAsync(Day, Day));
        (await _db.Screenshots().OpenAsync(s.Screenshot)).Should().BeNull();
    }

    [Fact]
    public async Task Visitor_Seated_SeesTheMeetingsBriefInTheTeamEdition_AndOnlyThatBrief()
    {
        var s = await Seed();
        using var presenter = Present(s.Brief);
        As(AppRole.Visitor);
        using var seat = Sit();

        var brief = (await _db.Briefs().GetAsync(Day))!;
        brief.Id.Should().Be(s.Brief);
        var edition = BriefEditions.Build(brief, Edition.Team, TlpLevel.AmberStrict, await _db.Briefs().EditionActionsAsync(brief));
        edition.Articles.Single().Actions.Should().ContainSingle(a => a.Number == s.Action, "the slide's actions, as the room sees them");
        (await _db.Settings().GetAsync()).Team.Should().Be(TlpLevel.AmberStrict);
        (await _db.Metrics().GetDayAsync(Day)).Groups.Should().ContainSingle();
        (await _db.Actions().OutstandingAsync(Day, TlpLevel.AmberStrict)).Should().NotBeNull();
        (await _db.Stack().ForArticlesAsync([s.Article]))[s.Article].Should().ContainSingle(p => p.Id == s.Product, "the We run chips");
        (await _db.Cves().GetAsync(["CVE-2026-0001"])).Should().BeEmpty();

        // Nothing of another day's: its brief, its numbers, its chips.
        await Refused(() => _db.Briefs().GetAsync(OtherDay));
        await Refused(() => _db.Metrics().GetDayAsync(OtherDay));
        await Refused(() => _db.Metrics().HistoryAsync(Day, Day));
        await Refused(() => _db.Stack().ForArticlesAsync([s.Article, s.OtherArticle]));
        var other = (await new BriefService(_db, new FakeUser(AppRole.Analyst), _db.Clock).GetAsync(OtherDay))!;
        await Refused(() => _db.Briefs().EditionActionsAsync(other));
        // Nor the team's pages behind the slides.
        await Refused(() => _db.Articles().GetAsync(s.Article));
        await Refused(() => _db.Stack().ListAsync());
        await Refused(() => _db.Actions().ListAsync(new ActionFilter()));
    }

    [Fact]
    public async Task Visitor_Seated_SeesOnlyTheCardsThePresenterOpens()
    {
        var s = await Seed();
        AsAdmin();
        var sibling = await _db.Actions().CreateAsync(new ActionForm { Title = "Patch the gateway", Kind = ActionKind.PatchRemediate, TeamId = s.Team, ArticleId = s.Article });
        (await _db.Actions().GetAsync(s.Action))!.RaisedWith.Should().ContainSingle(r => r.Number == sibling, "the team sees what else was raised");
        using var presenter = Present(s.Brief);
        As(AppRole.Visitor);
        using var seat = Sit();

        await Refused(() => _db.Actions().GetAsync(s.Action));
        await Refused(() => _db.Stack().CardAsync(s.Product, TlpLevel.AmberStrict));

        _meetings.Open(presenter, s.Action);
        var detail = (await _db.Actions().GetAsync(s.Action))!;
        (detail.CanWork, detail.CanComment).Should().Be((false, false));
        detail.RaisedWith.Should().BeEmpty("the presenter opened this action, not the others raised from its article");
        await Refused(() => _db.Actions().ChecklistAsync(s.ActionId));
        await Refused(() => _db.Actions().AddCommentAsync(s.ActionId, "Hello from elsewhere"));

        _meetings.Show(presenter, MeetingCard.ForProduct(s.Product));
        (await _db.Stack().CardAsync(s.Product, TlpLevel.AmberStrict)).Should().NotBeNull();
        await Refused(() => _db.Actions().GetAsync(s.Action));   // opening the card closed the action
    }

    [Fact]
    public async Task Visitor_LoadsASlideImage_OnlyWhileSeated_AndOnlyForThatBrief()
    {
        var s = await Seed();
        As(AppRole.Visitor);
        (await _db.Screenshots().OpenAsync(s.Screenshot)).Should().BeNull("no meeting yet");

        var presenter = Present(s.Brief);
        (await _db.Screenshots().OpenAsync(s.Screenshot)).Should().BeNull("a meeting on, but no seat in it");
        var seat = Sit();
        _db.Screenshots().NoStore.Should().BeTrue("the browser doesn't keep it");
        var open = await _db.Screenshots().OpenAsync(s.Screenshot);
        open.Should().NotBeNull();
        await open!.Content!.DisposeAsync();
        (await _db.Screenshots().OpenAsync(s.OtherScreenshot)).Should().BeNull("another brief's");

        seat.Dispose();
        (await _db.Screenshots().OpenAsync(s.Screenshot)).Should().BeNull("they left");
        seat = Sit();
        _meetings.End(presenter);
        (await _db.Screenshots().OpenAsync(s.Screenshot)).Should().BeNull("the meeting ended");
        seat.Dispose();
        presenter.Dispose();
    }

    [Fact]
    public async Task Visitor_IsRefusedAgain_OnceTheMeetingEnds_OrTheyLeave()
    {
        var s = await Seed();
        var presenter = Present(s.Brief);
        As(AppRole.Visitor);
        var seat = Sit();
        (await _db.Briefs().GetAsync(Day)).Should().NotBeNull();

        seat.Dispose();
        await Refused(() => _db.Briefs().GetAsync(Day));

        seat = Sit();
        (await _db.Briefs().GetAsync(Day)).Should().NotBeNull();
        _meetings.SetConnected("c-" + _db.User.UserId, connected: false);
        await Refused(() => _db.Briefs().GetAsync(Day));   // their connection dropped: away, not in it
        _meetings.SetConnected("c-" + _db.User.UserId, connected: true);

        _meetings.End(presenter);
        await Refused(() => _db.Briefs().GetAsync(Day));
        await Refused(() => _db.Metrics().GetDayAsync(Day));
        await Refused(() => _db.Meetings().GetAsync(s.Brief));
        seat.Dispose();
    }

    [Fact]
    public async Task Visitor_Seated_RaisesAHand_IsRecordedAsAttending_ButTakesNoNotesAndCantPresent()
    {
        var s = await Seed();
        await _db.Meetings().AddNoteAsync(s.Brief, null, "The team's own note");
        var presenter = Present(s.Brief);
        As(AppRole.Visitor);
        using var seat = Sit();

        _meetings.Hand(seat, up: true);
        _meetings.Get(Day)!.Hands.Should().ContainSingle(h => h.UserId == "S-1-5-21-TEST-9");

        var meetings = _db.Meetings();
        await meetings.JoinedAsync(s.Brief);
        var record = await meetings.GetAsync(s.Brief);
        record.Attendees.Should().ContainSingle(a => a.UserId == "S-1-5-21-TEST-9" && a.Name == "Vic Visitor");
        record.Notes.Should().BeEmpty("the notes are the team's");
        meetings.CanNote.Should().BeFalse();
        await Refused(() => meetings.AddNoteAsync(s.Brief, null, "Hello"));

        presenter.Dispose();   // the presenter dropped out for good
        _meetings.TakeOver(seat).Should().BeFalse("a Visitor's seat can't present");
        _meetings.End(seat).Should().BeNull("nor end the meeting");
        await Refused(() => _db.Briefs().SetDiscussedAsync(Guid.NewGuid(), true));
        await Refused(() => _db.Briefs().MarkPresentedAsync(s.Brief, null));
        await Refused(() => meetings.LeftAsync(s.Brief, new Dictionary<string, DateTimeOffset>()));
    }

    [Fact]
    public void MeetingAccess_IsNothingForTheTeam_OrForAnyoneWithoutASeat()
    {
        var analyst = new FakeUser(AppRole.Analyst);
        MeetingAccess.MeetingOnly(analyst).Should().BeFalse();
        MeetingAccess.Seats(analyst, _meetings).Should().BeEmpty();
        MeetingAccess.MeetingOnly(new FakeUser(AppRole.Visitor)).Should().BeTrue();
        MeetingAccess.MeetingOnly(new FakeUser(AppRole.Reader)).Should().BeFalse("a Reader can't join a meeting");
        MeetingAccess.Seats(new FakeUser(AppRole.Visitor), null).Should().BeEmpty();
        MeetingAccess.Seats(new FakeUser(AppRole.Visitor) { IsAuthenticated = false }, _meetings).Should().BeEmpty();
        ((IMeetingsInProgress)NoMeetingsInProgress.Instance).SeatedIn("anyone").Should().BeEmpty();
    }
}
