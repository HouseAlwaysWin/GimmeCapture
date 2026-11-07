using System;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Writes a value to disk off the caller's thread, keeping only the newest: the first save starts a short wait, saves
/// during it replace the value waiting, and one write follows. Writes never overlap and never put an older value over
/// a newer one. <see cref="Flush"/> writes whatever is still waiting, at once. Each value handed over must hold still
/// while it waits: a copy, or an object nothing changes after.
/// </summary>
internal class CoalescingWriter<T>
    where T : class
{
    private readonly Action<T> _write;
    private readonly TimeSpan _coalesceDelay;
    private readonly object _pendingLock = new();
    private readonly object _writeLock = new(); // held through a write: one at a time, each taking the newest value
    private T? _pending;
    private bool _writeScheduled;

    public CoalescingWriter(Action<T> write, TimeSpan coalesceDelay)
    {
        _write = write;
        _coalesceDelay = coalesceDelay;
    }

    /// <summary>Writes the value still waiting, if any, on the calling thread, after any write under way.</summary>
    public void Flush() => WritePending();

    /// <summary>Keeps <paramref name="value"/> to write shortly; returns without waiting for any write.</summary>
    protected void SaveValue(T value, string operation)
    {
        lock (_pendingLock)
        {
            _pending = value;
            if (_writeScheduled)
            {
                return;
            }

            _writeScheduled = true;
        }

        Task.Run(WriteAfterDelayAsync).Forget(operation);
    }

    private async Task WriteAfterDelayAsync()
    {
        await Task.Delay(_coalesceDelay).ConfigureAwait(false);
        WritePending();
    }

    private void WritePending()
    {
        lock (_writeLock)
        {
            T? value;
            lock (_pendingLock)
            {
                value = _pending;
                _pending = null;
                _writeScheduled = false; // a save from here on needs a write of its own
            }

            if (value != null)
            {
                _write(value);
            }
        }
    }
}
