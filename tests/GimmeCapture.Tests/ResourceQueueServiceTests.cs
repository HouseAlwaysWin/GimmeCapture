using System.Collections.Concurrent;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Tests;

public sealed class ResourceQueueServiceTests
{
    [Fact]
    public async Task EnqueueAsync_LimitsConcurrencyToWorkerCount()
    {
        await using var queue = new ResourceQueueService(workerCount: 3);
        var running = 0;
        var maxRunning = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, 8)
            .Select(index => queue.EnqueueAsync($"job-{index}", async token =>
            {
                var current = Interlocked.Increment(ref running);
                UpdateMax(ref maxRunning, current);
                await release.Task.WaitAsync(token);
                Interlocked.Decrement(ref running);
                return true;
            }))
            .ToArray();

        await WaitUntilAsync(() => Volatile.Read(ref running) == 3);
        Assert.Equal(3, Volatile.Read(ref maxRunning));

        release.SetResult();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, result => Assert.Equal(ResourceQueueStatus.Completed, result.Status));
    }

    [Fact]
    public async Task EnqueueAsync_DeduplicatesRunningKeyAndSharesCompletion()
    {
        await using var queue = new ResourceQueueService();
        var calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = queue.EnqueueAsync("same", async token =>
        {
            Interlocked.Increment(ref calls);
            await release.Task.WaitAsync(token);
            return true;
        });
        var second = queue.EnqueueAsync("same", _ => Task.FromResult(true));

        Assert.Same(first, second);
        release.SetResult();

        Assert.Equal(ResourceQueueStatus.Completed, (await first).Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cancel_CancelsPendingAndRunningJobs()
    {
        await using var queue = new ResourceQueueService(workerCount: 1);
        var runningStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = queue.EnqueueAsync("running", async token =>
        {
            runningStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        });
        await runningStarted.Task;

        var pendingExecuted = false;
        var pending = queue.EnqueueAsync("pending", _ =>
        {
            pendingExecuted = true;
            return Task.FromResult(true);
        });

        Assert.True(queue.Cancel("pending"));
        Assert.True(queue.Cancel("running"));
        Assert.Equal(ResourceQueueStatus.Cancelled, (await pending).Status);
        Assert.Equal(ResourceQueueStatus.Cancelled, (await running).Status);
        Assert.False(pendingExecuted);
    }

    [Fact]
    public async Task CompletedSnapshot_IsRetainedUntilRetryReplacesIt()
    {
        await using var queue = new ResourceQueueService();

        var first = await queue.EnqueueAsync("retry", () => Task.FromResult(true));
        Assert.Equal(ResourceQueueStatus.Completed, first.Status);
        Assert.Equal(ResourceQueueStatus.Completed, queue.GetSnapshot("retry")?.Status);

        var second = await queue.EnqueueAsync("retry", () => Task.FromResult(false));
        Assert.Equal(ResourceQueueStatus.Failed, second.Status);
        Assert.Equal(ResourceQueueStatus.Failed, queue.GetSnapshot("retry")?.Status);
    }

    [Fact]
    public async Task RetryDuringRunningCancellation_SharesCompletionUntilCleanupFinishes()
    {
        await using var queue = new ResourceQueueService(workerCount: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = queue.EnqueueAsync("retry-after-cancel", async token =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            }
            catch (OperationCanceledException)
            {
                await releaseCleanup.Task;
                throw;
            }
        });
        await started.Task;

        Assert.True(queue.Cancel("retry-after-cancel"));
        var duringCleanup = queue.EnqueueAsync("retry-after-cancel", () => Task.FromResult(true));

        Assert.Same(first, duringCleanup);
        Assert.False(first.IsCompleted);

        releaseCleanup.SetResult();
        Assert.Equal(ResourceQueueStatus.Cancelled, (await first).Status);

        var retry = await queue.EnqueueAsync("retry-after-cancel", () => Task.FromResult(true));
        Assert.Equal(ResourceQueueStatus.Completed, retry.Status);
    }

    [Fact]
    public async Task ProgressAndError_AreIncludedInSnapshots()
    {
        await using var queue = new ResourceQueueService();
        var snapshots = new ConcurrentQueue<ResourceQueueSnapshot>();
        using var subscription = queue.Observe("progress").Subscribe(snapshots.Enqueue);

        var result = await queue.EnqueueAsync("progress", (_, progress) =>
        {
            progress.Report(42);
            throw new InvalidOperationException("download failed");
        });

        Assert.Equal(ResourceQueueStatus.Failed, result.Status);
        Assert.Equal("download failed", result.ErrorMessage);
        Assert.Contains(snapshots, snapshot => snapshot.Progress == 42);
        Assert.Equal("download failed", queue.GetSnapshot("progress")?.ErrorMessage);
    }

    [Fact]
    public async Task DisposeAsync_CancelsOutstandingWork()
    {
        var queue = new ResourceQueueService(workerCount: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = queue.EnqueueAsync("dispose", async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        });

        await started.Task;
        await queue.DisposeAsync();

        Assert.Equal(ResourceQueueStatus.Cancelled, (await completion).Status);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static void UpdateMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
