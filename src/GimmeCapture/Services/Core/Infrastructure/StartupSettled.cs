using System.Threading;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// The start has settled once its timings line is complete (the hotkeys work, and for a window launch its first frame
/// is up) and the UI thread's own start-up work after that is done (the tray icon, and the jobs the start queued). Then
/// the app goes back to normal priority (<see cref="StartupPriority"/>) and reads the fonts' file ahead
/// (<see cref="StartupPrefetch.ReadAfterStart"/>). Both used to happen at the hotkeys, before the tray icon: at the
/// 2026-11-07 20:13 sign-in the UI thread then took 2.7 s for that last work at 10% of a processor, with the machine
/// 97% busy and the fonts' 28 MB read off the same hard disk alongside it (2.8 s).
/// </summary>
internal static class StartupSettled
{
    private static readonly SettleLatch Latch = new(2, () =>
    {
        StartupPriority.Restore();
        StartupPrefetch.ReadAfterStart();
    });

    /// <summary>The start's timings line is complete.</summary>
    internal static void TimingsComplete() => Latch.Release();

    /// <summary>The UI thread's start-up work is done: posted behind it at Background priority.</summary>
    internal static void UiWorkDone() => Latch.Release();
}

/// <summary>
/// Runs its action once, when the last of a number of conditions is released, on whichever thread releases it.
/// </summary>
internal sealed class SettleLatch(int conditions, System.Action settled)
{
    private int _pending = conditions;

    internal void Release()
    {
        if (Interlocked.Decrement(ref _pending) == 0)
        {
            settled();
        }
    }
}
