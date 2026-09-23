using System;
using System.Collections.Generic;
using Avalonia;

namespace GimmeCapture.Services.Core.Interaction;

/// <summary>
/// Finds the opaque parts of a pinned image, so a pin with see-through areas (a removed background, say) gets a
/// window region that lets clicks through them.
///
/// Split in two because the halves change at different rates. <see cref="Scan"/> reads every pixel, and depends only
/// on the image; <see cref="Map"/> places the result wherever the image is drawn, and is cheap. The pin window used
/// to do both on every size change — a full-bitmap copy and scan for each step of a drag-resize or zoom.
/// </summary>
internal static class OpaqueRegionScanner
{
    /// <summary>Alpha at or below this counts as see-through.</summary>
    internal const byte TransparentAlpha = 8;

    /// <summary>
    /// Opaque areas of a 32-bit image (alpha in the 4th byte of each pixel), in the image's own pixels: each row's
    /// runs of opaque pixels, with a run that repeats on the rows below merged into one taller rectangle.
    /// </summary>
    internal static List<PixelRect> Scan(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        var result = new List<PixelRect>();
        if (width <= 0 || height <= 0)
        {
            return result;
        }

        // Runs still growing downward, keyed by (first column, length); the value is the row the run started on.
        var active = new Dictionary<(int Start, int Length), int>();
        var next = new Dictionary<(int Start, int Length), int>();
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = pixels.Slice(y * stride, width * 4);
            int x = 0;
            while (x < width)
            {
                if (row[(x * 4) + 3] <= TransparentAlpha)
                {
                    x++;
                    continue;
                }

                int start = x++;
                while (x < width && row[(x * 4) + 3] > TransparentAlpha)
                {
                    x++;
                }

                var key = (start, x - start);
                next[key] = active.Remove(key, out int top) ? top : y;
            }

            // A run that did not continue on this row ended on the row above.
            foreach (var ((start, length), top) in active)
            {
                result.Add(new PixelRect(start, top, length, y - top));
            }

            (active, next) = (next, active);
            next.Clear();
        }

        foreach (var ((start, length), top) in active)
        {
            result.Add(new PixelRect(start, top, length, height - top));
        }

        return result;
    }

    /// <summary>Places <see cref="Scan"/>'s pixel rectangles where a <paramref name="width"/>×<paramref name="height"/>
    /// image is drawn at <paramref name="renderedRect"/>.</summary>
    internal static List<Rect> Map(IReadOnlyList<PixelRect> pixelRects, int width, int height, Rect renderedRect)
    {
        var rects = new List<Rect>(pixelRects.Count);
        if (width <= 0 || height <= 0)
        {
            return rects;
        }

        double scaleX = renderedRect.Width / width;
        double scaleY = renderedRect.Height / height;
        foreach (var pixelRect in pixelRects)
        {
            rects.Add(new Rect(
                renderedRect.X + (pixelRect.X * scaleX),
                renderedRect.Y + (pixelRect.Y * scaleY),
                pixelRect.Width * scaleX,
                pixelRect.Height * scaleY));
        }

        return rects;
    }
}
