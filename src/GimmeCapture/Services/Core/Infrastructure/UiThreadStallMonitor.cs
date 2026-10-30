using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Notices when the UI thread stops pumping messages.
///
/// A blocked UI thread is invisible in the log and yet explains the whole class of "I pressed the hotkey and
/// nothing happened": WM_HOTKEY and the dispatcher post that follows it queue behind whatever is running, so the
/// overlay appears only once that finishes. A periodic tick is late by the length of the block, minus however much
/// of the block fell before the tick was due — so the reading is short by up to one interval. The interval is kept
/// small for that reason: at one second, a 1.8 s freeze at recording start was logged as 698 ms, and a freeze just
/// under 1.25 s could go unreported altogether.
///
/// Each line also says whether the UI thread was working, waiting or held by the GC meanwhile (see
/// <see cref="UiStallReport"/>): a 2.69 s stall was once the last line its process wrote, with nothing to say what it
/// was. That comes from its CPU time and the GC's pause total, read at every tick so the stall's start is on hand when
/// it ends. And as a stall is only logged once the UI thread is back, one the process dies or is killed in left no trace
/// at all: a watchdog thread now writes down a stall still going after
/// <see cref="UiStallReport.OngoingReportThreshold"/>, once per stall.
/// </summary>
internal static class UiThreadStallMonitor
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Below this, lateness is ordinary scheduling noise (a frame, a layout pass) and not worth a line.</summary>
    private static readonly TimeSpan WarnThreshold = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(250);

    private static DispatcherTimer? _timer;
    private static ThreadCpuClock? _uiThreadCpu;
    private static ManualResetEventSlim? _watchdogStop;

    // Taken at the last tick. Written by the UI thread, read by the watchdog.
    private static readonly object LastTickGate = new();
    private static UiThreadSample _lastTick;

    // The watchdog thread's own: the tick after which it has already written a stall down.
    private static long? _reportedOngoingTick;

    /// <summary>Starts the monitor, and its watchdog thread, from the UI thread. Safe to call more than once.</summary>
    internal static void Start()
    {
        if (_timer != null)
        {
            return;
        }

        _uiThreadCpu = ThreadCpuClock.ForCurrentThread();
        lock (LastTickGate)
        {
            _lastTick = TakeSample();
        }

        // Background priority on purpose: the tick must queue behind input and rendering like the capture
        // request does, or it would measure a queue it is jumping.
        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, OnTick);
        _timer.Start();

        // Its own thread rather than a pool timer: a stalled UI thread is often waiting on the pool, which then has no
        // thread to spare for a timer either.
        var stop = new ManualResetEventSlim();
        _watchdogStop = stop;
        new Thread(() => RunWatchdog(stop)) { IsBackground = true, Name = "UiStall watchdog" }.Start();
    }

    /// <summary>
    /// Stops the monitor until the result is disposed, around a wait on the UI thread the user is in charge of, a UAC
    /// prompt say, which is no stall. Nothing to undo when the monitor was not running.
    /// </summary>
    internal static IDisposable Pause()
    {
        if (_timer == null)
        {
            return System.Reactive.Disposables.Disposable.Empty;
        }

        Stop();
        return System.Reactive.Disposables.Disposable.Create(Start);
    }

    internal static void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _watchdogStop?.Set();
        _watchdogStop = null;
        _uiThreadCpu?.Dispose();
        _uiThreadCpu = null;
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        UiThreadSample now = TakeSample();
        UiThreadSample previous;
        lock (LastTickGate)
        {
            previous = _lastTick;
            _lastTick = now;
        }

        var late = Stopwatch.GetElapsedTime(previous.Timestamp, now.Timestamp) - TickInterval;
        if (late >= WarnThreshold)
        {
            AppLog.Warning("UiStall", UiStallReport.DescribeEnded(late, previous, now));
        }
    }

    private static void RunWatchdog(ManualResetEventSlim stop)
    {
        while (!stop.Wait(WatchdogInterval))
        {
            try
            {
                ReportOngoingStall();
            }
            catch (Exception ex)
            {
                // A watchdog that fails costs one report, never the app.
                AppLog.Debug("UiStall.Watchdog", ex);
            }
        }
    }

    private static void ReportOngoingStall()
    {
        UiThreadSample lastTick;
        lock (LastTickGate)
        {
            lastTick = _lastTick;
        }

        var stalled = Stopwatch.GetElapsedTime(lastTick.Timestamp) - TickInterval;
        if (!UiStallReport.ShouldReportOngoing(stalled, lastTick.Timestamp, _reportedOngoingTick))
        {
            return;
        }

        _reportedOngoingTick = lastTick.Timestamp;
        AppLog.Warning("UiStall", UiStallReport.DescribeOngoing(stalled, lastTick, TakeSample()));
    }

    // Every tick, so it stays cheap: one GetThreadTimes and a few counters the GC keeps anyway, and no allocation.
    private static UiThreadSample TakeSample() => new(
        Stopwatch.GetTimestamp(),
        _uiThreadCpu?.Read(),
        GC.GetTotalPauseDuration(),
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2));
}
