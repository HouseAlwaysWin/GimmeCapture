using Avalonia;
using GimmeCapture.Services.Platforms.Linux;

namespace GimmeCapture.Tests;

/// <summary>The geometry <see cref="LinuxScreenCaptureService"/> works out before it asks X11 for pixels.</summary>
public class LinuxScreenCaptureServiceTests
{
    [Fact]
    public void ToPhysicalRect_ScalesTheRegion_AndAddsTheScreenOffsetAsItIs()
    {
        // A 150% screen whose top-left sits at (1920, 0) in the X11 root, right of a 1920-pixel-wide one.
        var rect = LinuxScreenCaptureService.ToPhysicalRect(new Rect(10, 20, 100, 50), new PixelPoint(1920, 0), 1.5);

        Assert.Equal((1935, 30, 150, 75), rect);
    }

    [Theory]
    [InlineData(100, 100, 200, 100, 100, 100, 200, 100)]    // inside: unchanged
    [InlineData(-50, -20, 200, 100, 0, 0, 150, 80)]         // over the left and top edges
    [InlineData(1800, 1000, 200, 100, 1800, 1000, 120, 80)] // over the right and bottom edges
    [InlineData(-10, -10, 1940, 1100, 0, 0, 1920, 1080)]    // over every edge
    public void ClampToRoot_KeepsTheRectangleInsideA1920x1080Root(
        int x, int y, int width, int height, int expectedX, int expectedY, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(
            (expectedX, expectedY, expectedWidth, expectedHeight),
            LinuxScreenCaptureService.ClampToRoot(x, y, width, height, 1920, 1080));
    }

    // XGetImage outside the root raises an X protocol error, which Xlib's default handler answers by ending the
    // process, so a rectangle with nothing inside the root must not reach it.
    [Theory]
    [InlineData(2000, 0, 100, 100)] // right of the root
    [InlineData(-300, 0, 200, 100)] // left of it
    [InlineData(0, 1080, 100, 100)] // starting at its bottom edge
    public void ClampToRoot_ReturnsNull_WhenNothingIsInsideTheRoot(int x, int y, int width, int height)
    {
        Assert.Null(LinuxScreenCaptureService.ClampToRoot(x, y, width, height, 1920, 1080));
    }
}
