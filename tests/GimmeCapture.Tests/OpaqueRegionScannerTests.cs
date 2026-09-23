using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using GimmeCapture.Services.Core.Interaction;
using Xunit;

namespace GimmeCapture.Tests;

public class OpaqueRegionScannerTests
{
    [Fact]
    public void FullyOpaqueImage_IsOneRectangle()
    {
        var pixels = Image(4, 3, (_, _) => 255);

        var rects = OpaqueRegionScanner.Scan(pixels, 4, 3, 16);

        Assert.Equal(new[] { new PixelRect(0, 0, 4, 3) }, rects);
    }

    [Fact]
    public void FullyTransparentImage_HasNoRegion()
    {
        var pixels = Image(4, 3, (_, _) => 0);

        Assert.Empty(OpaqueRegionScanner.Scan(pixels, 4, 3, 16));
    }

    [Fact]
    public void TransparentHole_SplitsTheRowsAroundIt()
    {
        var pixels = Image(3, 3, (x, y) => x == 1 && y == 1 ? (byte)0 : (byte)255);

        var rects = OpaqueRegionScanner.Scan(pixels, 3, 3, 12);

        Assert.Equal(
            new[] { new PixelRect(0, 0, 3, 1), new PixelRect(0, 1, 1, 1), new PixelRect(2, 1, 1, 1), new PixelRect(0, 2, 3, 1) }
                .OrderBy(Key),
            rects.OrderBy(Key));
    }

    [Fact]
    public void AlphaAtTheThreshold_IsSeeThrough()
    {
        var pixels = Image(2, 1, (x, _) => x == 0 ? OpaqueRegionScanner.TransparentAlpha : (byte)(OpaqueRegionScanner.TransparentAlpha + 1));

        Assert.Equal(new[] { new PixelRect(1, 0, 1, 1) }, OpaqueRegionScanner.Scan(pixels, 2, 1, 8));
    }

    [Fact]
    public void RowPadding_IsIgnored()
    {
        const int width = 2, height = 2, stride = 12; // 4 bytes of padding per row, all "opaque" garbage
        var pixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
        pixels[3] = 0; // (0,0) see-through

        var rects = OpaqueRegionScanner.Scan(pixels, width, height, stride);

        // Row 0 is one run (column 1); row 1 is a different run (both columns), so they do not merge. Were the
        // padding read as pixels, both runs would reach into it.
        Assert.Equal(new[] { new PixelRect(1, 0, 1, 1), new PixelRect(0, 1, 2, 1) }.OrderBy(Key), rects.OrderBy(Key));
    }

    [Fact]
    public void Map_ScalesAndOffsetsIntoTheDrawnImage()
    {
        var mapped = OpaqueRegionScanner.Map(new[] { new PixelRect(1, 2, 3, 4) }, 10, 10, new Rect(100, 50, 20, 40));

        Assert.Equal(new Rect(102, 58, 6, 16), Assert.Single(mapped));
    }

    // The window used to rescan the bitmap for every drawn size. Scanning once and mapping must give the region
    // that per-size scan produced, for any image and any size it is drawn at.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ScanOnceThenMap_MatchesTheOldPerSizeScan(int seed)
    {
        var random = new Random(seed);
        int width = random.Next(1, 48), height = random.Next(1, 36);
        var pixels = Image(width, height, (_, _) => 255);
        // Overlapping opaque and see-through blocks, so runs start, continue and end at different rows.
        for (int i = 0; i < 12; i++)
        {
            int x0 = random.Next(width), y0 = random.Next(height);
            int x1 = random.Next(x0, width), y1 = random.Next(y0, height);
            byte alpha = random.Next(2) == 0 ? (byte)0 : (byte)200;
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                pixels[(y * width * 4) + (x * 4) + 3] = alpha;
            }
        }

        var pixelRects = OpaqueRegionScanner.Scan(pixels, width, height, width * 4);
        foreach (var drawn in new[] { new Rect(0, 0, width, height), new Rect(13.5, 7.25, width * 2.5, height * 0.4), new Rect(-3, 2, 999, 17) })
        {
            var expected = LegacyScanAtSize(pixels, width, height, drawn).Select(Rounded).OrderBy(r => r.ToString()).ToList();
            var actual = OpaqueRegionScanner.Map(pixelRects, width, height, drawn).Select(Rounded).OrderBy(r => r.ToString()).ToList();

            Assert.Equal(expected, actual);
        }
    }

    private static byte[] Image(int width, int height, Func<int, int, byte> alpha)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width * 4) + (x * 4);
            pixels[i] = 10;
            pixels[i + 1] = 20;
            pixels[i + 2] = 30;
            pixels[i + 3] = alpha(x, y);
        }

        return pixels;
    }

    private static (int, int, int, int) Key(PixelRect r) => (r.Y, r.X, r.Width, r.Height);

    private static Rect Rounded(Rect r) =>
        new(Math.Round(r.X, 6), Math.Round(r.Y, 6), Math.Round(r.Width, 6), Math.Round(r.Height, 6));

    // FloatingImageWindow.BuildOpaqueImageRects as it was before the scan moved out of the resize path, kept verbatim
    // (minus the bitmap copy) as the reference for the region a pin must end up with.
    private static List<Rect> LegacyScanAtSize(byte[] pixels, int width, int height, Rect renderedRect)
    {
        const int bytesPerPixel = 4;
        int stride = width * bytesPerPixel;
        double scaleX = renderedRect.Width / width;
        double scaleY = renderedRect.Height / height;
        var active = new Dictionary<(int Start, int Length), Rect>();
        var result = new List<Rect>();

        for (int y = 0; y < height; y++)
        {
            var next = new Dictionary<(int Start, int Length), Rect>();
            int rowOffset = y * stride;
            int x = 0;

            while (x < width)
            {
                int alphaIndex = rowOffset + (x * bytesPerPixel) + 3;
                if (pixels[alphaIndex] <= 8)
                {
                    x++;
                    continue;
                }

                int start = x;
                x++;
                while (x < width)
                {
                    alphaIndex = rowOffset + (x * bytesPerPixel) + 3;
                    if (pixels[alphaIndex] <= 8)
                    {
                        break;
                    }

                    x++;
                }

                int length = x - start;
                var key = (start, length);
                if (active.Remove(key, out var existing))
                {
                    next[key] = new Rect(existing.X, existing.Y, existing.Width, existing.Height + scaleY);
                }
                else
                {
                    next[key] = new Rect(
                        renderedRect.X + (start * scaleX),
                        renderedRect.Y + (y * scaleY),
                        Math.Max(scaleX, length * scaleX),
                        Math.Max(scaleY, scaleY));
                }
            }

            foreach (var leftover in active.Values)
            {
                result.Add(leftover);
            }

            active = next;
        }

        foreach (var rect in active.Values)
        {
            result.Add(rect);
        }

        return result;
    }
}
