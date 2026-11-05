using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>
/// The clock box. Chromium reports a change at every keystroke, and a half-typed time as "". @bind wrote the old time
/// back for that, which put the cursor on the hours again, so a reminder or meeting time couldn't be typed at all.
/// </summary>
public class TimeBoxTests(TideApp app) : IClassFixture<TideApp>
{
    [Theory]
    [InlineData("09:30", 9, 30)]
    [InlineData("23:59", 23, 59)]
    [InlineData("09:30:00", 9, 30)]
    public void AWholeTime_IsRead(string raw, int hour, int minute) =>
        TimeBox.Read(raw).Should().Be(new TimeOnly(hour, minute));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("9")]
    [InlineData("25:00")]
    [InlineData("nine")]
    public void AHalfTypedOrBadTime_IsNotRead(string? raw) => TimeBox.Read(raw).Should().BeNull();

    [Fact]
    public async Task AHalfTypedTime_ChangesNothing_AndAWholeOne_IsPassedOn_WithoutRewritingTheBox()
    {
        var got = new List<TimeOnly>();
        var values = new Dictionary<string, object?>
        {
            [nameof(TimeBox.Id)] = "t",
            [nameof(TimeBox.Value)] = new TimeOnly(8, 0),
            [nameof(TimeBox.ValueChanged)] = EventCallback.Factory.Create<TimeOnly>(this, t => got.Add(t)),
        };
        await using var r = await ComponentHarness.RenderAsync<TimeBox>(app, "/", values);
        (await r.Html()).Should().Contain("value=\"08:00\"");

        // The hour typed, the minutes not yet: the browser reports "". Nothing changes, so the box isn't rewritten.
        await r.Click(c => c.OnChange(new ChangeEventArgs { Value = "" }));
        got.Should().BeEmpty();
        (await r.Html()).Should().Contain("value=\"08:00\"", "what's rendered is unchanged, so the box isn't written to");

        // A whole time: passed on, and the rendered value still isn't changed under the typing.
        await r.Click(c => c.OnChange(new ChangeEventArgs { Value = "09:30" }));
        got.Should().Equal(new TimeOnly(9, 30));
        (await r.Html()).Should().Contain("value=\"08:00\"", "the box already shows 09:30; writing it again would move the cursor");
    }
}
