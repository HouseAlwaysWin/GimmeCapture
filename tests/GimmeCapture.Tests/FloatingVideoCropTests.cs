using Avalonia;
using GimmeCapture.ViewModels.Floating;

namespace GimmeCapture.Tests;

public class FloatingVideoCropTests
{
    [Fact]
    public void SelectionToPixelCropRect_IdentityScale_ReturnsSelection()
    {
        var crop = FloatingVideoViewModel.SelectionToPixelCropRect(
            new Rect(10, 20, 100, 50), displayWidth: 200, displayHeight: 200, originalWidth: 200, originalHeight: 200);

        Assert.Equal(10, crop.X);
        Assert.Equal(20, crop.Y);
        Assert.Equal(100, crop.Width);
        Assert.Equal(50, crop.Height);
    }

    [Fact]
    public void SelectionToPixelCropRect_ScalesDisplayToVideoPixels()
    {
        // Display is half the video size -> scale factor 2.
        var crop = FloatingVideoViewModel.SelectionToPixelCropRect(
            new Rect(10, 10, 40, 30), displayWidth: 100, displayHeight: 100, originalWidth: 200, originalHeight: 200);

        Assert.Equal(20, crop.X);
        Assert.Equal(20, crop.Y);
        Assert.Equal(80, crop.Width);
        Assert.Equal(60, crop.Height);
    }

    [Fact]
    public void SelectionToPixelCropRect_RoundsWidthAndHeightDownToEven()
    {
        var crop = FloatingVideoViewModel.SelectionToPixelCropRect(
            new Rect(0, 0, 51, 51), displayWidth: 200, displayHeight: 200, originalWidth: 200, originalHeight: 200);

        Assert.Equal(50, crop.Width);
        Assert.Equal(50, crop.Height);
    }

    [Fact]
    public void SelectionToPixelCropRect_ClampsToVideoBounds()
    {
        var crop = FloatingVideoViewModel.SelectionToPixelCropRect(
            new Rect(190, 190, 100, 100), displayWidth: 200, displayHeight: 200, originalWidth: 200, originalHeight: 200);

        Assert.True(crop.X + crop.Width <= 200);
        Assert.True(crop.Y + crop.Height <= 200);
        Assert.True(crop.Width >= 2);
        Assert.True(crop.Height >= 2);
    }

    [Fact]
    public void SelectionToPixelCropRect_DegenerateVideo_ReturnsEmpty()
    {
        var crop = FloatingVideoViewModel.SelectionToPixelCropRect(
            new Rect(0, 0, 10, 10), displayWidth: 1, displayHeight: 1, originalWidth: 1, originalHeight: 1);

        Assert.Equal(0, crop.Width);
        Assert.Equal(0, crop.Height);
    }
}
