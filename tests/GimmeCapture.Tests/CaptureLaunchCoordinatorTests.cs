using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class CaptureLaunchCoordinatorTests
{
    [Theory]
    [InlineData(CaptureMode.Normal)]
    [InlineData(CaptureMode.Translate)]
    [InlineData(CaptureMode.TextCopy)]
    public async Task TryLaunchAsync_BypassesDelay_ForNonDelayedModes(CaptureMode mode)
    {
        int countdownCalls = 0;
        int launchCalls = 0;
        var coordinator = new CaptureLaunchCoordinator((_, _) => throw new InvalidOperationException());

        var result = await coordinator.TryLaunchAsync(
            mode,
            CaptureDelay.TenSeconds,
            (_, _) =>
            {
                countdownCalls++;
                return Task.CompletedTask;
            },
            () =>
            {
                launchCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(CaptureLaunchResult.Launched, result);
        Assert.Equal(0, countdownCalls);
        Assert.Equal(1, launchCalls);
    }

    [Theory]
    [InlineData(CaptureMode.Copy)]
    [InlineData(CaptureMode.Pin)]
    [InlineData(CaptureMode.Record)]
    public async Task TryLaunchAsync_CountsDown_AndLaunchesOnce(CaptureMode mode)
    {
        var ticks = new List<int>();
        int launchCalls = 0;
        var coordinator = new CaptureLaunchCoordinator((_, _) => Task.CompletedTask);

        var result = await coordinator.TryLaunchAsync(
            mode,
            CaptureDelay.ThreeSeconds,
            (remaining, _) =>
            {
                ticks.Add(remaining);
                return Task.CompletedTask;
            },
            () =>
            {
                launchCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(CaptureLaunchResult.Launched, result);
        Assert.Equal([3, 2, 1], ticks);
        Assert.Equal(1, launchCalls);
    }

    [Fact]
    public async Task TryLaunchAsync_RejectsDuplicate_AndSupportsCancel()
    {
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new CaptureLaunchCoordinator(async (_, ct) =>
        {
            delayEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        int launchCalls = 0;

        var first = coordinator.TryLaunchAsync(
            CaptureMode.Pin,
            CaptureDelay.OneSecond,
            (_, _) => Task.CompletedTask,
            () =>
            {
                launchCalls++;
                return Task.CompletedTask;
            });
        await delayEntered.Task;

        var duplicate = await coordinator.TryLaunchAsync(
            CaptureMode.Pin,
            CaptureDelay.OneSecond,
            (_, _) => Task.CompletedTask,
            () => Task.CompletedTask);
        coordinator.Cancel();
        var firstResult = await first;

        Assert.Equal(CaptureLaunchResult.AlreadyRunning, duplicate);
        Assert.Equal(CaptureLaunchResult.Cancelled, firstResult);
        Assert.Equal(0, launchCalls);
        Assert.False(coordinator.IsRunning);
    }
}
