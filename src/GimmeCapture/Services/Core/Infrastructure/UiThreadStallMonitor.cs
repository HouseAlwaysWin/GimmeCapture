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
/// overlay appears only once that finishes. A watchdog thread posts a probe to the UI thread every beat, one at a time,
/// and a probe waits for the length of the block, minus however much of the block fell before it was posted — so the
/// reading is short by up to one beat. The beat is kept small for that reason: at one second, a 1.8 s freeze at
/// recording start was logged as 698 ms, and a freeze just under 1.25 s could go unreported altogether.
///
/// Until 2026-11 the beat was a 100 ms DispatcherTimer on the UI thread, which came to 80-110 wakeups a second, in the
/// tray too: nearly all the app's idle wakeups. A probe costs the UI thread about two, as any post from another thread
/// does, so the monitor now takes about 35 a second, the watchdog's included.
///
/// Each line also says whether the UI thread was working, waiting or held by the GC meanwhile (see
/// <see cref="UiStallReport"/>): a 2.69 s stall was once the last line its process wrote, with nothing to say what it
/// was. That comes from its CPU time and the GC's pause total, read as each probe is posted so the stall's start is on
/// hand when it ends. And as a stall is only logged once the UI thread is back, one the process dies or is killed in
/// left no trace at all: the watchdog also writes down a probe still waiting after
/// <see cref="UiStallReport.OngoingReportThreshold"/>, once per stall.
/// </summary>
internal static class UiThreadStallMonitor
{
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(100);

    /// <summary>Below this, a wait is ordinary scheduling noise (a frame, a layout pass) and not worth a line.</summary>
    private static readonly TimeSpan WarnThreshold = TimeSpan.FromMilliseconds(250);

    private static readonly UiStallProbe Probe = new();
    private static ThreadCpuClock? _uiThreadCpu;
    private static ManualResetEventSlim? _watchdogStop;

    /// <summary>Starts the monitor, and its watchdog thread, from the UI thread. Safe to call more than once.</summary>
    internal static void Start()
    {
        if (_watchdogStop != null)
        {
            return;
        }

        _uiThreadCpu = ThreadCpuClock.ForCurrentThread();
        long generation = Probe.Generation;

        // The first probe now rather than a beat from now, so whatever the caller goes on to do before the UI thread
        // pumps is measured from here: a slow start-up included.
        PostProbeOrReportOngoing(generation);

        // Its own thread rather than a pool timer: a stalled UI thread is often waiting on the pool, which then has no
        // thread to spare for a timer either. No spinning before each wait, as every wait lasts the whole beat.
        var stop = new ManualResetEventSlim(initialState: false, spinCount: 0);
        _watchdogStop = stop;
        new Thread(() => RunWatchdog(stop, generation)) { IsBackground = true, Name = "UiStall watchdog" }.Start();
    }

    /// <summary>
    /// Stops the monitor until the result is disposed, around a wait on the UI thread the user is in charge of, a UAC
    /// prompt say, which is no stall. Nothing to undo when the monitor was not running.
    /// </summary>
    internal static IDisposable Pause()
    {
        if (_watchdogStop == null)
        {
            return System.Reactive.Disposables.Disposable.Empty;
        }

        Stop();
        return System.Reactive.Disposables.Disposable.Create(Start);
    }

    internal static void Stop()
    {
        _watchdogStop?.Set();
        _watchdogStop = null;
        Probe.Reset();
        _uiThreadCpu?.Dispose();
        _uiThreadCpu = null;
    }

    private static void RunWatchdog(ManualResetEventSlim stop, long generation)
    {
        while (!stop.Wait(Beat))
        {
            try
            {
                PostProbeOrReportOngoing(generation);
            }
            catch (Exception ex)
            {
                // A watchdog that fails costs one report, never the app.
                AppLog.Debug("UiStall.Watchdog", ex);
            }
        }
    }

    private static void PostProbeOrReportOngoing(long generation)
    {
        UiThreadSample now = TakeSample();
        if (Probe.TryPost(now, generation) is { } sequence)
        {
            // The priority a hotkey press is posted at, so the probe waits for just what a press would: whatever runs,
            // and queued work at that priority or above (rendering, layout), not input. The old timer ran at
            // Background, behind input as well, and Avalonia runs a Background job only while no message is waiting,
            // else on a timer: about 2.6 UI-thread wakeups a post against 1.9.
            Dispatcher.UIThread.Post(() => OnProbe(sequence), DispatcherPriority.Default);
        }
        else if (Probe.TakeOngoing(now.Timestamp) is { } posted)
        {
            var stalled = Stopwatch.GetElapsedTime(posted.Timestamp, now.Timestamp);
            AppLog.Warning("UiStall", UiStallReport.DescribeOngoing(stalled, posted, now));
        }
    }

    private static void OnProbe(long sequence)
    {
        UiThreadSample now = TakeSample();
        if (Probe.Complete(sequence) is not { } posted)
        {
            return;
        }

        var stalled = Stopwatch.GetElapsedTime(posted.Timestamp, now.Timestamp);
        if (stalled >= WarnThreshold)
        {
            AppLog.Warning("UiStall", UiStallReport.DescribeEnded(stalled, posted, now));
        }
    }

    // Twice a beat, so it stays cheap: one GetThreadTimes and a few counters the GC keeps anyway, and no allocation.
    private static UiThreadSample TakeSample() => new(
        Stopwatch.GetTimestamp(),
        _uiThreadCpu?.Read(),
        GC.GetTotalPauseDuration(),
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2));
}
