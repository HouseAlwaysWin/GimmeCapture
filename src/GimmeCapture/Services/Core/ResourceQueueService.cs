using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System.Reactive.Subjects;

namespace GimmeCapture.Services.Core;

public enum QueueItemStatus
{
    Pending,
    Downloading,
    Completed,
    Failed
}

public class DownloadQueueItem : ReactiveObject
{
    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    private QueueItemStatus _status;
    public QueueItemStatus Status
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    public Func<Task<bool>>? DownloadAction { get; set; }
}

public class ResourceQueueService : ReactiveObject
{
    private static ResourceQueueService? _instance;
    public static ResourceQueueService Instance => _instance ??= new ResourceQueueService();

    private readonly SemaphoreSlim _semaphore = new(3, 3);
    private readonly ConcurrentQueue<DownloadQueueItem> _queue = new();
    private readonly ConcurrentDictionary<string, DownloadQueueItem> _activeItems = new();
    private readonly ISubject<(string Name, QueueItemStatus Status)> _statusSubject = new Subject<(string Name, QueueItemStatus Status)>();
    
    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set => this.RaiseAndSetIfChanged(ref _isProcessing, value);
    }

    public IObservable<QueueItemStatus> ObserveStatus(string name)
    {
        return Observable.Create<QueueItemStatus>(observer =>
        {
            if (_activeItems.TryGetValue(name, out var item))
            {
                observer.OnNext(item.Status);
            }
            
            return _statusSubject
                .Where(x => x.Name == name)
                .Select(x => x.Status)
                .Subscribe(observer);
        });
    }

    public QueueItemStatus? GetStatus(string name)
    {
        if (_activeItems.TryGetValue(name, out var item))
        {
            return item.Status;
        }
        return null;
    }

    public Task<bool> EnqueueAsync(string name, Func<Task<bool>> downloadAction, Action<double>? progressCallback = null)
    {
        // Deduplication: If already pending or downloading, don't queue again.
        if (_activeItems.TryGetValue(name, out var existingItem) && 
            (existingItem.Status == QueueItemStatus.Pending || existingItem.Status == QueueItemStatus.Downloading))
        {
            return Task.FromResult(true);
        }

        var item = new DownloadQueueItem
        {
            Name = name,
            Status = QueueItemStatus.Pending,
            DownloadAction = downloadAction
        };
        
        // Publish initial status
        _statusSubject.OnNext((name, QueueItemStatus.Pending));
        
        // Forward future status changes
        item.WhenAnyValue(x => x.Status)
            .Subscribe(status => _statusSubject.OnNext((name, status)));

        _queue.Enqueue(item);
        _activeItems.AddOrUpdate(name, item, (k, v) => item);
        
        if (!IsProcessing)
        {
            _ = Task.Run(ProcessQueueAsync);
        }

        return Task.FromResult(true);
    }

    private async Task ProcessQueueAsync()
    {
        if (IsProcessing) return;
        
        try
        {
            IsProcessing = true;
            
            while (!_queue.IsEmpty)
            {
                // Wait for a slot
                await _semaphore.WaitAsync();

                if (_queue.TryDequeue(out var item))
                {
                    // Dispatch to background task
                    _ = Task.Run(async () => 
                    {
                        try 
                        {
                            // Retrieve the canonical item from dictionary to ensure updates are visible
                            if (!_activeItems.TryGetValue(item.Name, out var activeItem))
                            {
                                activeItem = item;
                            }

                            activeItem.Status = QueueItemStatus.Downloading;
                            
                            if (activeItem.DownloadAction != null)
                            {
                                bool success = await activeItem.DownloadAction();
                                activeItem.Status = success ? QueueItemStatus.Completed : QueueItemStatus.Failed;
                            }
                            else
                            {
                                activeItem.Status = QueueItemStatus.Failed;
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Queue Download Error ({item.Name}): {ex.Message}");
                            if (_activeItems.TryGetValue(item.Name, out var activeItem))
                            {
                                activeItem.Status = QueueItemStatus.Failed;
                            }
                        }
                        finally
                        {
                            _semaphore.Release();
                            
                            // Check if we need to restart the pump/ensure it keeps running?
                            // The main loop is running, it triggers on 'queue not empty'.
                            // If main loop is blocked on WaitAsync, Release() will unblock it.
                            // So this is self-sustaining until queue is empty.
                        }
                    });
                }
                else
                {
                    _semaphore.Release();
                    break; 
                }
            }
        }
        finally
        {
            IsProcessing = false;
        }
    }
}
