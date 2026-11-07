using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Adversaries;
using Tide.Application.Articles;
using Tide.Application.Intake;
using Tide.Application.Metrics;
using Tide.Application.Scorecard;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Pages.Admin;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Bug round 4, the concurrency round's follow-ups, services: a source's form saved over another
/// administrator's change, and lists whose places tie (two added at once) moving the wrong item.</summary>
public class FollowUpsRound4Tests : IDisposable
{
    private TestDb _db = new();

    public FollowUpsRound4Tests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    // --- a source's form ------------------------------------------------------------------------------------------

    private async Task<Source> NewSource(string name)
    {
        var id = await _db.Intake().SaveSourceAsync(new SourceForm { Name = name, Url = $"https://{name.ToLowerInvariant()}.example.com/feed", PollMinutes = 60 });
        return (await _db.Intake().SourcesAsync(includeRetired: true)).Single(s => s.Id == id);
    }

    [Fact]
    public async Task ASourcesForm_SavedAfterAnotherAdministratorsSave_IsRefused_AndTheirsKept()
    {
        var source = await NewSource("Wire");
        var mine = SourceForm.From(source);
        var theirs = SourceForm.From(source);
        theirs.PollMinutes = 240;
        await _db.Intake().SaveSourceAsync(theirs);

        mine.LicenseNote = "Members only.";
        await FluentActions.Invoking(() => _db.Intake().SaveSourceAsync(mine))
            .Should().ThrowAsync<StaleEditException>().WithMessage("Someone else changed Wire since you opened it*");
        var now = (await _db.Intake().SourcesAsync()).Single(s => s.Id == source.Id);
        (now.PollMinutes, now.LicenseNote).Should().Be((240, null));

        // Opened again, it saves.
        var again = SourceForm.From(now);
        again.LicenseNote = "Members only.";
        await _db.Intake().SaveSourceAsync(again);
        (await _db.Intake().SourcesAsync()).Single(s => s.Id == source.Id).LicenseNote.Should().Be("Members only.");
    }

    [Fact]
    public async Task ASourcesForm_OpenedBeforeItWasPaused_CantTurnPollingBackOn()
    {
        var source = await NewSource("Feedly");
        var form = SourceForm.From(source);
        await _db.Intake().SetSourceEnabledAsync(source.Id, false);   // Source health's "Pause polling"

        form.Name = "Feedly news";
        await FluentActions.Invoking(() => _db.Intake().SaveSourceAsync(form)).Should().ThrowAsync<StaleEditException>();
        (await _db.Intake().SourcesAsync()).Single(s => s.Id == source.Id).Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task ASourcesForm_IsntMadeStaleByAPoll()
    {
        var source = await NewSource("Polled");
        var form = SourceForm.From(source);
        _db.Http.Set("https://polled.example.com/feed", "<rss version=\"2.0\"><channel><title>t</title></channel></rss>");
        await _db.Intake().PollNowAsync(source.Id);

        form.LicenseNote = "Fine to forward.";
        await _db.Intake().SaveSourceAsync(form);
        (await _db.Intake().SourcesAsync()).Single(s => s.Id == source.Id).LicenseNote.Should().Be("Fine to forward.");
    }

    // --- tied places ----------------------------------------------------------------------------------------------

    // Two added at once read the same highest place and take the same next one. The test makes the tie directly.
    private async Task Place<T>(Guid id, int place) where T : class
    {
        await using var ctx = _db.PlainContext();
        var row = await ctx.Set<T>().FindAsync(id);
        ctx.Entry(row!).Property("SortOrder").CurrentValue = place;
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task MovingATeamUp_PastAColleagueAddedAtTheSameMoment_MovesItOnePlace()
    {
        var teams = _db.Teams();
        var first = await teams.AddAsync("Applications", null, null);
        var beta = await teams.AddAsync("beta", null, null);
        var gamma = await teams.AddAsync("Gamma", null, null);
        await Place<Team>(first, 50);
        await Place<Team>(beta, 51);
        await Place<Team>(gamma, 51);   // beta and Gamma were added at once
        string[] Shown() => teams.ListAsync().Result.Where(t => t.Id == first || t.Id == beta || t.Id == gamma).Select(t => t.Name).ToArray();
        Shown().Should().Equal("Applications", "beta", "Gamma");

        await teams.MoveAsync(gamma, -1);

        Shown().Should().Equal(["Applications", "Gamma", "beta"], "Gamma goes up one place, as shown, not over Applications");
    }

    // Two of one name in one place, the later-added one with the lower id, after one placed before them: the page shows
    // the twins in one order, and moving the second twin up must swap the twins, not jump it over the one before.
    private static readonly Guid Before = Guid.NewGuid(), TwinAddedFirst = Guid.Parse("ffffffff-0000-0000-0000-00000000000a"),
        TwinAddedSecond = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private async Task Twins(Func<Guid, string, int, object> make)
    {
        foreach (var (id, name, place) in new[] { (Before, "Applications", 50), (TwinAddedFirst, "Twin", 51), (TwinAddedSecond, "Twin", 51) })
        {
            await using var ctx = _db.PlainContext();
            ctx.Add(make(id, name, place));
            await ctx.SaveChangesAsync();
        }
    }

    private static Guid[] Mine(IEnumerable<Guid> ids) => ids.Where(id => id == Before || id == TwinAddedFirst || id == TwinAddedSecond).ToArray();

    [Fact]
    public async Task TwoTeamsOfOneNameInOnePlace_MoveAsShown()
    {
        await Twins((id, name, place) => new Team { Id = id, Key = "r4c-" + id.ToString("N"), Name = name, SortOrder = place });
        var teams = _db.Teams();
        var shown = Mine((await teams.ListAsync()).Select(t => t.Id));
        shown[0].Should().Be(Before);

        await teams.MoveAsync(shown[2], -1);

        Mine((await teams.ListAsync()).Select(t => t.Id)).Should().Equal(Before, shown[2], shown[1]);
    }

    [Fact]
    public async Task MovingAMetricGroupUp_PastItsTwinInTheSamePlace_MovesItOnePlace()
    {
        await Twins((id, name, place) => new MetricGroup { Id = id, Key = "r4c-" + id.ToString("N"), Name = name, SortOrder = place });
        var metrics = _db.Metrics();
        var shown = Mine((await metrics.ListAsync(includeRetired: true)).Select(g => g.Id));
        shown[0].Should().Be(Before);

        await metrics.MoveGroupAsync(shown[2], -1);

        Mine((await metrics.ListAsync(includeRetired: true)).Select(g => g.Id)).Should().Equal(Before, shown[2], shown[1]);
    }

    [Fact]
    public async Task MovingAScorecardCategoryUp_PastItsTwinInTheSamePlace_MovesItOnePlace()
    {
        await Twins((id, name, place) => new ScorecardCategory { Id = id, Key = "r4c-" + id.ToString("N"), Name = name, SortOrder = place });
        var scorecard = _db.Scorecard();
        var shown = Mine((await scorecard.ListAsync(includeRetired: true)).Select(c => c.Id));
        shown[0].Should().Be(Before);

        await scorecard.MoveCategoryAsync(shown[2], -1);

        Mine((await scorecard.ListAsync(includeRetired: true)).Select(c => c.Id)).Should().Equal(Before, shown[2], shown[1]);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TwoCardRulesOfOneLabelInOnePlace_AreReadInOneOrder(string provider)
    {
        _db = _db.On(provider);
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var rules = _db.CardRules();
        // The first matching rule colours a card: two in one place must not trade colours from one read to the next.
        var high = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
        var low = Guid.Parse("00000000-0000-0000-0000-000000000002");
        await using (var ctx = _db.PlainContext())
        {
            ctx.ActionCardRules.Add(new ActionCardRule { Id = high, Name = "Urgent", Colour = CardColour.Red, Kind = ActionKind.Block, SortOrder = 70 });
            ctx.ActionCardRules.Add(new ActionCardRule { Id = low, Name = "urgent", Colour = CardColour.Amber, Kind = ActionKind.Block, SortOrder = 70 });
            await ctx.SaveChangesAsync();
        }

        (await rules.ListAsync()).Where(r => r.SortOrder == 70).Select(r => r.Id).Should().Equal(low, high);
    }
}

/// <summary>Bug round 4, the follow-ups on pages: the Adversaries list and a profile follow a colleague's changes, and
/// the sources page says when its form was refused as stale.</summary>
public class FollowUpsRound4PageTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Colleague = new("dev:r4c-robin", "Robin", "Analyst", "Admin");

    private static IReadOnlyList<ToastService.Toast> Toasts<T>(Rendered<T> r) => r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts;

    private static IReadOnlyList<Change> ByRobin() => [new Change(ChangeFeed.Adversaries, Colleague.UserId, Colleague.Name, Guid.NewGuid())];

    // Robin's profile service, saving through the page's own origin, which its live feed skips: the page hears Robin's
    // change only when the test hands it over (OnChangedElsewhere), never between the test's steps.
    private static AdversaryService Unheard<T>(AsyncServiceScope robin, Rendered<T> page) where T : Microsoft.AspNetCore.Components.IComponent =>
        ActivatorUtilities.CreateInstance<AdversaryService>(robin.ServiceProvider, page.Scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>());

    private async Task<Guid> NewProfile(string name)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return await scope.ServiceProvider.GetRequiredService<AdversaryService>().SaveAsync(new AdversaryForm { Name = name + " " + Guid.NewGuid().ToString("N")[..6] });
    }

    [Fact]
    public async Task AProfilesTypedForm_KeepsWhatsTyped_SaysWhoSavedItMeanwhile_AndReloadTakesTheirs()
    {
        var id = await NewProfile("R4c Spider");
        await using var page = await RenderAsync<AdversariesPage>(app, $"/adversaries/{id}", new() { ["Id"] = id });
        await page.Click(p => { p.Form!.Notes = "Typed, not saved"; return Task.CompletedTask; });

        await using (var robin = SignedIn(app, Colleague))
        {
            var svc = Unheard(robin, page);
            var form = AdversaryForm.From((await svc.GetAsync(id))!.Adversary);
            form.Aliases = "Octo Tempest";
            await svc.SaveAsync(form);
        }
        page.Component.Detail!.Adversary.Aliases.Should().BeNull("the page hasn't heard it yet");
        await page.Click(p => p.OnChangedElsewhere(ByRobin()));

        page.Component.ChangedBy.Should().Be("Robin");
        page.Component.Detail!.Adversary.Aliases.Should().Be("Octo Tempest");
        page.Component.Form!.Notes.Should().Be("Typed, not saved", "what's typed stays");
        Toasts(page).Select(t => t.Message).Should().Contain("Robin updated this profile.");

        await page.Click(p => p.Save());
        Toasts(page).Select(t => t.Message).Should().Contain(m => m.StartsWith("Someone else changed"));

        await page.Click(p => p.ReloadTheirs());
        page.Component.ChangedBy.Should().BeNull();
        page.Component.Form!.Aliases.Should().Be("Octo Tempest");
        page.Component.Form.Notes.Should().BeNull("what was typed goes");
    }

    [Fact]
    public async Task AnUntouchedProfile_TakesAColleaguesChange_AndAnotherProfilesChangeIsQuiet()
    {
        var id = await NewProfile("R4c Libra");
        var other = await NewProfile("R4c Other");
        await using var page = await RenderAsync<AdversariesPage>(app, $"/adversaries/{id}", new() { ["Id"] = id });

        await using (var robin = SignedIn(app, Colleague))
            await Unheard(robin, page).SetWatchedAsync(id, true);
        await page.Click(p => p.OnChangedElsewhere(ByRobin()));

        page.Component.Detail!.Adversary.Watched.Should().BeTrue();
        (page.Component.ChangedBy, page.Component.Form!.Watched).Should().Be((null, true), "an untouched form takes their change");
        Toasts(page).Select(t => t.Message).Should().Contain("Robin updated this profile.");

        var toasts = Toasts(page).Count;
        await using (var robin = SignedIn(app, Colleague))
            await Unheard(robin, page).SetWatchedAsync(other, true);
        await page.Click(p => p.OnChangedElsewhere(ByRobin()));
        Toasts(page).Count.Should().Be(toasts, "the topic is every profile's: only a change to this one is told");
    }

    [Fact]
    public async Task TheList_ShowsAProfileAColleagueWatched_KeepingWhatsTypedInAdd()
    {
        var id = await NewProfile("R4c Watched");
        await using var page = await RenderAsync<AdversariesPage>(app, "/adversaries");
        page.Component.Shown!.Should().NotContain(r => r.Adversary.Id == id, "never reported nor watched");

        await using (var robin = SignedIn(app, Colleague))
            await Unheard(robin, page).SetWatchedAsync(id, true);
        await page.Click(p => p.OnChangedElsewhere(ByRobin()));

        page.Component.Shown!.Should().Contain(r => r.Adversary.Id == id);
        Toasts(page).Select(t => t.Message).Should().Contain("Robin updated Adversaries.");
    }

    // Robin's team service, saving through the External teams page's own origin, as Unheard does for profiles.
    private static TeamService UnheardTeams(AsyncServiceScope robin, Rendered<ExternalTeams> page) =>
        ActivatorUtilities.CreateInstance<TeamService>(robin.ServiceProvider, page.Scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>());

    private static IReadOnlyList<Change> TeamsByRobin() => [new Change(ChangeFeed.Teams, Colleague.UserId, Colleague.Name, Guid.NewGuid())];

    [Fact]
    public async Task ExternalTeams_ShowsATeamAColleagueAdded_KeepingWhatsTypedInAdd()
    {
        await using var page = await RenderAsync<ExternalTeams>(app, "/teams/external");
        await page.Click(p => { p.NewForm.Name = "Typed, not added"; return Task.CompletedTask; });
        var name = "R4c Facilities " + Guid.NewGuid().ToString("N")[..6];

        await using (var robin = SignedIn(app, Colleague))
            await UnheardTeams(robin, page).AddExternalAsync(name, null);
        page.Component.Shown.Should().NotContain(t => t.Name == name, "the page hasn't heard it yet");
        await page.Click(p => p.OnChangedElsewhere(TeamsByRobin()));

        page.Component.Shown.Should().Contain(t => t.Name == name);
        page.Component.NewForm.Name.Should().Be("Typed, not added");
        Toasts(page).Select(t => t.Message).Should().Contain("Robin updated External teams.");
    }

    [Fact]
    public async Task ExternalTeams_ATeamRetiredWhileItsEdited_ClosesTheEdit_AndSaysSo()
    {
        Guid id;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            id = await scope.ServiceProvider.GetRequiredService<TeamService>().AddExternalAsync("R4c Legal " + Guid.NewGuid().ToString("N")[..6], null);
        await using var page = await RenderAsync<ExternalTeams>(app, "/teams/external");
        await page.Click(p => { p.Edit(p.Shown.Single(t => t.Id == id)); return Task.CompletedTask; });

        await using (var robin = SignedIn(app, Colleague))
            await UnheardTeams(robin, page).SetRetiredAsync(id, true);
        await page.Click(p => p.OnChangedElsewhere(TeamsByRobin()));

        page.Component.Editing.Should().BeNull();
        Toasts(page).Select(t => t.Message).Should().Contain("Robin retired or removed the team you were editing.");
    }

    [Fact]
    public async Task ASourcesFormRefusedAsStale_StaysWithABanner_AndReloadShowsTheirs()
    {
        var name = "R4c Wire " + Guid.NewGuid().ToString("N")[..6];
        Guid id;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            id = await scope.ServiceProvider.GetRequiredService<IntakeService>().SaveSourceAsync(new SourceForm { Name = name, Url = "https://r4c.example.com/feed", PollMinutes = 60, Enabled = false });
        await using var page = await RenderAsync<SourcesAdmin>(app, "/admin/sources");
        await page.Click(async p =>
        {
            await using var scope = SignedIn(app, SignIn.Analyst);
            var svc = scope.ServiceProvider.GetRequiredService<IntakeService>();
            p.Form = SourceForm.From((await svc.SourcesAsync()).Single(s => s.Id == id));
            p.Form.LicenseNote = "Typed, not saved";
        });

        await using (var robin = SignedIn(app, Colleague))
        {
            var svc = robin.ServiceProvider.GetRequiredService<IntakeService>();
            var theirs = SourceForm.From((await svc.SourcesAsync()).Single(s => s.Id == id));
            theirs.PollMinutes = 240;
            await svc.SaveSourceAsync(theirs);
        }
        await page.Click(p => p.Save());

        Toasts(page).Should().Contain(t => t.IsError && t.Message.StartsWith($"Someone else changed {name}"));
        page.Component.ChangedBy.Should().Be("Someone");
        page.Component.Form!.LicenseNote.Should().Be("Typed, not saved", "the form stays to copy from");

        await page.Click(p => p.ReloadTheirs());
        page.Component.ChangedBy.Should().BeNull();
        page.Component.Form!.PollMinutes.Should().Be(240);
        page.Component.Form.LicenseNote.Should().BeNull("what was typed goes");
    }
}
