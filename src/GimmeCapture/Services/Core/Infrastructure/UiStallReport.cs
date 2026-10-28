using System;
using System.Diagnostics;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// What the UI thread and the garbage collector had done by one moment. Two of these, taken where a stall began and
/// where it ended (or where it still is), tell what the stall was.
/// </summary>
/// <param name="Timestamp">A <see cref="Stopwatch.GetTimestamp"/> reading.</param>
/// <param name="ThreadCpu">The UI thread's CPU time so far; null where it cannot be read.</param>
/// <param name="GcPause">The process's time paused for garbage collection so far.</param>
internal readonly record struct UiThreadSample(
    long Timestamp,
    TimeSpan? ThreadCpu,
    TimeSpan GcPause,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

internal enum UiStallCause
{
    /// <summary>The UI thread ran code for most of it.</summary>
    Busy,

    /// <summary>The UI thread waited for most of it: a lock, a file, a task it blocked on, a page coming back in.</summary>
    Blocked,

    /// <summary>A garbage collection held every thread, the UI thread included, for most of it.</summary>
    GarbageCollection,

    /// <summary>The UI thread's CPU time could not be read, and it was not the GC.</summary>
    Unknown,
}

/// <summary>
/// The text of a <c>UiStall</c> log line. A stall's length alone does not tell a UI thread that was busy from one that
/// was blocked or one the GC held, and those have nothing in common when it comes to finding the cause.
/// </summary>
internal static class UiStallReport
{
    /// <summary>How long a stall runs before the watchdog writes it down without waiting for it to end.</summary>
    internal static readonly TimeSpan OngoingReportThreshold = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The GC if it paused the process for half the window or more: a GC pause stops the UI thread without running it, so
    /// it would otherwise look like waiting. Then busy if the UI thread ran for half the window or more, else blocked.
    /// </summary>
    internal static UiStallCause Classify(TimeSpan window, TimeSpan? threadCpu, TimeSpan gcPause)
    {
        if (window <= TimeSpan.Zero)
        {
            return UiStallCause.Unknown;
        }

        if (gcPause.Ticks * 2 >= window.Ticks)
        {
            return UiStallCause.GarbageCollection;
        }

        if (threadCpu is not { } cpu)
        {
            return UiStallCause.Unknown;
        }

        return cpu.Ticks * 2 >= window.Ticks ? UiStallCause.Busy : UiStallCause.Blocked;
    }

    /// <summary>A stall that has ended, written by the UI thread once it pumps again.</summary>
    /// <param name="stalled">How late the tick that ended it was.</param>
    /// <param name="lastPump">Taken at the tick before the stall.</param>
    /// <param name="now">Taken at the tick that ended it.</param>
    internal static string DescribeEnded(TimeSpan stalled, UiThreadSample lastPump, UiThreadSample now) =>
        $"The UI thread did not pump for {stalled.TotalMilliseconds:F0}ms ({Diagnose(lastPump, now)}). Anything the "
        + "user pressed in that window (a capture hotkey included) only got through afterwards.";

    /// <summary>A stall still going, written by the watchdog: the end of it may never be logged.</summary>
    internal static string DescribeOngoing(TimeSpan stalled, UiThreadSample lastPump, UiThreadSample now) =>
        $"The UI thread has not pumped for {stalled.TotalMilliseconds:F0}ms so far ({Diagnose(lastPump, now)}). "
        + "Logged while it lasts, in case the app does not come back from it.";

    /// <summary>
    /// Whether the watchdog writes down the stall that followed the tick at <paramref name="lastPumpTimestamp"/>: once it
    /// has gone on for <see cref="OngoingReportThreshold"/>, and only once.
    /// </summary>
    internal static bool ShouldReportOngoing(TimeSpan stalled, long lastPumpTimestamp, long? reportedPumpTimestamp) =>
        stalled >= OngoingReportThreshold && lastPumpTimestamp != reportedPumpTimestamp;

    private static string Diagnose(UiThreadSample lastPump, UiThreadSample now)
    {
        // Measured from the last pump, so the window is the stall plus up to one tick interval.
        TimeSpan window = Stopwatch.GetElapsedTime(lastPump.Timestamp, now.Timestamp);
        TimeSpan? cpu = lastPump.ThreadCpu is { } before && now.ThreadCpu is { } after ? after - before : null;
        TimeSpan gcPause = now.GcPause - lastPump.GcPause;

        string cause = Classify(window, cpu, gcPause) switch
        {
            UiStallCause.Busy => "busy",
            UiStallCause.Blocked => "blocked",
            UiStallCause.GarbageCollection => "GC",
            _ => "cause unknown",
        };

        string cpuText = cpu is { } ran
            ? $"UI thread CPU {ran.TotalMilliseconds:F0}ms ({SharePercent(ran, window)}%)"
            : "UI thread CPU unknown";

        return $"{cause}: {cpuText}, GC paused {gcPause.TotalMilliseconds:F0}ms, collections "
            + $"gen0 +{now.Gen0Collections - lastPump.Gen0Collections} "
            + $"gen1 +{now.Gen1Collections - lastPump.Gen1Collections} "
            + $"gen2 +{now.Gen2Collections - lastPump.Gen2Collections}";
    }

    // CPU time is counted in scheduler ticks (15.6 ms on Windows), so over a short window it can come out above 100%.
    private static int SharePercent(TimeSpan part, TimeSpan whole) =>
        whole <= TimeSpan.Zero ? 0 : (int)Math.Clamp(Math.Round(100.0 * part.Ticks / whole.Ticks), 0, 100);
}
