using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.History;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// The team's opening note: carried from the previous brief when one starts, written by analysts and administrators until
/// the brief is sent, in the audit trail, and in the team email as it was sent, never in the company-wide edition.
/// </summary>
public class TeamNoteTests : IDisposable
{
    private readonly TestDb _db = new();
    private static readonly DateOnly Monday = new(2026, 10, 26), Tuesday = new(2026, 10, 27), Wednesday = new(2026, 10, 28);

    private const string Note = "Phishing trends this week\n\n- Invoice lures are up\n  - Most spoof a payroll vendor\n- Patch window on Thursday";

    public TeamNoteTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetOrStart_PrefillsTheNote_FromTheLatestEarlierBrief()
    {
        var briefs = _db.Briefs();
        var monday = await briefs.GetOrStartAsync(Monday);
        monday.TeamNote.Should().BeNull("there's no earlier brief");
        await briefs.SetTeamNoteAsync(monday.Id, Note);

        var tuesday = await briefs.GetOrStartAsync(Tuesday);
        tuesday.TeamNote.Should().Be(Note, "standing items roll forward");

        // Changed on Tuesday: Wednesday starts with Tuesday's, not Monday's.
        await briefs.SetTeamNoteAsync(tuesday.Id, "- Only this now");
        (await briefs.GetOrStartAsync(Wednesday)).TeamNote.Should().Be("- Only this now");

        // A brief already started isn't refilled, and a later one doesn't feed an earlier day.
        (await briefs.GetOrStartAsync(Tuesday)).TeamNote.Should().Be("- Only this now");
        (await briefs.GetOrStartAsync(new DateOnly(2026, 10, 23))).TeamNote.Should().BeNull("nothing is earlier than it");
    }

    [Fact]
    public async Task ACarriedNote_IsFlagged_UntilItsChangedOrKeptAsStillCurrent_AndNotOnceTheMeetingsHeld()
    {
        var briefs = _db.Briefs();
        var monday = await briefs.GetOrStartAsync(Monday);
        await briefs.SetTeamNoteAsync(monday.Id, Note);
        BriefService.TeamNoteCarriedOver((await briefs.GetAsync(Monday))!).Should().BeFalse("written on this brief");

        var tuesday = await briefs.GetOrStartAsync(Tuesday);
        BriefService.TeamNoteCarriedOver(tuesday).Should().BeTrue("Monday's, not looked at for Tuesday");

        // Saved unchanged: kept as still current.
        await briefs.SetTeamNoteAsync(tuesday.Id, Note);
        tuesday = (await briefs.GetAsync(Tuesday))!;
        BriefService.TeamNoteCarriedOver(tuesday).Should().BeFalse();
        tuesday.TeamNote.Should().Be(Note);
        tuesday.TeamNoteReviewedAtUtc.Should().NotBeNull();

        // Wednesday's starts flagged again; once its meeting is held there's nothing left to update it for.
        var wednesday = await briefs.GetOrStartAsync(Wednesday);
        BriefService.TeamNoteCarriedOver(wednesday).Should().BeTrue();
        wednesday.PresentedAtUtc = DateTimeOffset.UtcNow;
        BriefService.TeamNoteCarriedOver(wednesday).Should().BeFalse("the meeting's over");

        // A blank note has nothing to flag.
        BriefService.TeamNoteCarriedOver(new Tide.Domain.Entities.Brief()).Should().BeFalse();
    }

    [Fact]
    public async Task GetOrStart_AfterTheNoteWasCleared_StartsBlank()
    {
        var briefs = _db.Briefs();
        var monday = await briefs.GetOrStartAsync(Monday);
        await briefs.SetTeamNoteAsync(monday.Id, Note);
        var tuesday = await briefs.GetOrStartAsync(Tuesday);
        await briefs.SetTeamNoteAsync(tuesday.Id, "   ");

        (await briefs.GetAsync(Tuesday))!.TeamNote.Should().BeNull("blank clears it");
        (await briefs.GetOrStartAsync(Wednesday)).TeamNote.Should().BeNull("a cleared note stays cleared, rather than Monday's coming back");
    }

    [Fact]
    public async Task ABriefStartedByCarryingArticles_StartsWithTheNoteToo()
    {
        var briefs = _db.Briefs();
        var monday = await briefs.GetOrStartAsync(Monday);
        var article = await _db.Articles().CreateAsync("Carried article", "Vendor", null, TlpLevel.Clear);
        await briefs.AddArticleAsync(monday.Id, article);
        await briefs.SetTeamNoteAsync(monday.Id, Note);

        (await briefs.MarkPresentedAsync(monday.Id, carry: [article])).Should().Be(Tuesday);

        (await briefs.GetAsync(Tuesday))!.TeamNote.Should().Be(Note);
    }

    [Theory]
    [InlineData(AppRole.Analyst, true)]
    [InlineData(AppRole.Admin, true)]
    [InlineData(AppRole.ActionOwner, false)]
    [InlineData(AppRole.Reader, false)]
    public async Task SetTeamNote_IsForAnalystsAndAdministrators(AppRole role, bool allowed)
    {
        var brief = await _db.Briefs().GetOrStartAsync(Monday);
        _db.User.Roles = new HashSet<AppRole> { role };

        var act = () => _db.Briefs().SetTeamNoteAsync(brief.Id, Note);

        if (allowed) await act.Should().NotThrowAsync();
        else await act.Should().ThrowAsync<ForbiddenException>();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        (await _db.Briefs().GetAsync(Monday))!.TeamNote.Should().Be(allowed ? Note : null);
    }

    [Fact]
    public async Task SetTeamNote_IsRefused_OnceTheBriefIsSent_AndTheTeamEmailKeepsItAsSent()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var briefs = _db.Briefs();
        var brief = await briefs.GetOrStartAsync(Monday);
        await briefs.AddArticleAsync(brief.Id, await _db.Articles().CreateAsync("Open article", "Vendor", null, TlpLevel.Clear));
        await briefs.SetTeamNoteAsync(brief.Id, Note);
        await briefs.MarkPresentedAsync(brief.Id);
        await _db.Settings().SaveRecipientsAsync(Edition.Team, "cti@example.org");
        await _db.Settings().SaveRecipientsAsync(Edition.Public, "everyone@example.org");
        var svc = _db.Distribution();

        await svc.SendAsync(await svc.PlanAsync(Monday, Edition.Team));
        await svc.SendAsync(await svc.PlanAsync(Monday, Edition.Public));

        var act = () => briefs.SetTeamNoteAsync(brief.Id, "too late");
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*sent*");

        // The note changed under the frozen sends anyway (straight in the database): what went out stays as it went.
        await using (var ctx = _db.Context())
        {
            var row = await ctx.Briefs.SingleAsync();
            row.TeamNote = "- Rewritten afterwards";
            await ctx.SaveChangesAsync();
        }
        await using var read = _db.Context();
        var sends = await read.BriefSends.AsNoTracking().Where(s => !s.IsTest).ToListAsync();
        var team = sends.Single(s => s.Edition == Edition.Team);
        team.EmailHtml.Should().Contain("Invoice lures are up").And.Contain("Most spoof a payroll vendor").And.NotContain("Rewritten afterwards");
        team.WebHtml.Should().Contain("Invoice lures are up");

        var pub = sends.Single(s => s.Edition == Edition.Public);
        foreach (var leak in new[] { "Phishing trends this week", "Invoice lures", "payroll vendor", "Opening note" })
        {
            pub.EmailHtml.Should().NotContain(leak);
            pub.WebHtml.Should().NotContain(leak);
            var mail = _db.Email.Sent.Single(m => m.To.Contains("everyone@example.org"));
            mail.Html.Should().NotContain(leak);
            mail.Text.Should().NotContain(leak);
        }

        // The archive shows the company-wide send, and so never the note.
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var archived = await svc.GetPublishedAsync(Monday);
        archived!.WebHtml.Should().NotContain("Invoice lures").And.Contain("Open article");
    }

    [Fact]
    public async Task ThePublicApiAndTheSharePointCopy_NeverCarryTheNote()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var briefs = _db.Briefs();
        var brief = await briefs.GetOrStartAsync(Monday);
        await briefs.AddArticleAsync(brief.Id, await _db.Articles().CreateAsync("Open article", "Vendor", null, TlpLevel.Clear));
        await briefs.SetTeamNoteAsync(brief.Id, Note);

        var sharePoint = await _db.Distribution().SharePointAsync(Monday, id => $"img/{id}");
        sharePoint.Html.Should().NotContain("Invoice lures").And.Contain("Open article");

        var api = await _db.Api().BriefAsync(Monday, TlpLevel.Clear);
        System.Text.Json.JsonSerializer.Serialize(api).Should().NotContain("Invoice lures");

        var preview = await _db.Distribution().PreviewEmailAsync(Monday, Edition.Public, id => $"img/{id}");
        preview.Should().NotContain("Invoice lures");
        (await _db.Distribution().PreviewEmailAsync(Monday, Edition.Team, id => $"img/{id}")).Should().Contain("Invoice lures");
    }

    [Fact]
    public async Task SetTeamNote_IsInTheBriefsHistory_AndSavingItUnchangedRecordsNothing()
    {
        var briefs = _db.Briefs();
        var brief = await briefs.GetOrStartAsync(Monday);
        await briefs.SetTeamNoteAsync(brief.Id, "- First");
        var version = (await briefs.GetAsync(Monday))!.EditVersion;
        await briefs.SetTeamNoteAsync(brief.Id, "  - First  ");
        (await briefs.GetAsync(Monday))!.EditVersion.Should().Be(version, "the same note, trimmed, is no change");
        await briefs.SetTeamNoteAsync(brief.Id, "- Second");

        var page = await new HistoryService(_db, _db.User).BriefAsync(brief.Id);
        page.Entries[0].Changes.Should().Contain(new HistoryChange("Team opening note", "- First", "- Second"));
        page.Entries.SelectMany(e => e.Changes).Count(c => c.Field == "Team opening note").Should().Be(2);
    }

    [Fact]
    public async Task SetTeamNote_LongerThanAllowed_IsRefused()
    {
        var brief = await _db.Briefs().GetOrStartAsync(Monday);
        var act = () => _db.Briefs().SetTeamNoteAsync(brief.Id, new string('x', Application.Briefs.BriefService.TeamNoteMax + 1));
        await act.Should().ThrowAsync<ArgumentException>();
        var missing = () => _db.Briefs().SetTeamNoteAsync(Guid.NewGuid(), Note);
        await missing.Should().ThrowAsync<KeyNotFoundException>();
    }
}
