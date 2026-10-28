using System;
using System.Diagnostics;
using System.Threading;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit;

namespace GimmeCapture.Tests;

// The pinned-video player busy-waited through the last 20 ms before every frame, which kept about half a core busy
// while a pin played at 60 fps. It now waits on a timer: these pin down that the timer keeps time and cancels.
// That Windows gets the high-resolution timer is in FrameDeadlineWaiterWindowsTests, which only the Windows head builds.
public class FrameDeadlineWaiterTests
{
    [Fact]
    public void AWait_LastsAboutAsLongAsAsked()
    {
        using var waiter = FrameDeadlineWaiter.Create(CancellationToken.None);

        var elapsed = Stopwatch.StartNew();
        waiter.Wait(TimeSpan.FromMilliseconds(20));

        // The upper bound only guards against "never woke"; a loaded CI runner can be slow.
        Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 19, 500);
    }

    [Fact]
    public void Cancelling_EndsAWaitAtOnce()
    {
        using var cts = new CancellationTokenSource();
        using var waiter = FrameDeadlineWaiter.Create(cts.Token);
        cts.CancelAfter(50);

        var elapsed = Stopwatch.StartNew();
        waiter.Wait(TimeSpan.FromSeconds(10));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"the wait took {elapsed.ElapsedMilliseconds} ms after a cancel at 50 ms");
    }

    [Fact]
    public void DelayUntilFrame_ReturnsOnceTheFrameIsDue()
    {
        using var waiter = FrameDeadlineWaiter.Create(CancellationToken.None);
        var clock = Stopwatch.StartNew();

        LibavVideoFramePlayer.DelayUntilFrame(waiter, clock, startedAtSeconds: 0, frameSeconds: 0.03, speed: 1.0, CancellationToken.None);

        Assert.True(clock.Elapsed.TotalSeconds >= 0.03 - FrameDeadlineWaiter.ToleranceSeconds,
            $"returned at {clock.Elapsed.TotalMilliseconds:F1} ms for a frame due at 30 ms");
    }
}
