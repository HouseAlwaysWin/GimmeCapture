namespace GimmeCapture.Tests;

// The device is stood in for by a counter of holds taken and released, and the idle wait by a delay the test ends,
// so no GPU and no real time are needed.
public class GpuDeviceHoldTests
{
    private static readonly TimeSpan IdleRelease = TimeSpan.FromMinutes(5);

    [Fact]
    public void SessionBuilt_OnTheGpu_HoldsTheDeviceOnce()
    {
        var devices = new FakeDevices();
        var hold = new GpuDeviceHold(devices.Hold, IdleRelease, NeverElapses);

        hold.SessionBuilt("DirectML");
        hold.SessionBuilt("DirectML"); // the session rebuilt after an idle unload

        Assert.True(hold.IsHolding);
        Assert.Equal(1, devices.Taken);
        Assert.Equal(0, devices.Released);
    }

    [Fact]
    public void SessionBuilt_OnTheCpu_LetsGoOfTheDevice()
    {
        // A held device that a rebuild just failed on (a driver reset) is let go, to be opened afresh next time.
        var devices = new FakeDevices();
        var hold = new GpuDeviceHold(devices.Hold, IdleRelease, NeverElapses);
        hold.SessionBuilt("DirectML");

        hold.SessionBuilt(OnnxProviderConfigurator.CpuProvider);

        Assert.False(hold.IsHolding);
        Assert.Equal(1, devices.Released);

        hold.SessionBuilt("DirectML");
        Assert.True(hold.IsHolding);
        Assert.Equal(2, devices.Taken);
    }

    [Fact]
    public void SessionBuilt_OnTheCpu_HoldsNothing_WhereThereIsNoGpu()
    {
        var devices = new FakeDevices();
        var hold = new GpuDeviceHold(devices.Hold, IdleRelease, NeverElapses);

        hold.SessionBuilt(OnnxProviderConfigurator.CpuProvider);

        Assert.False(hold.IsHolding);
        Assert.Equal(0, devices.Taken);
    }

    [Fact]
    public void Release_LetsGoOfTheDevice_AndIsSafeToRepeat()
    {
        var devices = new FakeDevices();
        var hold = new GpuDeviceHold(devices.Hold, IdleRelease, NeverElapses);
        hold.SessionBuilt("DirectML");

        hold.Release();
        hold.Release();

        Assert.False(hold.IsHolding);
        Assert.Equal(1, devices.Released);
    }

    [Fact]
    public async Task TheDeviceIsLetGo_OnceUnusedForTheIdleDelay()
    {
        var devices = new FakeDevices();
        var delay = new ControllableDelay();
        var hold = new GpuDeviceHold(devices.Hold, IdleRelease, delay.DelayAsync);
        hold.SessionBuilt("DirectML");
        hold.NotifyUse(); // a removal finished: the countdown starts again

        delay.ElapseLatest();

        await devices.FirstRelease.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(hold.IsHolding);
        Assert.Equal(1, devices.Released);
    }

    private static Task NeverElapses(TimeSpan delay, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

    // A stand-in for Task.Delay that elapses when the test says so. Cancelling the token cancels it, as the scheduler
    // does when a use restarts the countdown.
    private sealed class ControllableDelay
    {
        private volatile TaskCompletionSource? _latest;

        public Task DelayAsync(TimeSpan _, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled());
            _latest = tcs;
            return tcs.Task;
        }

        public void ElapseLatest() => _latest?.TrySetResult();
    }

    private sealed class FakeDevices
    {
        private readonly TaskCompletionSource _firstRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Taken { get; private set; }
        public int Released { get; private set; }
        public Task FirstRelease => _firstRelease.Task;

        public IDisposable Hold()
        {
            Taken++;
            return new Device(this);
        }

        private sealed class Device(FakeDevices owner) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                owner.Released++;
                owner._firstRelease.TrySetResult();
            }
        }
    }
}
