using System;
using System.Threading;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Keeps a shared native resource alive for the duration of every use, so it cannot be torn down mid-use.
///
/// This exists because of a real crash: the OCR runtime holds one pair of ONNX sessions shared by every consumer,
/// and a language switch disposed them while another thread was inside <c>InferenceSession.Run</c> on the very same
/// objects. Freeing a native session under a running inference faults the process with 0xC0000005 — an access
/// violation is NOT a catchable .NET exception, so the app simply vanished with no dialog and nothing in the log.
///
/// Many uses may overlap; exclusive access (the teardown/rebuild) waits for them all to finish and blocks new ones
/// meanwhile. Exclusive access takes priority over new uses, otherwise a steady stream of captures could starve a
/// pending swap forever.
///
/// Do NOT nest <see cref="BeginUse"/> on one thread: a waiting exclusive request blocks the inner call while the
/// outer one keeps the gate open, and both deadlock.
/// </summary>
internal sealed class ResourceUseGate
{
    private readonly object _gate = new();
    private int _activeUses;
    private bool _exclusiveHeld;

    /// <summary>Uses currently in flight. Diagnostics and tests only.</summary>
    internal int ActiveUses
    {
        get
        {
            lock (_gate)
            {
                return _activeUses;
            }
        }
    }

    /// <summary>
    /// Marks the resource in use until the returned scope is disposed. Blocks while it is being swapped out.
    /// Always dispose in a <c>finally</c> (or a <c>using</c>) — a leaked scope stalls every later swap until it
    /// times out.
    /// </summary>
    public IDisposable BeginUse()
    {
        lock (_gate)
        {
            while (_exclusiveHeld)
            {
                Monitor.Wait(_gate);
            }

            _activeUses++;
        }

        return new Scope(this, exclusive: false);
    }

    /// <summary>
    /// Takes exclusive access for tearing the resource down and rebuilding it: blocks new uses immediately, then
    /// waits for the in-flight ones to finish.
    ///
    /// Returns false if <paramref name="timeout"/> elapses first, leaving the gate exactly as it was — a wedged
    /// inference must degrade to "skip this swap", never to "block every future use forever". Callers must not
    /// dispose the resource when this returns false.
    /// </summary>
    public bool TryBeginExclusive(TimeSpan timeout, out IDisposable? scope)
    {
        scope = null;
        long deadline = Environment.TickCount64 + (long)Math.Max(0d, timeout.TotalMilliseconds);

        lock (_gate)
        {
            while (_exclusiveHeld)
            {
                if (!WaitUntil(deadline))
                {
                    return false;
                }
            }

            // Claimed before draining, so uses arriving from here on queue behind this swap instead of extending it.
            _exclusiveHeld = true;

            while (_activeUses > 0)
            {
                if (!WaitUntil(deadline))
                {
                    _exclusiveHeld = false;
                    Monitor.PulseAll(_gate);
                    return false;
                }
            }
        }

        scope = new Scope(this, exclusive: true);
        return true;
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private bool WaitUntil(long deadlineTicks)
    {
        long remaining = deadlineTicks - Environment.TickCount64;
        return remaining > 0 && Monitor.Wait(_gate, (int)Math.Min(remaining, int.MaxValue));
    }

    private void EndUse()
    {
        lock (_gate)
        {
            _activeUses--;
            if (_activeUses == 0)
            {
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void EndExclusive()
    {
        lock (_gate)
        {
            _exclusiveHeld = false;
            Monitor.PulseAll(_gate);
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly ResourceUseGate _owner;
        private readonly bool _exclusive;
        private int _disposed;

        internal Scope(ResourceUseGate owner, bool exclusive)
        {
            _owner = owner;
            _exclusive = exclusive;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_exclusive)
            {
                _owner.EndExclusive();
            }
            else
            {
                _owner.EndUse();
            }
        }
    }
}
