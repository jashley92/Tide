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
        await FluentActions.Invoking(() => svc.AddAttendeeAsync(brief, "Someone")).Should().ThrowAsync<ForbiddenException>("the presenter's");

        As("S-1-5-21-TEST-1", "Test Analyst", AppRole.Analyst);
        await svc.DeleteNoteAsync(mine);
        (await svc.GetAsync(brief)).Notes.Select(n => n.AuthorName).Should().Equal("Test Analyst", "Owner");
    }

    [Fact]
    public async Task SomeoneAddedByName_WhoThenJoins_IsListedOnce()
    {
        var (brief, _) = await Brief();
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
        var svc = _db.Meetings();
        await FluentActions.Invoking(() => svc.JoinedAsync(Guid.NewGuid())).Should().ThrowAsync<KeyNotFoundException>();
        await FluentActions.Invoking(() => svc.AddAttendeeAsync(Guid.NewGuid(), "Sam")).Should().ThrowAsync<KeyNotFoundException>();
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
