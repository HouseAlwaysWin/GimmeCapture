using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Views.Main;

// A thin outline showing the capture region during manual scrolling capture. It is excluded
// from screen capture (so it never appears in the stitched image) and fully click-through /
// non-activating (so it never blocks the user scrolling the target window underneath).
public sealed class ScrollingCaptureRegionWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20;
    private const long WS_EX_LAYERED = 0x80000;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    public ScrollingCaptureRegionWindow(double borderThickness, Color borderColor)
    {
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        SystemDecorations = SystemDecorations.None;
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

            Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: false);
            MakeClickThrough(hwnd);
        };
    }

    private static void MakeClickThrough(IntPtr hwnd)
    {
        try
        {
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE));
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
