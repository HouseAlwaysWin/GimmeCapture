using System;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Shows a fatal/error message using the best facility available for the platform. These are used
/// in pre-UI / shutdown paths (startup crash, update verification/apply failure) where an Avalonia
/// dialog may not be available, so Windows uses a native WinForms MessageBox. Off-Windows there is
/// no toolkit-free modal here yet (docs/LINUX_PORT_FEASIBILITY.md), so it falls back to stderr + log.
///
/// The <c>WINDOWS</c> symbol is auto-defined by the SDK for the <c>net10.0-windows</c> TFM and is
/// absent for a plain <c>net10.0</c> (Linux) TFM, so this file compiles on both without change and
/// keeps the only <c>System.Windows.Forms</c> reference in the shell paths isolated here.
/// </summary>
public static class PlatformErrorDialog
{
    public static void ShowError(string message, string title)
    {
#if WINDOWS
        System.Windows.Forms.MessageBox.Show(message, title);
#else
        Console.Error.WriteLine($"[{title}] {message}");
        AppLog.Information($"PlatformErrorDialog.{title}");
#endif
    }
}
