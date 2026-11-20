using System.Net;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Today's "Drafts in progress", for whoever writes the brief: the drafts still being written, yours first and then the
/// team's, each with who is writing it now and when it was last edited; a draft handed to you says so, with its note.
/// Readers see none of it. Its own app, so other tests' drafts don't reach it.
/// </summary>
public class TodayDraftsInProgressTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Me = SignIn.Analyst;
    private static readonly SignIn Sam = new("dev:dip-sam", "Sam Okafor", "Analyst");

    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private Dictionary<string, object?> TodayPage => new() { ["Day"] = $"{Today:yyyy-MM-dd}", ["AsToday"] = true };

    private async Task<Guid> Draft(string title, SignIn who, TlpLevel tlp = TlpLevel.Clear, ArticleStatus status = ArticleStatus.Draft)
    {
        Guid id;
        await using (var scope = SignedIn(app, who))
            id = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(title, "Vendor", null, tlp);
        if (status != ArticleStatus.Draft) await Change(id, a => a.Status = status);
        return id;
    }

    private async Task Change(Guid id, Action<Domain.Entities.Article> change)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        change(await db.Articles.SingleAsync(a => a.Id == id));
        await db.SaveChangesAsync();
    }

    // An edit some time ago, as a save then would have left it (written past the save's own stamp).
    private async Task EditedAgo(Guid id, TimeSpan ago)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        var at = DateTimeOffset.UtcNow - ago;
        await db.Articles.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ModifiedAtUtc, at));
    }

    [Fact]
    public async Task YourDraftsComeFirst_ThenTheTeams_EachMostRecentlyEditedFirst_WithTheWriterAndWhen()
    {
        var mineOld = await Draft("DIP mine, edited yesterday", Me);
        var mineNew = await Draft("DIP mine, edited just now", Me);
        var samsNew = await Draft("DIP Sam's, edited an hour ago", Sam);
        var samsOld = await Draft("DIP Sam's, edited two days ago", Sam);
        await Draft("DIP ready, not a draft", Me, status: ArticleStatus.Ready);
        await EditedAgo(mineOld, TimeSpan.FromDays(1));
        await EditedAgo(mineNew, TimeSpan.FromMinutes(1));
        await EditedAgo(samsNew, TimeSpan.FromHours(1));
        await EditedAgo(samsOld, TimeSpan.FromDays(2));

        await using var r = await RenderAsync<BriefPage>(app, "/", TodayPage);
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("Drafts in progress");
        r.Component.DraftsInProgress.Where(c => c.Title.StartsWith("DIP mine", StringComparison.Ordinal) || c.Title.StartsWith("DIP Sam's", StringComparison.Ordinal)).Select(c => c.Id)
            .Should().Equal([mineNew, mineOld, samsNew, samsOld], "yours first, then the team's, each most recently edited first");
        r.Component.DraftsInProgress.Should().OnlyContain(c => c.Status == ArticleStatus.Draft, "a ready article is in Not on a brief yet");
        r.Component.NotYetBriefed.Select(c => c.Title).Should().Contain("DIP ready, not a draft").And.NotContain("DIP mine, edited just now");
        html.Should().Contain("You're writing it").And.Contain("Sam Okafor &middot; edited".Replace("&middot;", "·"));
    }

    [Fact]
    public async Task ADraftHandedToYou_LeadsYourList_SaysSo_AndCarriesItsNote()
    {
        var handed = await Draft("DIP handed from Sam", Sam);
        await EditedAgo(handed, TimeSpan.FromDays(3));
        await Change(handed, a => (a.WriterId, a.WriterName, a.HandoffNote) = (Me.UserId, Me.Name, "Finish the IOCs"));

        await using var r = await RenderAsync<BriefPage>(app, "/", TodayPage);
        var drafts = r.Component.DraftsInProgress;
        var row = drafts.Single(c => c.Id == handed);
        (row.AuthorName, row.WriterId).Should().Be(("Sam Okafor", Me.UserId), "Sam stays the author; it's mine to finish");
        drafts.TakeWhile(c => c.WriterId == Me.UserId).Should().Contain(row, "it's among mine, which lead");
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("Handed to you").And.Contain("“Finish the IOCs”");

        await using var sams = await RenderAsync<BriefPage>(app, "/", TodayPage, Sam);
        sams.Component.DraftsInProgress.Single(c => c.Id == handed).WriterName.Should().Be(Me.Name, "Sam sees who has it now");
        sams.Component.DraftsInProgress.TakeWhile(c => c.WriterId == Sam.UserId).Should().NotContain(c => c.Id == handed, "it isn't Sam's any more");
    }

    [Fact]
    public async Task EveryMarking_IsListedWithItsTlp_ToWhoeverWritesTheBrief()
    {
        await Draft("DIP red draft", Sam, TlpLevel.Red);
        await Draft("DIP amber+strict draft", Sam, TlpLevel.AmberStrict);

        await using var r = await RenderAsync<BriefPage>(app, "/", TodayPage);
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("DIP red draft", "inside the app, the team reads every marking (T14)").And.Contain("DIP amber+strict draft")
            .And.Contain("TLP:RED").And.Contain("TLP:AMBER+STRICT");
    }

    [Fact]
    public async Task TheEditor_OffersHandTo_WhileItsBeingWritten_AndSaysWhoHasItNow()
    {
        var handed = await Draft("DIP editor, handed to Sam", Me);
        await Change(handed, a => (a.WriterId, a.WriterName) = (Sam.UserId, Sam.Name));
        var presented = await Draft("DIP editor, presented", Me, status: ArticleStatus.Presented);

        await using (var r = await RenderAsync<Web.Components.Shared.ArticleEditorPanel>(app, $"/articles/{handed}", new() { ["Id"] = handed }))
        {
            var html = WebUtility.HtmlDecode(await r.Html());
            html.Should().Contain("Hand to…").And.Contain($"By {Me.Name}").And.Contain("Sam Okafor is writing it now", "the author is kept; the writer is named");
        }
        await using (var r = await RenderAsync<Web.Components.Shared.ArticleEditorPanel>(app, $"/articles/{presented}", new() { ["Id"] = presented }))
            WebUtility.HtmlDecode(await r.Html()).Should().NotContain("Hand to…", "a presented article has nothing left to finish");
        await using (var r = await RenderAsync<Web.Components.Shared.ArticleEditorPanel>(app, $"/articles/{handed}", new() { ["Id"] = handed }, new SignIn("dev:dip-owner", "Robin", "ActionOwner")))
            WebUtility.HtmlDecode(await r.Html()).Should().NotContain("Hand to…", "only someone who may edit it hands it on");
    }

    [Theory]
    [InlineData("ActionOwner")]
    [InlineData("Viewer")]
    public async Task SomeoneWhoDoesntWriteTheBrief_SeesNoDraftsCard_NorAnyDraftsTitle(string role)
    {
        await Draft($"DIP draft hidden from a {role}", Sam);
        await Draft($"DIP red draft hidden from a {role}", Sam, TlpLevel.Red);

        await using var r = await RenderAsync<BriefPage>(app, "/", TodayPage, new SignIn($"dev:dip-{role}", "Robin", role));
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().NotContain("Drafts in progress").And.NotContain($"DIP draft hidden from a {role}").And.NotContain($"DIP red draft hidden from a {role}");
        r.Component.DraftsInProgress.Should().BeEmpty("the page reads no candidates for them");
    }
}
