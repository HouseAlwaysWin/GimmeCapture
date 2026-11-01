using System.Diagnostics;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// The probes <see cref="UiThreadStallMonitor"/> posts to the UI thread, one at a time: how long one waits to run is how
/// long the UI thread did not pump. Kept apart from the threads and the dispatcher so a test can drive it. The watchdog
/// thread posts and checks and the UI thread completes, so every call takes the lock.
/// </summary>
internal sealed class UiStallProbe
{
    private readonly object _gate = new();
    private long _lastSequence;
    private long _generation;

    // The probe in flight, 0 for none, and where it was posted.
    private long _pendingSequence;
    private UiThreadSample _posted;

    // The probe whose wait has been written down while it lasted.
    private long _reportedSequence;

    /// <summary>The generation a watchdog starting now posts in, until <see cref="Reset"/>.</summary>
    internal long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    /// <summary>
    /// A number for a new probe, posted at <paramref name="now"/>; null while the last one has not run, as the UI thread
    /// has not pumped since and another would only queue behind it, and null once <paramref name="generation"/> is over.
    /// </summary>
    internal long? TryPost(UiThreadSample now, long generation)
    {
        lock (_gate)
        {
            if (generation != _generation || _pendingSequence != 0)
            {
                return null;
            }

            _pendingSequence = ++_lastSequence;
            _posted = now;
            return _pendingSequence;
        }
    }

    /// <summary>
    /// The probe has run: where it was posted, or null for one posted before <see cref="Reset"/>, which no longer counts.
    /// </summary>
    internal UiThreadSample? Complete(long sequence)
    {
        lock (_gate)
        {
            if (_pendingSequence == 0 || sequence != _pendingSequence)
            {
                return null;
            }

            _pendingSequence = 0;
            return _posted;
        }
    }

    /// <summary>
    /// Where the probe still waiting was posted, the first time it has waited
    /// <see cref="UiStallReport.OngoingReportThreshold"/>: once per stall, though the watchdog asks at every beat.
    /// </summary>
    internal UiThreadSample? TakeOngoing(long nowTimestamp)
    {
        lock (_gate)
        {
            if (_pendingSequence == 0
                || _pendingSequence == _reportedSequence
                || Stopwatch.GetElapsedTime(_posted.Timestamp, nowTimestamp) < UiStallReport.OngoingReportThreshold)
            {
                return null;
            }

            _reportedSequence = _pendingSequence;
            return _posted;
        }
    }

    /// <summary>
    /// Forgets the probe in flight and ends the generation, as the monitor stops: neither a probe still queued nor one
    /// that a watchdog caught mid-beat goes on to post is read as a stall, when the UI thread then waits on a UAC prompt.
    /// </summary>
    internal void Reset()
    {
        lock (_gate)
        {
            _pendingSequence = 0;
            _generation++;
        }
    }
}
