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
/// reading is short by up to one beat (<see cref="UiStallWatchdog.Beat"/>).
///
/// Until 2026-11 the beat was a 100 ms DispatcherTimer on the UI thread, which came to 80-110 wakeups a second, in the
/// tray too: nearly all the app's idle wakeups. A probe cost the watchdog about two wakeups and the UI thread one or two
/// more, so at a 100 ms beat the monitor still took 30-45 a second of the app's 55-70 in the tray. It beats once a second
/// there now (<see cref="UiStallWatchdog.TrayBeat"/>), told by <see cref="SetWindowShown"/>, and what can reach the app
/// in the tray, a hotkey press or the tray icon, posts a probe as it arrives (<see cref="NotifyInput"/>): behind the
/// press, it waits for whatever the press waits for, and for what the press sets off.
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
    /// <summary>Below this, a wait is ordinary scheduling noise (a frame, a layout pass) and not worth a line.</summary>
    private static readonly TimeSpan WarnThreshold = TimeSpan.FromMilliseconds(250);

    private static readonly UiStallProbe Probe = new();
    private static ThreadCpuClock? _uiThreadCpu;
    private static Running? _running;

    // Until the app says otherwise, as a window may be shown: the short beat. Kept across a pause.
    private static volatile bool _windowShown = true;

    private sealed record Running(UiStallWatchdog Watchdog, long Generation);

    /// <summary>Starts the monitor, and its watchdog thread, from the UI thread. Safe to call more than once.</summary>
    internal static void Start()
    {
        if (_running != null)
        {
            return;
        }

        _uiThreadCpu = ThreadCpuClock.ForCurrentThread();
        long generation = Probe.Generation;

        // The first probe now rather than a beat from now, so whatever the caller goes on to do before the UI thread
        // pumps is measured from here: a slow start-up included.
        PostProbeOrReportOngoing(generation, forInput: false);

        var watchdog = new UiStallWatchdog(() => PostProbeOrReportOngoing(generation, forInput: false), _windowShown);
        Volatile.Write(ref _running, new Running(watchdog, generation));
        watchdog.Start("UiStall watchdog");
    }

    /// <summary>
    /// Stops the monitor until the result is disposed, around a wait on the UI thread the user is in charge of, a UAC
    /// prompt say, which is no stall. Nothing to undo when the monitor was not running.
    /// </summary>
    internal static IDisposable Pause()
    {
        if (_running == null)
        {
            return System.Reactive.Disposables.Disposable.Empty;
        }

        Stop();
        return System.Reactive.Disposables.Disposable.Create(Start);
    }

    internal static void Stop()
    {
        _running?.Watchdog.Stop();
        Volatile.Write(ref _running, null);
        Probe.Reset();
        _uiThreadCpu?.Dispose();
        _uiThreadCpu = null;
    }

    /// <summary>
    /// Whether a window of the app's is on screen (<c>AppWindowsOnScreen</c>): with none, the app sits in the tray and the
    /// watchdog beats once a second.
    /// </summary>
    internal static void SetWindowShown(bool shown)
    {
        _windowShown = shown;
        if (Volatile.Read(ref _running) is { } running)
        {
            running.Watchdog.WindowShown = shown;
        }
    }

    /// <summary>
    /// From the thread an input event arrives on, a hotkey press just posted to the UI thread: a probe posted behind it
    /// reads how long the press waited and what it set off, whatever the beat. Never waits for the UI thread.
    /// </summary>
    internal static void NotifyInput()
    {
        if (Volatile.Read(ref _running) is not { } running)
        {
            return;
        }

        try
        {
            PostProbeOrReportOngoing(running.Generation, forInput: true);
        }
        catch (Exception ex)
        {
            AppLog.Debug("UiStall.Input", ex);
        }
    }

    private static void PostProbeOrReportOngoing(long generation, bool forInput)
    {
        UiThreadSample now = TakeSample();
        long? sequence = forInput ? Probe.TryPostForInput(now, generation) : Probe.TryPost(now, generation);
        if (sequence is { } posted)
        {
            // The priority a hotkey press is posted at, so the probe waits for just what a press would: whatever runs,
            // and queued work at that priority or above (rendering, layout), not input. The old timer ran at
            // Background, behind input as well, and Avalonia runs a Background job only while no message is waiting,
            // else on a timer: about 2.6 UI-thread wakeups a post against 1.9.
            Dispatcher.UIThread.Post(() => OnProbe(posted), DispatcherPriority.Default);
        }
        else if (Probe.TakeOngoing(now.Timestamp) is { } waiting)
        {
            var stalled = Stopwatch.GetElapsedTime(waiting.Timestamp, now.Timestamp);
            AppLog.Warning("UiStall", UiStallReport.DescribeOngoing(stalled, waiting, now));
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

        // A press arrived while this probe waited and is queued behind it: a probe posted from here queues behind the
        // press. A stop since would have ended this probe, so the running generation is still this probe's.
        if (Probe.TakeFollowUp() && Volatile.Read(ref _running) is { } running)
        {
            PostProbeOrReportOngoing(running.Generation, forInput: false);
        }
    }

    // Twice a beat, so it stays cheap: one GetThreadTimes, one GetProcessMemoryInfo and a few counters the GC keeps
    // anyway, and no allocation.
    private static UiThreadSample TakeSample() => new(
        Stopwatch.GetTimestamp(),
        _uiThreadCpu?.Read(),
        GC.GetTotalPauseDuration(),
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        ProcessPageFaults.Read());
}
