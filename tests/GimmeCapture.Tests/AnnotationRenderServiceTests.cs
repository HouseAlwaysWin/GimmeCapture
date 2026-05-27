using Avalonia;
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

    [Fact]
    public void RenderAnnotationsToBitmap_RectangleDrawsVisiblePixelsOnTransparentBitmap()
    {
        using var bitmap = new SKBitmap(32, 32, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);

        var annotation = new Annotation
        {
            Type = AnnotationType.Rectangle,
            StartPoint = new Point(4, 4),
            EndPoint = new Point(28, 24),
            Color = Avalonia.Media.Colors.Red,
            Thickness = 2
        };

        AnnotationRenderService.Shared.RenderAnnotationsToBitmap(bitmap, new[] { annotation }, 32, 32, 32, 32);

        Assert.True(bitmap.GetPixel(4, 4).Alpha > 0);
        Assert.True(bitmap.GetPixel(16, 4).Alpha > 0);
        Assert.Equal((byte)0, bitmap.GetPixel(16, 16).Alpha);
    }

    [Fact]
    public void RenderAnnotationPreview_BlurKeepsOpaqueEdgesWhenRegionTouchesSnapshotBounds()
    {
        using var snapshot = CreateSolidBitmap(40, 20, new SKColor(240, 240, 240, 255));
        var annotation = new Annotation
        {
            Type = AnnotationType.Blur,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(40, 20),
            EffectSettings = new AnnotationEffectSettings { BlurRadius = 12f },
            DrawingModeReferenceSize = new Size(40, 20)
        };

        using var preview = AnnotationRenderService.Shared.RenderAnnotationPreviewToSkBitmap(snapshot, annotation, 40, 20);

        Assert.NotNull(preview);
        Assert.Equal((byte)255, preview!.GetPixel(0, 0).Alpha);
        Assert.Equal((byte)255, preview.GetPixel(preview.Width - 1, 0).Alpha);
        Assert.Equal((byte)255, preview.GetPixel(0, preview.Height - 1).Alpha);
        Assert.Equal((byte)255, preview.GetPixel(preview.Width - 1, preview.Height - 1).Alpha);
    }

    [Fact]
    public void RenderAnnotationsToBitmap_BlurPreservesVisibleImageVariation()
    {
        using var bitmap = CreateStripedBitmap(80, 24);
        var annotation = new Annotation
        {
            Type = AnnotationType.Blur,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(80, 24),
            EffectSettings = new AnnotationEffectSettings { BlurRadius = 8f }
        };

        AnnotationRenderService.Shared.RenderAnnotationsToBitmap(bitmap, new[] { annotation }, 80, 24, 80, 24);

        var darkBand = bitmap.GetPixel(10, 12).Red;
        var lightBand = bitmap.GetPixel(30, 12).Red;

        Assert.True(Math.Abs(lightBand - darkBand) > 8);
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

    private static SKBitmap CreateSolidBitmap(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, color);
            }
        }
        return bitmap;
    }

    private static SKBitmap CreateStripedBitmap(int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = ((x / 16) % 2) == 0 ? (byte)35 : (byte)220;
                bitmap.SetPixel(x, y, new SKColor(value, value, value, 255));
            }
        }
        return bitmap;
    }

}
