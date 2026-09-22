using System.Diagnostics;
using System.Threading;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Stage timings for "the user asked for a capture" → "the overlay is on screen".
///
/// Everything the log recorded about a capture so far (snip-opened, the OCR scan…) starts AFTER Show(), so the
/// one complaint we could not investigate — "I pressed the hotkey and nothing appeared for a few seconds" — left
/// no trace at all. The entry point stamps the request here and the overlay factory reports what each stage
/// cost, including how long the request sat waiting for the UI thread before the factory even ran: that wait is
/// the tell for a blocked UI thread, which <see cref="UiThreadStallMonitor"/> then names.
/// </summary>
internal static class CaptureOpenTrace
{
    private static long _requestedAtTimestamp;

    /// <summary>
    /// Stamped where a capture is requested (the hotkey handler), BEFORE the dispatcher hop — so the wait that
    /// follows is measured rather than assumed.
    /// </summary>
    internal static void Requested()
    {
        Volatile.Write(ref _requestedAtTimestamp, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// How long the pending request waited, or null when this capture did not come from a stamped entry point
    /// (tray menu, toolbar button). Consumed on read, so a later unstamped open reports nothing instead of an
    /// ancient stamp.
    /// </summary>
    internal static double? TakeWaitedForUiThreadMs()
    {
        long stamped = Interlocked.Exchange(ref _requestedAtTimestamp, 0);
        return stamped == 0 ? null : Stopwatch.GetElapsedTime(stamped).TotalMilliseconds;
    }
}
