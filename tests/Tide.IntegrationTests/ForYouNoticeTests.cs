using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Distribution;
using Tide.Application.Notices;
using Tide.Application.Reviews;
using Tide.Application.Rotation;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// For you notices: each save that hands someone something or asks something of them writes one, in that save, for them
/// and never for whoever made it; the person reads, counts and marks only their own; a kind they can no longer act on
/// isn't shown; what it's about is read when it's shown; old ones are pruned; and their open pages hear it.
/// </summary>
public class ForYouNoticeTests : IDisposable
{
    private readonly TestDb _db = new();
    private const string Me = "S-1-5-21-TEST-1", Sam = "S-1-5-21-SAM", Robin = "S-1-5-21-ROBIN";

    public ForYouNoticeTests()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        As(Me, "Test Analyst");
    }

    public void Dispose() => _db.Dispose();

    private void As(string id, string name) => (_db.User.UserId, _db.User.DisplayName) = (id, name);

    private ArticleHandoffService ArticleHandoffs() =>
        new(_db, _db.User, _db.Clock, _db.Email, Options.Create(new AppOptions { BaseUrl = "https://tide.example.org" }), _db.Settings(), _db.EmailTemplates);

    private ReviewService Reviews() => new(_db, _db.User, _db.Rotation(), _db.Clock, TimeProvider.System);

    private async Task People()
    {
        await using var ctx = _db.Context();
        ctx.Users.AddRange(
            new AppUser { Sid = Me, DisplayName = "Test Analyst", RolesCsv = "Analyst", Email = "me@example.org" },
            new AppUser { Sid = Sam, DisplayName = "Sam", RolesCsv = "Analyst", Email = "sam@example.org" },
            new AppUser { Sid = Robin, DisplayName = "Robin", RolesCsv = "Analyst", Email = "robin@example.org" });
        await ctx.SaveChangesAsync();
    }

    private async Task<List<Notice>> NoticesFor(string who)
    {
        await using var ctx = _db.Context();
        return await ctx.Notices.AsNoTracking().Where(n => n.UserId == who).OrderBy(n => n.CreatedAtUtc).ToListAsync();
    }

    private async Task<int> AllNotices()
    {
        await using var ctx = _db.Context();
        return await ctx.Notices.CountAsync();
    }

    private async Task<Guid> TeamWith(string name, params (string Id, string Name)[] members)
    {
        var team = await _db.Teams().AddAsync(name, null, null);
        foreach (var (id, who) in members) await _db.Teams().AddMemberAsync(team, id, who);
        return team;
    }

    [Fact]
    public async Task AnArticleHandedOn_TellsTheNewWriter_WithTheNote_AndTakingItOverTellsNobody()
    {
        await People();
        var id = await _db.Articles().CreateAsync("Half-written VPN advisory");

        await ArticleHandoffs().HandToAsync(id, Sam, "Finish the IOCs", Me);

        var n = (await NoticesFor(Sam)).Should().ContainSingle().Subject;
        (n.Kind, n.SubjectId, n.ActorId, n.ActorName, n.Detail, n.SeenAtUtc).Should()
            .Be((NoticeKind.ArticleHandedToYou, id, Me, "Test Analyst", "Finish the IOCs", (DateTimeOffset?)null));
        (await NoticesFor(Me)).Should().BeEmpty("whoever hands it on isn't told what they did");

        As(Sam, "Sam");
        var shown = (await _db.ForYou().ListAsync()).Should().ContainSingle().Subject;
        (shown.Line, shown.Title, shown.Link, shown.Seen).Should().Be(("Test Analyst handed you an article", "Half-written VPN advisory", $"articles/{id}", false));

        As(Robin, "Robin");
        await ArticleHandoffs().HandToAsync(id, Robin, null, Sam);   // Robin takes it over from Sam
        (await NoticesFor(Robin)).Should().BeEmpty("taking an article over isn't news to yourself");
    }

    [Fact]
    public async Task AReviewAsked_TellsTheReviewer_AndLinksToTheReview()
    {
        await People();
        var id = await _db.Articles().CreateAsync("Edge VPN appliance zero-day");

        await Reviews().AskAsync(id, Sam, "Check the TLP");

        var n = (await NoticesFor(Sam)).Should().ContainSingle().Subject;
        (n.Kind, n.SubjectId, n.Detail).Should().Be((NoticeKind.ReviewAsked, id, "Check the TLP"));
        As(Sam, "Sam");
        (await _db.ForYou().ListAsync()).Single().Link.Should().Be($"articles/{id}/review");
    }

    [Fact]
    public async Task AnActionHandedToATeam_TellsEachMemberButWhoeverHandedIt_AndAnExternalTeamNobody()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var from = await TeamWith("Cyber Defense", (Me, "Test Analyst"));
        var to = await TeamWith("Vulnerability Management", (Sam, "Sam"), (Robin, "Robin"), (Me, "Test Analyst"));
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = "Patch the gateways", TeamId = from });
        var id = (await _db.Actions().GetAsync(number))!.Action.Id;

        await _db.Handoffs().HandOffAsync(id, to, "Theirs to patch");

        foreach (var member in new[] { Sam, Robin })
        {
            var n = (await NoticesFor(member)).Should().ContainSingle().Subject;
            (n.Kind, n.SubjectId, n.Detail).Should().Be((NoticeKind.ActionHandedToTeam, id, "Theirs to patch"));
        }
        (await NoticesFor(Me)).Should().BeEmpty("the one who handed it is on the team, but did it");

        As(Sam, "Sam");
        var shown = (await _db.ForYou().ListAsync()).Single();
        (shown.Line, shown.Title, shown.Link).Should().Be(($"Test Analyst handed A-{number} to Vulnerability Management", $"A-{number} Patch the gateways", $"actions/{number}"));

        As(Me, "Test Analyst");
        var external = await _db.Teams().AddExternalAsync(new ExternalTeamForm { Name = "Managed SOC vendor", ContactName = "Vendor desk", ContactEmail = "vendor-soc@example.org" });
        var before = await AllNotices();
        await _db.Handoffs().HandOffAsync(id, external, "The vendor runs these");
        (await AllNotices()).Should().Be(before, "an external team hears through its own reminders");
    }

    [Fact]
    public async Task AnActionAssigned_TellsTheAssignee_OnceEvenWhenHandedToTheirTeamInTheSameSave_AndTakingItTellsNobody()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var from = await TeamWith("Cyber Defense", (Me, "Test Analyst"));
        var to = await TeamWith("Vulnerability Management", (Sam, "Sam"), (Robin, "Robin"));

        var made = await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep proxy logs", TeamId = to, AssigneeId = Sam, AssigneeName = "Sam" });
        var madeId = (await _db.Actions().GetAsync(made))!.Action.Id;
        (await NoticesFor(Sam)).Should().ContainSingle(n => n.Kind == NoticeKind.ActionAssigned && n.SubjectId == madeId, "made assigned to them");

        // The editor moves it to the other team and gives it to Robin: Robin is told it's theirs, not twice.
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = "Patch", TeamId = from });
        var id = (await _db.Actions().GetAsync(number))!.Action.Id;
        var form = ActionForm.From((await _db.Actions().GetAsync(number))!.Action);
        (form.TeamId, form.AssigneeId, form.AssigneeName, form.HandoffReason) = (to, Robin, "Robin", "Theirs to patch");
        await _db.Actions().SaveAsync(form);
        (await NoticesFor(Robin)).Where(n => n.SubjectId == id).Select(n => n.Kind).Should().Equal(NoticeKind.ActionAssigned);
        (await NoticesFor(Sam)).Where(n => n.SubjectId == id).Select(n => n.Kind).Should().Equal(NoticeKind.ActionHandedToTeam);

        As(Sam, "Sam");
        var before = await AllNotices();
        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        await _db.Actions().TakeAsync(id);
        (await AllNotices()).Should().Be(before, "taking an action on isn't news to yourself");
    }

    [Fact]
    public async Task ARotationRequest_TellsThePartner_AndItsDecisionTellsTheRequester()
    {
        await People();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };   // to set the rotation up
        var rotation = _db.Rotation();
        await rotation.AddMemberAsync(Me, "Test Analyst");
        await rotation.AddMemberAsync(Robin, "Robin");
        var week = RotationService.WeekStart(_db.Clock.Today).AddDays(7);
        await rotation.SetWeekAsync(week, Me);
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

        var change = await rotation.RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = week, GiveTo = week, Note = "Dentist" });
        var asked = (await NoticesFor(Robin)).Should().ContainSingle().Subject;
        (asked.Kind, asked.SubjectId, asked.Detail).Should().Be((NoticeKind.RotationAsked, change.Id, "Dentist"));

        As(Robin, "Robin");
        (await _db.ForYou().ListAsync()).Single().Line.Should().Be("Test Analyst asked you to cover brief days");
        await _db.Rotation().DeclineAsync(change.Id, "I'm out too");

        var decided = (await NoticesFor(Me)).Should().ContainSingle().Subject;
        (decided.Kind, decided.ActorName, decided.Detail).Should().Be((NoticeKind.RotationDecided, "Robin", "I'm out too"));
        As(Me, "Test Analyst");
        (await _db.ForYou().ListAsync()).Single().Line.Should().Be("Robin declined your cover request");
    }

    [Fact]
    public async Task SeenIsEachPersonsOwn_OneByOneOrAll_AndTheCountFollows()
    {
        await People();
        var first = await _db.Articles().CreateAsync("First");
        var second = await _db.Articles().CreateAsync("Second");
        await ArticleHandoffs().HandToAsync(first, Sam, null, Me);
        await ArticleHandoffs().HandToAsync(second, Sam, null, Me);
        await Reviews().AskAsync(first, Robin, null);

        As(Sam, "Sam");
        var forYou = _db.ForYou();
        (await forYou.UnseenCountAsync()).Should().Be(2);
        var robins = (await NoticesFor(Robin)).Single().Id;
        await forYou.SeenAsync(robins);
        (await NoticesFor(Robin)).Single().SeenAtUtc.Should().BeNull("nobody marks another's notice seen");

        var newest = (await forYou.ListAsync()).First();
        await forYou.SeenAsync(newest.Id);
        (await forYou.UnseenCountAsync()).Should().Be(1);
        (await forYou.ListAsync()).Should().HaveCount(2, "a seen notice stays listed").And.ContainSingle(n => n.Seen);

        var older = (await forYou.ListAsync()).Last();
        (await forYou.NewSinceAsync(older.AtUtc.AddTicks(-1))).Should().ContainSingle("the toast's: only new ones, made after the time given")
            .Which.Id.Should().Be(older.Id);
        (await forYou.NewSinceAsync(older.AtUtc)).Should().BeEmpty("the newer one was seen");

        (await forYou.AllSeenAsync()).Should().Be(1);
        (await forYou.UnseenCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AKindThePersonCanNoLongerActOn_IsntShownOrCounted_AndWhatsGoneSaysSo()
    {
        await People();
        var kept = await _db.Articles().CreateAsync("Kept");
        var gone = await _db.Articles().CreateAsync("Deleted later");
        await ArticleHandoffs().HandToAsync(kept, Sam, null, Me);
        await ArticleHandoffs().HandToAsync(gone, Sam, null, Me);
        await _db.Articles().DeleteAsync(gone);

        As(Sam, "Sam");
        var shown = await _db.ForYou().ListAsync();
        shown.Single(n => n.Title is null).Link.Should().BeNull("there's nothing to open");
        shown.Should().ContainSingle(n => n.Title == "Kept");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        _db.ForYou().CanReceive.Should().BeFalse("a reader is sent no notices, so has no bell");
        (await _db.ForYou().UnseenCountAsync()).Should().Be(0);
        (await _db.ForYou().ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Pruning_RemovesThoseSeenLongAgo_AndThoseNeverSeenForTooLong()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = _db.Context())
        {
            Notice N(string label, DateTimeOffset made, DateTimeOffset? seen) =>
                new() { UserId = Sam, Kind = NoticeKind.ReviewAsked, SubjectId = Guid.NewGuid(), ActorId = Me, ActorName = label, CreatedAtUtc = made, SeenAtUtc = seen };
            ctx.Notices.AddRange(
                N("seen-recently", now.AddDays(-40), now.AddDays(-NoticeService.KeepSeenDays + 1)),
                N("seen-long-ago", now.AddDays(-40), now.AddDays(-NoticeService.KeepSeenDays - 1)),
                N("waiting", now.AddDays(-NoticeService.KeepUnseenDays + 1), null),
                N("waiting-too-long", now.AddDays(-NoticeService.KeepUnseenDays - 1), null));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.Context()) (await NoticeService.PruneAsync(ctx, now)).Should().Be(2);
        (await NoticesFor(Sam)).Select(n => n.ActorName).Should().BeEquivalentTo(["seen-recently", "waiting"]);
    }

    [Fact]
    public async Task ANewNotice_IsHeardOnTheRecipientsTopic()
    {
        await People();
        var id = await _db.Articles().CreateAsync("Heard live");
        var heard = new List<Change>();
        _db.Feed.Changed += c => { lock (heard) heard.Add(c); };

        await ArticleHandoffs().HandToAsync(id, Sam, null, Me);

        heard.Where(c => c.Topic == ChangeFeed.Notices(Sam)).Should().ContainSingle().Which.ActorId.Should().Be(Me);
        heard.Should().NotContain(c => c.Topic == ChangeFeed.Notices(Me));
    }
}
