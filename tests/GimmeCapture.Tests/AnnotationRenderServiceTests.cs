using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Rendering;
using SkiaSharp;

namespace GimmeCapture.Tests;

// In the renderer's collection for the tests that start from an Avalonia bitmap, as a live mosaic or blur preview does.
[Collection(AvaloniaRendererCollection.Name)]
public class AnnotationRenderServiceTests
{
    [Theory]
    [InlineData(AlphaFormat.Premul)] // Skia's own layout: copied straight into the Skia bitmap
    [InlineData(AlphaFormat.Opaque)] // any other: copied through a pinned buffer
    public void RenderAnnotationPreview_FromAnAvaloniaBitmap_SeesEveryPixelOfIt(AlphaFormat alphaFormat)
    {
        // The preview copies the Avalonia bitmap into Skia first. That copy went through a pooled array it read after
        // unpinning, which a compacting GC could move under it. A mosaic over the whole picture averages every cell,
        // so a pixel copied wrong, or a row in the wrong place, changes the preview.
        const int Width = 37;
        const int Height = 23;
        AvaloniaRenderer.EnsureRegistered();
        using var avaloniaBitmap = new WriteableBitmap(
            new PixelSize(Width, Height), new Vector(96, 96), PixelFormat.Bgra8888, alphaFormat);
        AvaloniaRenderer.Fill(avaloniaBitmap, PatternPixel);
        using var skiaBitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                uint word = PatternPixel(x, y);
                skiaBitmap.SetPixel(x, y, new SKColor((byte)(word >> 16), (byte)(word >> 8), (byte)word, 255));
            }
        }

        var annotation = new Annotation
        {
            Type = AnnotationType.Mosaic,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(Width, Height),
            EffectSettings = new AnnotationEffectSettings { MosaicCellSize = 3 },
            DrawingModeReferenceSize = new Size(Width, Height)
        };

        using var expected = AnnotationRenderService.Shared.RenderAnnotationPreviewToSkBitmap(
            skiaBitmap, annotation, Width, Height);
        using var preview = AnnotationRenderService.Shared.RenderAnnotationPreview(
            avaloniaBitmap, annotation, Width, Height);

        Assert.NotNull(expected);
        Assert.NotNull(preview);
        Assert.Equal(Words(expected!), AvaloniaRenderer.ReadPixels(preview!));
    }

    // An opaque picture, as the word each pixel is in BGRA memory, with no two neighbours alike.
    private static uint PatternPixel(int x, int y) =>
        0xFF000000u | ((uint)(x * 7 % 256) << 16) | ((uint)(y * 13 % 256) << 8) | (uint)((x * 5 + y * 3) % 256);

    // The bitmap's pixels row after row, each as the word it is in BGRA memory (0xAARRGGBB).
    private static uint[] Words(SKBitmap bitmap)
    {
        var words = new uint[bitmap.Width * bitmap.Height];
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                words[(y * bitmap.Width) + x] =
                    ((uint)c.Alpha << 24) | ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue;
            }
        }

        return words;
    }

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

    [Fact]
    public void RenderAnnotationPreview_InteriorBlurMatchesFullBitmapResult()
    {
        // The preview path now processes only a padded ROI instead of copying the whole snapshot.
        // This must yield pixel-identical output to blurring the full bitmap and cropping the same
        // ROI — it guards the ROI-crop optimization (the padding must capture the same neighbors).
        var annotation = new Annotation
        {
            Type = AnnotationType.Blur,
            StartPoint = new Point(24, 10),
            EndPoint = new Point(56, 30), // interior region, not touching any snapshot edge
            EffectSettings = new AnnotationEffectSettings { BlurRadius = 6f },
            DrawingModeReferenceSize = new Size(80, 40)
        };

        using var snapshot = CreateStripedBitmap(80, 40);
        using var preview = AnnotationRenderService.Shared.RenderAnnotationPreviewToSkBitmap(snapshot, annotation, 80, 40);
        Assert.NotNull(preview);

        // Reference: blur the whole bitmap, then read the same ROI back out.
        using var full = CreateStripedBitmap(80, 40);
        AnnotationRenderService.Shared.RenderAnnotationsToBitmap(full, new[] { annotation }, 80, 40, 80, 40);

        const int roiLeft = 24;
        const int roiTop = 10;
        Assert.Equal(32, preview!.Width);
        Assert.Equal(20, preview.Height);
        for (int y = 0; y < preview.Height; y++)
        {
            for (int x = 0; x < preview.Width; x++)
            {
                Assert.Equal(full.GetPixel(roiLeft + x, roiTop + y), preview.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void RenderAnnotationsToBitmap_CalloutDrawsLeaderTowardTargetAndLabelAtAnchor()
    {
        using var bitmap = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);

        // StartPoint = target (leader tip), EndPoint = text-label anchor.
        var callout = new Annotation
        {
            Type = AnnotationType.Callout,
            StartPoint = new Point(8, 8),
            EndPoint = new Point(40, 40),
            Text = "Hi",
            Color = Avalonia.Media.Colors.Red,
            Thickness = 3,
            FontSize = 18,
            FontFamily = new Avalonia.Media.FontFamily("Arial")
        };

        AnnotationRenderService.Shared.RenderAnnotationsToBitmap(bitmap, new[] { callout }, 64, 64, 64, 64);

        // The leader line runs diagonally from the anchor (40,40) to the target (8,8);
        // its midpoint (24,24) should have painted pixels.
        Assert.True(bitmap.GetPixel(24, 24).Alpha > 0, "Expected leader line pixels along the diagonal.");

        // The arrow head lands on the target, so pixels near StartPoint should be painted.
        Assert.True(bitmap.GetPixel(10, 10).Alpha > 0, "Expected arrow-head pixels near the target.");

        // The label text is drawn at the anchor (EndPoint); scan the box region for ink.
        Assert.True(HasOpaquePixelInRegion(bitmap, 40, 24, 24, 22), "Expected label text pixels in the box region.");

        // A point far from both the leader and the label box should remain untouched.
        Assert.Equal((byte)0, bitmap.GetPixel(2, 60).Alpha);
    }

    [Fact]
    public void RenderAnnotationsToBitmap_CalloutWithoutTextStillDrawsLeader()
    {
        using var bitmap = new SKBitmap(48, 48, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);

        var callout = new Annotation
        {
            Type = AnnotationType.Callout,
            StartPoint = new Point(6, 6),
            EndPoint = new Point(36, 36),
            Text = string.Empty,
            Color = Avalonia.Media.Colors.Lime,
            Thickness = 2,
            FontSize = 16,
            FontFamily = new Avalonia.Media.FontFamily("Arial")
        };

        AnnotationRenderService.Shared.RenderAnnotationsToBitmap(bitmap, new[] { callout }, 48, 48, 48, 48);

        Assert.True(bitmap.GetPixel(21, 21).Alpha > 0, "Expected leader pixels even when the callout has no text.");
    }

    private static bool HasOpaquePixelInRegion(SKBitmap bitmap, int x, int y, int width, int height)
    {
        int right = Math.Min(bitmap.Width, x + width);
        int bottom = Math.Min(bitmap.Height, y + height);
        for (int yy = Math.Max(0, y); yy < bottom; yy++)
        {
            for (int xx = Math.Max(0, x); xx < right; xx++)
            {
                if (bitmap.GetPixel(xx, yy).Alpha > 0)
                {
                    return true;
                }
            }
        }
        return false;
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
