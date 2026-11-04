namespace Tide.TestSupport;

/// <summary>A clock that reads <see cref="Now"/>, which stays put until the test moves it.</summary>
public sealed class TestClock(DateTimeOffset now = default) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>The system clock, a fixed span ahead: "now", that much later.</summary>
public sealed class ShiftedClock(TimeSpan by) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + by;
}
