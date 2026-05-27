using System;
using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Platforms.Windows;

namespace GimmeCapture.Tests;

public class WindowDetectionServiceTests
{
    private static readonly IntPtr RootHwnd = new(100);
    private static readonly IntPtr ChildHwnd = new(200);

    [Fact]
    public void GetCandidateAtPoint_PrefersChildCandidateWithinSameTree()
    {
        var service = new WindowDetectionService();
        var parent = new WindowCandidate(new Rect(0, 0, 300, 200), RootHwnd, IntPtr.Zero, RootHwnd, 0, WindowCandidateKind.TopLevel);
        var child = new WindowCandidate(new Rect(40, 40, 120, 80), ChildHwnd, RootHwnd, RootHwnd, 1, WindowCandidateKind.ChildControl);

        var result = service.GetCandidateAtPoint(new Point(80, 80), [parent, child]);

        Assert.NotNull(result);
        Assert.Equal(ChildHwnd, result!.Hwnd);
    }

    [Fact]
    public void GetCandidateAtPoint_HoldsPreviousCandidateNearChildBoundary()
    {
        var service = new WindowDetectionService();
        var parent = new WindowCandidate(new Rect(0, 0, 300, 200), RootHwnd, IntPtr.Zero, RootHwnd, 0, WindowCandidateKind.TopLevel);
        var child = new WindowCandidate(new Rect(50, 50, 120, 80), ChildHwnd, RootHwnd, RootHwnd, 1, WindowCandidateKind.ChildControl);

        var result = service.GetCandidateAtPoint(new Point(54, 80), [parent, child], parent);

        Assert.NotNull(result);
        Assert.Equal(RootHwnd, result!.Hwnd);
    }

    [Fact]
    public void GetCandidateAtPoint_FallsBackToTopLevelWhenNoChildMatches()
    {
        var service = new WindowDetectionService();
        var parent = new WindowCandidate(new Rect(0, 0, 300, 200), RootHwnd, IntPtr.Zero, RootHwnd, 0, WindowCandidateKind.TopLevel);
        var child = new WindowCandidate(new Rect(50, 50, 120, 80), ChildHwnd, RootHwnd, RootHwnd, 1, WindowCandidateKind.ChildControl);

        var result = service.GetCandidateAtPoint(new Point(20, 20), [parent, child]);

        Assert.NotNull(result);
        Assert.Equal(RootHwnd, result!.Hwnd);
    }

    [Fact]
    public void TryGetDockedStripBounds_ReturnsBottomTaskbarStrip()
    {
        var monitorBounds = new Rect(0, 0, 1920, 1080);
        var workArea = new Rect(0, 0, 1920, 1040);

        var result = WindowDetectionService.TryGetDockedStripBounds(monitorBounds, workArea);

        Assert.NotNull(result);
        Assert.Equal(new Rect(0, 1040, 1920, 40), result!.Value);
    }

    [Fact]
    public void TryGetDockedStripBounds_ReturnsTopTaskbarStrip()
    {
        var monitorBounds = new Rect(0, 0, 1920, 1080);
        var workArea = new Rect(0, 48, 1920, 1032);

        var result = WindowDetectionService.TryGetDockedStripBounds(monitorBounds, workArea);

        Assert.NotNull(result);
        Assert.Equal(new Rect(0, 0, 1920, 48), result!.Value);
    }

    [Fact]
    public void BuildSyntheticTaskbarSegments_CenteredLayout_ReturnsCommandAppAndTrayBands()
    {
        var stripBounds = new Rect(0, 1040, 1920, 40);

        var result = WindowDetectionService.BuildSyntheticTaskbarSegments(stripBounds, centeredAlignment: true);

        Assert.Equal(3, result.Count);
        Assert.True(result[0].Left >= stripBounds.Left);
        Assert.True(result[1].Width > 0);
        Assert.Equal(stripBounds.Right, result[2].Right);
    }

    [Fact]
    public void BuildSyntheticTaskbarSegments_LeftLayout_ReturnsLeftBiasedCommandBand()
    {
        var stripBounds = new Rect(0, 1040, 1600, 48);

        var result = WindowDetectionService.BuildSyntheticTaskbarSegments(stripBounds, centeredAlignment: false);

        Assert.Equal(3, result.Count);
        Assert.Equal(stripBounds.Left, result[0].Left);
        Assert.True(result[1].Left >= result[0].Right);
        Assert.Equal(stripBounds.Right, result[2].Right);
    }

    [Fact]
    public void GetCandidateAtPoint_PrefersSyntheticTaskbarChildOverWholeStrip()
    {
        var service = new WindowDetectionService();
        var strip = new WindowCandidate(new Rect(0, 1040, 1920, 40), new IntPtr(10), IntPtr.Zero, new IntPtr(10), 0, WindowCandidateKind.TopLevel);
        var tray = new WindowCandidate(new Rect(1680, 1040, 240, 40), new IntPtr(11), new IntPtr(10), new IntPtr(10), 1, WindowCandidateKind.ChildControl);

        var result = service.GetCandidateAtPoint(new Point(1800, 1060), [strip, tray]);

        Assert.NotNull(result);
        Assert.Equal(tray.Hwnd, result!.Hwnd);
    }
}
