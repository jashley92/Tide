using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Articles;
using Tide.Application.Distribution;
using Tide.Application.History;
using Tide.Application.Preferences;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Handing an article to a colleague to finish (ADR 0031): the author stays the credit, the writer moves; who may hand it
/// on and to whom; the history line and the audit row; the new writer's email; and "mine" following the writer.
/// </summary>
public class ArticleHandoffTests : IDisposable
{
    private readonly TestDb _db = new();
    private const string Me = "S-1-5-21-TEST-1", Sam = "S-1-5-21-SAM", Pat = "S-1-5-21-PAT", Ray = "S-1-5-21-RAY";

    public ArticleHandoffTests()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        As(Me, "Test Analyst");
    }

    public void Dispose() => _db.Dispose();

    private ArticleHandoffService Handoffs() =>
        new(_db, _db.User, _db.Clock, _db.Email, Options.Create(new AppOptions { BaseUrl = "https://tide.example.org" }), _db.Settings(), _db.EmailTemplates);

    private void As(string id, string name) => (_db.User.UserId, _db.User.DisplayName) = (id, name);

    private async Task People()
    {
        await using var ctx = _db.Context();
        ctx.Users.AddRange(
            new AppUser { Sid = Me, DisplayName = "Test Analyst", RolesCsv = "Analyst", Email = "me@example.org" },
            new AppUser { Sid = Sam, DisplayName = "Sam", RolesCsv = "Analyst", Email = "sam@example.org" },
            new AppUser { Sid = Pat, DisplayName = "Pat", RolesCsv = "Reader", Email = "pat@example.org" },
            new AppUser { Sid = Ray, DisplayName = "Ray", RolesCsv = "ActionOwner", Email = "ray@example.org" });
        await ctx.SaveChangesAsync();
    }

    private async Task<Article> Saved(Guid id) => (await _db.Articles().GetAsync(id))!;

    [Fact]
    public async Task HandingOn_MovesTheWriter_KeepsTheAuthor_AndLeavesAnOpenEditorAbleToSave()
    {
        await People();
        var id = await _db.Articles().CreateAsync("Half-written VPN advisory");
        var open = ArticleForm.From(await Saved(id));   // an editor open on it, before the hand-off

        await Handoffs().HandToAsync(id, Sam, "  Finish the IOCs  ", Me);

        var a = await Saved(id);
        (a.AuthorId, a.AuthorName).Should().Be((Me, "Test Analyst"), "the author stays the credit");
        (a.CurrentWriterId, a.CurrentWriterName, a.HandoffNote).Should().Be((Sam, "Sam", "Finish the IOCs"));
        a.WriterSinceUtc.Should().NotBeNull();
        a.EditVersion.Should().Be(open.EditVersion, "nothing anyone wrote changed");

        open.Bluf = "Saved from the editor that was open.";
        await FluentActions.Invoking(() => _db.Articles().SaveAsync(open)).Should().NotThrowAsync("an open editor isn't made stale by a hand-off");
        (await Saved(id)).CurrentWriterId.Should().Be(Sam, "a save of the form doesn't take the article back");
    }

    [Fact]
    public async Task EditingRights_MoveWithIt_TheNewWriterSaves_AndOnlyMineFollowsTheWriter()
    {
        await People();
        var id = await _db.Articles().CreateAsync("Handed on, then finished");
        (await _db.Briefs().CandidatesAsync(Guid.Empty)).Single(c => c.Id == id).WriterId.Should().Be(Me, "the author writes it until it's handed on");

        await Handoffs().HandToAsync(id, Sam, null, Me);

        As(Sam, "Sam");
        var form = ArticleForm.From(await Saved(id));
        form.Bluf = "Finished by Sam.";
        await _db.Articles().SaveAsync(form);
        var picked = (await _db.Briefs().CandidatesAsync(Guid.Empty)).Single(c => c.Id == id);
        (picked.WriterId, picked.WriterName, picked.AuthorName).Should().Be((Sam, "Sam", "Test Analyst"), "the picker's \"Only mine\" is Sam's now");
        (await Saved(id)).Bluf.Should().Be("Finished by Sam.");

        As(Me, "Test Analyst");
        var mine = ArticleForm.From(await Saved(id));
        mine.Bluf = "Drafts are the team's: the author can still edit it.";
        await FluentActions.Invoking(() => _db.Articles().SaveAsync(mine)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task TheHistory_ReadsHandedFromTo_WithTheNote_AndTheAuditLogRecordsWho()
    {
        await People();
        var id = await _db.Articles().CreateAsync("Handed on, with a note");
        await Handoffs().HandToAsync(id, Sam, "Finish the IOCs", Me);

        var history = await new HistoryService(_db, _db.User).ArticleAsync(id);
        var line = history.Entries[0];
        line.What.Should().Be("Handed from Test Analyst to Sam");
        line.UserName.Should().Be("Test Analyst");
        line.Changes.Should().ContainSingle().Which.Should().Be(new HistoryChange("Hand-off note", null, "Finish the IOCs"));

        await using var ctx = _db.Context();
        var key = id.ToString();
        var row = await ctx.AuditLog.Where(e => e.EntityType == nameof(Article) && e.EntityKey == key && e.Action == "Modified").SingleAsync();
        row.UserId.Should().Be(Me);
        row.ChangesJson.Should().Contain("\"WriterId\"").And.Contain(Sam);
    }

    [Fact]
    public async Task OnlySomeoneWhoWritesArticles_HandsOn_AndOnlyToSomeoneWhoDoes()
    {
        await People();
        var id = await _db.Articles().CreateAsync("Who may");

        (await Handoffs().ChoicesAsync(id)).Select(c => c.Name).Should().Equal(["Sam"], "everyone who writes articles but its writer: not a Reader or an Action owner");
        await FluentActions.Invoking(() => Handoffs().HandToAsync(id, Pat, null, Me)).Should().ThrowAsync<ArgumentException>("a Reader can't write it");
        await FluentActions.Invoking(() => Handoffs().HandToAsync(id, Ray, null, Me)).Should().ThrowAsync<ArgumentException>("nor an Action owner");
        await FluentActions.Invoking(() => Handoffs().HandToAsync(id, Me, null, Me)).Should().ThrowAsync<ArgumentException>("it's already theirs");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Invoking(() => Handoffs().HandToAsync(id, Sam, null, Me)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => Handoffs().ChoicesAsync(id)).Should().ThrowAsync<ForbiddenException>();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        (await Saved(id)).CurrentWriterId.Should().Be(Me);
    }

    [Fact]
    public async Task AColleague_CanTakeItOver_ButNotOverAHandOffTheyHaventSeen_NorOnceItsPresented()
    {
        await People();
        var id = await _db.Articles().CreateAsync("Taken over");

        As(Sam, "Sam");
        (await Handoffs().ChoicesAsync(id)).Should().ContainSingle(c => c.Me && c.Id == Sam, "handing it to yourself takes it over");
        await Handoffs().HandToAsync(id, Sam, null, Me);
        (await Saved(id)).CurrentWriterId.Should().Be(Sam);
        _db.Email.Sent.Should().BeEmpty("no one is told they took something themselves");

        As(Me, "Test Analyst");
        await FluentActions.Invoking(() => Handoffs().HandToAsync(id, Me, null, expectedWriterId: Me))
            .Should().ThrowAsync<StaleEditException>("the page showed it as theirs; Sam has it now");

        await _db.Articles().SetStatusAsync(id, ArticleStatus.Presented);
        await FluentActions.Invoking(() => Handoffs().HandToAsync(id, Me, null, Sam)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TheNewWriter_IsEmailed_MarkedWithTheTlp_UnlessTheyTurnedItOff()
    {
        await People();
        var id = await _db.Articles().CreateAsync("Fortinet heap overflow", tlp: TlpLevel.Amber);
        await Handoffs().HandToAsync(id, Sam, "Finish the IOCs", Me);

        var mail = _db.Email.Sent.Should().ContainSingle().Subject;
        mail.To.Should().Equal("sam@example.org");
        mail.Subject.Should().Be("TIDE: Test Analyst handed you an article [TLP:AMBER]");
        mail.Html.Should().Contain("Fortinet heap overflow").And.Contain("Finish the IOCs").And.Contain($"https://tide.example.org/articles/{id}");
        mail.Template.Should().Be(EmailTemplateCatalog.ArticleHandoff.Id);

        await using (var ctx = _db.Context())
        {
            ctx.UserPreferences.Add(new UserPreference { UserId = Me, Name = UserPreferences.NotifyArticleHandoffs, Value = UserPreferences.Off, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await ctx.SaveChangesAsync();
        }
        As(Sam, "Sam");
        await Handoffs().HandToAsync(id, Me, null, Sam);
        _db.Email.Sent.Should().HaveCount(1, "they turned it off in My notifications; Today still shows it");
    }

    [Fact]
    public async Task AnArticleAboveTheTeamCeiling_IsntNamedInTheEmail_NorIsItsNote()
    {
        await People();
        var id = await _db.Articles().CreateAsync("Named only inside TIDE", tlp: TlpLevel.Red);
        await Handoffs().HandToAsync(id, Sam, "The implant beacons to 203.0.113.9", Me);

        var mail = _db.Email.Sent.Should().ContainSingle().Subject;
        mail.Subject.Should().EndWith("[TLP:RED]");
        mail.Html.Should().NotContain("Named only inside TIDE").And.Contain("a draft marked TLP:RED")
            .And.NotContain("203.0.113.9", "the note may say as much as the title").And.Contain("open TIDE to read it");
        (await Saved(id)).HandoffNote.Should().Be("The implant beacons to 203.0.113.9", "it's kept in TIDE, for the writer");
    }

    [Fact]
    public async Task OfTwoHandOffsAtOnce_TheSecondIsRefused_AndOnlyTheFirstWriterIsTold()
    {
        await People();
        const string Ada = "S-1-5-21-ADA";
        await using (var ctx = _db.Context())
        {
            ctx.Users.Add(new AppUser { Sid = Ada, DisplayName = "Ada", RolesCsv = "Analyst", Email = "ada@example.org" });
            await ctx.SaveChangesAsync();
        }
        var id = await _db.Articles().CreateAsync("Handed twice at once");

        // A colleague's hand-off to Ada lands after this one read the article (Me has it) and before it writes.
        _db.Commands.Before(sql => sql.StartsWith("UPDATE", StringComparison.Ordinal) && sql.Contains("\"WriterId\"", StringComparison.Ordinal), () =>
        {
            using var ctx = _db.PlainContext();
            ctx.Articles.Where(a => a.Id == id).ExecuteUpdate(u => u.SetProperty(a => a.WriterId, Ada).SetProperty(a => a.WriterName, "Ada"));
        });

        await FluentActions.Invoking(() => Handoffs().HandToAsync(id, Sam, "Finish it", Me))
            .Should().ThrowAsync<StaleEditException>("Ada has it now, and this hand-off was from Me");
        var a = await Saved(id);
        (a.CurrentWriterId, a.HandoffNote).Should().Be((Ada, (string?)null), "the colleague's hand-off stands");
        _db.Email.Sent.Should().BeEmpty("Sam was never handed it, so isn't told");
    }
}
