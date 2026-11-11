using AwesomeAssertions;
using Tide.Web.Components.Today;

namespace Tide.IntegrationTests;

/// <summary>Where focus goes on a Today card after Take it or Add: the user keeps their place in the list.</summary>
public class KeepPlaceTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

    [Fact]
    public void ARowStillListed_KeepsFocus() => KeepPlace.After([A, B, C], B, [A, B, C]).Should().Be(B);

    [Fact]
    public void ARowThatLeft_HandsFocusToTheNext() => KeepPlace.After([A, B, C], B, [A, C]).Should().Be(C);

    [Fact]
    public void TheLastRowLeaving_HandsFocusToTheOneBefore() => KeepPlace.After([A, B, C], C, [A, B]).Should().Be(B);

    [Fact]
    public void TheNextGoneToo_SkipsToTheFirstStillListed() => KeepPlace.After([A, B, C], A, [C]).Should().Be(C);

    [Fact]
    public void AnEmptyList_LeavesItToTheHeading() => KeepPlace.After([A], A, []).Should().BeNull();

    // The row FocusAsync would send focus to, read without a browser (null: none).
    private static async Task<string?> Target(KeepPlace place, IReadOnlyList<Guid> now)
    {
        string? to = null;
        await place.FocusAsync(now, id => { to = id.ToString(); return null; }, default);
        return to;
    }

    [Fact]
    public async Task AColleagueTakingTheFocusedRow_MovesFocusToTheNext()
    {
        var place = new KeepPlace();
        place.Listed([A, B, C]);
        place.Focus(B, true);
        place.Listed([A, C]);
        (await Target(place, [A, C])).Should().Be(C.ToString());
    }

    [Fact]
    public async Task AColleaguesChange_LeavesFocusAlone_WhenItWasntInTheList()
    {
        var place = new KeepPlace();
        place.Listed([A, B, C]);
        place.Focus(B, true);
        place.Focus(B, false);
        place.Listed([A, C]);
        string? to = "nothing";
        await place.FocusAsync([A, C], id => { to = id.ToString(); return null; }, default);
        to.Should().Be("nothing", "focus had left the list, so nothing moves it");
    }
}
