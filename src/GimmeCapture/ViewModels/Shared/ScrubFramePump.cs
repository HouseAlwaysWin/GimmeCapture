using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.ViewModels.Shared;

/// <summary>
/// The picture under a drag along a video's timeline: one frame decoded at a time, always for the newest position asked
/// for. A frame can take longer to decode than the pointer takes to move: on a 4K phone video a far seek took 115 ms
/// for the exact frame and 37 for its keyframe. Cancelling the decode under way at each new position would show nothing
/// until the drag stopped, so the one under way finishes and is shown, and only the newest of those asked for meanwhile
/// is decoded next.
/// </summary>
internal sealed class ScrubFramePump
{
    private readonly Func<double, CancellationToken, Task<byte[]?>> _decode;
    private readonly Action<byte[]> _show;
    private readonly object _gate = new();
    private CancellationTokenSource _stop = new();
    private double? _pending;
    private bool _running;

    /// <param name="decode">The frame at a position; null for none, or once its token is cancelled.</param>
    /// <param name="show">Draws a frame. Runs where <see cref="Request"/> was called from, its synchronization context's.</param>
    internal ScrubFramePump(Func<double, CancellationToken, Task<byte[]?>> decode, Action<byte[]> show)
    {
        _decode = decode;
        _show = show;
    }

    /// <summary>Asks for the frame at <paramref name="seconds"/>, in place of any asked for and not yet started.</summary>
    internal void Request(double seconds)
    {
        lock (_gate)
        {
            _pending = seconds;
            if (_running)
            {
                return;
            }

            _running = true;
        }

        _ = RunAsync();
    }

    /// <summary>
    /// Forgets what was asked for and cancels the decode under way, whose frame is then not shown: the drag is over, and
    /// whatever comes next on screen is the caller's.
    /// </summary>
    internal void Stop()
    {
        CancellationTokenSource stopped;
        lock (_gate)
        {
            _pending = null;
            stopped = _stop;
            _stop = new CancellationTokenSource();
        }

        stopped.Cancel();
        stopped.Dispose();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            double seconds;
            CancellationToken token;
            lock (_gate)
            {
                if (_pending is not { } next)
                {
                    _running = false;
                    return;
                }

                seconds = next;
                _pending = null;
                token = _stop.Token;
            }

            byte[]? frame = null;
            try
            {
                frame = await _decode(seconds, token);
            }
            catch (OperationCanceledException)
            {
                // Stopped under way.
            }
            catch (Exception ex)
            {
                // One position that cannot be decoded costs one frame of the drag.
                AppLog.Warning("ScrubFramePump.Decode", ex);
            }

            if (frame != null && !token.IsCancellationRequested)
            {
                _show(frame);
            }
        }
    }
}
