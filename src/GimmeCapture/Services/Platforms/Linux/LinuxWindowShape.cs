using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Platforms.Linux;

/// <summary>
/// X11 Shape-extension helper — makes parts of a window click-through by setting its INPUT shape.
/// This is the Linux equivalent of the Win32 snip-overlay pass-through (SetWindowRgn / WM_NCHITTEST =
/// HTTRANSPARENT): pointer events land only on the given rectangles (the selection frame, handles and
/// toolbar); everywhere else — including the selection interior — passes clicks through to the windows
/// beneath, while the overlay's visuals stay fully drawn (only input is reshaped, not the bounding shape).
/// </summary>
internal static class LinuxWindowShape
{
    private const int ShapeInput = 2;
    private const int ShapeSet = 0;
    private const int Unsorted = 0;

    // The shape is a server-side window property, so a private display connection can set it on a window
    // Avalonia created. Only ever touched from the UI thread (UpdateWindowRegion), so no locking needed.
    private static IntPtr _display;

    private static IntPtr Display()
    {
        if (_display == IntPtr.Zero)
        {
            _display = XOpenDisplay(null);
        }

        return _display;
    }

    /// <summary>Restrict pointer input to <paramref name="physicalRects"/> (window-relative pixels);
    /// everything else becomes click-through.</summary>
    public static void SetInputRegion(IntPtr window, IReadOnlyList<PixelRect> physicalRects)
    {
        try
        {
            var dpy = Display();
            if (dpy == IntPtr.Zero || window == IntPtr.Zero)
            {
                return;
            }

            int n = physicalRects.Count;
            var xr = new XRectangle[n == 0 ? 1 : n];
            for (int i = 0; i < n; i++)
            {
                var r = physicalRects[i];
                xr[i] = new XRectangle
                {
                    x = (short)r.X,
                    y = (short)r.Y,
                    width = (ushort)Math.Max(0, r.Width),
                    height = (ushort)Math.Max(0, r.Height),
                };
            }

            XShapeCombineRectangles(dpy, window, ShapeInput, 0, 0, xr, n, ShapeSet, Unsorted);
            XFlush(dpy);
        }
        catch (Exception ex)
        {
            AppLog.Warning("LinuxWindowShape.SetInputRegion", ex);
        }
    }

    /// <summary>Reset the input shape so the whole window receives pointer events again.</summary>
    public static void ClearInputRegion(IntPtr window)
    {
        try
        {
            var dpy = Display();
            if (dpy == IntPtr.Zero || window == IntPtr.Zero)
            {
                return;
            }

            // Mask = None resets ShapeInput to the default (the full bounding rectangle).
            XShapeCombineMask(dpy, window, ShapeInput, 0, 0, IntPtr.Zero, ShapeSet);
            XFlush(dpy);
        }
        catch (Exception ex)
        {
            AppLog.Warning("LinuxWindowShape.ClearInputRegion", ex);
        }
    }

    /// <summary>Closes the lazily opened display connection. Call once at orderly app shutdown
    /// (UI thread, like every other use of this class); subsequent calls re-open on demand.</summary>
    public static void Shutdown()
    {
        if (_display == IntPtr.Zero)
        {
            return;
        }

        try
        {
            XCloseDisplay(_display);
        }
        catch (Exception ex)
        {
            AppLog.Warning("LinuxWindowShape.Shutdown", ex);
        }
        finally
        {
            _display = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRectangle
    {
        public short x;
        public short y;
        public ushort width;
        public ushort height;
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(string? name);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr dpy);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr dpy);

    [DllImport("libXext.so.6")]
    private static extern void XShapeCombineRectangles(IntPtr dpy, IntPtr dest, int kind, int xOff, int yOff, [In] XRectangle[] rects, int nRects, int op, int ordering);

    [DllImport("libXext.so.6")]
    private static extern void XShapeCombineMask(IntPtr dpy, IntPtr dest, int destKind, int xOff, int yOff, IntPtr src, int op);
}
