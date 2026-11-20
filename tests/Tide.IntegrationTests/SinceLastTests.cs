using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.History;
using Tide.Application.Preferences;
using Tide.Application.Reviews;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Today's "Since you last looked" (ADR 0030): what a person who was away sees, that their own changes and their first
/// look list nothing, that it shows only what the pages it links to would, that Mark all seen moves their mark, and that
/// it's bounded in time, in lines and in queries.
/// </summary>
public class SinceLastTests : IDisposable
{
    private TestDb _db = new();
    private const string Me = "S-1-5-21-TEST-1", Sam = "S-1-5-21-SAM";

    public SinceLastTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private ActivityService Activity() => new(_db, _db.User, _db.Preferences(), TimeProvider.System);

    private ReviewService Reviews() => new(_db, _db.User, _db.Rotation(), _db.Clock, TimeProvider.System);

    private void As(string id, string name, params AppRole[] roles)
    {
        (_db.User.UserId, _db.User.DisplayName) = (id, name);
        _db.User.Roles = roles.Length > 0 ? roles.ToHashSet() : new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
    }

    private void AsMe(params AppRole[] roles) => As(Me, "Test Analyst", roles);

    private async Task People()
    {
        await using var ctx = _db.Context();
        ctx.Users.AddRange(
            new AppUser { Sid = Me, DisplayName = "Test Analyst", RolesCsv = "Analyst,Admin" },
            new AppUser { Sid = Sam, DisplayName = "Sam", RolesCsv = "Analyst" });
        await ctx.SaveChangesAsync();
    }

    // My mark, set as though I last looked a while ago.
    private Task LastLooked(TimeSpan ago) =>
        _db.Preferences().SetAsync(UserPreferences.SeenToday, SeenMark.Format(DateTimeOffset.UtcNow - ago));

    private async Task<Guid> MyTeam(string? mailbox = null)
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, mailbox);
        await _db.Teams().AddMemberAsync(team, Me, "Test Analyst");
        return team;
    }

    private async Task<Guid> ArticleAsync(string title, TlpLevel tlp = TlpLevel.Clear)
    {
        var id = await _db.Articles().CreateAsync(title, "Vendor", null, tlp);
        var form = ArticleForm.From((await _db.Articles().GetAsync(id))!);
        form.Bluf = "What happened.";
        form.Bullets = [new() { Text = "First" }];
        await _db.Articles().SaveAsync(form);
        return id;
    }

    private async Task Edit(Guid article, string bluf)
    {
        var form = ArticleForm.From((await _db.Articles().GetAsync(article))!);
        form.Bluf = bluf;
        await _db.Articles().SaveAsync(form);
    }

    private async Task<Guid> ActionId(int number)
    {
        await using var ctx = _db.Context();
        return await ctx.Actions.Where(a => a.Number == number).Select(a => a.Id).SingleAsync();
    }

    [Fact]
    public async Task APersonWhoWasAway_SeesAColleaguesActionForTheirTeam_ACommentOnTheirAction_AReviewDecided_AndAnEdit_EachOnce_Grouped()
    {
        await People();
        var team = await MyTeam();
        var mine = await _db.Actions().CreateAsync(new ActionForm { Title = "Patch the gateways", TeamId = team, AssigneeId = Me, AssigneeName = "Test Analyst" });
        var reviewed = await ArticleAsync("Edge VPN zero-day");
        var edited = await ArticleAsync("Invoice phishing wave");
        var review = await Reviews().AskAsync(reviewed, Sam, null);
        await LastLooked(TimeSpan.FromHours(1));

        As(Sam, "Sam");
        var raised = await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep VPN logs", TeamId = team });
        await _db.Actions().AddCommentAsync(await ActionId(mine), "Vendor says Friday.");
        await _db.Actions().AddCommentAsync(await ActionId(mine), "Now Monday.");
        await Reviews().DecideAsync(review, approve: false, [], ReviewService.ContentVersion((await _db.Articles().GetAsync(reviewed))!));
        await Edit(edited, "Sam's sharper bottom line.");
        await Edit(edited, "Sam's sharper bottom line, again.");

        AsMe();
        var since = await Activity().SinceAsync();

        since.FirstLook.Should().BeFalse();
        since.Lines.Select(l => (l.Group, l.Title, l.Detail)).Should().BeEquivalentTo(new[]
        {
            (SinceGroup.ForYou, $"A-{raised} Sweep VPN logs", "Raised by Sam"),
            (SinceGroup.ForYou, $"A-{mine} Patch the gateways", "2 comments, the latest from Sam"),
            (SinceGroup.ForYou, "Edge VPN zero-day", "Sam asked for changes"),
            (SinceGroup.YourWork, "Invoice phishing wave", "Edited by Sam"),
        });
        since.Lines.Single(l => l.Title == "Edge VPN zero-day").Href.Should().Be($"articles/{reviewed}/review");
        since.Lines.Single(l => l.Title.StartsWith($"A-{raised} ", StringComparison.Ordinal)).Href.Should().Be($"actions/{raised}");
        since.Groups.Select(g => g.Key).Should().Equal(SinceGroup.ForYou, SinceGroup.YourWork);
        since.More.Should().Be(0);
    }

    [Fact]
    public async Task MyOwnChanges_AndOtherPeoplesWork_DontAppear()
    {
        await People();
        var team = await MyTeam();
        await _db.Teams().AddMemberAsync(team, Sam, "Sam");
        await LastLooked(TimeSpan.FromHours(1));
        var mine = await _db.Actions().CreateAsync(new ActionForm { Title = "My own", TeamId = team, AssigneeId = Me, AssigneeName = "Test Analyst" });
        await _db.Actions().AddCommentAsync(await ActionId(mine), "Note to self.");
        await Edit(await ArticleAsync("My own article"), "Mine, edited by me.");

        As(Sam, "Sam");
        await _db.Actions().CreateAsync(new ActionForm { Title = "Sam's", TeamId = team, AssigneeId = Sam, AssigneeName = "Sam" });
        await ArticleAsync("Sam's article");

        AsMe();
        (await Activity().SinceAsync()).Lines.Should().BeEmpty("my changes are mine, and Sam's own work isn't mine");
    }

    [Fact]
    public async Task UnassignedActions_OnATeamWithAMailbox_AreTheMailboxs_AsTheDigestHasIt()
    {
        await People();
        var team = await MyTeam(mailbox: "defense@example.org");
        await LastLooked(TimeSpan.FromHours(1));

        As(Sam, "Sam");
        await _db.Actions().CreateAsync(new ActionForm { Title = "For the mailbox", TeamId = team });

        AsMe();
        (await Activity().SinceAsync()).Lines.Should().BeEmpty("the personal digest's rule: the team's mailbox hears of its unassigned actions");
    }

    [Fact]
    public async Task OnTheFirstLook_NothingIsListed_AndTheMarkIsSet_SoTheNextLookCountsFromThere()
    {
        await People();
        var team = await MyTeam();
        As(Sam, "Sam");
        await _db.Actions().CreateAsync(new ActionForm { Title = "Before I ever looked", TeamId = team });

        AsMe();
        var first = await Activity().SinceAsync();
        (first.FirstLook, first.UpToDate).Should().Be((true, true), "nothing from before the card is called new to me");
        SeenMark.Parse(await _db.Preferences().GetAsync(UserPreferences.SeenToday)).Should().Be(first.ReadAtUtc);

        As(Sam, "Sam");
        var after = await _db.Actions().CreateAsync(new ActionForm { Title = "After I looked", TeamId = team });

        AsMe();
        var next = await Activity().SinceAsync();
        next.FirstLook.Should().BeFalse();
        next.Lines.Select(l => l.Title).Should().Equal($"A-{after} After I looked");
    }

    [Fact]
    public async Task MarkAllSeen_MovesTheMarkToWhenTheCardWasRead_AndEmptiesIt_ButNeverMovesItBack()
    {
        await People();
        var team = await MyTeam();
        await LastLooked(TimeSpan.FromHours(1));
        As(Sam, "Sam");
        await _db.Actions().CreateAsync(new ActionForm { Title = "Seen soon", TeamId = team });

        AsMe();
        var read = await Activity().SinceAsync();
        read.Lines.Should().HaveCount(1);
        await Activity().MarkSeenAsync(read.ReadAtUtc);
        SeenMark.Parse(await _db.Preferences().GetAsync(UserPreferences.SeenToday)).Should().Be(read.ReadAtUtc);
        (await Activity().SinceAsync()).UpToDate.Should().BeTrue();

        await Activity().MarkSeenAsync(read.ReadAtUtc.AddHours(-2));
        SeenMark.Parse(await _db.Preferences().GetAsync(UserPreferences.SeenToday)).Should().Be(read.ReadAtUtc, "an older tab's mark doesn't move it back");
        await Activity().MarkSeenAsync(DateTimeOffset.UtcNow.AddDays(1));
        SeenMark.Parse(await _db.Preferences().GetAsync(UserPreferences.SeenToday)).Should().BeOnOrBefore(DateTimeOffset.UtcNow, "never past now");
    }

    [Fact]
    public async Task ItReachesBackSevenDaysAtMost_HoweverOldTheMark()
    {
        await People();
        var team = await MyTeam();
        await LastLooked(TimeSpan.FromDays(30));
        As(Sam, "Sam");
        var old = await _db.Actions().CreateAsync(new ActionForm { Title = "Ten days ago", TeamId = team });
        var recent = await _db.Actions().CreateAsync(new ActionForm { Title = "Yesterday", TeamId = team });
        await using (var ctx = _db.PlainContext())
        {
            var a = await ctx.Actions.SingleAsync(x => x.Number == old);
            a.CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-10);
            var b = await ctx.Actions.SingleAsync(x => x.Number == recent);
            b.CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1);
            await ctx.SaveChangesAsync();
        }

        AsMe();
        var since = await Activity().SinceAsync();
        since.HeldToReach.Should().BeTrue();
        since.Lines.Select(l => l.Title).Should().Equal($"A-{recent} Yesterday");
    }

    [Fact]
    public async Task TidesOwnChange_ToMyAction_ReadsAsTide()
    {
        await People();
        var team = await MyTeam();
        var mine = await _db.Actions().CreateAsync(new ActionForm { Title = "Escalated overnight", TeamId = team, AssigneeId = Me, AssigneeName = "Test Analyst" });
        await LastLooked(TimeSpan.FromHours(1));
        await using (var ctx = _db.PlainContext())
        {
            var a = await ctx.Actions.SingleAsync(x => x.Number == mine);
            (a.ModifiedAtUtc, a.ModifiedBy, a.EscalatedAtUtc) = (DateTimeOffset.UtcNow, "system", DateTimeOffset.UtcNow);
            await ctx.SaveChangesAsync();
        }

        AsMe();
        (await Activity().SinceAsync()).Lines.Should().ContainSingle().Which.Detail.Should().StartWith("Changed by TIDE");
    }

    [Fact]
    public async Task AtMostFiftyLines_AreListed_WithHowManyMore()
    {
        await People();
        var team = await MyTeam();
        await SeedActionsAsync(team, 50);
        await SeedArticlesAsync(10);

        AsMe();
        var since = await Activity().SinceAsync();
        since.Lines.Should().HaveCount(ActivityService.MaxLines);
        since.More.Should().Be(10);
        since.Lines.Should().OnlyContain(l => l.Group == SinceGroup.ForYou, "what's for me comes first, and the cut falls on the rest");
    }

    [Fact]
    public async Task TheKevLine_IsForWhoeverTriages_AndAViewerSeesNoMoreThanTheirPagesShow_AndAVisitorIsRefused()
    {
        await People();
        await SeedKevAsync(3);
        await SeedSendAsync(Sam, "Sam");
        await LastLooked(TimeSpan.FromHours(1));
        var analyst = await Activity().SinceAsync();
        analyst.Lines.Should().ContainSingle(l => l.Href == "triage?cve=kev").Which.Title.Should().Be("3 new triage items on KEV");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Viewer };
        await _db.Preferences().SetAsync(UserPreferences.SeenToday, SeenMark.Format(DateTimeOffset.UtcNow.AddHours(-1)));
        var viewer = await Activity().SinceAsync();
        viewer.Lines.Should().NotContain(l => l.Href.StartsWith("triage", StringComparison.Ordinal), "a Viewer can't open Triage");
        viewer.Lines.Should().ContainSingle(l => l.Group == SinceGroup.TheDay && l.Title.StartsWith("Brief for", StringComparison.Ordinal),
            "the team edition's readers read the briefs");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Visitor };
        await FluentActions.Invoking(() => Activity().SinceAsync()).Should().ThrowAsync<ForbiddenException>("Today isn't a Visitor's");
    }

    [Fact]
    public async Task ARedArticleOfMine_IsListedForMe_AsItsPageWouldShowIt_AndNotForSomeoneWhoDidntWriteIt()
    {
        await People();
        var red = await ArticleAsync("Partner breach details", TlpLevel.Red);
        await LastLooked(TimeSpan.FromHours(1));

        As(Sam, "Sam");
        await Edit(red, "More from the partner.");
        (await _db.Articles().GetAsync(red)).Should().NotBeNull("the team reads every article in the app, RED too (known-issues.md, Security)");

        AsMe();
        (await Activity().SinceAsync()).Lines.Should().ContainSingle(l => l.Href == $"articles/{red}");

        As("S-1-5-21-PAT", "Pat", AppRole.Viewer);
        await _db.Preferences().SetAsync(UserPreferences.SeenToday, SeenMark.Format(DateTimeOffset.UtcNow.AddHours(-1)));
        (await Activity().SinceAsync()).Lines.Should().BeEmpty("it's nobody else's work to be told of");
    }

    [Fact]
    public async Task ItsQueries_AreAFixedFew_HoweverMuchChanged()
    {
        await People();
        var team = await MyTeam();
        await SeedActionsAsync(team, 1);
        AsMe();
        _db.Commands.Reset();
        await Activity().SinceAsync();
        var few = _db.Commands.Count;

        await SeedActionsAsync(team, 80, comments: 3);
        await SeedArticlesAsync(40);
        await SeedKevAsync(30);
        AsMe();
        await LastLooked(TimeSpan.FromHours(2));
        _db.Commands.Reset();
        await Activity().SinceAsync();
        _db.Commands.Count.Should().Be(few + 1, "the mark, then one query each for actions, articles, sends and new triage items, and one for their KEV CVEs once there are any");
        few.Should().Be(5);
    }

    // --- seeding (no interceptors, so who and when are set here as a colleague's changes would set them) -----------------

    private async Task SeedActionsAsync(Guid team, int n, int comments = 0)
    {
        await LastLooked(TimeSpan.FromHours(1));
        await using var ctx = _db.PlainContext();
        var next = (await ctx.Actions.IgnoreQueryFilters().MaxAsync(a => (int?)a.Number) ?? 0) + 1;
        for (var i = 0; i < n; i++)
        {
            var a = new ActionItem
            {
                Number = next + i, Title = $"Seeded {i}", TeamId = team, AssigneeId = Me, AssigneeName = "Test Analyst",
                CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = Sam,
            };
            for (var c = 0; c < comments; c++)
                a.Comments.Add(new ActionComment { Text = "Hi", AuthorId = Sam, AuthorName = "Sam", AtUtc = DateTimeOffset.UtcNow });
            ctx.Actions.Add(a);
        }
        await ctx.SaveChangesAsync();
    }

    private async Task SeedArticlesAsync(int n)
    {
        await using var ctx = _db.PlainContext();
        for (var i = 0; i < n; i++)
            ctx.Articles.Add(new Article
            {
                Title = $"Seeded article {i}", AuthorId = Me, AuthorName = "Test Analyst",
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1), CreatedBy = Me, ModifiedAtUtc = DateTimeOffset.UtcNow, ModifiedBy = Sam,
            });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedKevAsync(int n)
    {
        await using var ctx = _db.PlainContext();
        var source = new Source { Name = "Seeded feed", Url = $"https://feed.example.org/{Guid.NewGuid():N}" };
        ctx.Sources.Add(source);
        for (var i = 0; i < n; i++)
        {
            var cve = $"CVE-2026-{Random.Shared.Next(10000, 99999)}";
            ctx.CveRecords.Add(new CveRecord { CveId = cve, IsKev = true });
            ctx.FeedItems.Add(new FeedItem
            {
                SourceId = source.Id, Title = $"Exploited {i}", Url = $"https://feed.example.org/{Guid.NewGuid():N}", UrlHash = Guid.NewGuid().ToString("N"),
                FetchedAtUtc = DateTimeOffset.UtcNow, Cves = cve,
            });
        }
        await ctx.SaveChangesAsync();
    }

    // Today's brief, its team edition sent by someone, now.
    private async Task SeedSendAsync(string byId, string byName)
    {
        var brief = await _db.Briefs().GetOrStartAsync(_db.SendDay);
        await using var ctx = _db.PlainContext();
        ctx.BriefSends.Add(new BriefSend
        {
            BriefId = brief.Id, BriefDate = brief.BriefDate, Edition = Edition.Team, Subject = "Brief", Recipients = "team@example.org", RecipientCount = 1,
            SentAtUtc = DateTimeOffset.UtcNow, SentById = byId, SentByName = byName, Status = SendStatus.Sent,
        });
        await ctx.SaveChangesAsync();
    }
}
