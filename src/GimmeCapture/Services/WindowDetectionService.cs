using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using System.Linq;

namespace GimmeCapture.Services;

public class WindowDetectionService
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public Rect ToAvaloniaRect() => new Rect(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    public List<Rect> GetVisibleWindowRects(IntPtr? excludeHWnd = null)
    {
        var rects = new List<Rect>();
        
        EnumWindows((hWnd, lParam) =>
        {
            if (hWnd == excludeHWnd) return true;
            
            if (IsWindowVisible(hWnd))
            {
                // Use DWM attribute to get bounds without shadows for better accuracy
                RECT dwmRect;
                int result = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out dwmRect, Marshal.SizeOf(typeof(RECT)));
                
                Rect finalRect;
                if (result == 0) // S_OK
                {
                    finalRect = dwmRect.ToAvaloniaRect();
                }
                else
                {
                    RECT winRect;
                    GetWindowRect(hWnd, out winRect);
                    finalRect = winRect.ToAvaloniaRect();
                }

                // Filter out empty or near-empty rects
                if (finalRect.Width > 10 && finalRect.Height > 10)
                {
                    rects.Add(finalRect);
                }
            }
            return true;
        }, IntPtr.Zero);

        return rects;
    }

    public Rect? GetRectAtPoint(Point point, List<Rect> windowRects)
    {
        // Try to find the smallest window that contains the point (often the most nested/specific)
        return windowRects
            .Where(r => r.Contains(point))
            .OrderBy(r => r.Width * r.Height)
            .Cast<Rect?>()
            .FirstOrDefault();
    }
}
