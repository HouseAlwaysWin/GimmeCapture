using System.Collections.Generic;
using System.Linq;
using GimmeCapture.Services.Core.Media;
using SkiaSharp;
using static GimmeCapture.Tests.ScrollCaptureSimulation;

namespace GimmeCapture.Tests;

// Replays simulated manual scrolling captures (ScrollCaptureSimulation: a chat page, a scroll path, the frames the
// capture grabs with what the app keeps in place drawn over them) through ManualScrollStitcher, and traces what it kept
// back to page rows. A page row kept twice is the duplication users saw; the stitching before ManualScrollStitcher kept
// a message box again with every frame scrolled down and stopped growing under a header bar (ScrollCaptureSimulationProbeTests,
// SCROLL_SIM=old, compares the two).
public class ManualScrollStitcherTests
{
    private const int Width = 400;
    private const int FrameHeight = 300;
    private const double Tolerance = 0.35; // SnipWindowViewModel.ManualRowMismatchTolerance

    private static readonly int MinOverlap = System.Math.Max(8, FrameHeight / 10);
    private static readonly int IgnoreRight = System.Math.Clamp(180, 24, System.Math.Max(24, Width * 35 / 100));
    private const int EdgeGuard = 48; // SnipWindowViewModel: 48 logical pixels, at 100%

    [Fact]
    public void ScrollingDown_KeepsEveryPageRowOnce()
    {
        using Page page = Chat(Width, 2600, seed: 3);
        List<int> path = WheelPath(0, 2000, seed: 1, notchesPerFrame: 0.6);

        (RowTrace trace, SKBitmap result) = Run(page, path, new Overlays());
        using (result)
        {
            AssertKeptOnce(trace, path);
            Assert.Equal(2000 + FrameHeight, result.Height);
            Assert.Equal(0, trace.DirtyRows(result, page));
        }
    }

    [Fact]
    public void ScrollingUp_KeepsEveryPageRowOnce()
    {
        using Page page = Chat(Width, 2600, seed: 4);
        List<int> path = WheelPath(2000, 0, seed: 2, notchesPerFrame: 0.6);

        (RowTrace trace, SKBitmap result) = Run(page, path, new Overlays());
        using (result)
        {
            AssertKeptOnce(trace, path);
            Assert.Equal(2000 + FrameHeight, result.Height);
            Assert.Equal(0, trace.DirtyRows(result, page));
        }
    }

    // A message box at the bottom of a chat stays put while the messages scroll. Each frame scrolled down added the
    // frame's bottom rows — the box — so it came out repeated, with the messages it covered missing.
    [Fact]
    public void AMessageBoxAtTheBottom_IsShownOnce_ScrollingDown()
    {
        using Page page = Chat(Width, 2600, seed: 5);
        List<int> path = WheelPath(0, 2000, seed: 3, notchesPerFrame: 0.6);
        var overlays = new Overlays(FooterRows: 60);

        (RowTrace trace, SKBitmap result) = Run(page, path, overlays);
        using (result)
        {
            AssertKeptOnce(trace, path);
            Assert.Equal(60, trace.OverlayRows);
        }
    }

    // A header bar is the same for a scroll up: the frame's top rows were added with every frame.
    [Fact]
    public void AHeaderBar_IsShownOnce_ScrollingUp()
    {
        using Page page = Chat(Width, 2600, seed: 6);
        List<int> path = WheelPath(2000, 0, seed: 4, notchesPerFrame: 0.6);
        var overlays = new Overlays(HeaderRows: 48);

        (RowTrace trace, SKBitmap result) = Run(page, path, overlays);
        using (result)
        {
            AssertKeptOnce(trace, path);
            Assert.Equal(48, trace.OverlayRows);
        }
    }

    [Fact]
    public void AHeaderBarAndAMessageBox_AreEachShownOnce()
    {
        using Page page = Chat(Width, 2600, seed: 7);
        List<int> path = WheelPath(0, 2000, seed: 5, notchesPerFrame: 0.6);
        var overlays = new Overlays(HeaderRows: 48, FooterRows: 60);

        (RowTrace trace, SKBitmap result) = Run(page, path, overlays);
        using (result)
        {
            AssertKeptOnce(trace, path);
            Assert.Equal(48 + 60, trace.OverlayRows);
            Assert.Equal(2000 + FrameHeight, result.Height);
        }
    }

    // A bar can appear partway through (Discord's "jump to present"): found then, kept out from then on, and what the
    // frame before added from under it taken back off the strip.
    [Fact]
    public void AMessageBoxThatAppearsPartway_IsShownOnce()
    {
        using Page page = Chat(Width, 2600, seed: 14);
        List<int> path = WheelPath(0, 2000, seed: 13, notchesPerFrame: 0.6);
        var overlays = new Overlays(FooterRows: 60, FooterFromFrame: 12);

        (RowTrace trace, SKBitmap result) = Run(page, path, overlays);
        using (result)
        {
            AssertKeptOnce(trace, path);
            Assert.Equal(60, trace.OverlayRows);
        }
    }

    // Chats repeat themselves word for word — the same short reply, the same sticker — so a frame can match the strip as
    // well somewhere it is not. It is looked for only where the motion from the last frame puts it.
    [Fact]
    public void RepeatedPhrasesAndStickers_AreNotKeptTwice()
    {
        using Page page = Chat(Width, 2600, seed: 8, repeatPercent: 70);
        foreach (int seed in new[] { 11, 12 })
        {
            List<int> path = WheelPath(0, 2000, seed, notchesPerFrame: 0.8);

            (RowTrace trace, SKBitmap result) = Run(page, path, new Overlays());
            using (result)
            {
                AssertKeptOnce(trace, path);
            }
        }
    }

    // A flick too fast for two frames to overlap loses the motion; once the view is brought back over the strip, the
    // whole strip is searched, and nothing is added twice when the scroll carries on.
    [Fact]
    public void AFlickPastTheStripsEdge_IsPickedUpAgainWithoutKeepingAnythingTwice()
    {
        using Page page = Chat(Width, 2600, seed: 9);
        List<int> path = WheelPath(0, 500, seed: 6, notchesPerFrame: 0.6);
        path.AddRange(SteadyPath(500, 1500, FrameHeight + 100).Skip(1));
        path.AddRange(WheelPath(1500, 600, seed: 7, notchesPerFrame: 0.6).Skip(1));
        path.AddRange(WheelPath(600, 2000, seed: 8, notchesPerFrame: 0.6).Skip(1));

        (RowTrace trace, SKBitmap result) = Run(page, path, new Overlays());
        using (result)
        {
            Assert.Equal(0, trace.DuplicatedRows);
            Assert.Equal(0, trace.Breaks);
            Assert.Equal(0, trace.Missing(path));
        }
    }

    // LINE floats the date over the top of a chat while it scrolls. Scrolling up, the new rows come in under it, and the
    // pill was kept with them every few rows down the whole capture. Rows next to the edges are kept only once the
    // content has scrolled past them, so the pill shows once, at the top, where the screen had it.
    [Fact]
    public void AFloatingPillAtTheTop_IsNotKeptAllDownTheStrip()
    {
        using Page page = Chat(Width, 2600, seed: 10);
        List<int> path = WheelPath(2000, 0, seed: 9, notchesPerFrame: 0.6);

        (RowTrace trace, SKBitmap result) = Run(page, path, new Overlays(DatePill: true));
        using (result)
        {
            AssertKeptOnce(trace, path);
            int pillRows = 34; // ScrollCaptureSimulation draws it over frame rows 8-33
            Assert.InRange(trace.DirtyRows(result, page), 1, pillRows);
        }
    }

    [Fact]
    public void BeforeTheContentMoves_TheResultIsTheFirstFrame()
    {
        using Page page = Chat(Width, 2600, seed: 12);
        using SKBitmap seed = Frame(page, 400, FrameHeight, new Overlays());
        using var stitcher = new ManualScrollStitcher(seed, MinOverlap, IgnoreRight, Tolerance, EdgeGuard);

        ManualScrollStep same = stitcher.Add(Frame(page, 400, FrameHeight, new Overlays()));
        ManualScrollStep nudged = stitcher.Add(Frame(page, 403, FrameHeight, new Overlays()));

        Assert.True(same.Placed);
        Assert.True(nudged.Placed);
        Assert.False(stitcher.Started);
        Assert.Equal(FrameHeight, stitcher.Height);
        using SKBitmap result = stitcher.ToBitmap();
        Assert.Equal(seed.GetPixel(100, 150), result.GetPixel(100, 150));
    }

    [Fact]
    public void AFrameOfAnotherSize_IsSkipped()
    {
        using Page page = Chat(Width, 2600, seed: 13);
        using SKBitmap seed = Frame(page, 0, FrameHeight, new Overlays());
        using var stitcher = new ManualScrollStitcher(seed, MinOverlap, IgnoreRight, Tolerance, EdgeGuard);

        ManualScrollStep step = stitcher.Add(new SKBitmap(Width, FrameHeight - 1));

        Assert.False(step.Placed);
        Assert.False(step.Grew);
        Assert.Equal(FrameHeight, stitcher.Height);
    }

    [Fact]
    public void EdgeGuard_LeavesTheStripAtLeast40PercentOfTheFrame()
    {
        Assert.Equal(48, ManualScrollStitcher.EdgeGuardRows(500, fixedRows: 0, wanted: 48));
        Assert.Equal(30, ManualScrollStitcher.EdgeGuardRows(100, fixedRows: 0, wanted: 48));
        Assert.Equal(10, ManualScrollStitcher.EdgeGuardRows(500, fixedRows: 280, wanted: 48));
        Assert.Equal(0, ManualScrollStitcher.EdgeGuardRows(500, fixedRows: 300, wanted: 48));
    }

    private static void AssertKeptOnce(RowTrace trace, List<int> path)
    {
        Assert.Equal(0, trace.DuplicatedRows);
        Assert.Equal(0, trace.Missing(path));
        Assert.Equal(0, trace.Breaks);
    }

    private static (RowTrace Trace, SKBitmap Result) Run(Page page, List<int> tops, Overlays overlays)
    {
        using SKBitmap seed = Frame(page, tops[0], FrameHeight, overlays);
        using var stitcher = new ManualScrollStitcher(seed, MinOverlap, IgnoreRight, Tolerance, EdgeGuard);
        var trace = new RowTrace(FrameHeight, overlays);
        for (int i = 1; i < tops.Count; i++)
        {
            ManualScrollStep step = stitcher.Add(Frame(page, tops[i], FrameHeight, overlays, i));
            if (!trace.Started && stitcher.Started)
            {
                // The strip started with this frame, cut with the bands this step reports.
                trace.Start(tops[0], step.TopBand, step.BottomBand);
            }

            if (trace.Started)
            {
                trace.Apply(step, tops[i], i, tops[i - 1], i - 1);
            }
        }

        Assert.True(trace.Started, "the content moved, so the strip should have started");
        return (trace, stitcher.ToBitmap());
    }
}
