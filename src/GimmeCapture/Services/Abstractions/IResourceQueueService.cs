using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Services.Abstractions;

public interface IResourceQueueService : IAsyncDisposable
{
    bool IsProcessing { get; }

    ResourceQueueSnapshot? GetSnapshot(string key);

    IObservable<ResourceQueueSnapshot> Observe(string key);

    Task<ResourceQueueResult> EnqueueAsync(
        string key,
        Func<CancellationToken, IProgress<double>, Task<bool>> work,
        Action<double>? progressCallback = null);

    Task<ResourceQueueResult> EnqueueAsync(
        string key,
        Func<CancellationToken, Task<bool>> work,
        Action<double>? progressCallback = null);

    Task<ResourceQueueResult> EnqueueAsync(
        string key,
        Func<Task<bool>> work,
        Action<double>? progressCallback = null);

    bool Cancel(string key);
}
