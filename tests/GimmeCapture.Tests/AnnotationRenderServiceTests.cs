using Avalonia;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Rendering;
using SkiaSharp;

namespace GimmeCapture.Tests;

public class AnnotationRenderServiceTests
{
    [Fact]
    public void RenderAnnotationsToBitmap_MosaicOnlyChangesPixelsInsideRoi()
    {
        using var bitmap = CreateGradientBitmap(12, 12);
        var outsideBefore = bitmap.GetPixel(1, 1);

        var annotation = new Annotation
        {
            Type = AnnotationType.Mosaic,
            StartPoint = new Point(3, 3),
            EndPoint = new Point(9, 9),
            EffectSettings = new AnnotationEffectSettings { MosaicCellSize = 3 }
        };

        AnnotationRenderService.Shared.RenderAnnotationsToBitmap(bitmap, new[] { annotation }, 12, 12, 12, 12);

        Assert.Equal(outsideBefore, bitmap.GetPixel(1, 1));
        Assert.Equal(bitmap.GetPixel(4, 4), bitmap.GetPixel(5, 5));
    }

    [Fact]
    public void RenderAnnotationsToBitmap_BlurOnlyChangesPixelsInsideRoi()
    {
        using var bitmap = CreateGradientBitmap(16, 16);
        var outsideBefore = bitmap.GetPixel(1, 1);
        var insideBefore = bitmap.GetPixel(8, 8);

        var annotation = new Annotation
        {
            Type = AnnotationType.Blur,
            StartPoint = new Point(4, 4),
            EndPoint = new Point(12, 12),
            EffectSettings = new AnnotationEffectSettings { BlurRadius = 6f }
        };

        AnnotationRenderService.Shared.RenderAnnotationsToBitmap(bitmap, new[] { annotation }, 16, 16, 16, 16);

        Assert.Equal(outsideBefore, bitmap.GetPixel(1, 1));
        Assert.NotEqual(insideBefore, bitmap.GetPixel(8, 8));
    }

    private static SKBitmap CreateGradientBitmap(int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 10), (byte)(y * 10), (byte)(x + y), 255));
            }
        }
        return bitmap;
    }

}
