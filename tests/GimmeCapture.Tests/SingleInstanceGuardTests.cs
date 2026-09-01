using System;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class SingleInstanceGuardTests
{
    // Acquisition happens on the calling thread and a mutex is re-entrant per thread, so the
    // "second launch" is simulated from a different (thread-pool) thread — as a real second
    // process would appear to the named mutex.

    [Fact]
    public async Task TryAcquire_WhileHeld_RefusesASecondAcquire()
    {
        string key = Guid.NewGuid().ToString("N");
        using var first = SingleInstanceGuard.TryAcquire(key);
        Assert.NotNull(first);

        var second = await Task.Run(() => SingleInstanceGuard.TryAcquire(key));
        Assert.Null(second);
    }

    [Fact]
    public async Task TryAcquire_AfterDispose_SucceedsAgain()
    {
        string key = Guid.NewGuid().ToString("N");
        var first = SingleInstanceGuard.TryAcquire(key);
        Assert.NotNull(first);
        first!.Dispose();

        var again = await Task.Run(() => SingleInstanceGuard.TryAcquire(key));
        Assert.NotNull(again);
        again!.Dispose();
    }

    [Fact]
    public void SignalRunningInstance_WithNoListener_DoesNotThrow()
    {
        SingleInstanceGuard.SignalRunningInstance(Guid.NewGuid().ToString("N"));
    }
}
