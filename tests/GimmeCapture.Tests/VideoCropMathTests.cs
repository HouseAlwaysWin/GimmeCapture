using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using Xunit;

namespace GimmeCapture.Tests;

// Pure display→source crop mapping used by the compress editor (the view maps a selection to content coords first).
public class VideoCropMathTests
{
    [Fact]
    public void SelectionToCrop_ScalesDisplayToSourcePixels()
    {
        // Display 960×540 → source 1920×1080 (2× scale). Left-half selection → source (0,0,960,540).
        VideoEditCrop? crop = VideoCropMath.SelectionToCrop(0, 0, 480, 270, 960, 540, 1920, 1080);

        Assert.NotNull(crop);
        Assert.Equal(0, crop!.X);
        Assert.Equal(0, crop.Y);
        Assert.Equal(960, crop.Width);
        Assert.Equal(540, crop.Height);
    }

    [Fact]
    public void SelectionToCrop_SnapsToEvenDimensions_AndClampsInBounds()
    {
        // 1× scale, odd-ish selection → even dims, clamped so x+w / y+h stay in bounds.
        VideoEditCrop? crop = VideoCropMath.SelectionToCrop(101, 51, 33, 27, 200, 200, 200, 200);

        Assert.NotNull(crop);
        Assert.Equal(0, crop!.Width % 2);
        Assert.Equal(0, crop.Height % 2);
        Assert.True(crop.X + crop.Width <= 200);
        Assert.True(crop.Y + crop.Height <= 200);
    }

    [Fact]
    public void SelectionToCrop_TinyOrDegenerate_ReturnsNull()
    {
        Assert.Null(VideoCropMath.SelectionToCrop(0, 0, 1, 1, 200, 200, 200, 200)); // collapses to < 2 source px
        Assert.Null(VideoCropMath.SelectionToCrop(0, 0, 100, 100, 100, 100, 1, 1)); // degenerate source
    }
}
