using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

/// <summary>
/// The gate that stops a shared native resource being disposed while it is in use. This exists because the OCR
/// runtime disposed its ONNX sessions on a language switch while another thread was mid-inference on them, which
/// killed the process with an access violation (0xC0000005) — an unrecoverable native fault, not a catchable
/// exception. Every rule below is what prevents that.
/// </summary>
public class ResourceUseGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    [Fact]
    public void BeginUse_AllowsConcurrentUsers()
    {
        var gate = new ResourceUseGate();

        using var first = gate.BeginUse();
        using var second = gate.BeginUse();

        Assert.Equal(2, gate.ActiveUses);
    }

    [Fact]
    public void EndingAUse_ReleasesIt()
    {
        var gate = new ResourceUseGate();

        using (gate.BeginUse())
        {
            Assert.Equal(1, gate.ActiveUses);
        }

        Assert.Equal(0, gate.ActiveUses);
    }

    [Fact]
    public void DisposingAUseTwice_DoesNotDoubleRelease()
    {
        var gate = new ResourceUseGate();

        var use = gate.BeginUse();
        use.Dispose();
        use.Dispose();

        Assert.Equal(0, gate.ActiveUses);
    }

    [Fact]
    public async Task TryBeginExclusive_WaitsForAnInFlightUseToFinish()
    {
        // The crash: exclusive access is "dispose the sessions", so it must never start while an inference holds them.
        var gate = new ResourceUseGate();
        var use = gate.BeginUse();

        var exclusive = Task.Run(() =>
        {
            bool acquired = gate.TryBeginExclusive(Timeout, out var scope);
            scope?.Dispose();
            return acquired;
        });

        Assert.False(exclusive.Wait(Settle), "exclusive access started while a use was still in flight");

        use.Dispose();

        Assert.True(await exclusive.WaitAsync(Timeout));
    }

    [Fact]
    public async Task BeginUse_BlocksWhileExclusiveIsHeld()
    {
        var gate = new ResourceUseGate();
        Assert.True(gate.TryBeginExclusive(Timeout, out var exclusive));

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var use = Task.Run(() =>
        {
            using var scope = gate.BeginUse();
            started.TrySetResult();
        });

        Assert.False(started.Task.Wait(Settle), "a use started while the resource was being swapped out");

        exclusive!.Dispose();

        await use.WaitAsync(Timeout);
    }

    [Fact]
    public async Task TryBeginExclusive_IsMutuallyExclusive()
    {
        var gate = new ResourceUseGate();
        Assert.True(gate.TryBeginExclusive(Timeout, out var first));

        var second = Task.Run(() =>
        {
            bool acquired = gate.TryBeginExclusive(Timeout, out var scope);
            scope?.Dispose();
            return acquired;
        });

        Assert.False(second.Wait(Settle));

        first!.Dispose();

        Assert.True(await second.WaitAsync(Timeout));
    }

    [Fact]
    public async Task TryBeginExclusive_TimesOut_AndLeavesTheGateUsable()
    {
        // A wedged inference must degrade to "skip the swap", never to "block every future use forever".
        var gate = new ResourceUseGate();
        using var stuck = gate.BeginUse();

        Assert.False(gate.TryBeginExclusive(TimeSpan.FromMilliseconds(100), out var scope));
        Assert.Null(scope);

        var laterUse = Task.Run(() =>
        {
            using var use = gate.BeginUse();
            return true;
        });

        Assert.True(await laterUse.WaitAsync(Timeout));
    }

    [Fact]
    public void TryBeginExclusive_WhenIdle_SucceedsImmediately()
    {
        var gate = new ResourceUseGate();

        Assert.True(gate.TryBeginExclusive(TimeSpan.Zero, out var scope));

        scope!.Dispose();
        Assert.True(gate.TryBeginExclusive(TimeSpan.Zero, out var again));
        again!.Dispose();
    }

    [Fact]
    public async Task ExclusiveAccess_TakesPriorityOverNewUses()
    {
        // Without writer preference a steady stream of captures could starve the swap indefinitely.
        var gate = new ResourceUseGate();
        var firstUse = gate.BeginUse();

        var exclusiveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exclusive = Task.Run(() =>
        {
            bool acquired = gate.TryBeginExclusive(Timeout, out var scope);
            exclusiveStarted.TrySetResult();
            Thread.Sleep(50);
            scope?.Dispose();
            return acquired;
        });

        // Give the exclusive waiter time to register its intent before a new use arrives.
        await Task.Delay(Settle);

        var queuedUse = Task.Run(() =>
        {
            using var scope = gate.BeginUse();
            return true;
        });

        Assert.False(queuedUse.Wait(Settle), "a new use jumped ahead of a waiting swap");

        firstUse.Dispose();

        Assert.True(await exclusive.WaitAsync(Timeout));
        Assert.True(await queuedUse.WaitAsync(Timeout));
    }
}
