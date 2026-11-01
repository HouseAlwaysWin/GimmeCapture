using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Main;
using SkiaSharp;

namespace GimmeCapture.Tests.ViewModels;

/// <summary>
/// The pixels mosaic and blur sample while they are drawn on the snip overlay. A frozen overlay took them from a live
/// grab of whatever was on screen by then, while its export crops the frozen still, so the preview and the saved
/// picture could disagree. Avalonia bitmaps need the renderer, hence the collection.
/// </summary>
[Collection(AvaloniaRendererCollection.Name)]
public sealed class DrawingModeSnapshotTests
{
    [Fact]
    public async Task AFrozenOverlay_DrawsOverItsStill_WithoutGrabbingTheScreen()
    {
        AvaloniaRenderer.EnsureRegistered();
        var capture = new Mock<IScreenCaptureService>();
        using var vm = new SnipWindowViewModel(Colors.Red, 2.0, capture.Object);
        bool grabbedLive = false;
        vm.CaptureDrawingModeSnapshotAsync = () =>
        {
            grabbedLive = true;
            return Task.FromResult<WriteableBitmap?>(null);
        };
        vm.Surface.UseHeadlessBackdropForTest();
        vm.Surface.FreezeFromPreOverlayGrab(CreateStill(200, 100), grabScaling: 2.0);
        vm.SelectionRect = new Rect(10, 5, 30, 20);

        using Bitmap? source = await vm.CaptureDrawingSourceForTestAsync();

        Assert.False(grabbedLive);
        capture.Verify(
            c => c.CaptureRegionBitmapAsync(It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>()),
            Times.Never);
        Assert.NotNull(source);

        // The selection at the scaling the still was grabbed at: 60x40 pixels from (20, 10).
        Assert.Equal(new PixelSize(60, 40), source!.PixelSize);
        uint[] pixels = AvaloniaRenderer.ReadPixels(source);
        Assert.Equal(StillPixel(20, 10), pixels[0]);
        Assert.Equal(StillPixel(79, 10), pixels[59]);
        Assert.Equal(StillPixel(79, 49), pixels[^1]);
    }

    [Fact]
    public async Task ALiveOverlay_StillGrabsTheScreen()
    {
        AvaloniaRenderer.EnsureRegistered();
        using var vm = new SnipWindowViewModel(Colors.Red, 2.0, new Mock<IScreenCaptureService>().Object);
        using var grabbed = new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        vm.CaptureDrawingModeSnapshotAsync = () => Task.FromResult<WriteableBitmap?>(grabbed);
        vm.SelectionRect = new Rect(10, 5, 30, 20);

        Bitmap? source = await vm.CaptureDrawingSourceForTestAsync();

        Assert.Same(grabbed, source);
    }

    [Fact]
    public void TheMosaicsAndBlursDrawn_FollowTheSelectionsSnapshot_AndNoneKeepsOneDisposed()
    {
        // A preview reads the picture again whenever it redraws, as when it is dragged. Leaving drawing mode disposes
        // the snapshot, and entering it again grabs another.
        AvaloniaRenderer.EnsureRegistered();
        using var vm = new SnipWindowViewModel(Colors.Red, 2.0, new Mock<IScreenCaptureService>().Object);
        var first = AvaloniaRenderer.Create(4, 4, static (_, _) => 0xFF000000u);
        vm.AdoptDrawingModeSnapshot(first);
        var mosaic = new Annotation { Type = AnnotationType.Mosaic, DrawingModeSnapshot = first };
        var blur = new Annotation { Type = AnnotationType.Blur, DrawingModeSnapshot = first };
        var arrow = new Annotation { Type = AnnotationType.Arrow };
        vm.Annotations.Add(mosaic);
        vm.Annotations.Add(blur);
        vm.Annotations.Add(arrow);

        var second = AvaloniaRenderer.Create(4, 4, static (_, _) => 0xFFFFFFFFu);
        vm.AdoptDrawingModeSnapshot(second);

        Assert.Same(second, vm.DrawingModeSnapshot);
        Assert.Same(second, mosaic.DrawingModeSnapshot);
        Assert.Same(second, blur.DrawingModeSnapshot);
        Assert.Null(arrow.DrawingModeSnapshot);
        Assert.Throws<ObjectDisposedException>(() => first.PixelSize);

        vm.IsDrawingMode = true; // keeps the snapshot it has
        vm.IsDrawingMode = false; // and lets it go

        Assert.Null(vm.DrawingModeSnapshot);
        Assert.Null(mosaic.DrawingModeSnapshot);
        Assert.Null(blur.DrawingModeSnapshot);
        Assert.Throws<ObjectDisposedException>(() => second.PixelSize);
    }

    // Each pixel of the still as the word it is in BGRA memory: opaque, and no two alike within the crop.
    private static uint StillPixel(int x, int y) =>
        0xFF000000u | ((uint)x << 16) | ((uint)y << 8) | (uint)((x + y) % 256);

    private static SKBitmap CreateStill(int width, int height)
    {
        var still = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint word = StillPixel(x, y);
                still.SetPixel(x, y, new SKColor((byte)(word >> 16), (byte)(word >> 8), (byte)word, 255));
            }
        }

        return still;
    }
}
