using GimmeCapture.Services.Core.Media;
using SkiaSharp;

namespace GimmeCapture.Tests;

// Covers ManualScrollStrip.RenderScaledPreview — the thumbnail feed for the live scrolling-capture
// preview. The contract under test is purely geometric (fit within the caps, preserve aspect, never
// upscale); the pixel content is irrelevant, so a solid seed is enough.
public class ManualScrollStripPreviewTests
{
    private static SKBitmap SolidStrip(int width, int height)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.CornflowerBlue);
        return bmp;
    }

    private static ManualScrollStrip StripOf(int width, int height) =>
        new(SolidStrip(width, height), ignoreRightColumns: 0);

    [Fact]
    public void RenderScaledPreview_WidthBound_ScalesToWidthCapAndKeepsAspect()
    {
        // 800x1600: width cap (420/800=0.525) bites before height cap (2000/1600=1.25).
        using var strip = StripOf(800, 1600);

        using SKBitmap preview = strip.RenderScaledPreview(maxWidth: 420, maxHeight: 2000);

        Assert.Equal(420, preview.Width);
        Assert.Equal(840, preview.Height); // 1600 * 0.525
    }

    [Fact]
    public void RenderScaledPreview_TallStrip_ClampsToHeightCapAndNarrows()
    {
        // 400x5000: a long capture — height cap (2000/5000=0.4) bites, so the whole strip scales to fit.
        using var strip = StripOf(400, 5000);

        using SKBitmap preview = strip.RenderScaledPreview(maxWidth: 420, maxHeight: 2000);

        Assert.Equal(2000, preview.Height);
        Assert.Equal(160, preview.Width); // 400 * 0.4
    }

    [Fact]
    public void RenderScaledPreview_SmallStrip_IsNeverUpscaled()
    {
        using var strip = StripOf(100, 200);

        using SKBitmap preview = strip.RenderScaledPreview(maxWidth: 420, maxHeight: 2000);

        Assert.Equal(100, preview.Width);
        Assert.Equal(200, preview.Height);
    }

    [Theory]
    [InlineData(800, 1600)]
    [InlineData(400, 5000)]
    [InlineData(100, 200)]
    [InlineData(1, 1)]
    public void RenderScaledPreview_NeverExceedsCaps(int width, int height)
    {
        using var strip = StripOf(width, height);

        using SKBitmap preview = strip.RenderScaledPreview(maxWidth: 420, maxHeight: 2000);

        Assert.True(preview.Width is >= 1 and <= 420);
        Assert.True(preview.Height is >= 1 and <= 2000);
        Assert.Equal(SKColorType.Bgra8888, preview.ColorType);
    }
}
