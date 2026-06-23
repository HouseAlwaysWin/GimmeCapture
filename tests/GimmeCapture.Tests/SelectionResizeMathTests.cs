using Avalonia;
using GimmeCapture.Views.Main;

namespace GimmeCapture.Tests;

public class SelectionResizeMathTests
{
    private static readonly Rect Original = new(10, 20, 100, 50);

    [Fact]
    public void ApplyResizeDelta_BottomRight_ExpandsWidthAndHeightOnly()
    {
        var (x, y, w, h) = SelectionResizeMath.ApplyResizeDelta(Original, ResizeDirection.BottomRight, 30, 15);
        Assert.Equal(10, x);
        Assert.Equal(20, y);
        Assert.Equal(130, w);
        Assert.Equal(65, h);
    }

    [Fact]
    public void ApplyResizeDelta_TopLeft_ShiftsOriginAndShrinks()
    {
        var (x, y, w, h) = SelectionResizeMath.ApplyResizeDelta(Original, ResizeDirection.TopLeft, 20, 10);
        Assert.Equal(30, x);   // 10 + 20
        Assert.Equal(30, y);   // 20 + 10
        Assert.Equal(80, w);   // 100 - 20
        Assert.Equal(40, h);   // 50 - 10
    }

    [Theory]
    [InlineData(ResizeDirection.Top, 10, 20, 100, 50, 10, 30, 100, 30)]    // y+=dy, h-=dy
    [InlineData(ResizeDirection.Bottom, 10, 20, 100, 50, 10, 20, 100, 70)] // h+=dy
    [InlineData(ResizeDirection.Left, 10, 20, 100, 50, 30, 20, 80, 50)]    // x+=dx, w-=dx
    [InlineData(ResizeDirection.Right, 10, 20, 100, 50, 10, 20, 130, 50)]  // w+=dx
    public void ApplyResizeDelta_Edges_MoveOnlyTheirComponent(
        ResizeDirection dir, double ox, double oy, double ow, double oh,
        double ex, double ey, double ew, double eh)
    {
        var (x, y, w, h) = SelectionResizeMath.ApplyResizeDelta(new Rect(ox, oy, ow, oh), dir, 20, 20);
        Assert.Equal(ex, x);
        Assert.Equal(ey, y);
        Assert.Equal(ew, w);
        Assert.Equal(eh, h);
    }

    [Fact]
    public void ApplyResizeDelta_LargeTopLeftDelta_ProducesNegativeRawDimensions()
    {
        // Dragging the top-left handle past the opposite edge yields raw negative w/h
        // (un-normalized) — normalization is the caller's responsibility.
        var (_, _, w, h) = SelectionResizeMath.ApplyResizeDelta(Original, ResizeDirection.TopLeft, 150, 80);
        Assert.True(w < 0);
        Assert.True(h < 0);
    }

    [Fact]
    public void NormalizeRect_NegativeWidth_FlipsXAndAbsWidth()
    {
        var r = SelectionResizeMath.NormalizeRect(50, 10, -30, 20);
        Assert.Equal(20, r.X);     // 50 + (-30)
        Assert.Equal(10, r.Y);
        Assert.Equal(30, r.Width); // abs(-30)
        Assert.Equal(20, r.Height);
    }

    [Fact]
    public void NormalizeRect_NegativeHeight_FlipsYAndAbsHeight()
    {
        var r = SelectionResizeMath.NormalizeRect(10, 50, 20, -25);
        Assert.Equal(10, r.X);
        Assert.Equal(25, r.Y);      // 50 + (-25)
        Assert.Equal(20, r.Width);
        Assert.Equal(25, r.Height); // abs(-25)
    }

    [Fact]
    public void NormalizeRect_PositiveRect_Unchanged()
    {
        var r = SelectionResizeMath.NormalizeRect(10, 20, 100, 50);
        Assert.Equal(new Rect(10, 20, 100, 50), r);
    }
}
