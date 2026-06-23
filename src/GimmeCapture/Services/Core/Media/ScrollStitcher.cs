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
    // Number of evenly-spaced columns sampled per row when building a row signature.
    // Comparing a fixed subset keeps the O(height^2) shift search affordable on tall regions.
    private const int SampleColumns = 32;

    // Per-channel (B/G/R) absolute difference, below which two sampled pixels are "the same".
    // Absorbs subpixel anti-aliasing / GPU compositing jitter so a row re-rendered slightly
    // differently between frames (e.g. Discord, Chromium GPU layers) still counts as matching.
    private const int ColorTolerance = 18;

    // Fraction of a row's sampled columns allowed to differ before the whole row is "changed".
    // Tolerates a hover highlight, blinking caret or a single moving glyph within a row.
    private const double RowPixelMismatchTolerance = 0.18;

    /// <summary>
    /// Returns how many rows at the bottom of <paramref name="previous"/> coincide with
    /// rows at the top of <paramref name="next"/> (the overlap). With a downward scroll of
    /// <c>s</c> pixels, <c>next</c> equals <c>previous</c> shifted up by <c>s</c>, so the
    /// overlap is <c>height - s</c> and the new content is the bottom <c>s</c> rows of
    /// <paramref name="next"/>.
    /// Returns <c>height</c> when the frames are identical (no scroll happened) and 0 when
    /// no overlap of at least <paramref name="minOverlapRows"/> rows is found or the frames
    /// differ in size.
    /// Rows are matched by sampled pixels with a colour tolerance (not byte-exact hashing),
    /// so frames that re-render with minor per-pixel differences still align.
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

        // Sampled BGRA bytes per row: [row * sampleStride .. +sampleStride). sampleStride is
        // sampleCount*4. A 0-width signature (no usable columns) means every row "matches".
        byte[] prevSig = ComputeRowSignatures(previous, ignoreRightColumns, out int sampleCount);
        byte[] nextSig = ComputeRowSignatures(next, ignoreRightColumns, out _);
        int sampleStride = sampleCount * 4;
        int allowedRowPixelMismatches = (int)(sampleCount * RowPixelMismatchTolerance);

        // s = scroll distance in rows; smallest matching s == largest overlap.
        for (int s = 0; s <= height - effectiveMin; s++)
        {
            int overlap = height - s;
            int allowedMismatches = (int)(overlap * clampedRatio);
            int mismatches = 0;
            bool match = true;
            for (int r = 0; r < overlap; r++)
            {
                if (!RowsMatch(prevSig, (s + r) * sampleStride, nextSig, r * sampleStride, sampleCount, allowedRowPixelMismatches))
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
    /// Two rows match when at most <paramref name="allowedPixelMismatches"/> of their sampled
    /// columns differ; a column differs when any B/G/R channel is off by more than
    /// <see cref="ColorTolerance"/>. Alpha is ignored (capture frames are opaque).
    /// </summary>
    private static bool RowsMatch(byte[] a, int aOffset, byte[] b, int bOffset, int sampleCount, int allowedPixelMismatches)
    {
        int diffs = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            int o = i * 4;
            int db = Math.Abs(a[aOffset + o] - b[bOffset + o]);
            int dg = Math.Abs(a[aOffset + o + 1] - b[bOffset + o + 1]);
            int dr = Math.Abs(a[aOffset + o + 2] - b[bOffset + o + 2]);
            if (db > ColorTolerance || dg > ColorTolerance || dr > ColorTolerance)
            {
                diffs++;
                if (diffs > allowedPixelMismatches)
                {
                    return false;
                }
            }
        }

        return true;
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

    /// <summary>
    /// Builds a per-row signature of <paramref name="sampleCount"/> BGRA samples taken at
    /// evenly-spaced columns across the usable width (excluding <paramref name="ignoreRightColumns"/>).
    /// Returned as a flat byte array of length <c>height * sampleCount * 4</c>; row <c>r</c>
    /// occupies <c>[r * sampleCount * 4 .. +sampleCount * 4)</c>.
    /// </summary>
    private static byte[] ComputeRowSignatures(SKBitmap bitmap, int ignoreRightColumns, out int sampleCount)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int usableColumns = Math.Max(0, width - Math.Max(0, ignoreRightColumns));

        sampleCount = Math.Min(SampleColumns, usableColumns);
        var signatures = new byte[height * Math.Max(1, sampleCount) * 4];

        int stride = bitmap.RowBytes;
        IntPtr pixels = bitmap.GetPixels();
        if (pixels == IntPtr.Zero || sampleCount == 0)
        {
            return signatures;
        }

        // Precompute the sampled column x-positions once (same for every row).
        var columns = new int[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            columns[i] = sampleCount == 1
                ? 0
                : (int)((long)i * (usableColumns - 1) / (sampleCount - 1));
        }

        unsafe
        {
            byte* basePtr = (byte*)pixels;
            for (int row = 0; row < height; row++)
            {
                byte* rowPtr = basePtr + (row * stride);
                int sigBase = row * sampleCount * 4;
                for (int i = 0; i < sampleCount; i++)
                {
                    byte* px = rowPtr + (columns[i] * 4);
                    int o = sigBase + (i * 4);
                    signatures[o] = px[0];
                    signatures[o + 1] = px[1];
                    signatures[o + 2] = px[2];
                    signatures[o + 3] = px[3];
                }
            }
        }

        return signatures;
    }
}
