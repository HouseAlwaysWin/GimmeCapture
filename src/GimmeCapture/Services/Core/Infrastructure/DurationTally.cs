using System;
using System.Globalization;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Durations taken one at a time, for a log line: how many, their total, average and longest. A plain struct with no
/// locking: one thread adds to it, and another reads it only once that one has finished.
/// </summary>
internal struct DurationTally
{
    private long _totalTicks;
    private long _longestTicks;

    public int Count { get; private set; }

    public readonly TimeSpan Total => TimeSpan.FromTicks(_totalTicks);

    public readonly TimeSpan Longest => TimeSpan.FromTicks(_longestTicks);

    public readonly TimeSpan Average => Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(_totalTicks / Count);

    public void Add(TimeSpan duration)
    {
        long ticks = Math.Max(0, duration.Ticks);
        _totalTicks += ticks;
        _longestTicks = Math.Max(_longestTicks, ticks);
        Count++;
    }

    /// <summary>The average and the longest in milliseconds, as <c>12.3/40ms</c>, or <c>none</c>.</summary>
    public readonly override string ToString() =>
        Count == 0
            ? "none"
            : string.Create(CultureInfo.InvariantCulture, $"{Average.TotalMilliseconds:F1}/{Longest.TotalMilliseconds:F0}ms");
}
