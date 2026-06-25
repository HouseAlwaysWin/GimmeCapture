using GimmeCapture.Services.Core.AI;
using SkiaSharp;

namespace GimmeCapture.Tests;

public class Sam2RedactionTrackerTests
{
    private static SKBitmap MaskWithWhiteRect(int w, int h, SKRect white)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false };
        canvas.DrawRect(white, paint);
        return bmp;
    }

    [Fact]
    public void TightBounds_ReturnsRegionOfWhitePixels()
    {
        using var mask = MaskWithWhiteRect(100, 80, new SKRect(20, 10, 60, 50));

        var bounds = Sam2RedactionTracker.TightBounds(mask);

        Assert.NotNull(bounds);
        Assert.Equal(20, bounds!.Value.Left);
        Assert.Equal(10, bounds.Value.Top);
        Assert.Equal(60, bounds.Value.Right);
        Assert.Equal(50, bounds.Value.Bottom);
    }

    [Fact]
    public void TightBounds_AllBlack_ReturnsNull()
    {
        using var mask = MaskWithWhiteRect(40, 40, SKRect.Empty);

        Assert.Null(Sam2RedactionTracker.TightBounds(mask));
    }

    [Fact]
    public void TightBounds_SinglePixel_IsOneByOne()
    {
        using var mask = MaskWithWhiteRect(40, 40, new SKRect(5, 7, 6, 8));

        var bounds = Sam2RedactionTracker.TightBounds(mask);

        Assert.NotNull(bounds);
        Assert.Equal(5, bounds!.Value.Left);
        Assert.Equal(7, bounds.Value.Top);
        Assert.Equal(1, bounds.Value.Width);
        Assert.Equal(1, bounds.Value.Height);
    }
}
