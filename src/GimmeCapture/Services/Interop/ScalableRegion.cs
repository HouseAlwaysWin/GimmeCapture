using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;

namespace GimmeCapture.Services.Interop;

/// <summary>
/// A region made of many rectangles, built once and then placed at whatever size and position it is drawn at: the
/// opaque parts of a pinned image, which go into its window region. That region is rebuilt every frame of a resize, and
/// ORing a cut-out pin's 700 rectangles in one <c>CombineRgn</c> at a time took 6-7 ms of each frame. A placed copy of
/// the finished region is one <c>ExtCreateRegion</c> call.
/// </summary>
internal sealed class ScalableRegion
{
    private const int RgnOr = 2;

    // The region's RGNDATA as GetRegionData gives it: GDI's own sorted bands, which it can scale and move as they are.
    private readonly byte[] _data;

    private ScalableRegion(byte[] data) => _data = data;

    /// <summary>The union of <paramref name="rects"/>, or null when there are none or GDI fails.</summary>
    public static ScalableRegion? FromRects(IReadOnlyList<PixelRect> rects)
    {
        if (rects.Count == 0)
        {
            return null;
        }

        IntPtr region = Union(rects, 0, rects.Count);
        if (region == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            int size = GetRegionData(region, 0, null);
            if (size <= 0)
            {
                return null;
            }

            var data = new byte[size];
            return GetRegionData(region, size, data) == size ? new ScalableRegion(data) : null;
        }
        finally
        {
            Gdi32.DeleteObject(region);
        }
    }

    /// <summary>
    /// A copy scaled by <paramref name="scaleX"/> and <paramref name="scaleY"/>, then moved by <paramref name="offsetX"/>
    /// and <paramref name="offsetY"/>, which the caller then owns; zero when GDI fails.
    /// </summary>
    public IntPtr CreatePlaced(double scaleX, double scaleY, double offsetX, double offsetY)
    {
        var transform = new Xform
        {
            M11 = (float)scaleX,
            M22 = (float)scaleY,
            Dx = (float)offsetX,
            Dy = (float)offsetY,
        };
        return ExtCreateRegion(ref transform, _data.Length, _data);
    }

    // ORs the rectangles a half at a time. Adding them one by one to a growing region costs time in proportion to the
    // region's size each time, which comes to the square of their count.
    private static IntPtr Union(IReadOnlyList<PixelRect> rects, int start, int count)
    {
        if (count == 1)
        {
            var rect = rects[start];
            return Gdi32.CreateRectRgn(rect.X, rect.Y, rect.Right, rect.Bottom);
        }

        IntPtr first = Union(rects, start, count / 2);
        IntPtr second = Union(rects, start + (count / 2), count - (count / 2));
        if (first == IntPtr.Zero || second == IntPtr.Zero)
        {
            if (first != IntPtr.Zero) Gdi32.DeleteObject(first);
            if (second != IntPtr.Zero) Gdi32.DeleteObject(second);
            return IntPtr.Zero;
        }

        Gdi32.CombineRgn(first, first, second, RgnOr);
        Gdi32.DeleteObject(second);
        return first;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Xform
    {
        public float M11;
        public float M12;
        public float M21;
        public float M22;
        public float Dx;
        public float Dy;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetRegionData(IntPtr hrgn, int nCount, byte[]? lpRgnData);

    [DllImport("gdi32.dll")]
    private static extern IntPtr ExtCreateRegion(ref Xform lpx, int nCount, byte[] lpRgnData);
}
