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

    // Frames with nothing in them have no shift that stands out from the others. Every row being the same in both, the
    // view has not moved: "no shift found" for a view at rest lost a capture its place.
    [Fact]
    public void FindVerticalShift_FramesWithNothingInThem_HaveNotMoved()
    {
        using var previous = new SKBitmap(new SKImageInfo(120, 90, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var next = new SKBitmap(new SKImageInfo(120, 90, SKColorType.Bgra8888, SKAlphaType.Premul));
        previous.Erase(new SKColor(34, 34, 34));
        next.Erase(new SKColor(34, 34, 34));

        var shift = ScrollStitcher.FindVerticalShift(previous, next, minOverlapRows: 8);

        Assert.True(shift.Found);
        Assert.Equal(0, shift.Rows);
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

    // The row signatures are pooled. A 900-row frame's came to 115 KB, on the large object heap, made anew two to four
    // times for every frame of a manual scroll (the frame's, the strip's, and both rotated before the axis is known).
    [Fact]
    public void AligningAFrame_SetsAsideNoRowSignatures()
    {
        using var strip = MakeUniqueFrame(64, 1500, sourceOffset: 0);
        using var frame = MakeUniqueFrame(64, 900, sourceOffset: 600);
        Assert.True(ScrollStitcher.AlignFrameToStrip(strip, frame).Found); // the pool's first rent allocates

        long before = GC.GetAllocatedBytesForCurrentThread();
        var align = ScrollStitcher.AlignFrameToStrip(strip, frame);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(align.Found);
        Assert.Equal(600, align.Offset);
        // The frame's signatures alone were 115,200 bytes and the strip's 192,000.
        Assert.True(allocated < 64 * 1024, $"aligning one frame set aside {allocated} bytes");
    }

    private static SKBitmap MakeUniqueFrame(int width, int height, int sourceOffset)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int y = 0; y < height; y++)
        {
            var color = UniqueColorForRow(sourceOffset + y);
            for (int x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, color);
            }
        }

        return bmp;
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

    // A frame of unique rows from sourceOffset, with `bar` rows of a textured, fixed bar drawn over its top or bottom: what
    // a window with a header bar or a message box shows while its content scrolls.
    private static SKBitmap MakeFrameWithBar(int width, int height, int sourceOffset, int bar, bool atTop)
    {
        SKBitmap bmp = MakeUniqueFrame(width, height, sourceOffset);
        int first = atTop ? 0 : height - bar;
        for (int y = first; y < first + bar; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Slanted marks: every row has edges and differs from the bar's other rows (they repeat every 11
                // rows), the same in every frame.
                bool dark = ((x * 7) + ((y - first) * 13)) % 11 < 4;
                byte v = dark ? (byte)40 : (byte)200;
                bmp.SetPixel(x, y, new SKColor(v, v, (byte)(255 - v), 255));
            }
        }

        return bmp;
    }

    [Fact]
    public void FindFixedBands_FindsABarAtTheTop_ThatStaysPutWhileTheContentScrolls()
    {
        using var previous = MakeFrameWithBar(64, 200, sourceOffset: 0, bar: 30, atTop: true);
        using var next = MakeFrameWithBar(64, 200, sourceOffset: 20, bar: 30, atTop: true);

        (int top, int bottom) = ScrollStitcher.FindFixedBands(previous, next, shift: 20, ignoreRightColumns: 0, maxBandRows: 60);

        Assert.Equal(30, top);
        Assert.Equal(0, bottom);
    }

    [Fact]
    public void FindFixedBands_FindsABarAtTheBottom_ScrollingEitherWay()
    {
        using var lower = MakeFrameWithBar(64, 200, sourceOffset: 40, bar: 24, atTop: false);
        using var upper = MakeFrameWithBar(64, 200, sourceOffset: 10, bar: 24, atTop: false);

        Assert.Equal((0, 24), ScrollStitcher.FindFixedBands(upper, lower, shift: 30, ignoreRightColumns: 0, maxBandRows: 60));
        Assert.Equal((0, 24), ScrollStitcher.FindFixedBands(lower, upper, shift: -30, ignoreRightColumns: 0, maxBandRows: 60));
    }

    [Fact]
    public void FindFixedBands_FindsNothing_WhenEverythingScrolls()
    {
        using var previous = MakeUniqueFrame(64, 200, sourceOffset: 0);
        using var next = MakeUniqueFrame(64, 200, sourceOffset: 20);

        Assert.Equal((0, 0), ScrollStitcher.FindFixedBands(previous, next, shift: 20, ignoreRightColumns: 0, maxBandRows: 60));
    }

    // Featureless rows look the same before and after any scroll, so on their own they prove no bar is there.
    [Fact]
    public void FindFixedBands_TakesNoBand_FromFeaturelessRowsAlone()
    {
        using var previous = MakeUniqueFrame(64, 200, sourceOffset: 0);
        using var next = MakeUniqueFrame(64, 200, sourceOffset: 20);
        foreach (SKBitmap frame in new[] { previous, next })
        {
            for (int y = 0; y < 40; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    frame.SetPixel(x, y, new SKColor(30, 30, 30, 255));
                }
            }
        }

        Assert.Equal((0, 0), ScrollStitcher.FindFixedBands(previous, next, shift: 20, ignoreRightColumns: 0, maxBandRows: 60));
    }

    [Fact]
    public void FindFixedBands_KeepsABandWithinItsLimit()
    {
        using var previous = MakeFrameWithBar(64, 200, sourceOffset: 0, bar: 90, atTop: true);
        using var next = MakeFrameWithBar(64, 200, sourceOffset: 20, bar: 90, atTop: true);

        Assert.Equal(60, ScrollStitcher.FindFixedBands(previous, next, shift: 20, ignoreRightColumns: 0, maxBandRows: 60).Top);
    }

    // A strip whose rows 300-399 repeat rows 50-149: a frame showing those rows matches both places exactly.
    private static SKBitmap MakeStripWithRepeat()
    {
        var bmp = new SKBitmap(new SKImageInfo(64, 400, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int y = 0; y < 400; y++)
        {
            SKColor color = UniqueColorForRow(y >= 300 ? y - 250 : y);
            for (int x = 0; x < 64; x++)
            {
                bmp.SetPixel(x, y, color);
            }
        }

        return bmp;
    }

    [Fact]
    public void AlignNear_PlacesAFrameWhereThePredictionIs_ThoughTheSameContentMatchesElsewhere()
    {
        using SKBitmap stripBitmap = MakeStripWithRepeat();
        using var strip = new ManualScrollStrip(stripBitmap, ignoreRightColumns: 0);
        using var frame = MakeUniqueFrame(64, 100, sourceOffset: 50);

        // Searching the whole strip cannot tell the two places apart and gives up.
        Assert.False(strip.Align(frame, 8, 0.35, maxScore: 0.5, out _, out _).Found);

        ScrollStitcher.FrameAlignment nearRepeat = strip.AlignNear(frame, 302, 10, 8, 0.35, 0.5, out _, out bool inReach);
        ScrollStitcher.FrameAlignment nearFirst = strip.AlignNear(frame, 47, 10, 8, 0.35, 0.5, out _, out _);

        Assert.True(inReach);
        Assert.Equal((300, true), (nearRepeat.Offset, nearRepeat.Found));
        Assert.Equal((50, true), (nearFirst.Offset, nearFirst.Found));
    }

    [Fact]
    public void AlignNear_DoesNotPlaceAFrame_WhereItDoesNotMatch()
    {
        using var stripBitmap = MakeUniqueFrame(64, 400, sourceOffset: 0);
        using var strip = new ManualScrollStrip(stripBitmap, ignoreRightColumns: 0);
        using var frame = MakeUniqueFrame(64, 100, sourceOffset: 150);

        ScrollStitcher.FrameAlignment wrong = strip.AlignNear(frame, 60, 10, 8, 0.35, 0.5, out _, out bool inReach);

        Assert.True(inReach);
        Assert.False(wrong.Found);
    }

    // Past the strip's edge there is nothing to meet: not found, and not in reach (the view has run ahead of the strip).
    [Fact]
    public void AlignNear_PastTheStripsEdge_IsOutOfReach()
    {
        using var stripBitmap = MakeUniqueFrame(64, 400, sourceOffset: 0);
        using var strip = new ManualScrollStrip(stripBitmap, ignoreRightColumns: 0);
        using var frame = MakeUniqueFrame(64, 100, sourceOffset: 600);

        ScrollStitcher.FrameAlignment beyond = strip.AlignNear(frame, 600, 10, 8, 0.35, 0.5, out _, out bool inReach);

        Assert.False(beyond.Found);
        Assert.False(inReach);
    }

    // A bounded search scores offsets near each edge. Once the strip grows past ~2x the frame height
    // those two windows stop meeting, and an alignment landing between them used to be scored by
    // NOTHING — reported as "no match" even though it was the correct placement. A real scrolling
    // capture hits this constantly: the strip quickly outgrows the frame, so an ordinary scroll lands
    // mid-range and the strip silently stops growing.
    [Fact]
    public void AlignFrameToStrip_FindsMidRangeOffset_OnStripTallerThanTwiceTheFrame()
    {
        const int width = 40;
        const int frameHeight = 60;
        const int stripHeight = 400;   // >> 2 * frameHeight, so the edge windows cannot meet
        const int trueOffset = 150;    // sits in the middle: far from both edges

        using var strip = MakeFrame(width, stripHeight, 0);
        using var frame = MakeFrame(width, frameHeight, trueOffset);

        // searchMargin = frame height is exactly what the manual scrolling capture passes.
        var alignment = ScrollStitcher.AlignFrameToStrip(
            strip,
            frame,
            out double bestScore,
            out _,
            out _,
            minOverlapRows: 8,
            ignoreRightColumns: 0,
            maxRowMismatchRatio: 0.0,
            searchMargin: frameHeight);

        Assert.True(alignment.Found, "a mid-range offset must still be scored, not skipped by the search window");
        Assert.Equal(trueOffset, alignment.Offset);
        Assert.Equal(0.0, bestScore, 6); // identical rows overlap exactly
    }
}
