using System;
using System.Threading;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Waits for a video frame's presentation time without spinning. The player used to busy-wait through the last 20 ms
/// before every frame, which kept about half a core busy while a pin played at 60 fps (frames are under 20 ms apart
/// there, so it never slept at all). Windows gets a high-resolution waitable timer; elsewhere, where the system
/// timer is already fine-grained, a cancellable sleep. One per playback session: it also wakes when the session is
/// cancelled.
/// </summary>
internal abstract class FrameDeadlineWaiter : IDisposable
{
    /// <summary>A frame this close to its time is shown now: a timer cannot do much better than a millisecond.</summary>
    internal const double ToleranceSeconds = 0.001;

    internal static FrameDeadlineWaiter Create(CancellationToken ct)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows()
            && GimmeCapture.Services.Platforms.Windows.HighResolutionTimerWaiter.TryCreate(ct) is { } timer)
        {
            return timer;
        }
#endif
        return new SleepWaiter(ct);
    }

    /// <summary>Blocks for about <paramref name="duration"/>, or until the session is cancelled.</summary>
    internal abstract void Wait(TimeSpan duration);

    public virtual void Dispose()
    {
    }

    private sealed class SleepWaiter(CancellationToken ct) : FrameDeadlineWaiter
    {
        internal override void Wait(TimeSpan duration) => ct.WaitHandle.WaitOne(duration);
    }
}
