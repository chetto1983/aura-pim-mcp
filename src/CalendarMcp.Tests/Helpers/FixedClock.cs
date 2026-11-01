namespace CalendarMcp.Tests.Helpers;

/// <summary>A clock a test moves by hand.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
