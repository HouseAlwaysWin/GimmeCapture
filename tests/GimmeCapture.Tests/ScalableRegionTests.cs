using System.Runtime.InteropServices;
using Avalonia;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Tests;

// A pinned image's opaque parts go into its window region: built once, then placed at the size and position the image
// is drawn at for every rebuild.
public sealed class ScalableRegionTests
{
    [WindowsFact]
    public void APlacedCopy_CoversTheRectsScaledThenMoved()
    {
        var region = ScalableRegion.FromRects(
        [
            new PixelRect(0, 0, 10, 4),
            new PixelRect(20, 0, 5, 4),
            new PixelRect(0, 4, 25, 6),
        ]);
        Assert.NotNull(region);

        IntPtr placed = region.CreatePlaced(2, 3, 100, 50);
        try
        {
            Assert.NotEqual(IntPtr.Zero, placed);
            // The first rect lands on x 100-120, y 50-62; the third on x 100-150, y 62-80.
            Assert.True(PtInRegion(placed, 101, 51));
            Assert.True(PtInRegion(placed, 119, 61));
            Assert.True(PtInRegion(placed, 145, 55));
            Assert.True(PtInRegion(placed, 130, 70));
            Assert.False(PtInRegion(placed, 130, 55)); // between the first two
            Assert.False(PtInRegion(placed, 99, 51));
            Assert.False(PtInRegion(placed, 151, 70));
            Assert.False(PtInRegion(placed, 130, 81));
        }
        finally
        {
            Gdi32.DeleteObject(placed);
        }
    }

    [WindowsFact]
    public void ARegionOfManyRects_IsTheirUnion()
    {
        // One run a row inside an ellipse, as a cut-out pin's scan gives, and a few that overlap them.
        var rects = new List<PixelRect>();
        for (int y = 0; y < 300; y++)
        {
            double dy = (y - 150) / 150.0;
            int half = (int)(200 * Math.Sqrt(Math.Max(0, 1 - (dy * dy))));
            if (half > 0)
            {
                rects.Add(new PixelRect(200 - half, y, half * 2, 1));
            }
        }

        rects.Add(new PixelRect(150, 100, 100, 100));
        rects.Add(new PixelRect(390, 140, 30, 20));

        IntPtr expected = Gdi32.CreateRectRgn(0, 0, 0, 0);
        IntPtr placed = IntPtr.Zero;
        try
        {
            foreach (var rect in rects)
            {
                IntPtr one = Gdi32.CreateRectRgn(rect.X, rect.Y, rect.Right, rect.Bottom);
                Gdi32.CombineRgn(expected, expected, one, RgnOr);
                Gdi32.DeleteObject(one);
            }

            placed = ScalableRegion.FromRects(rects)!.CreatePlaced(1, 1, 0, 0);

            Assert.True(EqualRgn(expected, placed));
        }
        finally
        {
            Gdi32.DeleteObject(expected);
            if (placed != IntPtr.Zero) Gdi32.DeleteObject(placed);
        }
    }

    [Fact]
    public void NoRects_GiveNoRegion()
    {
        Assert.Null(ScalableRegion.FromRects([]));
    }

    private const int RgnOr = 2;

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PtInRegion(IntPtr hrgn, int x, int y);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualRgn(IntPtr hrgn1, IntPtr hrgn2);
}
