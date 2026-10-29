using System;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Interop;

/// <summary>
/// Win32 API helpers for window region manipulation.
/// Used to create transparent "hole" windows that allow mouse pass-through.
/// </summary>
public static class Win32Helpers
{
    #region Win32 API Declarations

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    // Copies the window's region into hRgn; ERROR (0) when the window has none.
    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualRgn(IntPtr hrgn1, IntPtr hrgn2);

    private const int RegionError = 0;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_MONITOR = 0x00000001;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    // Combine region modes
    private const int RGN_AND = 1;
    private const int RGN_OR = 2;
    private const int RGN_XOR = 3;
    private const int RGN_DIFF = 4;  // Subtract second region from first
    private const int RGN_COPY = 5;

    #endregion

    /// <summary>
    /// Creates a window region with a "hole" that allows mouse events to pass through.
    /// The hole is created inside the selection area, keeping the border interactive.
    /// </summary>
    /// <param name="hwnd">Window handle</param>
    /// <param name="windowWidth">Window width in pixels</param>
    /// <param name="windowHeight">Window height in pixels</param>
    /// <param name="selectionRect">Selection rectangle in window coordinates</param>
    /// <param name="borderWidth">Width of the interactive border to keep around the hole</param>
    /// <param name="toolbarRect">Optional toolbar rectangle to keep interactive</param>
    /// <param name="extraOpaqueRects">Optional extra rectangles to keep opaque (e.g. wings)</param>
    /// <returns>True if region was applied successfully</returns>
    public static bool SetWindowHoleRegion(IntPtr hwnd, int windowWidth, int windowHeight, Rect selectionRect, int borderWidth = 4, Rect? toolbarRect = null, System.Collections.Generic.IEnumerable<Rect>? extraOpaqueRects = null)
    {
        return SetMultiWindowHoleRegion(hwnd, windowWidth, windowHeight, new[] { selectionRect }, borderWidth, toolbarRect, extraOpaqueRects);
    }

    /// <summary>
    /// Creates a window region with multiple "holes" for mouse pass-through.
    /// </summary>
    public static bool SetMultiWindowHoleRegion(IntPtr hwnd, int windowWidth, int windowHeight, System.Collections.Generic.IEnumerable<Rect> selectionRects, int borderWidth = 4, Rect? toolbarRect = null, System.Collections.Generic.IEnumerable<Rect>? extraOpaqueRects = null)
    {
        if (hwnd == IntPtr.Zero) return false;

        IntPtr fullRegion = IntPtr.Zero;
        IntPtr tempRegion = IntPtr.Zero;

        try
        {
            // 1. Create region covering entire window
            fullRegion = CreateRectRgn(0, 0, windowWidth, windowHeight);
            if (fullRegion == IntPtr.Zero) return false;

            // 2. Subtract each selection hole (shrunk by border width)
            foreach (var rect in selectionRects)
            {
                if (rect.Width <= borderWidth * 2 || rect.Height <= borderWidth * 2) continue;

                int holeLeft = (int)(rect.X + borderWidth);
                int holeTop = (int)(rect.Y + borderWidth);
                int holeRight = (int)(rect.Right - borderWidth);
                int holeBottom = (int)(rect.Bottom - borderWidth);

                if (holeRight <= holeLeft || holeBottom <= holeTop) continue;

                IntPtr holeRegion = CreateRectRgn(holeLeft, holeTop, holeRight, holeBottom);
                if (holeRegion != IntPtr.Zero)
                {
                    CombineRgn(fullRegion, fullRegion, holeRegion, RGN_DIFF);
                    Gdi32.DeleteObject(holeRegion);
                }
            }

            // 3. Add extra opaque rects back (islands like wings/handles)
            if (extraOpaqueRects != null)
            {
                foreach (var rect in extraOpaqueRects)
                {
                    if (rect.Width <= 0 || rect.Height <= 0) continue;
                    tempRegion = CreateRectRgn((int)rect.X, (int)rect.Y, (int)rect.Right, (int)rect.Bottom);
                    if (tempRegion != IntPtr.Zero)
                    {
                        CombineRgn(fullRegion, fullRegion, tempRegion, RGN_OR);
                        Gdi32.DeleteObject(tempRegion);
                        tempRegion = IntPtr.Zero;
                    }
                }
            }

            // 4. Add toolbar rect back
            if (toolbarRect.HasValue && toolbarRect.Value.Width > 0 && toolbarRect.Value.Height > 0)
            {
                var tbRect = toolbarRect.Value;
                int padding = 5;
                tempRegion = CreateRectRgn(
                    Math.Max(0, (int)tbRect.X - padding),
                    Math.Max(0, (int)tbRect.Y - padding),
                    Math.Min(windowWidth, (int)tbRect.Right + padding),
                    Math.Min(windowHeight, (int)tbRect.Bottom + padding)
                );
                if (tempRegion != IntPtr.Zero)
                {
                    CombineRgn(fullRegion, fullRegion, tempRegion, RGN_OR);
                    Gdi32.DeleteObject(tempRegion);
                    tempRegion = IntPtr.Zero;
                }
            }

            // 5. Apply region to window
            bool applied = ApplyWindowRegion(hwnd, fullRegion);
            fullRegion = IntPtr.Zero; // the window's now, or deleted
            return applied;
        }
        finally
        {
            if (tempRegion != IntPtr.Zero) Gdi32.DeleteObject(tempRegion);
            if (fullRegion != IntPtr.Zero) Gdi32.DeleteObject(fullRegion);
        }
    }

    /// <summary>
    /// Creates a window region that is fully click-through, EXCEPT for a single outer bounding box.
    /// The bounding box itself has a hole in the middle defined by the holeRect.
    /// This prevents DWM shadow glitches caused by complex disjoint extraRegions.
    /// </summary>
    public static bool SetBoundingBoxHoleRegion(IntPtr hwnd, Rect outerBoundingBox, Rect innerHole, Rect? toolbarRect = null)
    {
        if (hwnd == IntPtr.Zero) return false;

        IntPtr fullRegion = IntPtr.Zero;
        IntPtr tempRegion = IntPtr.Zero;

        try
        {
            // Start empty
            fullRegion = CreateRectRgn(0, 0, 0, 0);
            
            // Add outer bounding box
            tempRegion = CreateRectRgn((int)outerBoundingBox.X, (int)outerBoundingBox.Y, (int)outerBoundingBox.Right, (int)outerBoundingBox.Bottom);
            if (tempRegion != IntPtr.Zero)
            {
                CombineRgn(fullRegion, fullRegion, tempRegion, RGN_OR);
                Gdi32.DeleteObject(tempRegion);
                tempRegion = IntPtr.Zero;
            }

            // Punch hole in the middle
            if (innerHole.Width > 0 && innerHole.Height > 0)
            {
                tempRegion = CreateRectRgn((int)innerHole.X, (int)innerHole.Y, (int)innerHole.Right, (int)innerHole.Bottom);
                if (tempRegion != IntPtr.Zero)
                {
                    CombineRgn(fullRegion, fullRegion, tempRegion, RGN_DIFF);
                    Gdi32.DeleteObject(tempRegion);
                    tempRegion = IntPtr.Zero;
                }
            }

            // Add toolbar 
            if (toolbarRect.HasValue && toolbarRect.Value.Width > 0 && toolbarRect.Value.Height > 0)
            {
                var tbRect = toolbarRect.Value;
                int padding = 5;
                tempRegion = CreateRectRgn(
                    Math.Max(0, (int)tbRect.X - padding),
                    Math.Max(0, (int)tbRect.Y - padding),
                    (int)tbRect.Right + padding,
                    (int)tbRect.Bottom + padding
                );
                if (tempRegion != IntPtr.Zero)
                {
                    CombineRgn(fullRegion, fullRegion, tempRegion, RGN_OR);
                    Gdi32.DeleteObject(tempRegion);
                    tempRegion = IntPtr.Zero;
                }
            }

            bool applied = ApplyWindowRegion(hwnd, fullRegion);
            fullRegion = IntPtr.Zero; // the window's now, or deleted
            return applied;
        }
        finally
        {
            if (tempRegion != IntPtr.Zero) Gdi32.DeleteObject(tempRegion);
            if (fullRegion != IntPtr.Zero) Gdi32.DeleteObject(fullRegion);
        }
    }

    /// <summary>
    /// Same mouse model as <see cref="SetBoundingBoxHoleRegion"/> but ORs multiple disjoint rings
    /// (each outer rect minus an inner hole), then optional toolbar and opaque islands.
    /// Outside every outer rect and inside each inner hole: pass-through — matches screenshot/recording selection.
    /// </summary>
    public static bool SetMultipleBoundingBoxHoleRegions(
        IntPtr hwnd,
        System.Collections.Generic.IReadOnlyList<(Rect Outer, Rect InnerHole)> rings,
        Rect? toolbarRect = null,
        System.Collections.Generic.IEnumerable<Rect>? extraOpaqueRects = null)
    {
        if (hwnd == IntPtr.Zero) return false;

        IntPtr fullRegion = IntPtr.Zero;
        IntPtr tempRegion = IntPtr.Zero;

        try
        {
            fullRegion = CreateRectRgn(0, 0, 0, 0);
            if (fullRegion == IntPtr.Zero) return false;

            if (rings != null)
            {
                foreach (var (outer, inner) in rings)
                {
                    if (outer.Width <= 0 || outer.Height <= 0) continue;

                    tempRegion = CreateRectRgn((int)outer.X, (int)outer.Y, (int)outer.Right, (int)outer.Bottom);
                    if (tempRegion != IntPtr.Zero)
                    {
                        CombineRgn(fullRegion, fullRegion, tempRegion, RGN_OR);
                        Gdi32.DeleteObject(tempRegion);
                        tempRegion = IntPtr.Zero;
                    }

                    if (inner.Width > 0 && inner.Height > 0)
                    {
                        tempRegion = CreateRectRgn((int)inner.X, (int)inner.Y, (int)inner.Right, (int)inner.Bottom);
                        if (tempRegion != IntPtr.Zero)
                        {
                            CombineRgn(fullRegion, fullRegion, tempRegion, RGN_DIFF);
                            Gdi32.DeleteObject(tempRegion);
                            tempRegion = IntPtr.Zero;
                        }
                    }
                }
            }

            if (extraOpaqueRects != null)
            {
                foreach (var rect in extraOpaqueRects)
                {
                    if (rect.Width <= 0 || rect.Height <= 0) continue;
                    tempRegion = CreateRectRgn((int)rect.X, (int)rect.Y, (int)rect.Right, (int)rect.Bottom);
                    if (tempRegion != IntPtr.Zero)
                    {
                        CombineRgn(fullRegion, fullRegion, tempRegion, RGN_OR);
                        Gdi32.DeleteObject(tempRegion);
                        tempRegion = IntPtr.Zero;
                    }
                }
            }

            if (toolbarRect.HasValue && toolbarRect.Value.Width > 0 && toolbarRect.Value.Height > 0)
            {
                var tbRect = toolbarRect.Value;
                int padding = 5;
                tempRegion = CreateRectRgn(
                    Math.Max(0, (int)tbRect.X - padding),
                    Math.Max(0, (int)tbRect.Y - padding),
                    (int)tbRect.Right + padding,
                    (int)tbRect.Bottom + padding);
                if (tempRegion != IntPtr.Zero)
                {
                    CombineRgn(fullRegion, fullRegion, tempRegion, RGN_OR);
                    Gdi32.DeleteObject(tempRegion);
                    tempRegion = IntPtr.Zero;
                }
            }

            bool applied = ApplyWindowRegion(hwnd, fullRegion);
            fullRegion = IntPtr.Zero; // the window's now, or deleted
            return applied;
        }
        finally
        {
            if (tempRegion != IntPtr.Zero) Gdi32.DeleteObject(tempRegion);
            if (fullRegion != IntPtr.Zero) Gdi32.DeleteObject(fullRegion);
        }
    }

    /// <summary>
    /// Hit-test region = OR of opaque rects only (no full-window base). Matches screenshot-style pass-through:
    /// clicks outside all rects reach windows below without WM_NCHITTEST tricks.
    /// </summary>
    public static bool SetDisjointOpaqueRegions(
        IntPtr hwnd,
        System.Collections.Generic.IEnumerable<Rect> opaqueRects,
        Rect? toolbarRect = null)
    {
        if (hwnd == IntPtr.Zero) return false;

        IntPtr fullRegion = IntPtr.Zero;
        IntPtr tempRegion = IntPtr.Zero;

        try
        {
            fullRegion = CreateRectRgn(0, 0, 0, 0);
            if (fullRegion == IntPtr.Zero) return false;

            if (opaqueRects != null)
            {
                foreach (var rect in opaqueRects)
                {
                    if (rect.Width <= 0 || rect.Height <= 0) continue;
                    tempRegion = CreateRectRgn((int)rect.X, (int)rect.Y, (int)rect.Right, (int)rect.Bottom);
                    if (tempRegion != IntPtr.Zero)
                    {
                        CombineRgn(fullRegion, fullRegion, tempRegion, RGN_OR);
                        Gdi32.DeleteObject(tempRegion);
                        tempRegion = IntPtr.Zero;
                    }
                }
            }

            if (toolbarRect.HasValue && toolbarRect.Value.Width > 0 && toolbarRect.Value.Height > 0)
            {
                var tbRect = toolbarRect.Value;
                int padding = 5;
                tempRegion = CreateRectRgn(
                    Math.Max(0, (int)tbRect.X - padding),
                    Math.Max(0, (int)tbRect.Y - padding),
                    (int)tbRect.Right + padding,
                    (int)tbRect.Bottom + padding);
                if (tempRegion != IntPtr.Zero)
                {
                    CombineRgn(fullRegion, fullRegion, tempRegion, RGN_OR);
                    Gdi32.DeleteObject(tempRegion);
                    tempRegion = IntPtr.Zero;
                }
            }

            bool applied = ApplyWindowRegion(hwnd, fullRegion);
            fullRegion = IntPtr.Zero; // the window's now, or deleted
            return applied;
        }
        finally
        {
            if (tempRegion != IntPtr.Zero) Gdi32.DeleteObject(tempRegion);
            if (fullRegion != IntPtr.Zero) Gdi32.DeleteObject(fullRegion);
        }
    }

    /// <summary>
    /// Clears the window region, restoring the window to its default rectangular shape.
    /// Call this when closing the window or when no selection is active.
    /// </summary>
    /// <param name="hwnd">Window handle</param>
    public static void ClearWindowRegion(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        // A window without a region has nothing to clear, and the redraw would repaint all of it.
        IntPtr probe = CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (probe != IntPtr.Zero && GetWindowRgn(hwnd, probe) == RegionError)
            {
                return;
            }
        }
        finally
        {
            if (probe != IntPtr.Zero) Gdi32.DeleteObject(probe);
        }

        SetWindowRgn(hwnd, IntPtr.Zero, true);
    }

    /// <summary>
    /// Gives the window <paramref name="region"/>, which is then the window's, or deletes it when the window already
    /// has an equal one. The region handlers re-apply an unchanged region on most of their triggers, and every
    /// SetWindowRgn redraws the whole window: for the full-screen snip overlay, a repaint of everything it covers.
    /// </summary>
    private static bool ApplyWindowRegion(IntPtr hwnd, IntPtr region)
    {
        IntPtr current = CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (current != IntPtr.Zero
                && GetWindowRgn(hwnd, current) != RegionError
                && EqualRgn(current, region))
            {
                Gdi32.DeleteObject(region);
                return true;
            }
        }
        finally
        {
            if (current != IntPtr.Zero) Gdi32.DeleteObject(current);
        }

        if (SetWindowRgn(hwnd, region, true) != 0)
        {
            return true;
        }

        // The window takes the region only when SetWindowRgn succeeds.
        Gdi32.DeleteObject(region);
        return false;
    }

    /// <summary>
    /// Sets whether the window should be visible to screen capture software (FFmpeg, OBS, etc).
    /// </summary>
    /// <param name="hwnd">Window handle</param>
    /// <param name="visible">True to be visible, False to be excluded from capture</param>
    public static void SetWindowCaptureVisibility(IntPtr hwnd, bool visible)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return;
        
        uint affinity = visible ? WDA_NONE : WDA_EXCLUDEFROMCAPTURE;
        SetWindowDisplayAffinity(hwnd, affinity);
    }

    // ---- Pointer position: which monitor the user is actually looking at ----

    /// <summary>
    /// The mouse pointer's position in physical desktop pixels, or <c>null</c> off-Windows or when the call
    /// fails. Used to place transient notifications on the monitor the user is working on rather than on the
    /// primary one — callers must handle <c>null</c> by falling back to their own default.
    /// </summary>
    public static PixelPoint? TryGetCursorPosition()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            return User32.GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : null;
        }
        catch (Exception ex)
        {
            AppLog.Warning("Win32.GetCursorPos", ex);
            return null;
        }
    }

    // ---- Diagnostics: enumerate this process's top-level windows (to find what draws a lingering frame) ----

    /// <summary>
    /// Logs every top-level window owned by the current process (handle, visibility, class, title, rect, extended
    /// style, region type) via <see cref="AppLog"/>. Diagnostic aid for locating a window that draws a lingering
    /// on-screen frame after recording. Safe/no-op off Windows.
    /// </summary>
    public static void LogTopLevelWindowsOfCurrentProcess(string context)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            uint self = (uint)System.Environment.ProcessId;
            int count = 0;
            User32.EnumWindows((h, _) =>
            {
                User32.GetWindowThreadProcessId(h, out uint wp);
                if (wp != self)
                {
                    return true;
                }

                var cn = new StringBuilder(256);
                User32.GetClassName(h, cn, cn.Capacity);
                var tt = new StringBuilder(256);
                User32.GetWindowText(h, tt, tt.Capacity);
                User32.GetWindowRect(h, out User32.RECT r);
                int ex = User32.GetWindowLong(h, User32.GWL_EXSTYLE);
                int rgn = GetWindowRgn(h, IntPtr.Zero); // 1=NULLREGION,2=SIMPLE,3=COMPLEX,0=ERROR
                bool vis = User32.IsWindowVisible(h);
                AppLog.Information(
                    $"WinDiag[{context}] hwnd=0x{h.ToInt64():X} vis={vis} class='{cn}' title='{tt}' " +
                    $"rect=({r.Left},{r.Top})-({r.Right},{r.Bottom}) exStyle=0x{ex:X} rgnType={rgn}");
                count++;
                return true;
            }, IntPtr.Zero);
            AppLog.Information($"WinDiag[{context}] total top-level windows for this process: {count}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Win32Helpers.LogTopLevelWindowsOfCurrentProcess", ex);
        }
    }
}
