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
}
