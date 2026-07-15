using System;
using System.Collections.Generic;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Pure image-stitching helpers for scrolling capture. Works like a phone's panorama
/// mode: detect how far the content moved between two consecutive frames of the same
/// region (a signed vertical shift) and grow the captured strip on the correct side.
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

    // If even the best-aligned shift still has more than this fraction of sampled pixels
    // differing, the frames don't really overlap (scrolled too far, or unrelated content):
    // report no match instead of stitching garbage.
    private const double MaxMatchSampleDiffRatio = 0.5;

    // The best alignment must beat the best *competing* alignment (one that is at least
    // ShiftSeparationRows away) by this margin. A clean scroll produces one sharp minimum;
    // ambiguous frames (uniform bands, hover highlights, mid-animation) produce several
    // similar candidates — when the winner is not clearly the best, skip the frame rather
    // than risk stitching it at the wrong offset (the cause of the Discord scrambling).
    private const double AmbiguityMargin = 0.035;

    // A near-perfect best score (an essentially exact overlap) is trusted against a much smaller
    // ambiguity margin. Horizontally-sparse content folded into stitch space — a horizontal scroll of a
    // region with few vertical edges — carries little discriminative signal, so the runner-up offset
    // scores only slightly worse even though the true offset matches exactly; the strict 0.035 margin
    // would reject it and capture nothing. Genuinely periodic content instead makes the runner-up score
    // *equally* well (gap ~ 0), so it is still rejected. Only matches at/under NearPerfectScore use the
    // relaxed margin, so the normal (higher-score) path is unchanged.
    private const double NearPerfectScore = 0.02;
    private const double NearPerfectAmbiguityMargin = 0.006;

    // Competing alignments nearer than this to the winner are treated as the same minimum
    // (sub-pixel neighbours), not as a rival candidate.
    private const int ShiftSeparationRows = 3;

    // Row-subsampling target for AlignFrameToStrip: score roughly this many rows of the overlap
    // regardless of frame height, keeping the per-frame match cheap on tall regions so the
    // capture pipeline keeps a high frame rate (smaller gaps). Offsets stay pixel-accurate.
    private const int CoarseTargetRows = 256;
    private const int MaxRowStep = 8;

    /// <summary>
    /// The signed vertical motion of the content between two frames.
    /// <list type="bullet">
    /// <item><c>Rows &gt; 0</c>: content moved up (the user scrolled down); new content is at the BOTTOM.</item>
    /// <item><c>Rows &lt; 0</c>: content moved down (the user scrolled up); new content is at the TOP.</item>
    /// <item><c>Rows == 0</c>: no movement.</item>
    /// </list>
    /// <c>Found == false</c> means no confident alignment — do not stitch this frame.
    /// </summary>
    public readonly record struct VerticalShift(int Rows, bool Found);

    /// <summary>
    /// Finds the signed vertical shift that best aligns <paramref name="next"/> onto
    /// <paramref name="previous"/>. Rows are matched by sampled pixels with a colour
    /// tolerance (not byte-exact hashing), and the globally best-scoring shift is chosen
    /// (not merely the first acceptable one). The winner is also checked for ambiguity:
    /// if a competing alignment scores almost as well, the match is rejected
    /// (<c>Found == false</c>) rather than risk stitching at the wrong offset. Also returns
    /// <c>Found == false</c> when no overlap of at least <paramref name="minOverlapRows"/>
    /// rows aligns well enough.
    /// </summary>
    /// <param name="ignoreRightColumns">Columns to ignore on the right edge (e.g. a moving scrollbar).</param>
    /// <param name="maxRowMismatchRatio">
    /// Fraction of overlap rows allowed to be "changed" and still accept the alignment
    /// (0 = every row must match). Tolerates a static header/footer or a few re-rendered rows.
    /// </param>
    public static VerticalShift FindVerticalShift(
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
            return new VerticalShift(0, false);
        }

        int height = previous.Height;
        if (height <= 0)
        {
            return new VerticalShift(0, false);
        }

        int effectiveMin = Math.Clamp(minOverlapRows, 1, height);
        double clampedRatio = Math.Clamp(maxRowMismatchRatio, 0.0, 1.0);

        byte[] prevSig = ComputeRowSignatures(previous, ignoreRightColumns, out int sampleCount);
        byte[] nextSig = ComputeRowSignatures(next, ignoreRightColumns, out _);
        if (sampleCount == 0)
        {
            return new VerticalShift(0, false);
        }

        int allowedRowSampleDiffs = (int)(sampleCount * RowPixelMismatchTolerance);

        // Per-row "distinctiveness" weight from the established frame: how much each row
        // differs from the one above it (a content edge / text line). Uniform bands (blank
        // gaps, flat backgrounds) get ~0 weight so they don't dilute the match — only the
        // textured content drives alignment, which makes the true shift a sharp, unique
        // minimum even on noisy pages like Discord.
        double[] prevWeights = ComputeRowWeights(prevSig, height, sampleCount);

        // d = signed shift. d >= 0 aligns previous[r+d] with next[r] (content moved up);
        // d < 0 aligns previous[r] with next[r-d] (content moved down). Score every shift
        // first, then judge the winner against its competitors for ambiguity.
        int maxShift = height - effectiveMin;
        var scores = new double[(2 * maxShift) + 1]; // index = d + maxShift
        Array.Fill(scores, double.MaxValue);

        for (int d = -maxShift; d <= maxShift; d++)
        {
            int overlap = height - Math.Abs(d);
            int prevStart = d >= 0 ? d : 0;
            int nextStart = d >= 0 ? 0 : -d;
            int allowedRowMismatches = (int)(overlap * clampedRatio);

            double weightedDiff = 0;
            double weightSum = 0;
            int rowMismatches = 0;
            bool gated = false;
            for (int r = 0; r < overlap; r++)
            {
                int diffs = RowSampleDiffs(
                    prevSig, (prevStart + r) * sampleCount * 4,
                    nextSig, (nextStart + r) * sampleCount * 4,
                    sampleCount);

                double w = prevWeights[prevStart + r];
                weightedDiff += diffs * w;
                weightSum += w;

                if (diffs > allowedRowSampleDiffs)
                {
                    rowMismatches++;
                    if (rowMismatches > allowedRowMismatches)
                    {
                        gated = true;
                        break;
                    }
                }
            }

            // Need enough textured content in the overlap to trust the alignment.
            if (!gated && weightSum > 1e-9)
            {
                scores[d + maxShift] = weightedDiff / (weightSum * sampleCount);
            }
        }

        // Best alignment = lowest score; on ties prefer the larger overlap (smaller scroll).
        double bestScore = double.MaxValue;
        int bestShift = 0;
        bool found = false;
        for (int d = -maxShift; d <= maxShift; d++)
        {
            double score = scores[d + maxShift];
            if (score == double.MaxValue)
            {
                continue;
            }

            if (score < bestScore || (score == bestScore && Math.Abs(d) < Math.Abs(bestShift)))
            {
                bestScore = score;
                bestShift = d;
                found = true;
            }
        }

        if (!found || bestScore > MaxMatchSampleDiffRatio)
        {
            return new VerticalShift(0, false);
        }

        // Reject ambiguous matches: the winner must clearly beat the best rival that is at
        // least ShiftSeparationRows away. If no such rival exists, the minimum is unambiguous.
        double rivalScore = double.MaxValue;
        for (int d = -maxShift; d <= maxShift; d++)
        {
            if (Math.Abs(d - bestShift) < ShiftSeparationRows)
            {
                continue;
            }

            double score = scores[d + maxShift];
            if (score < rivalScore)
            {
                rivalScore = score;
            }
        }

        if (rivalScore != double.MaxValue && rivalScore - bestScore < AmbiguityMargin)
        {
            return new VerticalShift(0, false);
        }

        return new VerticalShift(bestShift, true);
    }

    /// <summary>
    /// Backwards-compatible overlap query for the downward-scroll case: returns how many
    /// rows at the bottom of <paramref name="previous"/> coincide with the top of
    /// <paramref name="next"/>. Returns <c>height</c> for identical frames and 0 when the
    /// best alignment is an upward scroll or no confident overlap is found.
    /// </summary>
    public static int FindVerticalOverlap(
        SKBitmap previous,
        SKBitmap next,
        int minOverlapRows = 8,
        int ignoreRightColumns = 0,
        double maxRowMismatchRatio = 0.0)
    {
        VerticalShift shift = FindVerticalShift(previous, next, minOverlapRows, ignoreRightColumns, maxRowMismatchRatio);
        if (!shift.Found || shift.Rows < 0)
        {
            return 0;
        }

        return previous.Height - shift.Rows;
    }

    /// <summary>The placement of a frame against the accumulated strip (see <see cref="AlignFrameToStrip"/>).</summary>
    /// <param name="Offset">
    /// Strip row index where the frame's first row sits. Negative => the frame overhangs the top
    /// (new content above); <c>Offset + frame.Height &gt; strip.Height</c> => it overhangs the
    /// bottom (new content below); otherwise the frame lies fully inside the strip.
    /// </param>
    /// <param name="Found">False when the frame doesn't confidently overlap the strip at all.</param>
    public readonly record struct FrameAlignment(int Offset, bool Found);

    /// <summary>
    /// Aligns a full region <paramref name="frame"/> against the accumulated <paramref name="strip"/>,
    /// allowing the frame to overhang either edge, and returns where the frame sits. Matching the
    /// frame directly against the real strip content (rather than summing per-frame shifts) means
    /// the position can never drift, so the same content is never appended twice. Only offsets
    /// within <paramref name="searchMargin"/> rows of either edge are evaluated, to bound cost.
    /// <c>Found == false</c> means the frame doesn't overlap the strip (new content past a gap).
    /// </summary>
    public static FrameAlignment AlignFrameToStrip(
        SKBitmap strip,
        SKBitmap frame,
        int minOverlapRows = 8,
        int ignoreRightColumns = 0,
        double maxRowMismatchRatio = 0.0,
        int searchMargin = int.MaxValue)
        => AlignFrameToStrip(strip, frame, out _, out _, out _, minOverlapRows, ignoreRightColumns, maxRowMismatchRatio, searchMargin);

    /// <summary>
    /// Diagnostic overload of <see cref="AlignFrameToStrip(SKBitmap,SKBitmap,int,int,double,int)"/> that
    /// also reports why a match was (not) accepted: <paramref name="bestScoreOut"/> is the lowest
    /// (best) weighted sample-diff ratio found (<c>double.MaxValue</c> when no offset scored at all —
    /// e.g. weight-starved/uniform frames); <paramref name="ambiguityGapOut"/> is rival−best (small =
    /// ambiguous; <c>double.MaxValue</c> when there is no rival); <paramref name="sampleCountOut"/> is
    /// the per-row sample count.
    /// </summary>
    public static FrameAlignment AlignFrameToStrip(
        SKBitmap strip,
        SKBitmap frame,
        out double bestScoreOut,
        out double ambiguityGapOut,
        out int sampleCountOut,
        int minOverlapRows = 8,
        int ignoreRightColumns = 0,
        double maxRowMismatchRatio = 0.0,
        int searchMargin = int.MaxValue)
    {
        bestScoreOut = double.MaxValue;
        ambiguityGapOut = double.MaxValue;
        sampleCountOut = 0;

        ArgumentNullException.ThrowIfNull(strip);
        ArgumentNullException.ThrowIfNull(frame);

        if (strip.Width != frame.Width || strip.Width <= 0 || frame.Height <= 0 || strip.Height <= 0)
        {
            return new FrameAlignment(0, false);
        }

        byte[] stripSig = ComputeRowSignatures(strip, ignoreRightColumns, out int stripSampleCount);
        return AlignCore(
            stripSig, 0, strip.Height, stripSampleCount, frame,
            out bestScoreOut, out ambiguityGapOut, out sampleCountOut,
            minOverlapRows, ignoreRightColumns, maxRowMismatchRatio, searchMargin);
    }

    /// <summary>
    /// Alignment core shared by the public <see cref="AlignFrameToStrip(SKBitmap,SKBitmap,out double,out double,out int,int,int,double,int)"/>
    /// and the incremental <see cref="ManualScrollStrip"/>: aligns <paramref name="frame"/> against a strip
    /// whose row signatures are already computed (row <c>r</c> at
    /// <c>stripSig[stripFirstRowByteOffset + r * stripSampleCount * 4 ..]</c>). Passing the strip signatures
    /// in — rather than re-sampling the whole strip every frame — is what lets a long capture stay O(n).
    /// <para>
    /// Only offsets within <paramref name="searchMargin"/> rows of either edge are scored; this is exactly
    /// the set the naive full scan scored (every other offset was <c>double.MaxValue</c> and could never
    /// win), so the chosen placement, ambiguity rejection and diagnostics are byte-for-byte identical — it
    /// just avoids allocating and scanning an O(stripHeight) score array.
    /// </para>
    /// </summary>
    internal static FrameAlignment AlignCore(
        byte[] stripSig,
        int stripFirstRowByteOffset,
        int stripHeight,
        int stripSampleCount,
        SKBitmap frame,
        out double bestScoreOut,
        out double ambiguityGapOut,
        out int sampleCountOut,
        int minOverlapRows,
        int ignoreRightColumns,
        double maxRowMismatchRatio,
        int searchMargin)
    {
        bestScoreOut = double.MaxValue;
        ambiguityGapOut = double.MaxValue;
        sampleCountOut = 0;

        int frameH = frame.Height;
        if (frameH <= 0 || stripHeight <= 0 || frame.Width <= 0)
        {
            return new FrameAlignment(0, false);
        }

        int effectiveMin = Math.Clamp(minOverlapRows, 1, Math.Min(frameH, stripHeight));
        double clampedRatio = Math.Clamp(maxRowMismatchRatio, 0.0, 1.0);

        byte[] frameSig = ComputeRowSignatures(frame, ignoreRightColumns, out int sampleCount);
        sampleCountOut = sampleCount;
        if (sampleCount == 0 || sampleCount != stripSampleCount)
        {
            return new FrameAlignment(0, false);
        }

        double[] frameWeights = ComputeRowWeights(frameSig, frameH, sampleCount);
        int allowedRowSampleDiffs = (int)(sampleCount * RowPixelMismatchTolerance);
        int stride = sampleCount * 4;

        // Score on every rowStep-th row so the per-frame cost stays bounded on tall regions
        // (~O(frameH^2 / rowStep)). Offsets are still searched at full pixel resolution, so the
        // chosen placement — and therefore the stitch seam — remains pixel-accurate.
        int rowStep = Math.Clamp(frameH / CoarseTargetRows, 1, MaxRowStep);

        int lo = -(frameH - effectiveMin); // frame overhangs the top
        int hi = stripHeight - effectiveMin; // frame overhangs the bottom
        int margin = searchMargin <= 0 ? (hi - lo) : searchMargin;

        // The scored offsets are the two edge windows { o - lo <= margin } ∪ { hi - o <= margin }.
        // Collected in ascending-offset order so the winner/rival selection below matches the old
        // ascending scan exactly (strict "<" => first wins on ties).
        var scored = new List<(int Offset, double Score)>();

        void ScoreRange(int from, int to)
        {
            for (int o = from; o <= to; o++)
            {
                int startR = Math.Max(0, -o);
                int endR = Math.Min(frameH, stripHeight - o);
                int overlap = endR - startR;
                if (overlap < effectiveMin)
                {
                    continue;
                }

                int sampledRows = (overlap + rowStep - 1) / rowStep;
                int allowedRowMismatches = (int)(sampledRows * clampedRatio);
                double weightedDiff = 0;
                double weightSum = 0;
                int rowMismatches = 0;
                bool gated = false;
                for (int r = startR; r < endR; r += rowStep)
                {
                    int diffs = RowSampleDiffs(
                        frameSig, r * stride,
                        stripSig, stripFirstRowByteOffset + ((o + r) * stride),
                        sampleCount);

                    double w = frameWeights[r];
                    weightedDiff += diffs * w;
                    weightSum += w;

                    if (diffs > allowedRowSampleDiffs)
                    {
                        rowMismatches++;
                        if (rowMismatches > allowedRowMismatches)
                        {
                            gated = true;
                            break;
                        }
                    }
                }

                if (!gated && weightSum > 1e-9)
                {
                    scored.Add((o, weightedDiff / (weightSum * sampleCount)));
                }
            }
        }

        int topWindowHi = Math.Min(lo + margin, hi);
        int bottomWindowLo = Math.Max(hi - margin, lo);
        if (bottomWindowLo <= topWindowHi + 1)
        {
            ScoreRange(lo, hi); // windows meet/overlap → one contiguous range
        }
        else
        {
            ScoreRange(lo, topWindowHi);
            ScoreRange(bottomWindowLo, hi);
        }

        double bestScore = double.MaxValue;
        int bestOffset = 0;
        bool found = false;
        foreach ((int offset, double score) in scored)
        {
            if (score < bestScore)
            {
                bestScore = score;
                bestOffset = offset;
                found = true;
            }
        }

        bestScoreOut = bestScore;

        if (!found || bestScore > MaxMatchSampleDiffRatio)
        {
            return new FrameAlignment(0, false);
        }

        // Reject an ambiguous placement: a clear winner must beat the best rival at least
        // ShiftSeparationRows away.
        double rivalScore = double.MaxValue;
        foreach ((int offset, double score) in scored)
        {
            if (Math.Abs(offset - bestOffset) < ShiftSeparationRows)
            {
                continue;
            }

            if (score < rivalScore)
            {
                rivalScore = score;
            }
        }

        if (rivalScore != double.MaxValue)
        {
            ambiguityGapOut = rivalScore - bestScore;
        }

        double ambiguityMargin = bestScore < NearPerfectScore ? NearPerfectAmbiguityMargin : AmbiguityMargin;
        if (rivalScore != double.MaxValue && rivalScore - bestScore < ambiguityMargin)
        {
            return new FrameAlignment(0, false);
        }

        return new FrameAlignment(bestOffset, true);
    }

    /// <summary>
    /// Per-row distinctiveness weight: the summed B/G/R difference between each row's samples
    /// and the row above it. Content edges / text lines score high; uniform bands score ~0.
    /// Used to weight the alignment match so featureless regions don't create false overlaps.
    /// </summary>
    private static double[] ComputeRowWeights(byte[] sig, int height, int sampleCount)
    {
        var weights = new double[height];
        int stride = sampleCount * 4;
        for (int row = 1; row < height; row++)
        {
            int cur = row * stride;
            int prev = (row - 1) * stride;
            long sum = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                int o = i * 4;
                sum += Math.Abs(sig[cur + o] - sig[prev + o]);
                sum += Math.Abs(sig[cur + o + 1] - sig[prev + o + 1]);
                sum += Math.Abs(sig[cur + o + 2] - sig[prev + o + 2]);
            }

            // Per-sample average vertical gradient. Uniform rows -> 0 (ignored); textured
            // rows (text, edges) -> large, so they dominate the alignment score.
            weights[row] = sum / (double)(sampleCount * 3);
        }

        weights[0] = height > 1 ? weights[1] : 1.0;
        return weights;
    }

    /// <summary>
    /// Counts how many of the two rows' sampled columns differ; a column differs when any
    /// B/G/R channel is off by more than <see cref="ColorTolerance"/>. Alpha is ignored
    /// (capture frames are opaque).
    /// </summary>
    private static int RowSampleDiffs(byte[] a, int aOffset, byte[] b, int bOffset, int sampleCount)
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
            }
        }

        return diffs;
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
    /// Returns a new bitmap consisting of the top <paramref name="newRows"/> rows of
    /// <paramref name="next"/> placed above <paramref name="accumulated"/> (the upward-scroll
    /// case). If there is no new content, a copy of <paramref name="accumulated"/> is returned.
    /// </summary>
    public static SKBitmap Prepend(SKBitmap accumulated, SKBitmap next, int newRows)
    {
        ArgumentNullException.ThrowIfNull(accumulated);
        ArgumentNullException.ThrowIfNull(next);

        newRows = Math.Clamp(newRows, 0, next.Height);
        if (newRows <= 0)
        {
            return accumulated.Copy();
        }

        int width = accumulated.Width;
        var result = new SKBitmap(new SKImageInfo(width, accumulated.Height + newRows, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);

        var src = new SKRect(0, 0, next.Width, newRows);
        var dst = new SKRect(0, 0, next.Width, newRows);
        canvas.DrawBitmap(next, src, dst);
        canvas.DrawBitmap(accumulated, 0, newRows);

        return result;
    }

    /// <summary>
    /// Returns <paramref name="src"/> rotated 90° clockwise (BGRA8888); the result has swapped
    /// dimensions (width = src.Height, height = src.Width). The source's top row maps to the
    /// result's right column. The exact inverse of <see cref="RotateCcw90"/>; used to restore screen
    /// orientation after a horizontal-scroll strip has been stitched in rotated space.
    /// </summary>
    public static SKBitmap RotateCw90(SKBitmap src)
    {
        ArgumentNullException.ThrowIfNull(src);

        int w = src.Width;
        int h = src.Height;
        var result = new SKBitmap(new SKImageInfo(h, w, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (w <= 0 || h <= 0)
        {
            return result;
        }

        IntPtr srcPixels = src.GetPixels();
        IntPtr dstPixels = result.GetPixels();
        if (srcPixels == IntPtr.Zero || dstPixels == IntPtr.Zero)
        {
            return result;
        }

        int srcStride = src.RowBytes;
        int dstStride = result.RowBytes;
        unsafe
        {
            byte* srcBase = (byte*)srcPixels;
            byte* dstBase = (byte*)dstPixels;
            for (int r = 0; r < h; r++)
            {
                uint* srcRow = (uint*)(srcBase + ((long)r * srcStride));
                int dstCol = h - 1 - r; // src(c, r) -> dst(col = h-1-r, row = c)
                for (int c = 0; c < w; c++)
                {
                    *((uint*)(dstBase + ((long)c * dstStride)) + dstCol) = srcRow[c];
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Returns <paramref name="src"/> rotated 90° counter-clockwise (BGRA8888); the result has
    /// swapped dimensions (width = src.Height, height = src.Width). The source's bottom row maps to
    /// the result's right column. Used to fold a horizontal scroll into the vertical "stitch space"
    /// so the exact same alignment/append/prepend logic applies — and so the cross-axis right-edge
    /// exclusion (<c>ignoreRightColumns</c>) lands on a bottom horizontal scrollbar. The exact
    /// inverse of <see cref="RotateCw90"/>.
    /// </summary>
    public static SKBitmap RotateCcw90(SKBitmap src)
    {
        ArgumentNullException.ThrowIfNull(src);

        int w = src.Width;
        int h = src.Height;
        var result = new SKBitmap(new SKImageInfo(h, w, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (w <= 0 || h <= 0)
        {
            return result;
        }

        IntPtr srcPixels = src.GetPixels();
        IntPtr dstPixels = result.GetPixels();
        if (srcPixels == IntPtr.Zero || dstPixels == IntPtr.Zero)
        {
            return result;
        }

        int srcStride = src.RowBytes;
        int dstStride = result.RowBytes;
        unsafe
        {
            byte* srcBase = (byte*)srcPixels;
            byte* dstBase = (byte*)dstPixels;
            for (int r = 0; r < h; r++)
            {
                uint* srcRow = (uint*)(srcBase + ((long)r * srcStride));
                int dstCol = r; // src(c, r) -> dst(col = r, row = w-1-c)
                for (int c = 0; c < w; c++)
                {
                    int dstRow = w - 1 - c;
                    *((uint*)(dstBase + ((long)dstRow * dstStride)) + dstCol) = srcRow[c];
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The evenly-spaced sample column x-positions across the usable width (excluding
    /// <paramref name="ignoreRightColumns"/>), shared by every row. Extracted so a strip's signatures
    /// can be extended incrementally (see <see cref="ManualScrollStrip"/>) with the exact same sampling
    /// the whole-bitmap <see cref="ComputeRowSignatures"/> uses. Returns an empty array with
    /// <paramref name="sampleCount"/> = 0 when there is no usable width.
    /// </summary>
    internal static int[] BuildSampleColumns(int width, int ignoreRightColumns, out int sampleCount)
    {
        int usableColumns = Math.Max(0, width - Math.Max(0, ignoreRightColumns));
        sampleCount = Math.Min(SampleColumns, usableColumns);
        if (sampleCount == 0)
        {
            return Array.Empty<int>();
        }

        var columns = new int[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            columns[i] = sampleCount == 1
                ? 0
                : (int)((long)i * (usableColumns - 1) / (sampleCount - 1));
        }

        return columns;
    }

    /// <summary>
    /// Samples <paramref name="rowCount"/> rows of <paramref name="bitmap"/> (from <paramref name="firstRow"/>)
    /// at <paramref name="columns"/> into <paramref name="dest"/> starting at <paramref name="destByteOffset"/>
    /// (BGRA per sample, <paramref name="sampleCount"/> samples per row). No-op when the pixels are
    /// unavailable or nothing is sampled — the destination bytes are left untouched.
    /// </summary>
    internal static void SampleRowsInto(
        SKBitmap bitmap, int firstRow, int rowCount, int[] columns, int sampleCount, byte[] dest, int destByteOffset)
    {
        if (sampleCount == 0 || rowCount <= 0)
        {
            return;
        }

        int stride = bitmap.RowBytes;
        IntPtr pixels = bitmap.GetPixels();
        if (pixels == IntPtr.Zero)
        {
            return;
        }

        unsafe
        {
            byte* basePtr = (byte*)pixels;
            for (int row = 0; row < rowCount; row++)
            {
                byte* rowPtr = basePtr + ((long)(firstRow + row) * stride);
                int sigBase = destByteOffset + (row * sampleCount * 4);
                for (int i = 0; i < sampleCount; i++)
                {
                    byte* px = rowPtr + (columns[i] * 4);
                    int o = sigBase + (i * 4);
                    dest[o] = px[0];
                    dest[o + 1] = px[1];
                    dest[o + 2] = px[2];
                    dest[o + 3] = px[3];
                }
            }
        }
    }

    /// <summary>
    /// Builds a per-row signature of <paramref name="sampleCount"/> BGRA samples taken at
    /// evenly-spaced columns across the usable width (excluding <paramref name="ignoreRightColumns"/>).
    /// Returned as a flat byte array of length <c>height * sampleCount * 4</c>; row <c>r</c>
    /// occupies <c>[r * sampleCount * 4 .. +sampleCount * 4)</c>.
    /// </summary>
    private static byte[] ComputeRowSignatures(SKBitmap bitmap, int ignoreRightColumns, out int sampleCount)
    {
        int height = bitmap.Height;
        int[] columns = BuildSampleColumns(bitmap.Width, ignoreRightColumns, out sampleCount);
        var signatures = new byte[height * Math.Max(1, sampleCount) * 4];
        SampleRowsInto(bitmap, 0, height, columns, sampleCount, signatures, 0);
        return signatures;
    }
}
