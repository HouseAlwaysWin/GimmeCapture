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
        var factory = new DebouncedSettingsSaveCoordinatorFactory(TimeSpan.FromMilliseconds(25));
        var coordinator = factory.Create(() =>
        {
            Interlocked.Increment(ref saveCount);
            return Task.FromResult(true);
        });

        coordinator.RequestSave();
        coordinator.RequestSave();
        coordinator.RequestSave();

        await Task.Delay(120);

        Assert.Equal(1, Volatile.Read(ref saveCount));
    }
}
