using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Avalonia;
using Avalonia.Media.Imaging;

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

    // A pixel read as one little-endian word has its alpha in the top byte, so it is see-through when the word is at most
    // this, and runs can be found eight pixels at a time (Find).
    private const uint LastTransparentWord = ((uint)TransparentAlpha << 24) | 0x00FFFFFFu;

    // How much of a bitmap is read at a time: a band of whole rows, about a megabyte.
    private const int BandBytes = 1 << 20;

    /// <summary>
    /// Opaque areas of a 32-bit image (alpha in the 4th byte of each pixel), in the image's own pixels: each row's
    /// runs of opaque pixels, with a run that repeats on the rows below merged into one taller rectangle.
    /// </summary>
    internal static List<PixelRect> Scan(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        var runs = new RunMerger();
        if (width <= 0 || height <= 0)
        {
            return runs.Finish(0);
        }

        for (int y = 0; y < height; y++)
        {
            runs.AddRow(MemoryMarshal.Cast<byte, uint>(pixels.Slice(y * stride, width * 4)), y);
        }

        return runs.Finish(height);
    }

    /// <summary>
    /// <see cref="Scan(ReadOnlySpan{byte}, int, int, int)"/> of a bitmap, read a band of rows at a time into a buffer
    /// freed at the end. The pin copied the whole picture first, into an array from the shared pool, which kept it: 33 MB
    /// on the .NET heap from a 4K pin's first scan on.
    /// </summary>
    internal static unsafe List<PixelRect> Scan(Bitmap bitmap)
    {
        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        var runs = new RunMerger();
        if (width <= 0 || height <= 0)
        {
            return runs.Finish(0);
        }

        int stride = width * 4;
        int bandRows = Math.Clamp(BandBytes / stride, 1, height);
        byte* band = (byte*)NativeMemory.Alloc((nuint)bandRows * (nuint)stride);
        try
        {
            for (int top = 0; top < height; top += bandRows)
            {
                int rows = Math.Min(bandRows, height - top);
                bitmap.CopyPixels(new PixelRect(0, top, width, rows), (IntPtr)band, rows * stride, stride);
                for (int row = 0; row < rows; row++)
                {
                    runs.AddRow(new ReadOnlySpan<uint>(band + ((nint)row * stride), width), top + row);
                }
            }
        }
        finally
        {
            NativeMemory.Free(band);
        }

        return runs.Finish(height);
    }

    // Merges each row's opaque runs into the rows above: a run that repeats on the next row grows into a taller rect.
    private sealed class RunMerger
    {
        private readonly List<PixelRect> _result = new();

        // Runs still growing downward, keyed by (first column, length); the value is the row the run started on.
        private Dictionary<(int Start, int Length), int> _active = new();
        private Dictionary<(int Start, int Length), int> _next = new();

        public void AddRow(ReadOnlySpan<uint> row, int y)
        {
            int x = 0;
            while (true)
            {
                int start = Find(row, x, seeThrough: false);
                if (start == row.Length)
                {
                    break;
                }

                x = Find(row, start, seeThrough: true);
                var key = (start, x - start);
                _next[key] = _active.Remove(key, out int top) ? top : y;
            }

            // A run that did not continue on this row ended on the row above.
            foreach (var ((start, length), top) in _active)
            {
                _result.Add(new PixelRect(start, top, length, y - top));
            }

            (_active, _next) = (_next, _active);
            _next.Clear();
        }

        // The first pixel from `from` on that is see-through (or, with seeThrough false, opaque); the row's length if
        // none is. Compiled optimized from its first call: through the span's own range searches, which .NET runs
        // unoptimized until it recompiles them, an opaque 4K picture took 9.5 ms instead of 2.4, and a pin scans seldom.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static int Find(ReadOnlySpan<uint> row, int from, bool seeThrough)
        {
            int x = from;
            if (Vector256.IsHardwareAccelerated)
            {
                var last = Vector256.Create(LastTransparentWord);
                ref uint first = ref MemoryMarshal.GetReference(row);
                for (; x <= row.Length - Vector256<uint>.Count; x += Vector256<uint>.Count)
                {
                    var transparent = Vector256.LessThanOrEqual(Vector256.LoadUnsafe(ref first, (nuint)x), last);
                    uint found = (seeThrough ? transparent : ~transparent).ExtractMostSignificantBits();
                    if (found != 0)
                    {
                        return x + BitOperations.TrailingZeroCount(found);
                    }
                }
            }

            for (; x < row.Length; x++)
            {
                if ((row[x] <= LastTransparentWord) == seeThrough)
                {
                    return x;
                }
            }

            return row.Length;
        }

        public List<PixelRect> Finish(int height)
        {
            foreach (var ((start, length), top) in _active)
            {
                _result.Add(new PixelRect(start, top, length, height - top));
            }

            _active.Clear();
            return _result;
        }
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
