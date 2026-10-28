using System;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using GimmeCapture.Services.Platforms.Desktop;

namespace GimmeCapture.Tests;

/// <summary>
/// The app's main-thread scheduler runs work scheduled on the UI thread at once, as ReactiveUI's did before 26. A
/// <see cref="HistoricalScheduler"/> stands in for ReactiveUI 26's AvaloniaScheduler, which queues everything: what
/// it holds runs only when the test advances it.
/// </summary>
public class AvaloniaMainThreadSchedulerTests
{
    [Fact]
    public void WorkScheduledOnTheUiThreadRunsAtOnce()
    {
        var queued = new HistoricalScheduler();
        var scheduler = new AvaloniaMainThreadScheduler(() => true, queued);
        bool ran = false;

        scheduler.Schedule(() => ran = true);

        Assert.True(ran);
    }

    [Fact]
    public void WorkFromAnotherThreadIsQueued_AndNeverRunsOnceDisposed()
    {
        var queued = new HistoricalScheduler();
        var scheduler = new AvaloniaMainThreadScheduler(() => false, queued);
        var ran = new List<string>();

        scheduler.Schedule(() => ran.Add("kept"));
        scheduler.Schedule(() => ran.Add("disposed")).Dispose();
        Assert.Empty(ran);

        queued.Start();
        Assert.Equal(new[] { "kept" }, ran);
    }

    [Fact]
    public void DelayedWorkIsQueuedEvenOnTheUiThread()
    {
        var queued = new HistoricalScheduler();
        var scheduler = new AvaloniaMainThreadScheduler(() => true, queued);
        bool ran = false;

        scheduler.Schedule(TimeSpan.FromSeconds(1), () => ran = true);
        Assert.False(ran);

        queued.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.True(ran);
    }

    [Fact]
    public void WorkNestedPastTheLimitIsQueuedInsteadOfOverflowingTheStack()
    {
        var queued = new HistoricalScheduler();
        var scheduler = new AvaloniaMainThreadScheduler(() => true, queued);
        int runs = 0;

        void Again()
        {
            if (++runs < 100)
            {
                scheduler.Schedule(Again);
            }
        }

        scheduler.Schedule(Again);
        Assert.Equal(AvaloniaMainThreadScheduler.MaxNestedRuns, runs);

        // Queued work that schedules more runs that at once again, up to the same depth.
        queued.Start();
        Assert.Equal(100, runs);
    }

    [Fact]
    public void ANotificationRaisedOnTheUiThreadArrivesBeforeItsObserverIsDisposed()
    {
        // The snip overlay's IsFinalizing binding: the recording service raises "finalize done" on the UI thread, and
        // the overlay closes, disposing the binding, before that job ends. Through a scheduler that queues, the
        // notification was dropped and the recording's progress window never closed.
        var dropped = new List<bool>();
        var queued = new HistoricalScheduler();
        Raise(queued, dropped);
        queued.Start();
        Assert.Empty(dropped);

        var delivered = new List<bool>();
        Raise(new AvaloniaMainThreadScheduler(() => true, new HistoricalScheduler()), delivered);
        Assert.Equal(new[] { true, false }, delivered);

        static void Raise(IScheduler scheduler, List<bool> received)
        {
            var isFinalizing = new Subject<bool>();
            var binding = isFinalizing.DistinctUntilChanged().ObserveOn(scheduler).Subscribe(received.Add);
            isFinalizing.OnNext(true);
            isFinalizing.OnNext(false);
            binding.Dispose();
        }
    }
}
