using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Core.Media;

public enum ResourceQueueStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record ResourceQueueSnapshot(
    string Key,
    ResourceQueueStatus Status,
    double Progress,
    string? ErrorMessage,
    DateTimeOffset UpdatedAt);

public sealed record ResourceQueueResult(
    string Key,
    ResourceQueueStatus Status,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Status == ResourceQueueStatus.Completed;
}

public sealed class ResourceQueueService : IResourceQueueService
{
    public const int DefaultWorkerCount = 3;

    private readonly object _gate = new();
    private readonly Dictionary<string, QueueEntry> _entries = new(StringComparer.Ordinal);
    private readonly Channel<QueueEntry> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Subject<ResourceQueueSnapshot> _snapshotSubject = new();
    private readonly ISubject<ResourceQueueSnapshot> _snapshots;
    private readonly Task[] _workers;
    private bool _disposed;

    public ResourceQueueService(int workerCount = DefaultWorkerCount)
    {
        if (workerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workerCount));
        }

        _channel = Channel.CreateUnbounded<QueueEntry>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = workerCount == 1,
            SingleWriter = false
        });
        _snapshots = Subject.Synchronize(_snapshotSubject);
        _workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => WorkerAsync(_shutdown.Token)))
            .ToArray();
    }

    public bool IsProcessing
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.Any(entry =>
                    entry.Snapshot.Status is ResourceQueueStatus.Pending or ResourceQueueStatus.Running);
            }
        }
    }

    public ResourceQueueSnapshot? GetSnapshot(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) ? entry.Snapshot : null;
        }
    }

    public IObservable<ResourceQueueSnapshot> Observe(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return Observable.Create<ResourceQueueSnapshot>(observer =>
        {
            var current = GetSnapshot(key);
            if (current is not null)
            {
                observer.OnNext(current);
            }

            return _snapshots
                .Where(snapshot => string.Equals(snapshot.Key, key, StringComparison.Ordinal))
                .Subscribe(observer);
        });
    }

    public Task<ResourceQueueResult> EnqueueAsync(
        string key,
        Func<CancellationToken, IProgress<double>, Task<bool>> work,
        Action<double>? progressCallback = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(work);

        QueueEntry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_entries.TryGetValue(key, out var existing) &&
                !existing.Completion.Task.IsCompleted)
            {
                return existing.Completion.Task;
            }

            entry = new QueueEntry(key, work, progressCallback);
            _entries[key] = entry;
        }

        Publish(entry.Snapshot);
        if (!_channel.Writer.TryWrite(entry))
        {
            Complete(entry, ResourceQueueStatus.Failed, "The resource queue is not accepting work.");
        }

        return entry.Completion.Task;
    }

    public Task<ResourceQueueResult> EnqueueAsync(
        string key,
        Func<CancellationToken, Task<bool>> work,
        Action<double>? progressCallback = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        return EnqueueAsync(key, (token, _) => work(token), progressCallback);
    }

    public Task<ResourceQueueResult> EnqueueAsync(
        string key,
        Func<Task<bool>> work,
        Action<double>? progressCallback = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        return EnqueueAsync(key, (_, _) => work(), progressCallback);
    }

    public bool Cancel(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        QueueEntry? entry;
        ResourceQueueStatus currentStatus;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry) ||
                entry.Snapshot.Status is ResourceQueueStatus.Completed or ResourceQueueStatus.Failed or ResourceQueueStatus.Cancelled)
            {
                return false;
            }

            currentStatus = entry.Snapshot.Status;
        }

        entry.Cancellation.Cancel();
        if (currentStatus == ResourceQueueStatus.Pending)
        {
            Complete(entry, ResourceQueueStatus.Cancelled);
        }
        else
        {
            Update(entry, ResourceQueueStatus.Cancelled, entry.Snapshot.Progress, null);
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        QueueEntry[] entries;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = _entries.Values.ToArray();
        }

        foreach (var entry in entries)
        {
            entry.Cancellation.Cancel();
            Complete(entry, ResourceQueueStatus.Cancelled);
        }

        _channel.Writer.TryComplete();
        _shutdown.Cancel();

        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var entry in entries)
        {
            entry.Cancellation.Dispose();
        }

        _snapshotSubject.OnCompleted();
        _snapshotSubject.Dispose();
        _shutdown.Dispose();
    }

    private async Task WorkerAsync(CancellationToken shutdownToken)
    {
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(shutdownToken).ConfigureAwait(false))
            {
                if (entry.Completion.Task.IsCompleted)
                {
                    continue;
                }

                Update(entry, ResourceQueueStatus.Running, entry.Snapshot.Progress, null);
                var progress = new InlineProgress<double>(value =>
                {
                    var normalized = Math.Clamp(value, 0, 100);
                    if (UpdateProgress(entry, normalized))
                    {
                        entry.ProgressCallback?.Invoke(normalized);
                    }
                });

                try
                {
                    var succeeded = await entry.Work(entry.Cancellation.Token, progress).ConfigureAwait(false);
                    if (entry.Cancellation.IsCancellationRequested)
                    {
                        Complete(entry, ResourceQueueStatus.Cancelled);
                    }
                    else
                    {
                        Complete(
                            entry,
                            succeeded ? ResourceQueueStatus.Completed : ResourceQueueStatus.Failed,
                            succeeded ? null : "The queued operation reported failure.");
                    }
                }
                catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
                {
                    Complete(entry, ResourceQueueStatus.Cancelled);
                }
                catch (Exception ex)
                {
                    Complete(entry, ResourceQueueStatus.Failed, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
        }
    }

    private bool UpdateProgress(QueueEntry entry, double progress)
    {
        ResourceQueueSnapshot? snapshot = null;
        lock (_gate)
        {
            if (IsCurrentEntry(entry) && entry.Snapshot.Status == ResourceQueueStatus.Running)
            {
                entry.Snapshot = entry.Snapshot with
                {
                    Progress = progress,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                snapshot = entry.Snapshot;
            }
        }

        if (snapshot is not null)
        {
            Publish(snapshot);
            return true;
        }

        return false;
    }

    private void Update(
        QueueEntry entry,
        ResourceQueueStatus status,
        double progress,
        string? errorMessage)
    {
        ResourceQueueSnapshot? snapshot = null;
        lock (_gate)
        {
            if (IsCurrentEntry(entry) && !entry.Completion.Task.IsCompleted)
            {
                entry.Snapshot = new ResourceQueueSnapshot(
                    entry.Key,
                    status,
                    progress,
                    errorMessage,
                    DateTimeOffset.UtcNow);
                snapshot = entry.Snapshot;
            }
        }

        if (snapshot is not null)
        {
            Publish(snapshot);
        }
    }

    private void Complete(
        QueueEntry entry,
        ResourceQueueStatus status,
        string? errorMessage = null)
    {
        ResourceQueueSnapshot? snapshot = null;
        ResourceQueueResult? result = null;
        lock (_gate)
        {
            if (IsCurrentEntry(entry) && !entry.Completion.Task.IsCompleted)
            {
                var progress = status == ResourceQueueStatus.Completed ? 100 : entry.Snapshot.Progress;
                entry.Snapshot = new ResourceQueueSnapshot(
                    entry.Key,
                    status,
                    progress,
                    errorMessage,
                    DateTimeOffset.UtcNow);
                snapshot = entry.Snapshot;
                result = new ResourceQueueResult(entry.Key, status, errorMessage);
            }
        }

        if (snapshot is null || result is null)
        {
            return;
        }

        Publish(snapshot);
        entry.Completion.TrySetResult(result);
    }

    private bool IsCurrentEntry(QueueEntry entry)
    {
        return _entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry);
    }

    private void Publish(ResourceQueueSnapshot snapshot)
    {
        _snapshots.OnNext(snapshot);
    }

    private sealed class QueueEntry
    {
        public QueueEntry(
            string key,
            Func<CancellationToken, IProgress<double>, Task<bool>> work,
            Action<double>? progressCallback)
        {
            Key = key;
            Work = work;
            ProgressCallback = progressCallback;
            Snapshot = new ResourceQueueSnapshot(
                key,
                ResourceQueueStatus.Pending,
                0,
                null,
                DateTimeOffset.UtcNow);
        }

        public string Key { get; }

        public Func<CancellationToken, IProgress<double>, Task<bool>> Work { get; }

        public Action<double>? ProgressCallback { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource<ResourceQueueResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ResourceQueueSnapshot Snapshot { get; set; }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
