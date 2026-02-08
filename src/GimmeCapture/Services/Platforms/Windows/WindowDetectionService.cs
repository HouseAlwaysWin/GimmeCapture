using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using System.Linq;

namespace GimmeCapture.Services.Platforms.Windows;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    private const uint GW_OWNER = 4;
    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;

    public List<Rect> GetVisibleWindowRects(IntPtr? excludeHWnd = null)
    {
        var rects = new List<Rect>();
        var coveredRegions = new List<Rect>(); // Track segments that are already "covered" by top windows
        
        IntPtr shellWindow = GetShellWindow();
        IntPtr desktopWindow = GetDesktopWindow();
        
        EnumWindows((hWnd, lParam) =>
        {
            if (hWnd == excludeHWnd || hWnd == shellWindow || hWnd == desktopWindow) return true;
            
            // 1. Basic Visibility Check
            if (!IsWindowVisible(hWnd)) return true;

            // 2. Filter by Owner (Ignore sub-windows/dialogs/internal layers)
            IntPtr owner = GetWindow(hWnd, GW_OWNER);
            if (owner != IntPtr.Zero) return true;

            // 3. Filter by Extended Style
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

            // 4. Filter by DWM Cloaking
            int cloaked = 0;
            DwmGetWindowAttributeInt(hWnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
            if (cloaked != 0) return true;

            // 5. Get Name and Class
            var sbTitle = new System.Text.StringBuilder(256);
            GetWindowText(hWnd, sbTitle, 256);
            string title = sbTitle.ToString();

            var sbClass = new System.Text.StringBuilder(256);
            GetClassName(hWnd, sbClass, 256);
            string className = sbClass.ToString();

            // Aggressive title check
            if (string.IsNullOrWhiteSpace(title) && className != "ApplicationFrameWindow") return true;

            // 6. Style Check
            int style = GetWindowLong(hWnd, GWL_STYLE);
            bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
            bool isAppWindow = (exStyle & WS_EX_APPWINDOW) != 0;
            if (!hasCaption && !isAppWindow) return true;

            // 7. Get Rect
            RECT dwmRect;
            int result = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out dwmRect, Marshal.SizeOf(typeof(RECT)));
            Rect finalRect = (result == 0) ? dwmRect.ToAvaloniaRect() : winRectNoDwm(hWnd);

            if (finalRect.Width <= 20 || finalRect.Height <= 20) return true;

            // 8. OCCLUSION CHECK (Z-Order Awareness)
            // Since EnumWindows is Top-to-Bottom, we check if this window is completely covered 
            // by windows we've already processed.
            if (IsRectCovered(finalRect, coveredRegions)) return true;

            // Add to results and mark this area as covered for windows BELOW this one
            rects.Add(finalRect);
            coveredRegions.Add(finalRect);
            
            return true;
        }, IntPtr.Zero);

        return rects;
    }

    private Rect winRectNoDwm(IntPtr hWnd)
    {
        RECT winRect;
        GetWindowRect(hWnd, out winRect);
        return winRect.ToAvaloniaRect();
    }

    private bool IsRectCovered(Rect target, List<Rect> occluders)
    {
        // Simple heuristic: If the center point and 4 corners are covered by ANY top window, 
        // it's likely heavily occluded.
        // For better accuracy, we check if it's contained within any existing single top window.
        foreach (var occluder in occluders)
        {
            if (occluder.Contains(target)) return true; // Fully inside a top window
            
            // If it's 95% covered by a single top window, skip it
            var intersect = target.Intersect(occluder);
            if (intersect.Width * intersect.Height > (target.Width * target.Height * 0.95))
                return true;
        }
        return false;
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
