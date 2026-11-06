using System;
using System.Threading;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// The thread <see cref="UiThreadStallMonitor"/> beats on: every <see cref="Beat"/> while a window of the app's is on
/// screen, and every <see cref="TrayBeat"/> while the app sits in the tray, where each beat cost three or four of the
/// app's idle wakeups with nobody there to meet a stall. What a user can do in the tray, press a hotkey or click the
/// tray icon, posts a probe of its own as it happens (<see cref="UiThreadStallMonitor.NotifyInput"/>), so the slow beat
/// costs that no precision. Kept apart from the dispatcher and the clocks so a test can drive it.
/// </summary>
internal sealed class UiStallWatchdog
{
    /// <summary>
    /// A probe reads short by up to one beat, as it is posted after the stall began: at one second, a 1.8 s freeze at
    /// recording start was logged as 698 ms, and a freeze just under 1.25 s could go unreported altogether.
    /// </summary>
    internal static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// With no window on screen. A stall nothing the user did set off (a timer's work) now reads short by up to this, or
    /// not at all below it.
    /// </summary>
    internal static readonly TimeSpan TrayBeat = TimeSpan.FromSeconds(1);

    private readonly Action _onBeat;
    // Not disposed: Stop and the window switch may still set it from another thread after the loop has ended.
    private readonly AutoResetEvent _wake = new(initialState: false);
    private volatile bool _stopped;
    private volatile bool _windowShown;

    /// <param name="onBeat">Posts a probe, or writes down one still waiting; called on the watchdog's thread.</param>
    /// <param name="windowShown">Whether a window of the app's is on screen as it starts.</param>
    internal UiStallWatchdog(Action onBeat, bool windowShown)
    {
        _onBeat = onBeat;
        _windowShown = windowShown;
    }

    /// <summary>
    /// Whether a window of the app's is on screen. Turned on, the short beat starts at once rather than a tray beat
    /// later, with a beat of its own: the window may be what the user is waiting for.
    /// </summary>
    internal bool WindowShown
    {
        get => _windowShown;
        set
        {
            bool was = _windowShown;
            _windowShown = value;
            if (value && !was)
            {
                _wake.Set();
            }
        }
    }

    internal TimeSpan CurrentBeat => _windowShown ? Beat : TrayBeat;

    /// <summary>
    /// Its own thread rather than a pool timer: a stalled UI thread is often waiting on the pool, which then has no thread
    /// to spare for a timer either.
    /// </summary>
    internal void Start(string threadName) =>
        new Thread(Run) { IsBackground = true, Name = threadName }.Start();

    internal void Stop()
    {
        _stopped = true;
        _wake.Set();
    }

    private void Run()
    {
        while (true)
        {
            _wake.WaitOne(CurrentBeat);
            if (_stopped)
            {
                return;
            }

            try
            {
                _onBeat();
            }
            catch (Exception ex)
            {
                // A watchdog that fails costs one report, never the app.
                AppLog.Debug("UiStall.Watchdog", ex);
            }
        }
    }
}
