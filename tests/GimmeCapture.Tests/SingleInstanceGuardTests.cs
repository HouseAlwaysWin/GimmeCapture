using System;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class SingleInstanceGuardTests
{
    // Acquisition happens on the calling thread, so the "second launch" is simulated from a
    // different (thread-pool) thread. That models a second process faithfully only on Windows:
    // on Unix, .NET named-mutex ownership is process-scoped (another thread in the SAME process
    // re-acquires successfully), while a real second process is still excluded — which is the
    // case the guard exists for and cannot be exercised in-process there.

    [Fact]
    public async Task TryAcquire_WhileHeld_RefusesASecondAcquire()
    {
        string key = Guid.NewGuid().ToString("N");
        using var first = SingleInstanceGuard.TryAcquire(key);
        Assert.NotNull(first);

        var second = await Task.Run(() => SingleInstanceGuard.TryAcquire(key));
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(second);
        }
        else
        {
            second?.Dispose();
        }
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
