using Avalonia;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests;

public class CaptureScopeGeometryTests
{
    [Fact]
    public void PhysicalToLogical_NoOffsetNoScale_IsIdentity()
    {
        var logical = CaptureScopeGeometry.PhysicalToLogical(new Rect(100, 200, 800, 600), new PixelPoint(0, 0), 1.0);
        Assert.Equal(new Rect(100, 200, 800, 600), logical);
    }

    [Fact]
    public void PhysicalToLogical_SubtractsOffsetThenDividesByScale()
    {
        // Overlay positioned at physical (50,30), 1.25x DPI. A window at physical (300,180) size 500x400.
        var logical = CaptureScopeGeometry.PhysicalToLogical(new Rect(300, 180, 500, 400), new PixelPoint(50, 30), 1.25);

        Assert.Equal((300 - 50) / 1.25, logical.X, 5);
        Assert.Equal((180 - 30) / 1.25, logical.Y, 5);
        Assert.Equal(500 / 1.25, logical.Width, 5);
        Assert.Equal(400 / 1.25, logical.Height, 5);
    }

    [Fact]
    public void PhysicalToLogical_RoundTripsThroughTheRecordingTransform()
    {
        // The recording path computes physical = logical * scaling + offset; this must invert it.
        var offset = new PixelPoint(-1920, 0); // a monitor to the left of the primary
        const double scaling = 1.5;
        var physical = new Rect(-1600, 240, 1280, 720);

        var logical = CaptureScopeGeometry.PhysicalToLogical(physical, offset, scaling);

        Assert.Equal(physical.X, logical.X * scaling + offset.X, 3);
        Assert.Equal(physical.Y, logical.Y * scaling + offset.Y, 3);
        Assert.Equal(physical.Width, logical.Width * scaling, 3);
        Assert.Equal(physical.Height, logical.Height * scaling, 3);
    }

    [Fact]
    public void PhysicalToLogical_TreatsNonPositiveScaleAsOne()
    {
        var logical = CaptureScopeGeometry.PhysicalToLogical(new Rect(10, 20, 30, 40), new PixelPoint(5, 5), 0);
        Assert.Equal(new Rect(5, 15, 30, 40), logical);
    }
}
