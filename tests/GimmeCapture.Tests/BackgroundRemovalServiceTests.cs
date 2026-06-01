using SkiaSharp;

namespace GimmeCapture.Tests;

public class BackgroundRemovalServiceTests
{
    [Fact]
    public void IsSolidBackground_DetectsUniformOpaqueEdges()
    {
        using var bitmap = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        bitmap.Erase(new SKColor(240, 240, 240, 255));

        bool result = BackgroundRemovalService.IsSolidBackground(bitmap, out SKColor backgroundColor);

        Assert.True(result);
        Assert.Equal((byte)240, backgroundColor.Red);
        Assert.Equal((byte)240, backgroundColor.Green);
        Assert.Equal((byte)240, backgroundColor.Blue);
    }

    [Fact]
    public void TryGetCornerBackgroundColor_DetectsDominantCornerColor()
    {
        using var bitmap = new SKBitmap(40, 40, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.White);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
        canvas.DrawRect(new SKRect(10, 10, 30, 30), paint);

        bool result = BackgroundRemovalService.TryGetCornerBackgroundColor(bitmap, out SKColor backgroundColor);

        Assert.True(result);
        Assert.Equal(SKColors.White.Red, backgroundColor.Red);
        Assert.Equal(SKColors.White.Green, backgroundColor.Green);
        Assert.Equal(SKColors.White.Blue, backgroundColor.Blue);
    }

    [Fact]
    public void RemoveSolidBackground_MakesMatchingPixelsTransparent()
    {
        using var bitmap = new SKBitmap(2, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.White);
        bitmap.SetPixel(1, 0, SKColors.Black);

        byte[] encoded = BackgroundRemovalService.RemoveSolidBackground(bitmap, SKColors.White);
        using var decoded = SKBitmap.Decode(encoded);

        Assert.NotNull(decoded);
        Assert.Equal((byte)0, decoded!.GetPixel(0, 0).Alpha);
        Assert.Equal((byte)255, decoded.GetPixel(1, 0).Alpha);
    }
}
