using System;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class SingleInstanceGuardTests
{
    // Acquisition happens on the calling thread and a mutex is re-entrant per thread, so the
    // "second launch" must run on a DEDICATED thread — Task.Run is not enough, because while the
    // test method awaits, its pool thread is free to execute the Task.Run work item, and the
    // re-entrant acquire then "succeeds" on the owner thread itself. Even on a dedicated thread
    // the refusal is only observable on Windows: Unix named-mutex ownership is process-scoped
    // (any thread of the SAME process re-acquires), while a real second process — the case the
    // guard exists for — is excluded on both OSes.
    private static SingleInstanceGuard? AcquireOnDedicatedThread(string key)
    {
        SingleInstanceGuard? result = null;
        var thread = new System.Threading.Thread(() => result = SingleInstanceGuard.TryAcquire(key));
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public void TryAcquire_WhileHeld_RefusesASecondAcquire()
    {
        string key = Guid.NewGuid().ToString("N");
        using var first = SingleInstanceGuard.TryAcquire(key);
        Assert.NotNull(first);

        var second = AcquireOnDedicatedThread(key);
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
    public void TryAcquire_AfterDispose_SucceedsAgain()
    {
        string key = Guid.NewGuid().ToString("N");
        var first = SingleInstanceGuard.TryAcquire(key);
        Assert.NotNull(first);
        first!.Dispose();

        var again = AcquireOnDedicatedThread(key);
        Assert.NotNull(again);
        again!.Dispose();
    }

    [Fact]
    public void SignalRunningInstance_WithNoListener_DoesNotThrow()
    {
        SingleInstanceGuard.SignalRunningInstance(Guid.NewGuid().ToString("N"));
    }
}
