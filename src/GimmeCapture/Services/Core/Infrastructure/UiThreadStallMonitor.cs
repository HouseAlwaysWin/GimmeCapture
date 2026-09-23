using System;
using System.Diagnostics;
using Avalonia.Threading;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Notices when the UI thread stops pumping messages.
///
/// A blocked UI thread is invisible in the log and yet explains the whole class of "I pressed the hotkey and
/// nothing happened": WM_HOTKEY and the dispatcher post that follows it queue behind whatever is running, so the
/// overlay appears only once that finishes. A timer that should tick every second is late by exactly the length
/// of the block — the lateness IS the measurement, and the log lines around the warning say what ran.
/// </summary>
internal static class UiThreadStallMonitor
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>Below this, lateness is ordinary scheduling noise (a frame, a layout pass) and not worth a line.</summary>
    private static readonly TimeSpan WarnThreshold = TimeSpan.FromMilliseconds(250);

    private static DispatcherTimer? _timer;
    private static long _lastTickTimestamp;

    /// <summary>Starts the watchdog on the UI thread. Safe to call more than once.</summary>
    internal static void Start()
    {
        if (_timer != null)
        {
            return;
        }

        _lastTickTimestamp = Stopwatch.GetTimestamp();

        // Background priority on purpose: the tick must queue behind input and rendering like the capture
        // request does, or it would measure a queue it is jumping.
        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, OnTick);
        _timer.Start();
    }

    internal static void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        long previousTick = _lastTickTimestamp;
        _lastTickTimestamp = Stopwatch.GetTimestamp();

        var late = Stopwatch.GetElapsedTime(previousTick) - TickInterval;
        if (late >= WarnThreshold)
        {
            AppLog.Warning(
                "UiStall",
                $"The UI thread did not pump for {late.TotalMilliseconds:F0}ms. Anything the user pressed in that "
                + "window (a capture hotkey included) only got through afterwards.");
        }
    }
}
