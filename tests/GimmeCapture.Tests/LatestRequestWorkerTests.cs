using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media;
using Xunit;

namespace GimmeCapture.Tests;

// Preview audio opens its reader and output device through this worker, off the UI thread, which used to spend
// 30–150 ms on that at every seek, speed change and mute. These pin down what the audio relies on: the caller never
// waits, requests never overlap, and a burst of them comes down to the newest.
public class LatestRequestWorkerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Submitting_DoesNotWaitForTheRequestThatIsRunning()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = new LatestRequestWorker<int>((_, _) =>
        {
            started.Set();
            release.Wait(Timeout);
        }, "Test");

        worker.Submit(1);
        Assert.True(started.Wait(Timeout));

        Task submit = Task.Run(() => worker.Submit(2));
        Task first = await Task.WhenAny(submit, Task.Delay(Timeout));
        release.Set();

        Assert.Same(submit, first);
        Assert.True(worker.WaitUntilIdle(Timeout));
    }

    [Fact]
    public void RequestsThatArriveWhileOneRuns_ComeDownToTheNewest()
    {
        var handled = new ConcurrentQueue<int>();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = new LatestRequestWorker<int>((request, _) =>
        {
            handled.Enqueue(request);
            if (request == 1)
            {
                started.Set();
                release.Wait(Timeout);
            }
        }, "Test");

        worker.Submit(1);
        Assert.True(started.Wait(Timeout));
        worker.Submit(2);
        worker.Submit(3);
        worker.Submit(4);
        release.Set();

        Assert.True(worker.WaitUntilIdle(Timeout));
        Assert.Equal(new[] { 1, 4 }, handled.ToArray());
    }

    [Fact]
    public void TheRunningRequest_CanTellThatANewerOneArrived()
    {
        bool supersededAtStart = true;
        bool supersededAtEnd = false;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = new LatestRequestWorker<int>((request, superseded) =>
        {
            if (request != 1)
            {
                return;
            }

            supersededAtStart = superseded();
            started.Set();
            release.Wait(Timeout);
            supersededAtEnd = superseded();
        }, "Test");

        worker.Submit(1);
        Assert.True(started.Wait(Timeout));
        worker.Submit(2);
        release.Set();

        Assert.True(worker.WaitUntilIdle(Timeout));
        Assert.False(supersededAtStart);
        Assert.True(supersededAtEnd);
    }

    [Fact]
    public async Task RequestsFromManyThreads_NeverRunAtTheSameTime()
    {
        int running = 0;
        int overlaps = 0;
        int handled = 0;
        var worker = new LatestRequestWorker<int>((_, _) =>
        {
            if (Interlocked.Increment(ref running) > 1)
            {
                Interlocked.Increment(ref overlaps);
            }

            Thread.SpinWait(500);
            Interlocked.Decrement(ref running);
            Interlocked.Increment(ref handled);
        }, "Test");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                worker.Submit(i);
            }
        })));

        Assert.True(worker.WaitUntilIdle(Timeout));
        Assert.Equal(0, overlaps);
        Assert.InRange(handled, 1, 8 * 500);
    }

    [Fact]
    public void ARequestThatThrows_DoesNotStopTheOnesAfterIt()
    {
        var handled = new ConcurrentQueue<int>();
        var worker = new LatestRequestWorker<int>((request, _) =>
        {
            if (request == 1)
            {
                throw new InvalidOperationException("the device is gone");
            }

            handled.Enqueue(request);
        }, "Test");

        worker.Submit(1);
        Assert.True(worker.WaitUntilIdle(Timeout));
        worker.Submit(2);
        Assert.True(worker.WaitUntilIdle(Timeout));

        Assert.Equal(new[] { 2 }, handled.ToArray());
    }

    [Fact]
    public void WaitingForIdle_GivesUpAfterTheTimeout()
    {
        using var release = new ManualResetEventSlim();
        var worker = new LatestRequestWorker<int>((_, _) => release.Wait(Timeout), "Test");

        worker.Submit(1);
        bool idle = worker.WaitUntilIdle(TimeSpan.FromMilliseconds(50));
        release.Set();

        Assert.False(idle);
        Assert.True(worker.WaitUntilIdle(Timeout));
    }
}
