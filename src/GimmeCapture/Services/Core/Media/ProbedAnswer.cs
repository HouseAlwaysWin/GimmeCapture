using System;
using System.Threading;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// The answer of a probe that is slow and needs to run once a process. The first caller probes; a caller that comes
/// meanwhile waits for that answer instead of probing alongside it, as the Record tab, the Record-mode warm-up and a
/// recording's start could; and a probe that could not answer (null) is not kept, so a later caller probes again.
/// </summary>
internal sealed class ProbedAnswer
{
    private readonly object _gate = new();

    // 0 not known yet, 1 yes, 2 no.
    private int _state;

    /// <summary>The answer reached or recalled so far, or null when there is none yet. Never probes.</summary>
    public bool? Known => Volatile.Read(ref _state) switch
    {
        1 => true,
        2 => false,
        _ => null,
    };

    /// <summary>
    /// The answer: the one known, or what <paramref name="probe"/> finds, run by one caller at a time.
    /// <paramref name="onFound"/> hears an answer the probe reached; false when it reached none.
    /// </summary>
    public bool Get(Func<bool?> probe, Action<bool>? onFound = null)
    {
        if (Known is bool known)
        {
            return known;
        }

        lock (_gate)
        {
            if (Known is bool foundMeanwhile)
            {
                return foundMeanwhile;
            }

            if (probe() is not bool found)
            {
                return false;
            }

            Volatile.Write(ref _state, found ? 1 : 2);
            onFound?.Invoke(found);
            return found;
        }
    }

    /// <summary>Takes an answer an earlier run reached, unless this one has one already. True when it was taken.</summary>
    public bool Recall(bool answer) => Interlocked.CompareExchange(ref _state, answer ? 1 : 2, 0) == 0;
}
