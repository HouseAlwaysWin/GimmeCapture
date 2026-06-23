using SkiaSharp;

namespace GimmeCapture.Tests;

public class ScrollStitcherTests
{
    // Each source row maps to a deterministic color, so a "frame" showing source rows
    // [offset, offset+height) shares identical rows with another frame wherever their
    // source ranges overlap — exactly the situation a vertical scroll produces.
    private static SKColor ColorForRow(int absoluteRow) =>
        new((byte)((absoluteRow * 7) & 0xFF), (byte)((absoluteRow * 13) & 0xFF), (byte)((absoluteRow * 29) & 0xFF), 255);

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
    public void IsAcceptableStep_NotFound_IsRejected()
    {
        var shift = new ScrollStitcher.VerticalShift(0, Found: false);
        Assert.False(ScrollStitcher.IsAcceptableStep(shift, height: 100, maxStepFraction: 0.55, minNewRows: 2));
    }

    [Fact]
    public void IsAcceptableStep_BelowMinNewRows_IsRejected()
    {
        var shift = new ScrollStitcher.VerticalShift(1, Found: true);
        Assert.False(ScrollStitcher.IsAcceptableStep(shift, height: 100, maxStepFraction: 0.55, minNewRows: 2));
    }

    [Fact]
    public void IsAcceptableStep_AboveMaxFraction_IsRejected()
    {
        // 60 > 0.55 * 100 == 55 -> the user scrolled too far between frames.
        var shift = new ScrollStitcher.VerticalShift(60, Found: true);
        Assert.False(ScrollStitcher.IsAcceptableStep(shift, height: 100, maxStepFraction: 0.55, minNewRows: 2));
    }

    [Fact]
    public void IsAcceptableStep_WithinBounds_IsAccepted()
    {
        var down = new ScrollStitcher.VerticalShift(30, Found: true);
        var up = new ScrollStitcher.VerticalShift(-30, Found: true); // upward scroll also acceptable
        Assert.True(ScrollStitcher.IsAcceptableStep(down, height: 100, maxStepFraction: 0.55, minNewRows: 2));
        Assert.True(ScrollStitcher.IsAcceptableStep(up, height: 100, maxStepFraction: 0.55, minNewRows: 2));
    }

    [Fact]
    public void IsAcceptableStep_ExactlyAtFractionBoundary_IsAccepted()
    {
        // 55 == 0.55 * 100 -> boundary is inclusive.
        var shift = new ScrollStitcher.VerticalShift(55, Found: true);
        Assert.True(ScrollStitcher.IsAcceptableStep(shift, height: 100, maxStepFraction: 0.55, minNewRows: 2));
    }
}
