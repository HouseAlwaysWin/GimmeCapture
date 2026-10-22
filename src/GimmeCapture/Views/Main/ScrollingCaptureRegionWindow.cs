using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GimmeCapture.Services.Interop;
using GimmeCapture.Services.Platforms.Linux;

namespace GimmeCapture.Views.Main;

// A thin outline showing the capture region during manual scrolling capture. It must be fully
// click-through / non-activating (so it never blocks the user scrolling the target window underneath)
// and must not appear in the stitched image. On Windows WDA_EXCLUDEFROMCAPTURE hides it from capture;
// X11 has no such affinity, so the caller (SnipWindow.ViewModelWiring) instead insets the outline just
// OUTSIDE the captured rect, and here we only make the window click-through via an empty X input shape.
public sealed class ScrollingCaptureRegionWindow : Window
{
    public ScrollingCaptureRegionWindow(double borderThickness, Color borderColor)
    {
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(borderColor),
            BorderThickness = new Thickness(Math.Max(2, borderThickness)),
            Background = Brushes.Transparent
        };

        Opened += (_, _) =>
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: false);
                MakeClickThrough(hwnd);
            }
            else if (OperatingSystem.IsLinux())
            {
                // Empty input shape = no part of the window catches the pointer, so every scroll/click
                // passes through to the target beneath. (Capture exclusion is handled by the caller
                // insetting this outline outside the captured rect — X11 has no WDA_EXCLUDEFROMCAPTURE.)
                LinuxWindowShape.SetInputRegion(hwnd, Array.Empty<PixelRect>());
            }
        };
    }

    private static void MakeClickThrough(IntPtr hwnd)
    {
        try
        {
            User32.SetExStyleFlags(hwnd, User32.WS_EX_TRANSPARENT | User32.WS_EX_LAYERED | User32.WS_EX_NOACTIVATE, set: true);
        }
        catch (Exception ex)
        {
            AppLogSafe(ex);
        }
    }

    private static void AppLogSafe(Exception ex)
    {
        GimmeCapture.Services.Core.Infrastructure.AppLog.Warning("ScrollingRegion.ClickThrough", ex);
    }
}
