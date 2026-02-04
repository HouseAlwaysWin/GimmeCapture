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
        if (hwnd == IntPtr.Zero) return false;
        if (selectionRect.Width <= borderWidth * 2 || selectionRect.Height <= borderWidth * 2) return false;

        IntPtr fullRegion = IntPtr.Zero;
        IntPtr holeRegion = IntPtr.Zero;
        IntPtr tempRegion = IntPtr.Zero;

        try
        {
            // 1. Create region covering entire window
            fullRegion = CreateRectRgn(0, 0, windowWidth, windowHeight);
            if (fullRegion == IntPtr.Zero) return false;

            // 2. Create hole region (shrunk by border width to keep border interactive)
            int holeLeft = (int)(selectionRect.X + borderWidth);
            int holeTop = (int)(selectionRect.Y + borderWidth);
            int holeRight = (int)(selectionRect.Right - borderWidth);
            int holeBottom = (int)(selectionRect.Bottom - borderWidth);

            // Ensure valid hole dimensions
            if (holeRight <= holeLeft || holeBottom <= holeTop) return false;

            holeRegion = CreateRectRgn(holeLeft, holeTop, holeRight, holeBottom);
            if (holeRegion == IntPtr.Zero) return false;

            // 3. Subtract hole from full region (RGN_DIFF)
            int result = CombineRgn(fullRegion, fullRegion, holeRegion, RGN_DIFF);
            if (result == 0) return false;

            // 4. Add extra opaque rects back (islands like wings)
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

            // 5. If toolbar rect is provided, add it back to the region
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

            // 6. Apply region to window
            int setResult = SetWindowRgn(hwnd, fullRegion, true);
            fullRegion = IntPtr.Zero; // Owned by window now
            
            return setResult != 0;
        }
        finally
        {
            if (holeRegion != IntPtr.Zero) DeleteObject(holeRegion);
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
