using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Rendering;
using GimmeCapture.ViewModels.Main;
using SkiaSharp;

namespace GimmeCapture.Tests.Rendering;

/// <summary>
/// The overlay's Live/Frozen state machine. These run with no UI thread and no render platform — the surface
/// takes its Skia→Avalonia conversion as a dependency precisely so the frozen path is reachable from a test,
/// which it was not when freeze activation was welded to a display-bitmap conversion.
/// </summary>
public class OverlaySurfaceTests
{
    private static readonly Rect Selection = new(10, 20, 100, 50);
    private static readonly OverlayGeometry Geometry =
        new(new Size(800, 600), new PixelPoint(0, 0), 1.0);

    private static SelectionCommit Commit() =>
        SelectionCommit.Annotated(Selection, Geometry, Array.Empty<Annotation>());

    private static OverlaySurface NewSurface(
        Mock<IScreenCaptureService> capture,
        Action? hide = null,
        Func<SelectionCommit>? provider = null)
    {
        var surface = new OverlaySurface(
            capture.Object,
            new ImmediateCaptureVisibilityCoordinator(),
            provider ?? Commit);
        // No render platform in the test host, so drop the display conversion rather than fall back to live.
        surface.UseHeadlessBackdropForTest();
        surface.HideOverlay = hide;
        return surface;
    }

    private static Mock<IScreenCaptureService> CaptureReturning(int w = 800, int h = 600)
    {
        var capture = new Mock<IScreenCaptureService>();
        capture
            .Setup(c => c.CaptureScreenAsync(It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>(), It.IsAny<bool>()))
            .ReturnsAsync(() => new SKBitmap(w, h));
        capture
            .Setup(c => c.CaptureScreenWithAnnotationsAsync(
                It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>(),
                It.IsAny<IEnumerable<Annotation>>(), It.IsAny<IEnumerable<UserSelectionRect>>(),
                It.IsAny<IEnumerable<TranslatedBlock>>(), It.IsAny<bool>()))
            .ReturnsAsync(() => new SKBitmap(100, 50));
        return capture;
    }

    [Fact]
    public async Task Commit_WhenFrozen_CropsTheStillAndNeitherHidesNorGrabs()
    {
        var capture = CaptureReturning();
        bool hidden = false;
        using var surface = NewSurface(capture, hide: () => hidden = true);

        await surface.FreezeAsync(Geometry);
        Assert.True(surface.IsFrozen);
        capture.Invocations.Clear();

        using var bitmap = await surface.CommitAsync();

        // SelectionRect x grab scaling, cropped out of the still — no second grab, no hide.
        Assert.Equal(100, bitmap.Width);
        Assert.Equal(50, bitmap.Height);
        Assert.False(hidden);
        Assert.Empty(capture.Invocations);
    }

    [Fact]
    public async Task Commit_WhenLive_HidesThenGrabs()
    {
        var capture = CaptureReturning();
        bool hidden = false;
        using var surface = NewSurface(capture, hide: () => hidden = true);

        using var bitmap = await surface.CommitAsync();

        Assert.True(hidden);
        capture.Verify(
            c => c.CaptureScreenWithAnnotationsAsync(
                It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>(),
                It.IsAny<IEnumerable<Annotation>>(), It.IsAny<IEnumerable<UserSelectionRect>>(),
                It.IsAny<IEnumerable<TranslatedBlock>>(), It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task CommitPlain_WhenFrozen_StillReadsTheStill()
    {
        var capture = CaptureReturning();
        bool hidden = false;
        using var surface = NewSurface(capture, hide: () => hidden = true);
        await surface.FreezeAsync(Geometry);
        capture.Invocations.Clear();

        using var bitmap = await surface.CommitPlainAsync();

        Assert.Equal(100, bitmap.Width);
        Assert.False(hidden);
        Assert.Empty(capture.Invocations);
    }

    [Fact]
    public async Task Freeze_IsRefusedOutsideScreenshotActivity()
    {
        var capture = CaptureReturning();
        using var surface = NewSurface(capture);
        surface.ConstrainTo(OverlayActivity.Recording);

        await surface.FreezeAsync(Geometry);

        Assert.False(surface.IsFrozen);
        Assert.Empty(capture.Invocations);
    }

    [Fact]
    public async Task ConstrainTo_NonScreenshotActivity_ReturnsAFrozenSurfaceToLive()
    {
        // The gate used to be enforced only where freeze was ENTERED, never on a mode change — so a frozen
        // screenshot overlay switched to recording kept painting a stale still and stayed opaque.
        var capture = CaptureReturning();
        using var surface = NewSurface(capture);
        await surface.FreezeAsync(Geometry);
        Assert.True(surface.IsFrozen);

        surface.ConstrainTo(OverlayActivity.Recording);

        Assert.False(surface.IsFrozen);
        Assert.Null(surface.Backdrop);
        Assert.True(surface.AllowsNoActivateOverlay);
        Assert.False(surface.WantsOpaqueFullHitTest);
    }

    [Fact]
    public async Task Freeze_WhenDisplayConversionFails_StaysLiveAndDisposesTheStill()
    {
        var capture = CaptureReturning();
        using var surface = new OverlaySurface(
            capture.Object, new ImmediateCaptureVisibilityCoordinator(), Commit,
            toDisplay: _ => null); // a present converter that fails => nothing showable => stay live

        await surface.FreezeAsync(Geometry);

        Assert.False(surface.IsFrozen);
        Assert.Null(surface.Backdrop);
    }

    [Fact]
    public async Task Freeze_IsIdempotent_AndDoesNotGrabTwice()
    {
        var capture = CaptureReturning();
        using var surface = NewSurface(capture);

        await surface.FreezeAsync(Geometry);
        await surface.FreezeAsync(Geometry);

        Assert.Single(capture.Invocations);
    }

    [Fact]
    public async Task Lease_KeepsTheStillUsableAcrossAnUnfreeze()
    {
        // The AI scan hands the still to a background OCR thread. Unfreezing used to dispose it underneath.
        var capture = CaptureReturning();
        using var surface = NewSurface(capture);
        await surface.FreezeAsync(Geometry);

        using (var lease = surface.LeaseFrozenStill())
        {
            Assert.NotNull(lease);
            surface.ReturnToLive();

            Assert.False(surface.IsFrozen);
            // Still readable: disposal is deferred until the borrow is returned.
            Assert.Equal(800, lease!.Still.Width);
            Assert.Equal(1.0, lease.GrabScaling);
        }

        Assert.Null(surface.LeaseFrozenStill());
    }

    [Fact]
    public async Task FreezeFromPreOverlayGrab_TakesOwnership_AndRefusesNull()
    {
        var capture = CaptureReturning();
        using var surface = NewSurface(capture);

        surface.FreezeFromPreOverlayGrab(new SKBitmap(800, 600), 1.5);
        Assert.True(surface.IsFrozen);
        using (var lease = surface.LeaseFrozenStill())
        {
            Assert.Equal(1.5, lease!.GrabScaling);
        }

        // Unfreezing has its own verb; passing null is not how you do it.
        Assert.Throws<ArgumentNullException>(() => surface.FreezeFromPreOverlayGrab(null!, 1.0));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task FrozenCrop_UsesTheGrabScaling_NotTheOverlaysCurrentScaling()
    {
        // A DPI change after the grab moves VisualScaling; the crop must follow the still, not the overlay.
        var capture = CaptureReturning(1600, 1200);
        var driftedGeometry = new OverlayGeometry(new Size(800, 600), new PixelPoint(0, 0), 1.0);
        using var surface = new OverlaySurface(
            capture.Object,
            new ImmediateCaptureVisibilityCoordinator(),
            () => SelectionCommit.Annotated(Selection, driftedGeometry, Array.Empty<Annotation>()));
        surface.UseHeadlessBackdropForTest();

        surface.FreezeFromPreOverlayGrab(new SKBitmap(1600, 1200), grabScaling: 2.0);
        using var bitmap = await surface.CommitAsync();

        Assert.Equal(200, bitmap.Width);   // 100 DIP x grabScaling 2.0, not x VisualScaling 1.0
        Assert.Equal(100, bitmap.Height);
    }
}
