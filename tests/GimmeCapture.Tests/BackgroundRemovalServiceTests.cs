using SkiaSharp;
using Microsoft.ML.OnnxRuntime.Tensors;

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

    [Fact]
    public void ProcessMask_ResizesToSingleChannelMask()
    {
        var tensor = CreateLeftOpaqueMaskTensor();

        using var mask = BackgroundRemovalService.ProcessMask(tensor, 435, 434, out _, out _);

        Assert.Equal(435, mask.Width);
        Assert.Equal(434, mask.Height);
        Assert.Equal(SKColorType.Gray8, mask.ColorType);
    }

    [Fact]
    public void ApplyMask_UsesResizedMaskAcrossFullWidth()
    {
        using var bitmap = new SKBitmap(435, 4, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.Red);
        var tensor = CreateLeftOpaqueMaskTensor();
        using var mask = BackgroundRemovalService.ProcessMask(tensor, bitmap.Width, bitmap.Height, out _, out _);

        byte[] encoded = BackgroundRemovalService.ApplyMask(bitmap, mask, 0, 1);
        using var decoded = SKBitmap.Decode(encoded);

        Assert.NotNull(decoded);
        Assert.True(decoded!.GetPixel(20, 2).Alpha > 240);
        Assert.True(decoded.GetPixel(420, 2).Alpha < 15);
    }

    private static DenseTensor<float> CreateLeftOpaqueMaskTensor()
    {
        var data = new float[320 * 320];
        for (int y = 0; y < 320; y++)
        {
            int rowOffset = y * 320;
            for (int x = 0; x < 320; x++)
            {
                data[rowOffset + x] = x < 160 ? 6f : -6f;
            }
        }

        return new DenseTensor<float>(data, new[] { 1, 1, 320, 320 });
    }
}
