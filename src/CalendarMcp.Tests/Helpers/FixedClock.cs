namespace CalendarMcp.Tests.Helpers;

/// <summary>A clock a test moves by hand.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A stopwatch that moves by one step every time it is read, so a test can say how long work took.</summary>
internal sealed class SteppingClock(TimeSpan step, DateTimeOffset? now = null) : TimeProvider
{
    private long _ticks;

    public override DateTimeOffset GetUtcNow() => now ?? base.GetUtcNow();

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks += step.Ticks;
}
