using System;
using System.Threading;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Fires a <c>release</c> callback once the resource has gone unused for an idle delay. Every
/// <see cref="NotifyUse"/> (re)starts the countdown and cancels any pending release; <see cref="Cancel"/> stops
/// a pending release when the resource is freed by other means. Modeled on <c>IdleMemoryTrimScheduler</c> but for
/// unloading a heavy resource (an idle LLM / ONNX session) rather than trimming the GC heap.
///
/// The <c>release</c> callback runs on a thread-pool continuation and is expected to guard itself against
/// freeing a resource that is mid-use (e.g. try-acquire a lock and skip if busy). Pure timing (the delay is
/// injectable) so it is unit-testable without real time.
/// </summary>
internal sealed class IdleReleaseScheduler
{
    private readonly TimeSpan _idleDelay;
    private readonly Action _release;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly object _gate = new();
    private CancellationTokenSource? _pendingCts;
    private long _version;

    internal IdleReleaseScheduler(
        TimeSpan idleDelay,
        Action release,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _idleDelay = idleDelay < TimeSpan.Zero ? TimeSpan.Zero : idleDelay;
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _delayAsync = delayAsync ?? Task.Delay;
    }

    /// <summary>Call on every use of the resource: (re)start the idle countdown, dropping any pending release.</summary>
    internal void NotifyUse()
    {
        CancellationTokenSource cts;
        long version;
        lock (_gate)
        {
            _pendingCts?.Cancel();
            _pendingCts = cts = new CancellationTokenSource();
            version = ++_version;
        }

        _ = RunAsync(cts, version);
    }

    /// <summary>Cancel any pending release — e.g. the resource was explicitly released/disposed elsewhere.</summary>
    internal void Cancel()
    {
        lock (_gate)
        {
            _pendingCts?.Cancel();
            _pendingCts = null;
            _version++;
        }
    }

    private async Task RunAsync(CancellationTokenSource cts, long version)
    {
        try
        {
            await _delayAsync(_idleDelay, cts.Token).ConfigureAwait(false);

            lock (_gate)
            {
                // A newer NotifyUse/Cancel superseded this countdown, or it was cancelled — do nothing.
                if (!ReferenceEquals(_pendingCts, cts) || cts.IsCancellationRequested || _version != version)
                {
                    return;
                }

                _pendingCts = null;
            }

            _release();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer use — expected.
        }
        catch (Exception ex)
        {
            AppLog.Warning("IdleRelease.Run", ex);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingCts, cts))
                {
                    _pendingCts = null;
                }
            }

            cts.Dispose();
        }
    }
}
