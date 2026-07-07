using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;
using Xunit;

namespace GimmeCapture.Tests;

// The idle-unload timer behind LLama/U2Net memory release: fires once after the idle delay, restarts on use,
// and can be cancelled. Timing is injected (ControllableDelay) so the tests are deterministic — no real waits.
public class IdleReleaseSchedulerTests
{
    // A stand-in for Task.Delay whose "elapse" is driven by the test. Cancelling the token cancels the delay
    // (mirroring the real scheduler being reset/cancelled).
    private sealed class ControllableDelay
    {
        private volatile TaskCompletionSource<bool>? _latest;

        public Task DelayAsync(TimeSpan _, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled());
            _latest = tcs;
            return tcs.Task;
        }

        public void ElapseLatest() => _latest?.TrySetResult(true);
    }

    [Fact]
    public async Task FiresReleaseAfterIdleDelay()
    {
        var delay = new ControllableDelay();
        int count = 0;
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new IdleReleaseScheduler(
            TimeSpan.FromMinutes(5),
            () => { Interlocked.Increment(ref count); released.TrySetResult(); },
            delay.DelayAsync);

        scheduler.NotifyUse();
        delay.ElapseLatest();

        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task NotifyUse_ResetsCountdown_OnlyLatestReleases()
    {
        var delay = new ControllableDelay();
        int count = 0;
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new IdleReleaseScheduler(
            TimeSpan.FromMinutes(5),
            () => { Interlocked.Increment(ref count); released.TrySetResult(); },
            delay.DelayAsync);

        scheduler.NotifyUse();   // countdown #1
        scheduler.NotifyUse();   // cancels #1, starts #2
        delay.ElapseLatest();    // elapse #2 -> exactly one release

        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);    // give any stray (cancelled) #1 continuation a chance to (not) fire
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Cancel_PreventsRelease()
    {
        var delay = new ControllableDelay();
        int count = 0;
        var scheduler = new IdleReleaseScheduler(
            TimeSpan.FromMinutes(5),
            () => Interlocked.Increment(ref count),
            delay.DelayAsync);

        scheduler.NotifyUse();
        scheduler.Cancel();      // cancels the pending countdown
        delay.ElapseLatest();    // no-op: the delay was already cancelled

        await Task.Delay(100);
        Assert.Equal(0, count);
    }
}
