using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System.Reactive.Subjects;

namespace GimmeCapture.Services.Core.Media;

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

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();

    public void Cancel(string name)
    {
        System.Diagnostics.Debug.WriteLine($"[ResourceQueue] Attempting to cancel {name}...");
        if (_cancellationTokens.TryGetValue(name, out var cts))
        {
            try
            {
                if (cts.IsCancellationRequested)
                {
                     System.Diagnostics.Debug.WriteLine($"[ResourceQueue] {name} is ALREADY cancelled.");
                }
                else
                {
                    cts.Cancel();
                    System.Diagnostics.Debug.WriteLine($"[ResourceQueue] Cancelled item: {name}");
                }
            }
            catch (ObjectDisposedException) 
            {
                 System.Diagnostics.Debug.WriteLine($"[ResourceQueue] CTS for {name} was disposed.");
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[ResourceQueue] CTS for {name} NOT FOUND in _cancellationTokens.");
        }
    }

    public Task<bool> EnqueueAsync(string name, Func<CancellationToken, Task<bool>> downloadAction, Action<double>? progressCallback = null)
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
            DownloadAction = null // We use a wrapper or internal storage for the CT-aware action
        };

        // We need a way to store the CT-aware action. 
        // Let's modify DownloadQueueItem or use a wrapper.
        // For simplicity, let's wrap it here and assign to DownloadAction (which expects Task<bool> with no args).
        // But wait, we need to create the CTS *when processing starts* or just before?
        // If we create it now, we can allow cancelling Pending items too.
        
        var cts = new CancellationTokenSource();
        _cancellationTokens.AddOrUpdate(name, cts, (k, v) => cts);

        item.DownloadAction = async () => 
        {
            try
            {
                // Re-get CTS in case it changed (unlikely for same ID)
                if (!_cancellationTokens.TryGetValue(name, out var myCts)) return false;
                return await downloadAction(myCts.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
               // Cleanup happens in ProcessQueueAsync or here?
            }
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
    
    // Legacy overload
    public Task<bool> EnqueueAsync(string name, Func<Task<bool>> downloadAction, Action<double>? progressCallback = null)
    {
        return EnqueueAsync(name, (ct) => downloadAction(), progressCallback);
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
                            
                            // Check cancellation before starting
                            if (_cancellationTokens.TryGetValue(activeItem.Name, out var cts) && cts.IsCancellationRequested)
                            {
                                activeItem.Status = QueueItemStatus.Failed; // Or separate Cancelled status?
                                // "Failed" is probably safer for now as UI handles it as "Stop processing"
                            }
                            else
                            {
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
                            
                            // Cleanup CTS
                             if (_cancellationTokens.TryRemove(item.Name, out var cts))
                             {
                                 cts.Dispose();
                             }
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
