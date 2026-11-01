using System;
using System.Diagnostics;

namespace GimmeCapture.Tests;

// The stall monitor's probes, without its threads or a dispatcher. It used to tick a 100 ms DispatcherTimer, which woke
// the UI thread 80-110 times a second even in the tray; a watchdog thread now posts one probe at a time instead.
public sealed class UiStallProbeTests
{
    private static long Timestamp(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000);

    private static UiThreadSample At(double milliseconds) =>
        new(Timestamp(milliseconds), TimeSpan.FromMilliseconds(milliseconds / 10), TimeSpan.Zero, 0, 0, 0);

    private static long Post(UiStallProbe probe, double milliseconds) =>
        probe.TryPost(At(milliseconds), probe.Generation) ?? throw new InvalidOperationException("no probe was posted");

    [Fact]
    public void OneProbeAtATime_ReadFromWhereItWasPosted()
    {
        var probe = new UiStallProbe();

        long first = Post(probe, 0);
        // The UI thread has not run it yet: another would only queue behind it.
        Assert.Null(probe.TryPost(At(100), probe.Generation));
        Assert.Null(probe.TryPost(At(200), probe.Generation));

        Assert.Equal(At(0), probe.Complete(first));
        Assert.NotNull(probe.TryPost(At(700), probe.Generation));
    }

    [Fact]
    public void AProbeIsReadOnce()
    {
        var probe = new UiStallProbe();
        long sequence = Post(probe, 0);

        Assert.Equal(At(0), probe.Complete(sequence));
        Assert.Null(probe.Complete(sequence));
    }

    [Fact]
    public void AProbeQueuedBeforeAStop_NoLongerCounts()
    {
        // Paused around a UAC prompt: the probe queued before it runs only once the user has answered, and that wait is
        // no stall.
        var probe = new UiStallProbe();
        long stale = Post(probe, 0);
        probe.Reset();
        long fresh = Post(probe, 30_000);

        Assert.Null(probe.Complete(stale));
        Assert.Equal(At(30_000), probe.Complete(fresh));
    }

    [Fact]
    public void AWatchdogStoppedMidBeat_PostsNothing()
    {
        // Its stop came between its wait and its post: a probe posted now would wait out the prompt the pause is for.
        var probe = new UiStallProbe();
        long generation = probe.Generation;
        probe.Reset();

        Assert.Null(probe.TryPost(At(0), generation));
        // The watchdog started after the stop posts as usual.
        Assert.NotNull(probe.TryPost(At(0), probe.Generation));
    }

    [Fact]
    public void AStallStillGoing_IsWrittenDownOncePastTheThreshold()
    {
        var probe = new UiStallProbe();
        double threshold = UiStallReport.OngoingReportThreshold.TotalMilliseconds;
        long first = Post(probe, 1000);

        Assert.Null(probe.TakeOngoing(Timestamp(1000 + threshold - 1)));
        Assert.Equal(At(1000), probe.TakeOngoing(Timestamp(1000 + threshold)));
        // The watchdog asks again at every beat while the same probe waits, and must not write it again.
        Assert.Null(probe.TakeOngoing(Timestamp(1000 + threshold + 5000)));

        // Once that probe has run, the next stall is a new one, reported in its own right.
        Assert.Equal(At(1000), probe.Complete(first));
        Post(probe, 9000);
        Assert.Equal(At(9000), probe.TakeOngoing(Timestamp(9000 + threshold)));
    }

    [Fact]
    public void NothingInFlight_IsNoStall()
    {
        var probe = new UiStallProbe();
        Assert.Null(probe.TakeOngoing(Timestamp(60_000)));

        probe.Complete(Post(probe, 0));
        Assert.Null(probe.TakeOngoing(Timestamp(60_000)));

        Post(probe, 70_000);
        probe.Reset();
        Assert.Null(probe.TakeOngoing(Timestamp(140_000)));
    }
}
