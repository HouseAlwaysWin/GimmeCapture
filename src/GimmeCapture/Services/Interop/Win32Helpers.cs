using System;
using System.Runtime.InteropServices;
using Avalonia;

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

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

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
                    DeleteObject(holeRegion);
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
                        DeleteObject(tempRegion);
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
                    DeleteObject(tempRegion);
                    tempRegion = IntPtr.Zero;
                }
            }

            // 5. Apply region to window
            int setResult = SetWindowRgn(hwnd, fullRegion, true);
            fullRegion = IntPtr.Zero; 
            
            return setResult != 0;
        }
        finally
        {
            if (tempRegion != IntPtr.Zero) DeleteObject(tempRegion);
            if (fullRegion != IntPtr.Zero) DeleteObject(fullRegion);
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
                DeleteObject(tempRegion);
                tempRegion = IntPtr.Zero;
            }

            // Punch hole in the middle
            if (innerHole.Width > 0 && innerHole.Height > 0)
            {
                tempRegion = CreateRectRgn((int)innerHole.X, (int)innerHole.Y, (int)innerHole.Right, (int)innerHole.Bottom);
                if (tempRegion != IntPtr.Zero)
                {
                    CombineRgn(fullRegion, fullRegion, tempRegion, RGN_DIFF);
                    DeleteObject(tempRegion);
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
                    DeleteObject(tempRegion);
                    tempRegion = IntPtr.Zero;
                }
            }

            int setResult = SetWindowRgn(hwnd, fullRegion, true);
            fullRegion = IntPtr.Zero;
            return setResult != 0;
        }
        finally
        {
            if (tempRegion != IntPtr.Zero) DeleteObject(tempRegion);
            if (fullRegion != IntPtr.Zero) DeleteObject(fullRegion);
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
        SetWindowRgn(hwnd, IntPtr.Zero, true);
    }
}
