using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class SettingsSaveCoordinatorTests
{
    [Fact]
    public async Task RequestSave_Debounces_Rapid_Changes()
    {
        int saveCount = 0;
        var firstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DebouncedSettingsSaveCoordinatorFactory(TimeSpan.FromMilliseconds(25));
        var coordinator = factory.Create(() =>
        {
            Interlocked.Increment(ref saveCount);
            firstSave.TrySetResult();
            return Task.FromResult(true);
        });

        coordinator.RequestSave();
        coordinator.RequestSave();
        coordinator.RequestSave();

        // Await the debounced save's signal rather than polling a tight wall-clock deadline. Under heavy
        // parallel test load the threadpool is momentarily starved (it grows ~1 thread/sec), so the 25 ms
        // debounce continuation can be delayed for a second or two — which made the old 1 s deadline flaky
        // (saveCount still 0). A generous timeout absorbs that without weakening the coalescing assertion.
        await firstSave.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Brief settle to catch an erroneous second (un-debounced) save before asserting exactly one.
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref saveCount));
    }
}
