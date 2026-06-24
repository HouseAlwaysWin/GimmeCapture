using SkiaSharp;

namespace GimmeCapture.Tests;

public class ScrollStitcherTests
{
    // Each source row maps to a deterministic color, so a "frame" showing source rows
    // [offset, offset+height) shares identical rows with another frame wherever their
    // source ranges overlap — exactly the situation a vertical scroll produces.
    private static SKColor ColorForRow(int absoluteRow) =>
        new((byte)((absoluteRow * 7) & 0xFF), (byte)((absoluteRow * 13) & 0xFF), (byte)((absoluteRow * 29) & 0xFF), 255);

    // Per-row colour that is collision-free under the matcher's colour tolerance: the row index is
    // encoded as three base-13 digits scaled by 19, so any two DISTINCT rows (0..13^3-1 = 0..2196)
    // differ by at least 19 (> ColorTolerance 18) in at least one channel, and adjacent rows differ in
    // the low digit so they are locally distinct too. Used by the sparse-horizontal-texture test where
    // a plain linear ramp (row*k) would alias (row and row+d collide when k*d wraps to within tolerance
    // of a multiple of 256, e.g. 29*9 = 261 ≡ 5) and break the alignment assertion.
    private static SKColor UniqueColorForRow(int absoluteRow) =>
        new(
            (byte)((absoluteRow % 13) * 19),
            (byte)(((absoluteRow / 13) % 13) * 19),
            (byte)(((absoluteRow / 169) % 13) * 19),
            255);

    private static SKBitmap MakeFrame(int width, int height, int sourceOffset)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int y = 0; y < height; y++)
        {
            var color = ColorForRow(sourceOffset + y);
            for (int x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, color);
            }
        }

        return bmp;
    }

    [Fact]
    public void FindVerticalOverlap_DetectsScrollShift()
    {
        using var previous = MakeFrame(10, 20, sourceOffset: 0);
        using var next = MakeFrame(10, 20, sourceOffset: 5); // scrolled down 5px

        Assert.Equal(15, ScrollStitcher.FindVerticalOverlap(previous, next));
    }

    [Fact]
    public void FindVerticalOverlap_IdenticalFrames_ReturnsHeight()
    {
        using var a = MakeFrame(10, 20, sourceOffset: 0);
        using var b = MakeFrame(10, 20, sourceOffset: 0);

        Assert.Equal(20, ScrollStitcher.FindVerticalOverlap(a, b));
    }

    [Fact]
    public void FindVerticalOverlap_WithTolerance_IgnoresAFewMismatchedRows()
    {
        using var previous = MakeFrame(10, 20, sourceOffset: 0);
        using var next = MakeFrame(10, 20, sourceOffset: 5); // overlap 15 at scroll of 5
        // Corrupt one row inside the overlap region to simulate a per-frame render diff.
        for (int x = 0; x < 10; x++)
        {
            next.SetPixel(x, 3, new SKColor(1, 2, 3, 255));
        }

        // Exact matching can no longer confirm the 15-row overlap (one row differs).
        Assert.NotEqual(15, ScrollStitcher.FindVerticalOverlap(previous, next));
        // With tolerance the overlap is still detected despite the single mismatched row.
        Assert.Equal(15, ScrollStitcher.FindVerticalOverlap(previous, next, minOverlapRows: 8, ignoreRightColumns: 0, maxRowMismatchRatio: 0.2));
    }

    [Fact]
    public void FindVerticalOverlap_ToleratesSubpixelJitterAcrossEntireOverlap()
    {
        using var previous = MakeFrame(40, 30, sourceOffset: 0);
        using var next = MakeFrame(40, 30, sourceOffset: 6); // scrolled down 6px -> overlap 24

        // Simulate a GPU-composited app (e.g. Discord) re-rendering every row with tiny
        // per-channel differences. Byte-exact hashing would reject every shift; the
        // sampled colour-tolerance matcher should still recover the 24-row overlap.
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                var c = next.GetPixel(x, y);
                byte jitter = (byte)((x + y) % 5); // 0..4, well under the colour tolerance
                next.SetPixel(x, y, new SKColor(
                    (byte)Math.Min(255, c.Red + jitter),
                    (byte)Math.Min(255, c.Green + jitter),
                    (byte)Math.Min(255, c.Blue + jitter),
                    255));
            }
        }

        Assert.Equal(24, ScrollStitcher.FindVerticalOverlap(previous, next, minOverlapRows: 8));
    }

    [Fact]
    public void FindVerticalOverlap_DisjointFrames_ReturnsZero()
    {
        using var previous = MakeFrame(10, 20, sourceOffset: 0);
        using var next = MakeFrame(10, 20, sourceOffset: 100); // no shared rows

        Assert.Equal(0, ScrollStitcher.FindVerticalOverlap(previous, next));
    }

    [Fact]
    public void FindVerticalOverlap_DifferentSizes_ReturnsZero()
    {
        using var previous = MakeFrame(10, 20, sourceOffset: 0);
        using var next = MakeFrame(12, 20, sourceOffset: 0);

        Assert.Equal(0, ScrollStitcher.FindVerticalOverlap(previous, next));
    }

    [Fact]
    public void FindVerticalShift_DownwardScroll_ReturnsPositiveRows()
    {
        using var previous = MakeFrame(20, 30, sourceOffset: 0);
        using var next = MakeFrame(20, 30, sourceOffset: 5); // scrolled down -> new content at bottom

        var shift = ScrollStitcher.FindVerticalShift(previous, next, minOverlapRows: 8);

        Assert.True(shift.Found);
        Assert.Equal(5, shift.Rows);
    }

    [Fact]
    public void FindVerticalShift_UpwardScroll_ReturnsNegativeRows()
    {
        using var previous = MakeFrame(20, 30, sourceOffset: 5);
        using var next = MakeFrame(20, 30, sourceOffset: 0); // scrolled up -> new content at top

        var shift = ScrollStitcher.FindVerticalShift(previous, next, minOverlapRows: 8);

        Assert.True(shift.Found);
        Assert.Equal(-5, shift.Rows);
    }

    [Fact]
    public void FindVerticalShift_DisjointFrames_NotFound()
    {
        using var previous = MakeFrame(20, 30, sourceOffset: 0);
        using var next = MakeFrame(20, 30, sourceOffset: 100);

        var shift = ScrollStitcher.FindVerticalShift(previous, next, minOverlapRows: 8);

        Assert.False(shift.Found);
    }

    [Fact]
    public void FindVerticalShift_AmbiguousPeriodicContent_NotFound()
    {
        // A vertically periodic pattern aligns equally well at several different shifts, so
        // the true scroll cannot be told apart from a wrong one: it must be rejected rather
        // than stitched at a guessed offset (this is what scrambled the Discord captures).
        static SKColor Periodic(int row)
        {
            byte v = (byte)((row % 4) * 60);
            return new SKColor(v, v, v, 255);
        }

        static SKBitmap MakePeriodic(int w, int h, int off)
        {
            var b = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            for (int y = 0; y < h; y++)
            {
                var c = Periodic(off + y);
                for (int x = 0; x < w; x++)
                {
                    b.SetPixel(x, y, c);
                }
            }

            return b;
        }

        using var previous = MakePeriodic(20, 40, off: 0);
        using var next = MakePeriodic(20, 40, off: 2); // also aligns at -2, +6, ... (period 4)

        var shift = ScrollStitcher.FindVerticalShift(previous, next, minOverlapRows: 8, maxRowMismatchRatio: 0.2);

        Assert.False(shift.Found);
    }

    [Fact]
    public void Prepend_AddsNewRowsAtTop()
    {
        using var accumulated = MakeFrame(10, 20, sourceOffset: 5); // rows 5..24 captured so far
        using var next = MakeFrame(10, 20, sourceOffset: 0);        // scrolled up to reveal rows 0..4

        using var result = ScrollStitcher.Prepend(accumulated, next, newRows: 5);

        Assert.Equal(25, result.Height);
        Assert.Equal(ColorForRow(0), result.GetPixel(0, 0)); // new content at the very top
        Assert.Equal(ColorForRow(5), result.GetPixel(0, 5)); // original accumulated top preserved below it
    }

    [Fact]
    public void Append_AddsOnlyNewRows()
    {
        using var accumulated = MakeFrame(10, 20, sourceOffset: 0);
        using var next = MakeFrame(10, 20, sourceOffset: 5);
        int overlap = ScrollStitcher.FindVerticalOverlap(accumulated, next); // 15

        using var result = ScrollStitcher.Append(accumulated, next, overlap);

        Assert.Equal(10, result.Width);
        Assert.Equal(25, result.Height); // 20 + (20 - 15)
        Assert.Equal(ColorForRow(0), result.GetPixel(0, 0));    // original top preserved
        Assert.Equal(ColorForRow(24), result.GetPixel(0, 24));  // appended bottom == new content
    }

    [Fact]
    public void Append_NoNewContent_KeepsSameHeight()
    {
        using var accumulated = MakeFrame(10, 20, sourceOffset: 0);
        using var next = MakeFrame(10, 20, sourceOffset: 0);

        using var result = ScrollStitcher.Append(accumulated, next, overlapRows: 20);

        Assert.Equal(20, result.Height);
    }

    [Fact]
    public void AlignFrameToStrip_FrameFullyInside_ReturnsOffset()
    {
        using var strip = MakeFrame(20, 60, sourceOffset: 0);   // rows 0..59
        using var frame = MakeFrame(20, 20, sourceOffset: 25);  // window showing rows 25..44 (inside)

        var align = ScrollStitcher.AlignFrameToStrip(strip, frame);

        Assert.True(align.Found);
        Assert.Equal(25, align.Offset);
    }

    [Fact]
    public void AlignFrameToStrip_FrameOverhangsBottom_ReturnsOffset()
    {
        using var strip = MakeFrame(20, 40, sourceOffset: 0);   // rows 0..39
        using var frame = MakeFrame(20, 20, sourceOffset: 30);  // rows 30..49 -> overlaps bottom, 10 new rows

        var align = ScrollStitcher.AlignFrameToStrip(strip, frame);

        Assert.True(align.Found);
        Assert.Equal(30, align.Offset); // 30+20 > 40 -> overhangs bottom
    }

    [Fact]
    public void AlignFrameToStrip_FrameOverhangsTop_ReturnsNegativeOffset()
    {
        using var strip = MakeFrame(20, 40, sourceOffset: 10);  // rows 10..49
        using var frame = MakeFrame(20, 20, sourceOffset: 0);   // rows 0..19 -> overlaps top, 10 new rows

        var align = ScrollStitcher.AlignFrameToStrip(strip, frame);

        Assert.True(align.Found);
        Assert.Equal(-10, align.Offset); // frame's row 0 sits 10 rows above the strip top
    }

    [Fact]
    public void AlignFrameToStrip_DisjointFrame_NotFound()
    {
        using var strip = MakeFrame(20, 60, sourceOffset: 0);    // rows 0..59
        using var frame = MakeFrame(20, 20, sourceOffset: 200);  // content not in the strip

        var align = ScrollStitcher.AlignFrameToStrip(strip, frame);

        Assert.False(align.Found);
    }

    // A wide strip that is uniform except a narrow vertical band of per-row detail: the shape a
    // horizontal scroll of a low-texture region takes in stitch space (the matcher works in stitch
    // space, so this is the analogue of the user-reported horizontal failure). The discriminative
    // signal is sparse, so the runner-up offset scores only slightly worse than the exact overlap.
    // This regressed when the strict ambiguity margin rejected the weak-but-clear winner outright,
    // capturing nothing; the near-perfect relaxation must accept it.
    private static SKBitmap MakeBandedFrame(int width, int height, int sourceOffset, int bandStart, int bandWidth)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var gray = new SKColor(120, 120, 120, 255);
        for (int y = 0; y < height; y++)
        {
            SKColor detail = UniqueColorForRow(sourceOffset + y);
            for (int x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, (x >= bandStart && x < bandStart + bandWidth) ? detail : gray);
            }
        }

        return bmp;
    }

    [Fact]
    public void AlignFrameToStrip_SparseHorizontalTexture_StillAligns()
    {
        using var strip = MakeBandedFrame(600, 100, sourceOffset: 0, bandStart: 300, bandWidth: 20);
        using var frame = MakeBandedFrame(600, 100, sourceOffset: 5, bandStart: 300, bandWidth: 20);

        var align = ScrollStitcher.AlignFrameToStrip(strip, frame, minOverlapRows: 8);

        Assert.True(align.Found);
        Assert.Equal(5, align.Offset); // exact overlap, 5 new rows at the bottom
    }

    [Fact]
    public void AlignFrameToStrip_PeriodicContent_NotFound()
    {
        // Vertically periodic content aligns equally well at several offsets, so the runner-up scores
        // as well as the winner (gap ~ 0). The relaxed near-perfect margin must NOT accept it — only a
        // genuine, clearly-separated winner should stitch.
        static SKBitmap MakePeriodic(int width, int height, int period, int sourceOffset)
        {
            var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            for (int y = 0; y < height; y++)
            {
                byte v = (byte)(((sourceOffset + y) % period) * 50);
                var c = new SKColor(v, v, v, 255);
                for (int x = 0; x < width; x++)
                {
                    bmp.SetPixel(x, y, c);
                }
            }

            return bmp;
        }

        using var strip = MakePeriodic(40, 60, period: 5, sourceOffset: 0);
        using var frame = MakePeriodic(40, 60, period: 5, sourceOffset: 2);

        var align = ScrollStitcher.AlignFrameToStrip(strip, frame, minOverlapRows: 8);

        Assert.False(align.Found);
    }

    // Each source column maps to a deterministic color (rows are uniform), so a horizontal scroll
    // — the columns shifting — is the left/right analogue of MakeFrame's vertical case.
    private static SKBitmap MakeColumnFrame(int width, int height, int sourceOffset)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int x = 0; x < width; x++)
        {
            var color = ColorForRow(sourceOffset + x);
            for (int y = 0; y < height; y++)
            {
                bmp.SetPixel(x, y, color);
            }
        }

        return bmp;
    }

    private static SKBitmap MakePattern(int width, int height)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, new SKColor((byte)(x * 30), (byte)(y * 40), (byte)((x + y) * 20), 255));
            }
        }

        return bmp;
    }

    [Fact]
    public void RotateCw90_SwapsDimensionsAndMapsTopRowToRightColumn()
    {
        using var src = MakePattern(4, 3); // width 4, height 3
        using var cw = ScrollStitcher.RotateCw90(src);

        Assert.Equal(3, cw.Width);  // = src.Height
        Assert.Equal(4, cw.Height); // = src.Width
        // CW: source top-left (0,0) lands at the result's top-right (col = height-1 = 2, row 0).
        Assert.Equal(src.GetPixel(0, 0), cw.GetPixel(2, 0));
    }

    [Fact]
    public void RotateCcw90_MapsBottomRowToRightColumn()
    {
        using var src = MakePattern(4, 3); // width 4, height 3
        using var ccw = ScrollStitcher.RotateCcw90(src);

        Assert.Equal(3, ccw.Width);
        Assert.Equal(4, ccw.Height);
        // CCW: the source's bottom row (r = height-1 = 2) maps to the result's right column
        // (col = height-1 = 2). Bottom-right (3,2) lands at the top of that column (2,0);
        // bottom-left (0,2) lands at its bottom (2,3).
        Assert.Equal(src.GetPixel(3, 2), ccw.GetPixel(2, 0));
        Assert.Equal(src.GetPixel(0, 2), ccw.GetPixel(2, 3));
    }

    [Fact]
    public void RotateCw90_ThenCcw90_RoundTripsToOriginal()
    {
        using var src = MakePattern(7, 5);
        using var cw = ScrollStitcher.RotateCw90(src);
        using var back = ScrollStitcher.RotateCcw90(cw);

        Assert.Equal(src.Width, back.Width);
        Assert.Equal(src.Height, back.Height);
        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                Assert.Equal(src.GetPixel(x, y), back.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void HorizontalScroll_StitchesViaRotation()
    {
        // Two frames of a horizontally-scrollable view: prev shows source columns 0..19, next is
        // scrolled right by 5 (columns 5..24). Folding through CCW rotation lets the existing
        // vertical alignment + grow logic stitch them; rotating back (CW) yields the wide strip.
        using var prev = MakeColumnFrame(20, 10, sourceOffset: 0);
        using var next = MakeColumnFrame(20, 10, sourceOffset: 5);

        using var accRotated = ScrollStitcher.RotateCcw90(prev);
        using var frameRotated = ScrollStitcher.RotateCcw90(next);

        var align = ScrollStitcher.AlignFrameToStrip(accRotated, frameRotated, minOverlapRows: 4);
        Assert.True(align.Found);

        using SKBitmap grownRotated = align.Offset < 0
            ? ScrollStitcher.Prepend(accRotated, frameRotated, -align.Offset)
            : ScrollStitcher.Append(accRotated, frameRotated, accRotated.Height - align.Offset);
        using SKBitmap result = ScrollStitcher.RotateCw90(grownRotated);

        Assert.Equal(25, result.Width); // 20 + 5 new columns
        Assert.Equal(10, result.Height);
        Assert.Equal(ColorForRow(0), result.GetPixel(0, 0));    // original left edge (source col 0)
        Assert.Equal(ColorForRow(24), result.GetPixel(24, 0));  // new right edge (source col 24)
        Assert.Equal(ColorForRow(12), result.GetPixel(12, 5));  // interior column preserved
    }
}
