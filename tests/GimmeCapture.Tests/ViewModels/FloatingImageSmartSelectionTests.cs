using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Disposables;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Floating;
using SkiaSharp;

namespace GimmeCapture.Tests.ViewModels;

// A smart-selection click on a pin. Every click used to copy the whole pin on the UI thread (about 10 ms at 3840x2400)
// only for SAM2 to find it unchanged, then drew the preview and handed it to Avalonia there too (about 18 ms). The pin is
// now copied only when it changed since SAM2 prepared it, and both run off the UI thread. These drive the view model
// with FakeSam2Service: one copy too few hands SAM2 a stale pin, one too many brings the copy back.
// SmartSelectionClickUiStallTests measures the UI thread's share.
//
// The rest hold a click's mask, or a confirm's work, open while something else happens: Esc, another tool, an undo, a
// confirm, the pin changing or closing. A click's mask that comes back after its selection has moved on is dropped and
// freed, and nothing frees the mask a confirm is reading. Freed early, that mask was a native access violation that
// closed the app; FakeSam2Service's masks keep their pixels when freed here, so these fail instead.
[Collection(AvaloniaRendererCollection.Name)]
public sealed class FloatingImageSmartSelectionTests : IDisposable
{
    private const int Width = 64;
    private const int Height = 48;
    // Opaque, so the premultiplied copy SAM2 gets holds the same words.
    private const uint PinColour = 0xFF_10_20_30u;
    private const uint OtherPinColour = 0xFF_40_50_60u;
    // Lime and deep pink: the crosshairs of a point that adds to the selection and one that takes from it.
    private const uint Lime = 0xFF_00_FF_00u;
    private const uint DeepPink = 0xFF_FF_14_93u;
    // A click that never finishes fails its test instead of holding up the run. xUnit only times tests out in a
    // collection that runs on its own, which this one does.
    private const int TimeoutMs = 30_000;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", "SmartSelection", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = new();

    public FloatingImageSmartSelectionTests()
    {
        AvaloniaRenderer.EnsureRegistered();
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        for (int i = _disposables.Count - 1; i >= 0; i--)
        {
            _disposables[i].Dispose();
        }

        Directory.Delete(_directory, recursive: true);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Clicks_OnAnUnchangedPin_HandItToSam2Once()
    {
        var (pin, sam2, _) = await OpenSmartSelectionAsync();

        await pin.HandlePointClickAsync(10, 10);
        await pin.HandlePointClickAsync(20, 30, isPositive: false);
        await pin.HandlePointClickAsync(40, 12);

        // Once: the prepare the session starts in the background.
        Assert.Equal(1, sam2.PrepareCount);
        Assert.Equal(3, sam2.MaskRequests.Count);
        Assert.NotNull(pin.InteractiveMask);
        Assert.True(pin.CanConfirmInteractive);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AClick_AfterThePinChanged_HandsSam2TheNewPin_AndTheOldOneAgainAfterAnUndo()
    {
        var (pin, sam2, _) = await OpenSmartSelectionAsync();
        Bitmap original = pin.Image!;
        await pin.HandlePointClickAsync(10, 10);

        // A crop or a rotation gives the pin a new bitmap; undoing it puts the old one back.
        pin.Image = Track(AvaloniaRenderer.Create(Width, Height, (_, _) => OtherPinColour));
        await pin.HandlePointClickAsync(10, 10);
        Assert.Equal(2, sam2.PrepareCount);
        Assert.Equal(OtherPinColour, sam2.LastPreparedPixel);

        pin.Image = original;
        await pin.HandlePointClickAsync(10, 10);
        Assert.Equal(3, sam2.PrepareCount);
        Assert.Equal(PinColour, sam2.LastPreparedPixel);
        Assert.NotNull(pin.InteractiveMask);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AClick_AfterSam2LetGoOfThePin_HandsItOverAgain()
    {
        var (pin, sam2, _) = await OpenSmartSelectionAsync();
        await pin.HandlePointClickAsync(10, 10);

        // A switch of model drops the prepared image in InitializeAsync, which every click calls first.
        sam2.SwitchModelOnNextInitialize = true;
        await pin.HandlePointClickAsync(20, 20);

        Assert.Equal(2, sam2.PrepareCount);
        Assert.Equal(2, sam2.MaskRequests.Count);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AClick_WhileTheLastOneIsRunning_IsIgnored()
    {
        var (pin, sam2, _) = await OpenSmartSelectionAsync();
        // Also waits out the background prepare, so the next click goes straight to SAM2.
        await pin.HandlePointClickAsync(10, 10);

        sam2.HoldMasks();
        Task running = pin.HandlePointClickAsync(20, 20);
        Task ignored = pin.HandlePointClickAsync(30, 30);
        // Turned away at once, not queued behind the click before it.
        Assert.True(ignored.IsCompletedSuccessfully);
        sam2.ReleaseMasks();
        await running;
        await pin.HandlePointClickAsync(40, 40);

        Assert.Equal(3, sam2.MaskRequests.Count);
        Assert.Equal(new[] { (10.0, 10.0, true), (20.0, 20.0, true), (40.0, 40.0, true) }, sam2.MaskRequests[2]);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Undo_ShowsTheMaskOfThePointsLeft()
    {
        var (pin, sam2, _) = await OpenSmartSelectionAsync();
        await pin.HandlePointClickAsync(10, 10);
        await pin.HandlePointClickAsync(20, 20, isPositive: false);

        await pin.UndoLastPointAsync();

        Assert.Equal(3, sam2.MaskRequests.Count);
        Assert.Equal(new[] { (10.0, 10.0, true) }, sam2.MaskRequests[2]);
        Assert.Equal(1, sam2.PrepareCount);
        Assert.NotNull(pin.InteractiveMask);
        Assert.True(pin.CanConfirmInteractive);
    }

    [Theory(Timeout = TimeoutMs)]
    // Removing the selection: red, (255, 80, 80) at alpha 150, premultiplied as Avalonia's bitmaps are.
    [InlineData(true, 0x96_96_2F_2Fu)]
    // Keeping it, from a first click with Ctrl or Shift: green, (0, 255, 100) at alpha 150.
    [InlineData(false, 0x96_00_96_3Bu)]
    public async Task ThePreview_ColoursTheSelection_AndMarksTheClick(bool isPositive, uint selectedColour)
    {
        var (pin, _, _) = await OpenSmartSelectionAsync();

        await pin.HandlePointClickAsync(8, 8, isPositive);

        Bitmap preview = Assert.IsAssignableFrom<Bitmap>(pin.InteractiveMask);
        Assert.Equal(new PixelSize(Width, Height), preview.PixelSize);
        uint[] pixels = AvaloniaRenderer.ReadPixels(preview);
        // The mask selects the left half. Both of these are away from the marker.
        Assert.Equal(selectedColour, pixels[(40 * Width) + 24]);
        Assert.Equal(0u, pixels[(40 * Width) + 56]);
        // A first click always adds to SAM2's selection, so its crosshair is lime; 15 px right of the click is on it.
        Assert.Equal(Lime, pixels[(8 * Width) + 23]);
    }

    [Fact]
    public void PointMarkers_AreScaledFromThePinToThePreview()
    {
        // A preview half the pin's size each way.
        using var empty = TestBitmaps.Create(200, 150, SKColorType.Gray8, SKAlphaType.Opaque, (_, _) => (byte)0);
        using var preview = InteractiveMaskPixels.CreateOverlay(empty, SKColors.Red);

        FloatingImageViewModel.DrawPointMarkers(
            preview, new[] { (160.0, 100.0, true), (60.0, 240.0, false) }, imageWidth: 400, imageHeight: 300);

        uint[] pixels = TestBitmaps.ReadPixels<uint>(preview);
        // Each crosshair, 15 px right of where its point lands on the preview.
        Assert.Equal(Lime, pixels[(50 * 200) + 95]);
        Assert.Equal(DeepPink, pixels[(120 * 200) + 45]);
        // Nothing where the first would be, unscaled.
        Assert.Equal(0u, pixels[(100 * 200) + 175]);
    }

    public enum Leaving
    {
        Esc,
        AnotherTool,
        EscAndBackIn,
        PinClosed
    }

    [Theory(Timeout = TimeoutMs)]
    [InlineData(Leaving.Esc)]
    [InlineData(Leaving.AnotherTool)]
    [InlineData(Leaving.EscAndBackIn)]
    [InlineData(Leaving.PinClosed)]
    public async Task AClicksMask_ComingBackAfterTheToolWasLeft_IsDropped(Leaving leaving)
    {
        var (pin, sam2, _) = await OpenSmartSelectionAsync();
        await pin.HandlePointClickAsync(10, 10);

        sam2.HoldMasks();
        Task click = pin.HandlePointClickAsync(20, 20);
        Leave(pin, sam2, leaving);
        sam2.ReleaseMasks();
        await click;

        // Not shown, kept by no session (an old one or a new one), and freed.
        Assert.Null(pin.InteractiveMask);
        Assert.False(pin.CanConfirmInteractive);
        Assert.True(sam2.Requests[1].Mask!.IsFreed, "the mask that came back late was kept");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AClicksMask_ComingBackAfterAnUndoOfThatClick_IsDropped()
    {
        var (pin, sam2, _) = await OpenSmartSelectionAsync();
        await pin.HandlePointClickAsync(10, 10);

        // An undo is not held up by the click before it.
        sam2.HoldMasks();
        Task click = pin.HandlePointClickAsync(20, 20);
        Task undo = pin.UndoLastPointAsync();
        sam2.Requests[2].Release();
        await undo;
        Bitmap undosPreview = Assert.IsAssignableFrom<Bitmap>(pin.InteractiveMask);

        sam2.ReleaseMasks();
        await click;

        // The selection is still the undo's: the click's mask is for a point that is gone.
        Assert.Same(undosPreview, pin.InteractiveMask);
        Assert.True(pin.CanConfirmInteractive);
        Assert.True(sam2.Requests[1].Mask!.IsFreed, "the mask that came back late was kept");
        Assert.False(sam2.Requests[2].Mask!.IsFreed, "the selection's own mask was freed");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AClicksMask_ComingBackAfterAConfirm_IsDropped()
    {
        var (pin, sam2, _) = await OpenSmartSelectionAsync();
        await pin.HandlePointClickAsync(10, 10);

        // The click is still running when the undo after it has its mask and is confirmed.
        sam2.HoldMasks();
        Task click = pin.HandlePointClickAsync(20, 20);
        Task undo = pin.UndoLastPointAsync();
        sam2.Requests[2].Release();
        await undo;
        await pin.ConfirmInteractiveCommand.Execute().ToTask();
        Bitmap confirmed = pin.Image!;

        sam2.ReleaseMasks();
        await click;

        Assert.Null(pin.InteractiveMask);
        Assert.False(pin.IsPointRemovalMode);
        Assert.Same(confirmed, pin.Image);
        Assert.True(sam2.Requests[1].Mask!.IsFreed, "the mask that came back late was kept");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AClick_StillWaitingForThePinToBePrepared_WhenTheToolIsLeft_AsksSam2ForNothing()
    {
        // A click straight after the tool opens waits for the prepare the tool opened with.
        var (pin, sam2, _) = await OpenSmartSelectionAsync(holdPrepare: true);
        Task click = pin.HandlePointClickAsync(10, 10);

        Assert.True(pin.TryCancelCurrentAction());
        sam2.ReleasePrepares();
        await click;

        Assert.Empty(sam2.Requests);
        Assert.Null(pin.InteractiveMask);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AClick_AsksSam2ForItsOwnPoints_ThoughAnUndoTakesOneOffBeforeSam2ReadsThem()
    {
        var (pin, sam2, _) = await OpenSmartSelectionAsync();
        await pin.HandlePointClickAsync(10, 10);

        // SAM2 reads a click's points on the thread pool, after the UI thread has moved on: here, to an undo.
        sam2.HoldMasks();
        Task click = pin.HandlePointClickAsync(20, 20);
        Task undo = pin.UndoLastPointAsync();
        sam2.ReleaseMasks();
        await Task.WhenAll(click, undo);

        Assert.Equal(new[] { (10.0, 10.0, true), (20.0, 20.0, true) }, sam2.Requests[1].Points);
        Assert.Equal(new[] { (10.0, 10.0, true) }, sam2.Requests[2].Points);
    }

    public enum DuringConfirm
    {
        Esc,
        AnotherTool,
        PinChanged,
        PinClosed
    }

    [Theory(Timeout = TimeoutMs)]
    [InlineData(DuringConfirm.Esc, true)]
    [InlineData(DuringConfirm.Esc, false)]
    [InlineData(DuringConfirm.AnotherTool, true)]
    [InlineData(DuringConfirm.PinChanged, true)]
    [InlineData(DuringConfirm.PinClosed, true)]
    public async Task WhatHappensWhileAConfirmRuns_CannotFreeTheMaskItReads(DuringConfirm what, bool firstClickAdds)
    {
        var (pin, sam2, image) = await OpenSmartSelectionAsync();
        await pin.HandlePointClickAsync(10, 10, firstClickAdds);
        TrackedMask mask = sam2.Requests[0].Mask!;
        var otherPin = Track(AvaloniaRenderer.Create(Width, Height, (_, _) => OtherPinColour));

        image.HoldCopies();
        Task confirm = pin.ConfirmInteractiveCommand.Execute().ToTask();
        try
        {
            // The confirm's background work has the mask, and copies the pin before it reads it.
            await image.CopyWaiting;
            switch (what)
            {
                case DuringConfirm.Esc:
                    Assert.True(pin.TryCancelCurrentAction());
                    break;
                case DuringConfirm.AnotherTool:
                    pin.CurrentTool = FloatingTool.Selection;
                    break;
                case DuringConfirm.PinChanged:
                    // As an undo of a crop does.
                    pin.Image = otherPin;
                    break;
                case DuringConfirm.PinClosed:
                    pin.Dispose();
                    break;
            }

            Assert.False(mask.IsFreed, $"{what} freed the mask the confirm still had to read");
        }
        finally
        {
            image.ReleaseCopies();
        }

        await confirm;
        Assert.True(mask.IsFreed, "the confirm kept the mask");
        switch (what)
        {
            case DuringConfirm.Esc:
            case DuringConfirm.AnotherTool:
                // The confirm came first, so it lands, in the selection's own mode, and leaves the tool as it was left.
                Assert.NotSame(image, pin.Image);
                uint[] pixels = AvaloniaRenderer.ReadPixels(pin.Image!);
                Assert.Equal(firstClickAdds ? 0u : PinColour, pixels[(40 * Width) + 10]);
                Assert.Equal(firstClickAdds ? PinColour : 0u, pixels[(40 * Width) + 50]);
                Assert.Equal(what == DuringConfirm.Esc ? FloatingTool.None : FloatingTool.Selection, pin.CurrentTool);
                break;
            case DuringConfirm.PinChanged:
                // Applying it now would quietly take that change back.
                Assert.Same(otherPin, pin.Image);
                break;
            case DuringConfirm.PinClosed:
                Assert.Same(image, pin.Image);
                break;
        }
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AClicksMask_ComingBackWhileAConfirmRuns_CannotFreeTheMaskTheConfirmReads()
    {
        var (pin, sam2, image) = await OpenSmartSelectionAsync();
        await pin.HandlePointClickAsync(10, 10);

        // The click is still running when the undo after it has its mask and is confirmed.
        sam2.HoldMasks();
        Task click = pin.HandlePointClickAsync(20, 20);
        Task undo = pin.UndoLastPointAsync();
        sam2.Requests[2].Release();
        await undo;
        TrackedMask mask = sam2.Requests[2].Mask!;

        image.HoldCopies();
        Task confirm = pin.ConfirmInteractiveCommand.Execute().ToTask();
        try
        {
            await image.CopyWaiting;
            sam2.ReleaseMasks();
            await click;

            Assert.False(mask.IsFreed, "the click's late mask freed the one the confirm still had to read");
        }
        finally
        {
            image.ReleaseCopies();
        }

        await confirm;
        // The undo's selection, the left half, is the one applied.
        uint[] pixels = AvaloniaRenderer.ReadPixels(pin.Image!);
        Assert.Equal(0u, pixels[(40 * Width) + 10]);
        Assert.Equal(PinColour, pixels[(40 * Width) + 50]);
        Assert.True(mask.IsFreed, "the confirm kept the mask");
        Assert.True(sam2.Requests[1].Mask!.IsFreed, "the mask that came back late was kept");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AClickOrAnUndo_WhileAConfirmRuns_IsIgnored()
    {
        var (pin, sam2, image) = await OpenSmartSelectionAsync();
        await pin.HandlePointClickAsync(10, 10);

        image.HoldCopies();
        Task confirm = pin.ConfirmInteractiveCommand.Execute().ToTask();
        try
        {
            await image.CopyWaiting;
            await pin.HandlePointClickAsync(50, 20);
            await pin.UndoLastPointAsync();
        }
        finally
        {
            image.ReleaseCopies();
        }

        await confirm;
        Assert.Single(sam2.Requests);
        uint[] pixels = AvaloniaRenderer.ReadPixels(pin.Image!);
        Assert.Equal(0u, pixels[(40 * Width) + 10]);
        Assert.Equal(PinColour, pixels[(40 * Width) + 50]);
    }

    private static void Leave(FloatingImageViewModel pin, FakeSam2Service sam2, Leaving leaving)
    {
        switch (leaving)
        {
            case Leaving.Esc:
                Assert.True(pin.TryCancelCurrentAction());
                break;
            case Leaving.AnotherTool:
                pin.CurrentTool = FloatingTool.Selection;
                break;
            case Leaving.EscAndBackIn:
                Assert.True(pin.TryCancelCurrentAction());
                // Leaving let go of SAM2; going back in finds this one again.
                pin.SetSam2ServiceForTesting(sam2);
                pin.CurrentTool = FloatingTool.PointRemoval;
                break;
            case Leaving.PinClosed:
                pin.Dispose();
                break;
        }
    }

    // A pin in smart-selection mode whose session has handed it to SAM2, or, with holdPrepare, is still doing so. SAM2's
    // mask selects the left half.
    private async Task<(FloatingImageViewModel Pin, FakeSam2Service Sam2, HeldCopyBitmap Image)> OpenSmartSelectionAsync(bool holdPrepare = false)
    {
        // The model files only have to exist for the pin to count SAM2 as installed.
        string encoder = Path.Combine(_directory, "encoder.onnx");
        string decoder = Path.Combine(_directory, "decoder.onnx");
        File.WriteAllBytes(encoder, Array.Empty<byte>());
        File.WriteAllBytes(decoder, Array.Empty<byte>());

        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.Settings).Returns(new AppSettings());
        var paths = new Mock<AIPathService>(settings.Object);
        paths.Setup(p => p.GetSAM2Paths(It.IsAny<SAM2Variant>())).Returns((encoder, decoder));
        var ai = new AIResourceService(settings.Object, paths.Object, new Mock<AIModelDownloader>().Object);

        var mask = Track(TestBitmaps.Create(Width, Height, SKColorType.Gray8, SKAlphaType.Opaque, (x, _) => x < Width / 2 ? (byte)255 : (byte)0));
        var sam2 = new FakeSam2Service(mask, keepFreedMasks: true);
        // After the pin, which is disposed first.
        Track(Disposable.Create(sam2.FreeKeptMasks));
        var image = Track(new HeldCopyBitmap(Width, Height, (_, _) => PinColour));
        var pin = Track(new FloatingImageViewModel(
            image, Width, Height, Colors.Red, 2.0, false, false, new Mock<IClipboardService>().Object, ai,
            new SAM2RuntimeService(paths.Object), settings.Object, paths.Object, new ResourceQueueService()));
        pin.SetSam2ServiceForTesting(sam2);

        if (holdPrepare)
        {
            sam2.HoldPrepares();
        }

        pin.CurrentTool = FloatingTool.PointRemoval;
        await sam2.FirstPrepare;
        return (pin, sam2, image);
    }

    private T Track<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }
}
