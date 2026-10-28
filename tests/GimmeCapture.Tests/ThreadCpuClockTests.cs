using System;
using System.Diagnostics;
using System.Threading;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Tests;

// The stall watchdog reads the UI thread's CPU time from its own thread, mid-stall, to tell a busy UI thread from a
// blocked one.
public sealed class ThreadCpuClockTests
{
    [WindowsFact]
    public void AnotherThreadReadsTheCpuTimeOfAThreadThatWorked()
    {
        ThreadCpuClock? clock = null;
        TimeSpan? before = null;
        using var worked = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            clock = ThreadCpuClock.ForCurrentThread();
            before = clock?.Read();

            // Spins until its own clock has moved, so a loaded machine only makes this take longer.
            var deadline = Stopwatch.StartNew();
            while (clock?.Read() is { } now && now - before < TimeSpan.FromMilliseconds(100)
                   && deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
            }

            worked.Set();
            finish.Wait();
        })
        { IsBackground = true };
        thread.Start();

        try
        {
            Assert.True(worked.Wait(TimeSpan.FromSeconds(30)));
            Assert.NotNull(clock);
            Assert.NotNull(before);

            TimeSpan? after = clock!.Read();

            Assert.NotNull(after);
            Assert.True(after - before >= TimeSpan.FromMilliseconds(100), $"read {after - before} of CPU time");
        }
        finally
        {
            finish.Set();
            thread.Join();
            clock?.Dispose();
        }
    }

    [WindowsFact]
    public void AThreadThatWaitsUsesAlmostNoCpuTime()
    {
        ThreadCpuClock? clock = null;
        TimeSpan? before = null;
        using var slept = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            clock = ThreadCpuClock.ForCurrentThread();
            before = clock?.Read();
            Thread.Sleep(300);
            slept.Set();
        })
        { IsBackground = true };
        thread.Start();

        try
        {
            Assert.True(slept.Wait(TimeSpan.FromSeconds(30)));
            thread.Join();
            Assert.NotNull(clock);

            TimeSpan? after = clock!.Read();

            Assert.NotNull(before);
            Assert.NotNull(after);
            Assert.True(after - before < TimeSpan.FromMilliseconds(50), $"read {after - before} of CPU time");
        }
        finally
        {
            clock?.Dispose();
        }
    }

    [WindowsFact]
    public void AStoppedClockReadsNothingInsteadOfThrowing()
    {
        var clock = ThreadCpuClock.ForCurrentThread();
        Assert.NotNull(clock);

        clock!.Dispose();

        Assert.Null(clock.Read());
    }
}
