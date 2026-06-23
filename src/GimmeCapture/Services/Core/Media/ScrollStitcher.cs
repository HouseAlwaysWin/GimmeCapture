using System;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Pure image-stitching helpers for scrolling capture: detect the vertical overlap
/// between two consecutive frames of the same region and append the new content.
/// Kept free of Win32/UI so it can be unit tested. Operates on BGRA8888 SKBitmaps.
/// </summary>
internal static class ScrollStitcher
{
    private const ulong FnvOffset = 1469598103934665603UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// Returns how many rows at the bottom of <paramref name="previous"/> coincide with
    /// rows at the top of <paramref name="next"/> (the overlap). With a downward scroll of
    /// <c>s</c> pixels, <c>next</c> equals <c>previous</c> shifted up by <c>s</c>, so the
    /// overlap is <c>height - s</c> and the new content is the bottom <c>s</c> rows of
    /// <paramref name="next"/>.
    /// Returns <c>height</c> when the frames are identical (no scroll happened) and 0 when
    /// no overlap of at least <paramref name="minOverlapRows"/> rows is found or the frames
    /// differ in size.
    /// </summary>
    /// <param name="ignoreRightColumns">Columns to ignore on the right edge (e.g. a moving scrollbar).</param>
    /// <param name="maxRowMismatchRatio">
    /// Fraction of overlap rows allowed to differ and still count as a match (0 = exact).
    /// Tolerates minor per-frame rendering differences (hover, blinking caret, lazy-loaded rows).
    /// </param>
    public static int FindVerticalOverlap(
        SKBitmap previous,
        SKBitmap next,
        int minOverlapRows = 8,
        int ignoreRightColumns = 0,
        double maxRowMismatchRatio = 0.0)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);

        if (previous.Width != next.Width || previous.Height != next.Height || previous.Width <= 0)
        {
            return 0;
        }

        int height = previous.Height;
        if (height <= 0)
        {
            return 0;
        }

        int effectiveMin = Math.Clamp(minOverlapRows, 1, height);
        double clampedRatio = Math.Clamp(maxRowMismatchRatio, 0.0, 1.0);

        ulong[] prevHashes = ComputeRowHashes(previous, ignoreRightColumns);
        ulong[] nextHashes = ComputeRowHashes(next, ignoreRightColumns);

        // s = scroll distance in rows; smallest matching s == largest overlap.
        for (int s = 0; s <= height - effectiveMin; s++)
        {
            int overlap = height - s;
            int allowedMismatches = (int)(overlap * clampedRatio);
            int mismatches = 0;
            bool match = true;
            for (int r = 0; r < overlap; r++)
            {
                if (prevHashes[s + r] != nextHashes[r])
                {
                    mismatches++;
                    if (mismatches > allowedMismatches)
                    {
                        match = false;
                        break;
                    }
                }
            }

            if (match)
            {
                return overlap;
            }
        }

        return 0;
    }

    /// <summary>
    /// Returns a new bitmap consisting of <paramref name="accumulated"/> followed by the
    /// non-overlapping bottom rows of <paramref name="next"/>. If there is no new content,
    /// a copy of <paramref name="accumulated"/> is returned.
    /// </summary>
    public static SKBitmap Append(SKBitmap accumulated, SKBitmap next, int overlapRows)
    {
        ArgumentNullException.ThrowIfNull(accumulated);
        ArgumentNullException.ThrowIfNull(next);

        int newRows = next.Height - Math.Clamp(overlapRows, 0, next.Height);
        if (newRows <= 0)
        {
            return accumulated.Copy();
        }

        int width = accumulated.Width;
        var result = new SKBitmap(new SKImageInfo(width, accumulated.Height + newRows, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        canvas.DrawBitmap(accumulated, 0, 0);

        int srcTop = next.Height - newRows;
        var src = new SKRect(0, srcTop, next.Width, next.Height);
        var dst = new SKRect(0, accumulated.Height, next.Width, accumulated.Height + newRows);
        canvas.DrawBitmap(next, src, dst);

        return result;
    }

    private static ulong[] ComputeRowHashes(SKBitmap bitmap, int ignoreRightColumns)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int usableColumns = Math.Max(0, width - Math.Max(0, ignoreRightColumns));
        int usableBytes = usableColumns * 4;

        var hashes = new ulong[height];
        int stride = bitmap.RowBytes;
        IntPtr pixels = bitmap.GetPixels();
        if (pixels == IntPtr.Zero || usableBytes == 0)
        {
            return hashes;
        }

        unsafe
        {
            byte* basePtr = (byte*)pixels;
            for (int row = 0; row < height; row++)
            {
                byte* rowPtr = basePtr + (row * stride);
                ulong hash = FnvOffset;
                for (int b = 0; b < usableBytes; b++)
                {
                    hash ^= rowPtr[b];
                    hash *= FnvPrime;
                }

                hashes[row] = hash;
            }
        }

        return hashes;
    }
}
