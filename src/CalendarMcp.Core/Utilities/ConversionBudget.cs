using System.Diagnostics;

namespace CalendarMcp.Core.Utilities;

/// <summary>A body that will not be converted. The message is what a model reads in its place.</summary>
internal sealed class UnconvertibleMailException(string notice) : Exception(notice);

/// <summary>
/// The time one conversion may take. It reads the clock instead of waiting for a timer, because a
/// timer needs a free pool thread to fire, and conversions are what occupies them.
/// </summary>
internal readonly struct ConversionBudget(TimeSpan allowed, CancellationToken caller = default)
{
    private const string TookTooLong = "The body of this mail took too long to convert, so it is left out. The other fields are unaffected.";

    private readonly long _deadline = Stopwatch.GetTimestamp() + (long)(allowed.TotalSeconds * Stopwatch.Frequency);

    /// <summary>What is left of the time, and nothing below zero.</summary>
    public TimeSpan Left => TimeSpan.FromSeconds(Math.Max(_deadline - Stopwatch.GetTimestamp(), 0) / (double)Stopwatch.Frequency);

    public void Check()
    {
        caller.ThrowIfCancellationRequested();
        if (Stopwatch.GetTimestamp() >= _deadline)
            throw new UnconvertibleMailException(TookTooLong);
    }
}
