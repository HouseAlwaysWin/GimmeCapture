using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Runs requests one at a time, newest first, on a thread of its own while there are any. A request submitted while
/// another runs replaces any still waiting, so a burst of seeks costs one open instead of one each, and the caller
/// never waits for any of them. The running request can ask whether a newer one has arrived, and skip work that would
/// only be undone. Not on the thread pool: requests block (opening a device), and a playing video already keeps
/// pool threads busy with its decode loops, so a request could wait there for a free thread.
/// </summary>
internal sealed class LatestRequestWorker<TRequest>
{
    private readonly Action<TRequest, Func<bool>> _handle;
    private readonly string _operation;
    private readonly object _gate = new();
    private TRequest? _pending;
    private bool _hasPending;
    private int _submitted;
    private bool _draining;
    private Task _drain = Task.CompletedTask;

    /// <param name="handle">Handles one request; the function it is given says whether a newer request is waiting.</param>
    /// <param name="operation">The log category for a request that throws.</param>
    public LatestRequestWorker(Action<TRequest, Func<bool>> handle, string operation)
    {
        _handle = handle;
        _operation = operation;
    }

    public void Submit(TRequest request)
    {
        lock (_gate)
        {
            _pending = request;
            _hasPending = true;
            _submitted++;
            if (_draining)
            {
                return; // the running drain takes it next
            }

            _draining = true;
            _drain = Task.Factory.StartNew(Drain, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    /// <summary>Waits until every request submitted so far has run. False if <paramref name="timeout"/> passed first.</summary>
    public bool WaitUntilIdle(TimeSpan timeout)
    {
        Task drain;
        lock (_gate)
        {
            drain = _drain;
        }

        return drain.Wait(timeout);
    }

    private void Drain()
    {
        while (true)
        {
            TRequest request;
            int serial;
            lock (_gate)
            {
                if (!_hasPending)
                {
                    _draining = false;
                    return;
                }

                request = _pending!;
                _pending = default;
                _hasPending = false;
                serial = _submitted;
            }

            try
            {
                _handle(request, () => Volatile.Read(ref _submitted) != serial);
            }
            catch (Exception ex)
            {
                AppLog.Warning(_operation, ex);
            }
        }
    }
}
