using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Preferences;
using Tide.Application.Security;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The Actions page remembers each person's List or Board: the tab saves it, an address without a layout opens it, and
/// a link's own layout wins for that visit without changing it.
/// </summary>
public class ActionsLayoutPreferenceTests(TideApp app) : IClassFixture<TideApp>
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // Each test signs in as its own people, so what one saves can't reach another (or the rest of the suite's tester).
    private static SignIn Person(string role = "Analyst") => new($"dev:layout-{Guid.NewGuid():N}", "Layout tester", role);

    private static bool AsBoard(Rendered<ActionsBoard> r) => (bool)typeof(ActionsBoard).GetField("_asBoard", Private)!.GetValue(r.Component)!;

    // The tab's click. The page then writes the address, which a page rendered outside a circuit can't (it throws).
    private static async Task Choose(Rendered<ActionsBoard> r, string layout)
    {
        try { await r.Click(c => (Task)typeof(ActionsBoard).GetMethod("SetLayout", Private)!.Invoke(c, [layout])!); }
        catch (NavigationException) { }
    }

    private async Task<string> Saved(SignIn who)
    {
        await using var scope = SignedIn(app, who);
        return await scope.ServiceProvider.GetRequiredService<UserPreferenceService>().GetAsync(UserPreferences.ActionsLayout);
    }

    private async Task Save(SignIn who, string layout)
    {
        await using var scope = SignedIn(app, who);
        await scope.ServiceProvider.GetRequiredService<UserPreferenceService>().SetAsync(UserPreferences.ActionsLayout, layout);
    }

    [Fact]
    public async Task Layout_NeverChosen_IsTheBoard()
    {
        var who = Person();
        (await Saved(who)).Should().Be(UserPreferences.Board);
        await using var r = await RenderAsync<ActionsBoard>(app, "/actions", @as: who);
        AsBoard(r).Should().BeTrue();
    }

    [Fact]
    public async Task ChoosingList_SavesIt_AndTheNextVisitOpensTheList()
    {
        var who = Person();
        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions", @as: who))
        {
            await Choose(r, "list");
            AsBoard(r).Should().BeFalse();
        }
        (await Saved(who)).Should().Be(UserPreferences.List);

        await using var next = await RenderAsync<ActionsBoard>(app, "/actions", @as: who);
        AsBoard(next).Should().BeFalse("an address without a layout opens the one they chose");

        await Choose(next, "board");
        (await Saved(who)).Should().Be(UserPreferences.Board, "the tab is how it changes back");
    }

    [Fact]
    public async Task AListLink_OpensTheList_WithoutChangingWhatsSaved()
    {
        var who = Person();
        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=list", @as: who))
            AsBoard(r).Should().BeFalse("a shared link opens as it was sent");
        (await Saved(who)).Should().Be(UserPreferences.Board, "only the tab saves it");

        await using var again = await RenderAsync<ActionsBoard>(app, "/actions", @as: who);
        AsBoard(again).Should().BeTrue();
    }

    [Fact]
    public async Task ABoardLink_OpensTheBoard_ForSomeoneWhoKeepsTheList()
    {
        var who = Person();
        await Save(who, UserPreferences.List);
        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions?view=open&layout=board", @as: who))
            AsBoard(r).Should().BeTrue();
        (await Saved(who)).Should().Be(UserPreferences.List, "only the tab saves it");
    }

    [Theory]
    [InlineData("/actions?view=overdue")]
    [InlineData("/actions?view=closed")]
    [InlineData("/actions?view=mine&group=kind")]
    public async Task ALinkToWhatOnlyTheListShows_OpensTheList_EvenWhenTheBoardIsTheirs(string path)
    {
        var who = Person();
        await using var r = await RenderAsync<ActionsBoard>(app, path, @as: who);
        AsBoard(r).Should().BeFalse("Workload's Overdue and Present's link to the rest keep their view");
    }

    [Fact]
    public async Task Layout_IsEachPersonsOwn()
    {
        SignIn one = Person(), other = Person();
        await Save(one, UserPreferences.List);

        (await Saved(other)).Should().Be(UserPreferences.Board);
        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions", @as: other))
            AsBoard(r).Should().BeTrue();

        await Save(other, UserPreferences.Board);
        (await Saved(one)).Should().Be(UserPreferences.List, "saving one person's leaves the other's alone");
    }

    [Fact]
    public async Task AViewer_KeepsTheirOwn()
    {
        var viewer = Person(nameof(AppRole.Viewer));
        await using (var r = await RenderAsync<ActionsBoard>(app, "/actions", @as: viewer))
        {
            await Choose(r, "list");
            r.Scope.ServiceProvider.GetRequiredService<Tide.Web.Services.ToastService>().Toasts.Should().NotContain(t => t.IsError);
        }
        (await Saved(viewer)).Should().Be(UserPreferences.List);
    }

    [Fact]
    public async Task Preference_WithoutTheActionsPage_OrAValueItCantHold_IsRefused()
    {
        var reader = Person(nameof(AppRole.Reader));
        await FluentActions.Awaiting(() => Save(reader, UserPreferences.Board)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => Save(Person(), "grid")).Should().ThrowAsync<ArgumentException>();
        await using var scope = SignedIn(app, Person());
        await FluentActions.Awaiting(() => scope.ServiceProvider.GetRequiredService<UserPreferenceService>().SetAsync("someone.elses", "x"))
            .Should().ThrowAsync<ArgumentException>();
    }
}
