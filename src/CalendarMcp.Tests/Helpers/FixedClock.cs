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

/// <summary>
/// A clock whose timers fire only when a test moves it, so a budget built on it (a <c>CancellationTokenSource</c>
/// with a <see cref="TimeProvider"/>) runs out exactly when the test says. Timers are one-shot.
/// </summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];

    public DateTimeOffset Now { get; private set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock and fires, on this thread, every timer that came due.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            Now += by;
            due = _timers.Where(timer => timer.DueAt <= Now).ToList();
            _timers.RemoveAll(due.Contains);
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                    return true;
                DueAt = clock.Now + dueTime;
                clock._timers.Add(this);
            }

            return true;
        }

        public void Dispose() => Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
