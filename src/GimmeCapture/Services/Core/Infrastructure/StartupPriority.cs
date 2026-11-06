using System;
using System.Diagnostics;
using System.Threading;
using GimmeCapture.Launcher;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// The sign-in launcher starts the app above normal priority (<see cref="ReadAhead.StartPriority"/>), so that its start
/// is not left waiting for a processor behind the sign-in's other programs; this puts it back to normal once the start
/// is done, when the hotkeys work, or a minute after Main should the start never finish.
/// </summary>
internal static class StartupPriority
{
    private static readonly TimeSpan Fallback = TimeSpan.FromMinutes(1);

    private static int _raised;
    private static int _restored;
    private static Timer? _fallback;

    /// <summary>From the top of Main, for a start through the launcher: nothing here may log.</summary>
    internal static void Raised()
    {
        Volatile.Write(ref _raised, 1);
        _fallback = new Timer(_ => Restore(), null, Fallback, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Once the start is done: back to normal priority, once, if the launcher raised it.</summary>
    internal static void Restore()
    {
        if (Volatile.Read(ref _raised) == 0 || Interlocked.Exchange(ref _restored, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _fallback, null)?.Dispose();
        try
        {
            using var self = Process.GetCurrentProcess();
            Lower(self);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            AppLog.Debug("StartupPriority.Restore", ex);
        }
    }

    /// <summary>
    /// Puts <paramref name="process"/> back to normal priority if it still has the launcher's; one the user set otherwise
    /// in the meantime keeps theirs.
    /// </summary>
    internal static void Lower(Process process)
    {
        if (process.PriorityClass == ReadAhead.StartPriority)
        {
            process.PriorityClass = ProcessPriorityClass.Normal;
        }
    }
}
