using System;
using System.Runtime.InteropServices;
using System.Threading;
using GimmeCapture.Services.Core.Media;
using Microsoft.Win32.SafeHandles;

namespace GimmeCapture.Services.Platforms.Windows;

/// <summary>
/// <see cref="FrameDeadlineWaiter"/> on a high-resolution waitable timer (Windows 10 1803 and later). It wakes within
/// about a millisecond, where a sleep can overrun by the 15.6 ms system tick; that precision is why the player used to
/// spin instead.
/// </summary>
internal sealed class HighResolutionTimerWaiter : FrameDeadlineWaiter
{
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;

    private readonly TimerHandle _timer;
    private readonly WaitHandle[] _wakeOn;

    private HighResolutionTimerWaiter(SafeWaitHandle timer, CancellationToken ct)
    {
        _timer = new TimerHandle(timer);
        _wakeOn = [_timer, ct.WaitHandle];
    }

    /// <summary>Null where the system has no high-resolution timers.</summary>
    internal static HighResolutionTimerWaiter? TryCreate(CancellationToken ct)
    {
        SafeWaitHandle timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (timer.IsInvalid)
        {
            timer.Dispose();
            return null;
        }

        return new HighResolutionTimerWaiter(timer, ct);
    }

    internal override void Wait(TimeSpan duration)
    {
        // Negative means relative to now, in 100 ns units.
        long dueTime = -Math.Max(1, duration.Ticks);
        if (!SetWaitableTimer(_timer.SafeWaitHandle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            _wakeOn[1].WaitOne(duration);
            return;
        }

        WaitHandle.WaitAny(_wakeOn);
    }

    public override void Dispose() => _timer.Dispose();

    // WaitAny takes WaitHandles; this one owns the timer and closes it on Dispose.
    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(
        IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        SafeWaitHandle hTimer, ref long lpDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool fResume);
}
