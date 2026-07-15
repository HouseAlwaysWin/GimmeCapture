using GimmeCapture.Services.Core.Media;
using SkiaSharp;

namespace GimmeCapture.Tests;

// Proves the incremental ManualScrollStrip (cached signatures + geometric-capacity growth) is
// behaviourally identical to the old per-frame ScrollStitcher.AlignFrameToStrip + Append/Prepend chain:
// same alignment decisions, and a pixel-for-pixel identical accumulated strip. This is the safety net
// for a change that is otherwise only exercised by live scrolling capture on Windows.
public class ManualScrollStripTests
{
    // Collision-free per-row colour: the row index as three base-13 digits scaled by 19, so any two
    // distinct rows in 0..2196 differ by > ColorTolerance (18) in at least one channel and align uniquely.
    private static SKColor UniqueColorForRow(int absoluteRow) =>
        new(
            (byte)((absoluteRow % 13) * 19),
            (byte)(((absoluteRow / 13) % 13) * 19),
            (byte)(((absoluteRow / 169) % 13) * 19),
            255);

    // A "frame" showing source rows [sourceOffset, sourceOffset+height) — overlapping frames share rows,
    // exactly like a vertical scroll.
    private static SKBitmap MakeFrame(int width, int height, int sourceOffset)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int y = 0; y < height; y++)
        {
            SKColor color = UniqueColorForRow(sourceOffset + y);
            for (int x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, color);
            }
        }

        return bmp;
    }

    private static void AssertPixelIdentical(SKBitmap expected, SKBitmap actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
            }
        }
    }

    // Runs a frame sequence through BOTH the old static chain and the new strip, mirroring the driver's
    // ApplyStitchFrame logic, and asserts identical per-frame decisions + identical final pixels.
    private static void AssertEquivalent(int width, int frameHeight, int[] sourceOffsets, int minOverlap, int ignoreRight)
    {
        const double tol = 0.35; // ManualRowMismatchTolerance

        var frames = new SKBitmap[sourceOffsets.Length];
        for (int i = 0; i < sourceOffsets.Length; i++)
        {
            frames[i] = MakeFrame(width, frameHeight, sourceOffsets[i]);
        }

        try
        {
            // --- OLD reference path: iterative Append/Prepend on a plain bitmap ---
            SKBitmap acc = frames[0].Copy();
            var oldDecisions = new (bool Found, int Offset)[frames.Length];
            for (int i = 1; i < frames.Length; i++)
            {
                SKBitmap frame = frames[i];
                int h = frame.Height;
                ScrollStitcher.FrameAlignment align = ScrollStitcher.AlignFrameToStrip(
                    acc, frame, out _, out _, out _, minOverlap, ignoreRight, tol, h);
                oldDecisions[i] = (align.Found, align.Offset);
                if (align.Found)
                {
                    if (align.Offset < 0)
                    {
                        SKBitmap grown = ScrollStitcher.Prepend(acc, frame, -align.Offset);
                        acc.Dispose();
                        acc = grown;
                    }
                    else if (align.Offset + h > acc.Height)
                    {
                        SKBitmap grown = ScrollStitcher.Append(acc, frame, acc.Height - align.Offset);
                        acc.Dispose();
                        acc = grown;
                    }
                }
            }

            // --- NEW path: ManualScrollStrip ---
            using var strip = new ManualScrollStrip(frames[0], ignoreRight);
            var newDecisions = new (bool Found, int Offset)[frames.Length];
            for (int i = 1; i < frames.Length; i++)
            {
                SKBitmap frame = frames[i];
                int h = frame.Height;
                ScrollStitcher.FrameAlignment align = strip.Align(frame, minOverlap, tol, h, out _, out _, out _);
                newDecisions[i] = (align.Found, align.Offset);
                if (align.Found)
                {
                    int stripH = strip.Height;
                    if (align.Offset < 0)
                    {
                        strip.Prepend(frame, -align.Offset);
                    }
                    else if (align.Offset + h > stripH)
                    {
                        strip.Append(frame, align.Offset + h - stripH);
                    }
                }
            }

            for (int i = 1; i < frames.Length; i++)
            {
                Assert.Equal(oldDecisions[i], newDecisions[i]);
            }

            using SKBitmap newStrip = strip.ToLogicalBitmap();
            AssertPixelIdentical(acc, newStrip);
            acc.Dispose();
        }
        finally
        {
            foreach (SKBitmap f in frames)
            {
                f.Dispose();
            }
        }
    }

    [Fact]
    public void MatchesIterativeChain_DownwardScroll()
    {
        // Scroll down: each frame reveals rows further down; new content appends at the bottom.
        int[] offsets = { 0, 7, 15, 22, 30, 41, 48, 55, 66, 74 };
        AssertEquivalent(width: 40, frameHeight: 60, offsets, minOverlap: 8, ignoreRight: 0);
    }

    [Fact]
    public void MatchesIterativeChain_UpwardScroll()
    {
        // Scroll up: each frame reveals rows further up; new content prepends at the top.
        int[] offsets = { 80, 72, 65, 54, 47, 38, 30, 19, 11, 0 };
        AssertEquivalent(width: 40, frameHeight: 60, offsets, minOverlap: 8, ignoreRight: 0);
    }

    [Fact]
    public void MatchesIterativeChain_MixedScrollWithIgnoreRight()
    {
        // Down then back up a bit then down again, with a right-edge exclusion band (as the driver uses).
        int[] offsets = { 0, 9, 18, 12, 21, 33, 27, 40, 52, 61, 70 };
        AssertEquivalent(width: 80, frameHeight: 50, offsets, minOverlap: 8, ignoreRight: 20);
    }

    [Fact]
    public void ManyAppends_GrowCapacity_CorrectHeightAndEdges()
    {
        // Enough appends to force several buffer reallocations; the final strip must be exact.
        int width = 30, frameH = 40, step = 5, count = 60;
        var frames = new SKBitmap[count];
        for (int i = 0; i < count; i++)
        {
            frames[i] = MakeFrame(width, frameH, i * step);
        }

        try
        {
            using var strip = new ManualScrollStrip(frames[0], ignoreRightColumns: 0);
            for (int i = 1; i < count; i++)
            {
                int h = frames[i].Height;
                ScrollStitcher.FrameAlignment align = strip.Align(frames[i], 8, 0.35, h, out _, out _, out _);
                Assert.True(align.Found);
                int stripH = strip.Height;
                Assert.True(align.Offset + h > stripH); // pure downward scroll always appends
                strip.Append(frames[i], align.Offset + h - stripH);
            }

            int expectedHeight = frameH + (count - 1) * step; // 40 + 59*5
            Assert.Equal(expectedHeight, strip.Height);

            using SKBitmap final = strip.ToLogicalBitmap();
            Assert.Equal(expectedHeight, final.Height);
            Assert.Equal(UniqueColorForRow(0), final.GetPixel(0, 0));                    // original top
            Assert.Equal(UniqueColorForRow(expectedHeight - 1), final.GetPixel(0, expectedHeight - 1)); // newest bottom
            Assert.Equal(UniqueColorForRow(100), final.GetPixel(width - 1, 100));        // interior row preserved
        }
        finally
        {
            foreach (SKBitmap f in frames)
            {
                f.Dispose();
            }
        }
    }

    [Fact]
    public void CachedSignatures_MatchFreshCompute_ForAlignment()
    {
        // The cached strip signatures must produce the exact same alignment as re-sampling the assembled
        // strip fresh: aligning a probe against the strip (cached) vs against its ToLogicalBitmap (fresh).
        int width = 50, frameH = 40, ignoreRight = 12;
        var frames = new SKBitmap[6];
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = MakeFrame(width, frameH, i * 8);
        }

        using var probe = MakeFrame(width, frameH, sourceOffset: 5 * 8 + 6); // overlaps the strip's new bottom

        try
        {
            using var strip = new ManualScrollStrip(frames[0], ignoreRight);
            for (int i = 1; i < frames.Length; i++)
            {
                int h = frames[i].Height;
                ScrollStitcher.FrameAlignment a = strip.Align(frames[i], 8, 0.35, h, out _, out _, out _);
                if (a.Found && a.Offset + h > strip.Height)
                {
                    strip.Append(frames[i], a.Offset + h - strip.Height);
                }
            }

            ScrollStitcher.FrameAlignment cached = strip.Align(
                probe, 8, 0.35, probe.Height, out double cachedBest, out _, out _);

            using SKBitmap logical = strip.ToLogicalBitmap();
            ScrollStitcher.FrameAlignment fresh = ScrollStitcher.AlignFrameToStrip(
                logical, probe, out double freshBest, out _, out _, 8, ignoreRight, 0.35, probe.Height);

            Assert.Equal(fresh.Found, cached.Found);
            Assert.Equal(fresh.Offset, cached.Offset);
            Assert.Equal(freshBest, cachedBest, 10);
        }
        finally
        {
            foreach (SKBitmap f in frames)
            {
                f.Dispose();
            }
        }
    }

    [Fact]
    public void ToLogicalBitmap_SeedOnly_EqualsSeed()
    {
        using var seed = MakeFrame(24, 30, sourceOffset: 3);
        using var strip = new ManualScrollStrip(seed, ignoreRightColumns: 0);

        Assert.Equal(30, strip.Height);
        using SKBitmap logical = strip.ToLogicalBitmap();
        AssertPixelIdentical(seed, logical);
    }
}
