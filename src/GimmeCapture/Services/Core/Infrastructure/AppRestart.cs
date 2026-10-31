using System;
using System.Diagnostics;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>Starts a fresh copy of this exe, for a setting that only takes effect as the app starts.</summary>
internal static class AppRestart
{
    /// <summary>
    /// True once the copy has started. It runs with this process's rights, so an elevated app comes back elevated, and with
    /// no arguments, so it opens the main window the setting was changed in. Through the shell, so it inherits no handle.
    /// </summary>
    internal static bool TryStartCopy()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return false;
        }

        try
        {
            using var started = Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("Restart.StartCopy", ex);
            return false;
        }
    }
}
