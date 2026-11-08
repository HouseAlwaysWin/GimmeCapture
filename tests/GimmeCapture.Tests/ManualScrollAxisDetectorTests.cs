using System;
using System.Collections.Generic;
using GimmeCapture.Services.Core.Media;
using SkiaSharp;
using static GimmeCapture.Tests.ScrollCaptureSimulation;

namespace GimmeCapture.Tests;

// The Auto direction of a manual scrolling capture, replayed on simulated frames (ScrollCaptureSimulation). A user's
// LINE capture locked sideways and placed none of its 674 frames; in the simulation the old way did the same with a
// LINE chat scrolled up under its title bar and floating date, and never locked with a message box as well
// (ScrollCaptureSimulationProbeTests.AxisReport compares the two ways).
public class ManualScrollAxisDetectorTests
{
    private const double Tolerance = 0.35; // SnipWindowViewModel.ManualRowMismatchTolerance
    private const int RegionHeight = 731; // the user's LINE region that locked sideways was 545 x 731

    [Fact]
    public void ALineChatScrolledUpUnderItsTitleBarAndDate_LocksDown()
    {
        using Page page = Line(545, 5000, seed: 5);
        List<int> path = WheelPath(3000, 1500, seed: 34, notchesPerFrame: 0.5);

        Assert.Equal(false, Lock(page, path, new Overlays(HeaderRows: 170, DatePill: true), sideways: false));
    }

    [Fact]
    public void ALineChatWithATitleBarAndAMessageBox_LocksDown()
    {
        using Page page = Line(545, 5000, seed: 6);
        List<int> path = WheelPath(0, 1500, seed: 35, notchesPerFrame: 0.5);

        Assert.Equal(false, Lock(page, path, new Overlays(HeaderRows: 170, FooterRows: 60), sideways: false));
    }

    [Fact]
    public void ContentScrolledSideways_LocksSideways()
    {
        using Page page = Line(545, 5000, seed: 9);
        List<int> path = WheelPath(0, 1500, seed: 39, notchesPerFrame: 0.5);

        Assert.Equal(true, Lock(page, path, new Overlays(HeaderRows: 120), sideways: true));
    }

    [Fact]
    public void ContentThatDoesNotMove_LeavesItUndecided()
    {
        using Page page = Chat(400, 2000, seed: 2);
        var path = new List<int> { 300, 300, 302, 299, 300, 301 };

        Assert.Null(Lock(page, path, new Overlays(), sideways: false));
    }

    // One frame is not enough: a sideways match that was only a chance one must not lock the capture by itself.
    [Fact]
    public void TheAxisLocksOnlyWhenTwoFramesInARowAgree()
    {
        using Page page = Chat(400, 2000, seed: 2);
        using SKBitmap first = Frame(page, 0, 300, new Overlays());
        var detector = NewDetector(first);

        using (SKBitmap moved = Frame(page, 40, 300, new Overlays()))
        {
            Assert.Null(detector.Decide(first, moved));
        }

        using SKBitmap movedOn = Frame(page, 80, 300, new Overlays());
        Assert.Equal(false, detector.Decide(first, movedOn));
    }

    private static bool? Lock(Page page, List<int> path, Overlays overlays, bool sideways)
    {
        SKBitmap Grab(int i)
        {
            SKBitmap frame = Frame(page, path[i], RegionHeight, overlays, i);
            return sideways ? Sideways(frame) : frame;
        }

        using SKBitmap first = Grab(0);
        ManualScrollAxisDetector detector = NewDetector(first);
        for (int i = 1; i < path.Count; i++)
        {
            using SKBitmap frame = Grab(i);
            if (detector.Decide(first, frame) is bool axis)
            {
                return axis;
            }
        }

        return null;
    }

    // The parameters SnipWindowViewModel.StartManualScrollCaptureAsync gives a region this size, at 100%.
    private static ManualScrollAxisDetector NewDetector(SKBitmap first) =>
        new(
            Math.Max(8, first.Height / 10),
            Math.Clamp(180, 24, Math.Max(24, first.Width * 35 / 100)),
            Math.Max(8, first.Width / 10),
            Math.Clamp(32, 8, Math.Max(8, first.Height / 8)),
            Tolerance);
}
