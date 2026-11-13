using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Briefs;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>The record of a brief's meeting: who attended, and each person's notes.</summary>
public class MeetingServiceTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 1);
    private readonly TestDb _db = new();

    public MeetingServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<(Guid Brief, Guid Article)> Brief()
    {
        var article = await _db.Articles().CreateAsync("Edge VPN zero-day");
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, article);
        return (brief.Id, article);
    }

    private void As(string id, string name, params AppRole[] roles)
    {
        _db.User.UserId = id;
        _db.User.DisplayName = name;
        _db.User.Roles = roles.ToHashSet();
    }

    private const string Presenter = "S-1-5-21-TEST-1";
    private const string Val = "S-1-5-21-VAL";
    private const string Owner = "S-1-5-21-OWNER";
    private const string Andy = "S-1-5-21-ANDY";
    private const string Vic = "S-1-5-21-VISITOR";

    private async Task<List<Tide.Domain.Entities.MeetingAttendee>> Attendees(Guid brief)
    {
        await using var ctx = _db.Context();
        return (await ctx.MeetingAttendees.Where(a => a.BriefId == brief).ToListAsync()).OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
    }

    private async Task<List<string>> AuditedBy(string action)
    {
        await using var ctx = _db.Context();
        return await ctx.AuditLog.Where(e => e.EntityType == "MeetingAttendee" && e.Action == action).Select(e => e.UserName).ToListAsync();
    }

    [Fact]
    public async Task AFollower_AddsSomeoneWhoWasThere_AndTheRowSaysWhoAddedThem()
    {
        var (brief, _) = await Brief();
        _db.Live = new InMeeting(brief, Presenter);
        var svc = _db.Meetings();

        As(Val, "Val Viewer", AppRole.Viewer);
        svc.CanAddAttendees(brief).Should().BeTrue("a Viewer following the meeting");
        await svc.AddAttendeeAsync(brief, "Sam from the SOC");
        As(Owner, "Olive Owner", AppRole.ActionOwner);
        await svc.AddAttendeeAsync(brief, "Dana from Legal");
        As(Andy, "Andy Analyst", AppRole.Analyst);
        await svc.AddAttendeeAsync(brief, "Kim from IT");
        await svc.AddAttendeeAsync(brief, "  sam FROM the soc ");   // listed already
        await FluentActions.Invoking(() => svc.AddAttendeeAsync(brief, "   ")).Should().ThrowAsync<ArgumentException>();

        (await Attendees(brief)).Select(a => (a.Name, a.AddedById))
            .Should().Equal(("Dana from Legal", Owner), ("Kim from IT", Andy), ("Sam from the SOC", Val));
        (await AuditedBy("Added")).Should().BeEquivalentTo(["Val Viewer", "Olive Owner", "Andy Analyst"], "the audit says who added each");
    }

    [Fact]
    public async Task AVisitor_InTheMeeting_CantAddAnyone()
    {
        var (brief, _) = await Brief();
        _db.Live = new InMeeting(brief, Presenter);
        As(Vic, "Vic Visitor", AppRole.Visitor);
        var svc = _db.Meetings();
        svc.CanAddAttendees(brief).Should().BeFalse();
        await FluentActions.Invoking(() => svc.AddAttendeeAsync(brief, "Sam")).Should().ThrowAsync<ForbiddenException>();
        (await Attendees(brief)).Should().BeEmpty();
    }

    [Fact]
    public async Task InARehearsal_NoOneAddsOrRemoves_ForNoMeetingIsLive()
    {
        // A rehearsal never starts or joins the meeting: whoever rehearses has no seat in one.
        var (brief, _) = await Brief();
        var svc = _db.Meetings();
        svc.CanAddAttendees(brief).Should().BeFalse();
        await FluentActions.Invoking(() => svc.AddAttendeeAsync(brief, "Sam")).Should().ThrowAsync<ForbiddenException>();

        _db.Live = new InMeeting(brief, Presenter);
        await _db.Meetings().AddAttendeeAsync(brief, "Sam");
        var sam = (await Attendees(brief)).Single();
        _db.Live = null;
        _db.Meetings().CanRemoveAttendee(sam).Should().BeFalse();
        await FluentActions.Invoking(() => _db.Meetings().RemoveAttendeeAsync(sam.Id)).Should().ThrowAsync<ForbiddenException>();
        (await Attendees(brief)).Should().ContainSingle();
    }

    [Fact]
    public async Task SomeoneNotInThisBriefsLiveMeeting_CantAdd()
    {
        var (brief, _) = await Brief();
        As(Val, "Val Viewer", AppRole.Viewer);
        _db.Live = new InMeeting(Guid.NewGuid(), Presenter);   // another day's meeting is live
        await FluentActions.Invoking(() => _db.Meetings().AddAttendeeAsync(brief, "Sam")).Should().ThrowAsync<ForbiddenException>();

        _db.Live = new InMeeting(brief, Presenter, Owner);     // this brief's, without Val in it
        _db.Meetings().CanAddAttendees(brief).Should().BeFalse();
        await FluentActions.Invoking(() => _db.Meetings().AddAttendeeAsync(brief, "Sam")).Should().ThrowAsync<ForbiddenException>();
        (await Attendees(brief)).Should().BeEmpty();
    }

    [Fact]
    public async Task AFollower_RemovesOnlyWhomTheyAdded_ThePresenterAnyoneAdded_AndNoOneSomeoneWhoJoined()
    {
        var (brief, _) = await Brief();
        _db.Live = new InMeeting(brief, Presenter);
        var svc = _db.Meetings();
        await svc.JoinedAsync(brief);                       // the presenter, joined
        await svc.AddAttendeeAsync(brief, "Pat from HR");   // added by the presenter
        As(Owner, "Olive Owner", AppRole.ActionOwner);
        await svc.JoinedAsync(brief);
        await svc.AddAttendeeAsync(brief, "Dana from Legal");
        As(Val, "Val Viewer", AppRole.Viewer);
        await svc.AddAttendeeAsync(brief, "Sam from the SOC");
        await svc.AddAttendeeAsync(brief, "Kim from IT");
        var rows = (await Attendees(brief)).ToDictionary(a => a.Name);

        // Val: their own, and nothing of anyone else's.
        svc.CanRemoveAttendee(rows["Sam from the SOC"]).Should().BeTrue();
        svc.CanRemoveAttendee(rows["Dana from Legal"]).Should().BeFalse();
        svc.CanRemoveAttendee(rows["Olive Owner"]).Should().BeFalse();
        await svc.RemoveAttendeeAsync(rows["Sam from the SOC"].Id);
        await FluentActions.Invoking(() => svc.RemoveAttendeeAsync(rows["Dana from Legal"].Id)).Should().ThrowAsync<ForbiddenException>("another follower's");
        await FluentActions.Invoking(() => svc.RemoveAttendeeAsync(rows["Pat from HR"].Id)).Should().ThrowAsync<ForbiddenException>("the presenter's");
        await FluentActions.Invoking(() => svc.RemoveAttendeeAsync(rows["Olive Owner"].Id)).Should().ThrowAsync<ForbiddenException>("joined");

        // The presenter: anyone added by name, but not someone who joined.
        As(Presenter, "Test Analyst", AppRole.Analyst);
        svc.CanRemoveAttendee(rows["Kim from IT"]).Should().BeTrue();
        svc.CanRemoveAttendee(rows["Olive Owner"]).Should().BeFalse();
        await svc.RemoveAttendeeAsync(rows["Dana from Legal"].Id);
        await svc.RemoveAttendeeAsync(rows["Kim from IT"].Id);
        await svc.RemoveAttendeeAsync(rows["Kim from IT"].Id);   // gone already: nothing to do
        await FluentActions.Invoking(() => svc.RemoveAttendeeAsync(rows["Olive Owner"].Id)).Should().ThrowAsync<ForbiddenException>("joined");

        (await Attendees(brief)).Select(a => a.Name).Should().Equal("Olive Owner", "Pat from HR", "Test Analyst");
        (await AuditedBy("Deleted")).Should().BeEquivalentTo(["Val Viewer", "Test Analyst", "Test Analyst"], "the audit says who removed each");
    }

    [Fact]
    public async Task AReader_IsRefusedRemovingAnyone_EvenOneNotThere()
    {
        As("S-1-5-21-READER", "Reader", AppRole.Reader);
        await FluentActions.Invoking(() => _db.Meetings().RemoveAttendeeAsync(Guid.NewGuid())).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Joined_RecordsMeOnce_KeepsTheFirstJoin_AndClearsWhenILeft()
    {
        var (brief, _) = await Brief();
        var time = new TestClock(new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
        var svc = _db.Meetings(time);

        await svc.JoinedAsync(brief);
        var first = time.Now;
        time.Now = time.Now.AddMinutes(5);
        await svc.LeftAsync(brief, new Dictionary<string, DateTimeOffset> { [_db.User.UserId] = time.Now });
        (await svc.GetAsync(brief)).Attendees.Single().LeftAtUtc.Should().Be(time.Now);

        time.Now = time.Now.AddMinutes(1);
        await svc.JoinedAsync(brief);

        var me = (await svc.GetAsync(brief)).Attendees.Should().ContainSingle().Subject;
        me.Name.Should().Be("Test Analyst");
        me.JoinedAtUtc.Should().Be(first, "joining again keeps the first join");
        me.LeftAtUtc.Should().BeNull("I'm back");
    }

    [Fact]
    public async Task AddAttendee_AddsSomeoneByName_OnceOnly_AndRemoveTakesThemOff()
    {
        var (brief, _) = await Brief();
        _db.Live = new InMeeting(brief, Presenter);
        var svc = _db.Meetings();
        await svc.JoinedAsync(brief);

        await svc.AddAttendeeAsync(brief, "  Sam from the SOC ");
        await svc.AddAttendeeAsync(brief, "sam from the soc");
        await svc.AddAttendeeAsync(brief, "Test Analyst");
        await FluentActions.Invoking(() => svc.AddAttendeeAsync(brief, "  ")).Should().ThrowAsync<ArgumentException>();

        var attendees = (await svc.GetAsync(brief)).Attendees;
        attendees.Select(a => a.Name).Should().Equal("Sam from the SOC", "Test Analyst");
        attendees[0].UserId.Should().BeEmpty();
        attendees[0].JoinedAtUtc.Should().BeNull();

        await svc.RemoveAttendeeAsync(attendees[0].Id);
        await svc.RemoveAttendeeAsync(Guid.NewGuid());
        (await svc.GetAsync(brief)).Attendees.Should().ContainSingle().Which.Name.Should().Be("Test Analyst");
    }

    [Fact]
    public async Task Notes_AreEachPersons_OnTheArticleOnScreen_AndOnlyTheirAuthorChangesThem()
    {
        var (brief, article) = await Brief();
        var svc = _db.Meetings();
        var mine = await svc.AddNoteAsync(brief, article, "  Patch the VPN first ");
        await svc.AddNoteAsync(brief, null, "Next week: the tabletop");
        await FluentActions.Invoking(() => svc.AddNoteAsync(brief, null, " ")).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => svc.AddNoteAsync(brief, Guid.NewGuid(), "x")).Should().ThrowAsync<ArgumentException>("not an article on the brief");
        await FluentActions.Invoking(() => svc.AddNoteAsync(brief, null, new string('x', MeetingService.MaxNote + 1))).Should().ThrowAsync<ArgumentException>();

        var notes = (await svc.GetAsync(brief)).Notes;
        notes.Select(n => n.Text).Should().Equal("Patch the VPN first", "Next week: the tabletop");
        notes[0].ArticleId.Should().Be(article);
        notes[0].AuthorName.Should().Be("Test Analyst");

        await svc.EditNoteAsync(mine, "Patch the VPN and the jump hosts");
        await svc.EditNoteAsync(mine, "Patch the VPN and the jump hosts");
        var edited = (await svc.GetAsync(brief)).Notes[0];
        edited.Text.Should().Be("Patch the VPN and the jump hosts");
        edited.EditedAtUtc.Should().NotBeNull();

        As("S-1-5-21-OWNER", "Owner", AppRole.ActionOwner);
        svc.CanNote.Should().BeTrue("an action owner takes notes too");
        await svc.AddNoteAsync(brief, article, "We'll take the sweep");
        await FluentActions.Invoking(() => svc.EditNoteAsync(mine, "defaced")).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => svc.DeleteNoteAsync(mine)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => svc.AddAttendeeAsync(brief, "Someone")).Should().ThrowAsync<ForbiddenException>("not in a live meeting");

        As("S-1-5-21-TEST-1", "Test Analyst", AppRole.Analyst);
        await svc.DeleteNoteAsync(mine);
        (await svc.GetAsync(brief)).Notes.Select(n => n.AuthorName).Should().Equal("Test Analyst", "Owner");
    }

    [Fact]
    public async Task SomeoneAddedByName_WhoThenJoins_IsListedOnce()
    {
        var (brief, _) = await Brief();
        _db.Live = new InMeeting(brief, Presenter);
        var svc = _db.Meetings();
        await svc.AddAttendeeAsync(brief, "test analyst");
        await svc.JoinedAsync(brief);

        var me = (await svc.GetAsync(brief)).Attendees.Should().ContainSingle().Subject;
        (me.UserId, me.Name).Should().Be(("S-1-5-21-TEST-1", "test analyst"));
        me.JoinedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ANoteCarriedOverFromTheOldNotes_CanBeEdited_ThoughLongerThanANewOne()
    {
        var (brief, _) = await Brief();
        var old = new string('x', MeetingService.MaxNote + 500);
        Guid id;
        await using (var ctx = _db.Context())
        {
            var note = new Tide.Domain.Entities.MeetingNote { BriefId = brief, AuthorId = _db.User.UserId, AuthorName = "Test Analyst", AtUtc = DateTimeOffset.UtcNow, Text = old };
            ctx.MeetingNotes.Add(note);
            await ctx.SaveChangesAsync();
            id = note.Id;
        }

        await _db.Meetings().EditNoteAsync(id, old[..^10] + " (edited)");
        (await _db.Meetings().GetAsync(brief)).Notes.Single().Text.Should().EndWith("(edited)", "up to the length it had");
        await FluentActions.Invoking(() => _db.Meetings().EditNoteAsync(id, old + " and more")).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => _db.Meetings().AddNoteAsync(brief, null, old)).Should().ThrowAsync<ArgumentException>("a new one is still 2,000 at most");
    }

    [Fact]
    public async Task AReader_CantSeeOrJoinTheMeeting()
    {
        var (brief, _) = await Brief();
        As("S-1-5-21-READER", "Reader", AppRole.Reader);
        var svc = _db.Meetings();
        svc.CanNote.Should().BeFalse();
        await FluentActions.Invoking(() => svc.GetAsync(brief)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => svc.JoinedAsync(brief)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task AMeetingOnABriefThatsGone_IsRefused()
    {
        var gone = Guid.NewGuid();
        _db.Live = new InMeeting(gone, Presenter);
        var svc = _db.Meetings();
        await FluentActions.Invoking(() => svc.JoinedAsync(Guid.NewGuid())).Should().ThrowAsync<KeyNotFoundException>();
        await FluentActions.Invoking(() => svc.AddAttendeeAsync(gone, "Sam")).Should().ThrowAsync<KeyNotFoundException>();
        await FluentActions.Invoking(() => svc.AddNoteAsync(Guid.NewGuid(), null, "x")).Should().ThrowAsync<KeyNotFoundException>();
        await FluentActions.Invoking(() => svc.EditNoteAsync(Guid.NewGuid(), "x")).Should().ThrowAsync<KeyNotFoundException>();
        await svc.LeftAsync(Guid.NewGuid(), new Dictionary<string, DateTimeOffset>());
    }

    [Fact]
    public async Task Assign_GivesTheActionToATeamMember_SetsItsDueDate_AndSaysSoInItsHistory()
    {
        var (_, article) = await Brief();
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        await _db.Teams().AddMemberAsync(team, "S-1-5-21-ROBIN", "Robin");
        var actions = _db.Actions();
        var number = await actions.CreateAsync(new ActionForm { Title = "Sweep the proxy logs", TeamId = team, ArticleId = article, Kind = ActionKind.IocSweep });
        Guid id;
        await using (var ctx = _db.Context()) id = await ctx.Actions.Where(a => a.Number == number).Select(a => a.Id).SingleAsync();
        var due = new DateOnly(2026, 9, 8);

        await actions.AssignAsync(id, "S-1-5-21-ROBIN", due);
        await actions.AssignAsync(id, "S-1-5-21-ROBIN", null);
        await FluentActions.Invoking(() => actions.AssignAsync(id, "S-1-5-21-STRANGER", null)).Should().ThrowAsync<ArgumentException>();

        await using (var ctx = _db.Context())
        {
            var a = await ctx.Actions.SingleAsync(x => x.Id == id);
            (a.AssigneeId, a.AssigneeName, a.DueDate).Should().Be(("S-1-5-21-ROBIN", "Robin", due));
            (await ctx.ActionEvents.CountAsync(e => e.ActionId == id && e.Text.Contains("at the meeting"))).Should().Be(1, "the repeat changed nothing");
        }

        await actions.AssignAsync(id, null, null);
        await using (var ctx = _db.Context()) (await ctx.Actions.SingleAsync(x => x.Id == id)).AssigneeName.Should().BeNull();

        // An analyst who holds it without being on its team (given it before holders had to be members) can still have
        // its due date moved.
        await using (var ctx = _db.Context())
            await ctx.Actions.Where(x => x.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.AssigneeId, _db.User.UserId).SetProperty(x => x.AssigneeName, _db.User.DisplayName));
        await actions.AssignAsync(id, _db.User.UserId, due.AddDays(2));
        await using (var ctx = _db.Context())
        {
            var a = await ctx.Actions.SingleAsync(x => x.Id == id);
            (a.AssigneeId, a.DueDate).Should().Be((_db.User.UserId, due.AddDays(2)));
        }

        var edition = await _db.Briefs().EditionActionsAsync((await _db.Briefs().GetAsync(Day))!);
        edition[article].Single().Should().Match<EditionAction>(r => r.Id == id && r.TeamId == team && r.AssigneeId == _db.User.UserId);
    }
}
