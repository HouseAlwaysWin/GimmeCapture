using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public sealed class IdleMemoryTrimSchedulerTests
{
    [Fact]
    public async Task RequestTrimAsync_ShouldDebounceAndCombineReasons()
    {
        var delays = new List<TaskCompletionSource>();
        var reasons = new List<string>();
        var scheduler = new IdleMemoryTrimScheduler(
            reasons.Add,
            (_, _) =>
            {
                var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                delays.Add(delay);
                return delay.Task;
            });

        var first = scheduler.RequestTrimAsync("ocr-unloaded", TimeSpan.FromSeconds(2));
        var second = scheduler.RequestTrimAsync("translation-resources-released", TimeSpan.FromSeconds(2));
        delays[0].SetResult();
        delays[1].SetResult();

        Assert.False(await first);
        Assert.True(await second);
        Assert.Equal(["idle:ocr-unloaded+translation-resources-released"], reasons);
    }

    [Fact]
    public async Task NotifyActivity_ShouldCancelPendingTrim()
    {
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reasons = new List<string>();
        var scheduler = new IdleMemoryTrimScheduler(
            reasons.Add,
            (_, _) => delay.Task);

        var pending = scheduler.RequestTrimAsync("startup", TimeSpan.FromSeconds(2));
        scheduler.NotifyActivity("ocr");
        delay.SetResult();

        Assert.False(await pending);
        Assert.Empty(reasons);
    }

    [Fact]
    public async Task RequestTrimAsync_ShouldRunAfterActivityCancellation()
    {
        var delays = new List<TaskCompletionSource>();
        var reasons = new List<string>();
        var scheduler = new IdleMemoryTrimScheduler(
            reasons.Add,
            (_, _) =>
            {
                var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                delays.Add(delay);
                return delay.Task;
            });

        var cancelled = scheduler.RequestTrimAsync("startup", TimeSpan.FromSeconds(2));
        scheduler.NotifyActivity("translation");
        var replacement = scheduler.RequestTrimAsync("translation-resources-released", TimeSpan.FromSeconds(2));
        delays[0].SetResult();
        delays[1].SetResult();

        Assert.False(await cancelled);
        Assert.True(await replacement);
        Assert.Equal(["idle:translation-resources-released"], reasons);
    }

    [Fact]
    public async Task MainWindowRestore_ShouldCancelPendingTrayTrim()
    {
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reasons = new List<string>();
        var scheduler = new IdleMemoryTrimScheduler(
            reasons.Add,
            (_, _) => delay.Task);

        var pending = scheduler.RequestTrimAsync("main-window-tray", TimeSpan.FromSeconds(2));
        scheduler.NotifyActivity("main-window-visible");
        delay.SetResult();

        Assert.False(await pending);
        Assert.Empty(reasons);
    }

    [Fact]
    public void TrimNow_ShouldSkipBackToBackRequests()
    {
        var now = DateTimeOffset.UtcNow;
        var reasons = new List<string>();
        var scheduler = new IdleMemoryTrimScheduler(
            reasons.Add,
            utcNow: () => now);

        Assert.True(scheduler.TrimNow("first"));
        now += TimeSpan.FromSeconds(1);
        Assert.False(scheduler.TrimNow("second"));
        now += TimeSpan.FromSeconds(5);
        Assert.True(scheduler.TrimNow("third"));

        Assert.Equal(["first", "third"], reasons);
    }
}
