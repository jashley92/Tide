using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Briefs;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>A meeting's notes becoming follow-ups (raised as an action, or marked done), and the recap of an ended meeting.</summary>
public class MeetingNoteFollowUpTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 1);
    private readonly TestDb _db = new();

    // An analyst who also administers, to set up the teams.
    public MeetingNoteFollowUpTests() => As(Analyst, "Test Analyst", AppRole.Analyst, AppRole.Admin);

    public void Dispose() => _db.Dispose();

    private const string Analyst = "S-1-5-21-TEST-1";
    private const string Andy = "S-1-5-21-ANDY";
    private const string Val = "S-1-5-21-VAL";

    private void As(string id, string name, params AppRole[] roles)
    {
        _db.User.UserId = id;
        _db.User.DisplayName = name;
        _db.User.Roles = roles.ToHashSet();
    }

    private async Task<(Guid Brief, Guid Article)> Brief(string title = "Edge VPN zero-day")
    {
        var article = await _db.Articles().CreateAsync(title);
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, article);
        return (brief.Id, article);
    }

    private async Task<Tide.Domain.Entities.MeetingNote> Note(Guid id)
    {
        await using var ctx = _db.Context();
        return await ctx.MeetingNotes.AsNoTracking().SingleAsync(n => n.Id == id);
    }

    [Fact]
    public async Task RaisingMyNote_MakesAnActionOnItsArticle_LinkedBothWays_AndASecondRaiseIsRefused()
    {
        var (brief, article) = await Brief();
        await _db.Teams().AddAsync("Cyber Defense", null, null);
        var svc = _db.Meetings();
        var note = await svc.AddNoteAsync(brief, article, "Ask the vendor\nif the hotfix covers 9.x");

        var added = await svc.RaiseFromNoteAsync(note);

        var action = (await _db.Actions().GetAsync(added.Number))!.Action;
        action.Title.Should().Be("Ask the vendor if the hotfix covers 9.x", "the note's text, on one line");
        action.ArticleId.Should().Be(article, "the article the note was written against");
        action.Kind.Should().Be(ActionKind.Outreach, "guessed from its wording, as present mode's Raise guesses");
        action.RaisedOn.Should().Be(Day, "a past day's note is raised for that day's brief, as present mode raises it");
        (await Note(note)).ActionId.Should().Be(action.Id, "the note keeps the action it raised");
        var record = await svc.GetAsync(brief);
        record.RaisedAs(record.Notes.Single()).Should().Be(added.Number, "the page says \"Raised as A-n\"");

        await FluentActions.Invoking(() => svc.RaiseFromNoteAsync(note)).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"That note was raised already, as A-{added.Number}.");
        await using var ctx = _db.Context();
        (await ctx.Actions.CountAsync(a => a.ArticleId == article)).Should().Be(1, "no second action");
    }

    [Fact]
    public async Task ANoteOnTheMeeting_RaisesAnActionOnNoArticle_ForTheTeamThatLastTookThatKind()
    {
        var (brief, article) = await Brief();
        await _db.Teams().AddAsync("Alpha", null, null);
        var hunters = await _db.Teams().AddAsync("Threat Hunting", null, null);
        // The last sweep went to Threat Hunting: a sweep raised from a note goes there too, as present mode's Raise picks.
        await _db.Actions().QuickAddAsync(article, "Sweep for the C2", ActionKind.IocSweep, hunters, null);
        var svc = _db.Meetings();
        var note = await svc.AddNoteAsync(brief, null, "Sweep the proxy logs for the new domains");

        var action = (await _db.Actions().GetAsync((await svc.RaiseFromNoteAsync(note)).Number))!.Action;
        (action.ArticleId, action.TeamId, action.Kind).Should().Be(((Guid?)null, hunters, ActionKind.IocSweep));
    }

    [Fact]
    public async Task SomeoneElsesNote_CantBeRaisedOrMarkedDone()
    {
        var (brief, article) = await Brief();
        await _db.Teams().AddAsync("Cyber Defense", null, null);
        var mine = await _db.Meetings().AddNoteAsync(brief, article, "Patch the gateways");

        As(Andy, "Andy Analyst", AppRole.Analyst);
        var svc = _db.Meetings();
        await FluentActions.Invoking(() => svc.RaiseFromNoteAsync(mine)).Should().ThrowAsync<ForbiddenException>()
            .WithMessage("Only its author can raise a note as an action.");
        await FluentActions.Invoking(() => svc.MarkNoteDoneAsync(mine)).Should().ThrowAsync<ForbiddenException>()
            .WithMessage("Only its author can mark a note done.");
        var note = await Note(mine);
        (note.ActionId, note.DoneAtUtc).Should().Be(((Guid?)null, (DateTimeOffset?)null));
        await using var ctx = _db.Context();
        (await ctx.Actions.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task MarkingMyNoteDone_RecordsWhen_AndReopeningClearsIt()
    {
        var (brief, _) = await Brief();
        var time = new TestClock(new(2026, 9, 1, 15, 0, 0, TimeSpan.Zero));
        var svc = _db.Meetings(time);
        var note = await svc.AddNoteAsync(brief, null, "Next week: the tabletop");

        await svc.MarkNoteDoneAsync(note);
        (await Note(note)).DoneAtUtc.Should().Be(time.Now);
        time.Now = time.Now.AddHours(1);
        await svc.MarkNoteDoneAsync(note);
        (await Note(note)).DoneAtUtc.Should().Be(time.Now.AddHours(-1), "done already: when it was done stands");

        await svc.MarkNoteDoneAsync(note, done: false);
        (await Note(note)).DoneAtUtc.Should().BeNull();
    }

    // A Viewer takes notes (MeetingNotes) but can't create actions: they mark their own notes done, and raising one is
    // refused, as it is in the page, which offers them no Raise.
    [Fact]
    public async Task AViewer_MarksTheirOwnNoteDone_ButCantRaiseIt()
    {
        var (brief, article) = await Brief();
        await _db.Teams().AddAsync("Cyber Defense", null, null);
        As(Val, "Val Viewer", AppRole.Viewer);
        var svc = _db.Meetings();
        svc.CanNote.Should().BeTrue();
        svc.CanRaiseNotes.Should().BeFalse();
        var note = await svc.AddNoteAsync(brief, article, "Ask the SOC about the new detections");

        await svc.MarkNoteDoneAsync(note);
        (await Note(note)).DoneAtUtc.Should().NotBeNull();
        await FluentActions.Invoking(() => svc.RaiseFromNoteAsync(note)).Should().ThrowAsync<ForbiddenException>();

        As(Analyst, "Test Analyst", AppRole.Analyst);
        await FluentActions.Invoking(() => _db.Meetings().MarkNoteDoneAsync(note, done: false)).Should().ThrowAsync<ForbiddenException>("an analyst can't change a Viewer's note");

        // A Reader takes no notes, so has none to raise or mark.
        As("S-1-5-21-READER", "Rae Reader", AppRole.Reader);
        _db.Meetings().CanNote.Should().BeFalse();
        await FluentActions.Invoking(() => _db.Meetings().MarkNoteDoneAsync(note)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task TheRecap_ListsWhatTheWrapUpDoes_TheNotesOpenOrDone_TheCarriedArticles_AndWhoAttended()
    {
        var (brief, vpn) = await Brief();
        var later = await _db.Articles().CreateAsync("Phishing wave, for next time");
        await _db.Briefs().AddArticleAsync(brief, later);
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        await _db.Teams().AddMemberAsync(team, Analyst, "Test Analyst");
        await _db.Actions().QuickAddAsync(vpn, "Patch the gateways", ActionKind.PatchRemediate, team, null);
        var meetings = _db.Meetings();
        var raise = await meetings.AddNoteAsync(brief, vpn, "Ask the vendor about 9.x");
        await meetings.RaiseFromNoteAsync(raise);
        var done = await meetings.AddNoteAsync(brief, null, "Book the tabletop");
        await meetings.MarkNoteDoneAsync(done);
        await meetings.JoinedAsync(brief);
        await meetings.AddAttendeeAsync(brief, "Sam from the SOC");
        var briefs = _db.Briefs();
        var next = await briefs.MarkPresentedAsync(brief, carry: [later]);

        var b = (await briefs.GetAsync(Day))!;
        var edition = BriefEditions.Build(b, Edition.Team, TlpLevel.AmberStrict, await briefs.EditionActionsAsync(b));
        var record = await meetings.GetAsync(brief);
        var recap = WrapUp.Recap(edition, record, await briefs.CarriedAsync(brief));

        recap.WhoHasWhat.Should().BeEquivalentTo(WrapUp.WhoHasWhat(edition.Articles.SelectMany(x => x.Actions)), o => o.WithStrictOrdering(),
            "the wrap-up slide's own list");
        recap.WhoHasWhat.SelectMany(g => g.Actions).Select(a => a.Title).Should().BeEquivalentTo("Patch the gateways", "Ask the vendor about 9.x");
        recap.WhoHasWhat.Should().ContainSingle().Which.Who.Should().Be("Test Analyst · Cyber Defense");
        recap.Notes.Select(n => (n.Text, n.DoneAtUtc is not null)).Should().Equal(("Ask the vendor about 9.x", false), ("Book the tabletop", true));
        recap.OpenNotes.Should().Be(1);
        recap.Carried.Should().ContainSingle().Which.Should().Be(new CarriedArticle(later, "Phishing wave, for next time", next!.Value));
        recap.Attendees.Select(a => a.Name).Should().Equal("Sam from the SOC", "Test Analyst");
    }

    [Fact]
    public void ANotesTitle_IsItsTextOnOneLine_CutToAnActionsLongest()
    {
        MeetingService.ActionTitle("  Patch\nthe  gateways ").Should().Be("Patch the gateways");
        var cut = MeetingService.ActionTitle(new string('x', 400));
        cut.Should().HaveLength(Tide.Domain.Entities.ActionItem.MaxTitle).And.EndWith("…");
    }
}
