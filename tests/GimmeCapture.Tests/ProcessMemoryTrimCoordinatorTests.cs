using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public sealed class ProcessMemoryTrimCoordinatorTests
{
    [Fact]
    public async Task ScheduleStartupTrimAsync_ShouldExecuteOnlyOnce()
    {
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reasons = new List<string>();
        var coordinator = new ProcessMemoryTrimCoordinator(
            reasons.Add,
            (_, _) => delay.Task);

        var first = coordinator.ScheduleStartupTrimAsync(TimeSpan.FromSeconds(2));
        var duplicate = coordinator.ScheduleStartupTrimAsync(TimeSpan.FromSeconds(2));
        delay.SetResult();

        Assert.True(await first);
        Assert.False(await duplicate);
        Assert.Equal(["startup-delayed"], reasons);
    }

    [Fact]
    public async Task ScheduleStartupTrimAsync_ShouldSkipWhenRuntimeTrimOccursDuringDelay()
    {
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reasons = new List<string>();
        var coordinator = new ProcessMemoryTrimCoordinator(
            reasons.Add,
            (_, _) => delay.Task);

        var startupTrim = coordinator.ScheduleStartupTrimAsync(TimeSpan.FromSeconds(2));
        coordinator.TrimNow("runtime");
        delay.SetResult();

        Assert.False(await startupTrim);
        Assert.Equal(["runtime"], reasons);
    }

    [Fact]
    public void TrimNow_ShouldSkipBackToBackRequests()
    {
        var now = DateTimeOffset.UtcNow;
        var reasons = new List<string>();
        var coordinator = new ProcessMemoryTrimCoordinator(
            reasons.Add,
            utcNow: () => now);

        Assert.True(coordinator.TrimNow("startup-delayed"));
        now += TimeSpan.FromSeconds(1);
        Assert.False(coordinator.TrimNow("runtime"));
        now += TimeSpan.FromSeconds(5);
        Assert.True(coordinator.TrimNow("runtime"));

        Assert.Equal(["startup-delayed", "runtime"], reasons);
    }

}
