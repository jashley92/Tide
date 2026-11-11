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
}
