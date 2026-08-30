using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Interaction;
using GimmeCapture.ViewModels;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

public class SnipWindowViewModelTests
{
    [Fact]
    public void HandleRightClick_WhenSelecting_ResetsToDetectingAndEmptyRect()
    {
        var vm = new SnipWindowViewModel();
        vm.CurrentState = SnipState.Selecting;
        vm.SelectionRect = new Rect(10, 10, 100, 100);
        bool closed = false;
        vm.CloseAction = () => closed = true;

        vm.HandleRightClick();

        Assert.Equal(SnipState.Detecting, vm.CurrentState);
        Assert.Equal(new Rect(0, 0, 0, 0), vm.SelectionRect);
        Assert.False(closed, "Window should not close when resetting selection.");
    }

    [Fact]
    public void HandleRightClick_WhenSelected_ResetsToDetectingAndEmptyRect()
    {
        var vm = new SnipWindowViewModel();
        vm.CurrentState = SnipState.Selected;
        vm.SelectionRect = new Rect(10, 10, 100, 100);
        bool closed = false;
        vm.CloseAction = () => closed = true;

        vm.HandleRightClick();

        Assert.Equal(SnipState.Detecting, vm.CurrentState);
        Assert.Equal(new Rect(0, 0, 0, 0), vm.SelectionRect);
        Assert.False(closed, "Window should not close when resetting selection.");
    }

    [Fact]
    public void HandleRightClick_WhenDetecting_ClosesWindow()
    {
        var vm = new SnipWindowViewModel();
        vm.CurrentState = SnipState.Detecting;
        bool closed = false;
        vm.CloseAction = () => closed = true;

        vm.HandleRightClick();

        Assert.True(closed, "Window should close when right-clicking in Detecting state.");
    }

    [Fact]
    public void RefreshWindowRects_KeepsTopLevelVisualsWhileHoverTargetsChildCandidate()
    {
        var topLevel = new WindowCandidate(new Rect(0, 0, 300, 200), new IntPtr(1), IntPtr.Zero, new IntPtr(1), 0, WindowCandidateKind.TopLevel);
        var child = new WindowCandidate(new Rect(40, 40, 120, 80), new IntPtr(2), new IntPtr(1), new IntPtr(1), 1, WindowCandidateKind.ChildControl);
        var detection = new FakeWindowDetectionService([topLevel, child]);
        var capture = new Mock<IScreenCaptureService>(MockBehavior.Strict);
        var vm = new SnipWindowViewModel(
            Colors.Red,
            2.0,
            captureService: capture.Object,
            detectionService: detection);
        vm.ScreenOffset = default;
        vm.VisualScaling = 1;

        vm.RefreshWindowRects();
        vm.UpdateWindowHover(new Point(60, 60));

        Assert.Single(vm.WindowRects);
        Assert.Equal(topLevel.Bounds, new Rect(vm.WindowRects[0].X, vm.WindowRects[0].Y, vm.WindowRects[0].Width, vm.WindowRects[0].Height));
        Assert.Equal(child.Bounds, vm.HoverTargetRect);
        Assert.Equal(child.Bounds, vm.AnimatedHoverRect);
        Assert.True(vm.IsHoverPreviewVisible);

        vm.Dispose();
    }

    [Fact]
    public void EnteringSelectedState_ClearsTranslatedBlocks()
    {
        var vm = new SnipWindowViewModel();
        vm.TranslatedBlocks.Add(new TranslatedBlock
        {
            OriginalText = "before",
            TranslatedText = "after",
            Bounds = new Rect(0, 0, 10, 10)
        });

        vm.CurrentState = SnipState.Selected;

        Assert.Empty(vm.TranslatedBlocks);
    }

    [Fact]
    public async Task EnteringSelectedState_AppliesDefaultHideSnipToolbarSetting()
    {
        var mainVm = new MainWindowViewModel();
        await mainVm.InitialSettingsLoadTask; // let the async settings load settle before setting the flag
        mainVm.DefaultHideSnipToolbar = true;
        using var vm = new SnipWindowViewModel(Colors.Red, 2.0, recService: null, mainVm);

        Assert.True(vm.ShowToolbar);

        vm.SelectionRect = new Rect(10, 10, 120, 80);
        vm.CurrentState = SnipState.Selecting;
        vm.CurrentState = SnipState.Selected;

        Assert.False(vm.ShowToolbar);
        Assert.False(vm.IsToolbarShownOnScreen);
        Assert.True(vm.ToolbarLeft < -10000);
    }

    [Fact]
    public async Task EnteringSelectedRecordingState_AppliesDefaultHideRecordToolbarSetting()
    {
        var mainVm = new MainWindowViewModel();
        await mainVm.InitialSettingsLoadTask; // let the async settings load settle before setting the flag
        mainVm.DefaultHideRecordToolbar = true;
        using var vm = new SnipWindowViewModel(Colors.Red, 2.0, recService: null, mainVm);

        vm.CurrentMode = SnipMode.Recording;
        vm.SelectionRect = new Rect(10, 10, 120, 80);
        vm.CurrentState = SnipState.Selecting;
        vm.CurrentState = SnipState.Selected;

        Assert.False(vm.ShowToolbar);
        Assert.False(vm.IsToolbarShownOnScreen);
        Assert.True(vm.ToolbarLeft < -10000);
    }

    [Fact]
    public async Task ShowingDefaultHiddenToolbar_RestoresOnScreenInteraction()
    {
        var mainVm = new MainWindowViewModel();
        await mainVm.InitialSettingsLoadTask; // let the async settings load settle before setting the flag
        mainVm.DefaultHideSnipToolbar = true;
        using var vm = new SnipWindowViewModel(Colors.Red, 2.0, recService: null, mainVm);
        vm.SelectionRect = new Rect(10, 10, 120, 80);
        vm.ToolbarWidth = 300;
        vm.ToolbarHeight = 40;
        vm.CurrentState = SnipState.Selecting;
        vm.CurrentState = SnipState.Selected;

        vm.ShowToolbar = true;

        Assert.True(vm.IsToolbarShownOnScreen);
        Assert.True(vm.ToolbarLeft > -10000);
    }

    [Fact]
    public void ResolveAutoActionMode_Normal_ReturnsNone()
    {
        // A plain screenshot has no auto-action (the old auto-pin-on-normal branch was removed when lock-selection
        // was consolidated into freeze-frame).
        var action = SnipWindowViewModel.ResolveAutoActionMode(CaptureMode.Normal);

        Assert.Equal(SnipAutoAction.None, action);
    }

    [Fact]
    public void SelectedSnapshotLock_OnlyLocksSelectedScreenshotState()
    {
        using var vm = new SnipWindowViewModel();

        vm.LockSelectedScreenshotSelection = true;
        vm.SelectionRect = new Rect(10, 10, 120, 80);
        vm.CurrentMode = SnipMode.Screenshot;
        vm.CurrentState = SnipState.Selected;

        Assert.True(vm.IsSelectionSnapshotLocked);

        vm.CurrentMode = SnipMode.Recording;

        Assert.False(vm.IsSelectionSnapshotLocked);
    }

    [Theory]
    [InlineData(CaptureMode.Copy, SnipAutoAction.Copy)]
    [InlineData(CaptureMode.Pin, SnipAutoAction.Pin)]
    [InlineData(CaptureMode.Record, SnipAutoAction.EnterRecordMode)]
    [InlineData(CaptureMode.Translate, SnipAutoAction.None)]
    [InlineData(CaptureMode.TextCopy, SnipAutoAction.TextCopy)]
    public void ResolveAutoActionMode_PreservesExplicitCaptureModes(CaptureMode mode, SnipAutoAction expectedAction)
    {
        var action = SnipWindowViewModel.ResolveAutoActionMode(mode);

        Assert.Equal(expectedAction, action);
    }

    [Fact]
    public void AudioMeterTimer_ShouldRunOnlyInRecordingMode()
    {
        var vm = new SnipWindowViewModel();
        try
        {
            Assert.False(vm.IsAudioMeterTimerEnabledForTesting);

            vm.CurrentMode = SnipMode.Recording;
            Assert.True(vm.IsAudioMeterTimerEnabledForTesting);

            vm.CurrentMode = SnipMode.Translation;
            Assert.False(vm.IsAudioMeterTimerEnabledForTesting);

            vm.CurrentMode = SnipMode.Screenshot;
            Assert.False(vm.IsAudioMeterTimerEnabledForTesting);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Theory]
    [InlineData(RecordingState.Idle, SnipState.Selected, false, (int)RecordingPinAction.StartRecording)]
    [InlineData(RecordingState.Idle, SnipState.Selected, true, (int)RecordingPinAction.StartRecording)]
    [InlineData(RecordingState.Recording, SnipState.Selected, false, (int)RecordingPinAction.PinRecording)]
    [InlineData(RecordingState.Paused, SnipState.Selected, false, (int)RecordingPinAction.PinRecording)]
    [InlineData(RecordingState.Idle, SnipState.Detecting, true, (int)RecordingPinAction.PinRecording)]
    [InlineData(RecordingState.Idle, SnipState.Detecting, false, (int)RecordingPinAction.None)]
    public void ResolveRecordingPinAction_PrioritizesNewSelectedRecording(
        RecordingState recordingState,
        SnipState currentState,
        bool hasCurrentRecording,
        int expected)
    {
        Assert.Equal(
            (RecordingPinAction)expected,
            SnipWindowViewModel.ResolveRecordingPinAction(
                recordingState,
                currentState,
                hasCurrentRecording));
    }

    [Fact]
    public void SelectionChanges_RefreshInteractionRegionWithoutScreenMask()
    {
        var vm = new SnipWindowViewModel();
        try
        {
            int initialRevision = vm.InteractionRegionRevision;

            vm.SelectionRect = new Rect(20, 30, 200, 100);

            Assert.True(vm.InteractionRegionRevision > initialRevision);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task EnterTranslationOcrSearch_WhenAlreadyActive_DoesNotClearVisibleCandidates()
    {
        var vm = new SnipWindowViewModel();
        try
        {
            vm.CurrentMode = SnipMode.Translation;
            await vm.EnterTranslationOcrSearchAsync();
            vm.TranslationOcrSearchRects.Add(new VisualRect(new Rect(10, 20, 100, 30)));

            await vm.EnterTranslationOcrSearchAsync();

            Assert.Single(vm.TranslationOcrSearchRects);
            Assert.True(vm.IsTranslationOcrSearchActive);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task ToggleFreezeFrameLive_WhenLive_GrabsWholeDesktopWithViewportGeometry()
    {
        // The toolbar's live freeze button grabs the WHOLE desktop (viewport rect at ScreenOffset/VisualScaling),
        // exactly like the OCR full-screen grab and the factory's pre-overlay freeze — so the frozen still lines up
        // with the selection math (which crops SelectionRect × scaling out of it).
        var capture = new Mock<IScreenCaptureService>();
        capture
            .Setup(c => c.CaptureScreenAsync(It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>(), It.IsAny<bool>()))
            .ReturnsAsync(new SkiaSharp.SKBitmap(4, 4));
        var vm = new SnipWindowViewModel(Colors.Red, 2.0, capture.Object);
        try
        {
            vm.ViewportSize = new Size(800, 600);
            vm.ScreenOffset = new PixelPoint(0, 0);
            vm.VisualScaling = 1.0;

            await vm.ToggleFreezeFrameLiveAsync();

            capture.Verify(
                c => c.CaptureScreenAsync(new Rect(0, 0, 800, 600), new PixelPoint(0, 0), 1.0, false),
                Times.Once);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task ToggleFreezeFrameLive_InRecordingMode_IsNoOp()
    {
        // Recording (like translation / scrolling) is inherently live — the button is hidden there and the command
        // must not grab or freeze if it is ever invoked (e.g. by a stray hotkey) in those modes.
        var capture = new Mock<IScreenCaptureService>();
        var vm = new SnipWindowViewModel(Colors.Red, 2.0, capture.Object);
        try
        {
            vm.CurrentMode = SnipMode.Recording;
            vm.ViewportSize = new Size(800, 600);

            await vm.ToggleFreezeFrameLiveAsync();

            Assert.False(vm.Surface.IsFrozen);
            capture.Verify(
                c => c.CaptureScreenAsync(It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>(), It.IsAny<bool>()),
                Times.Never);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task CaptureCommit_WhenFrozen_ReadsTheStillAndNeitherHidesNorGrabsLive()
    {
        // Every capture action commits through the surface; none may reach for the visibility coordinator or a
        // live grab itself. A path that did (Upload, since removed) dropped the frozen overlay in freeze-frame
        // mode and captured whatever was on screen right then — the tray flyout or dropdown the still had been
        // taken for was long gone. Copy stands in for the shared ritual here.
        var capture = new Mock<IScreenCaptureService>();
        var coordinator = new Mock<ICaptureVisibilityCoordinator>();
        coordinator
            .Setup(c => c.HideAndWaitForCaptureAsync(It.IsAny<Action>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mainVm = new MainWindowViewModel();
        await mainVm.InitialSettingsLoadTask;
        mainVm.EnableHistory = false; // keep the commit clipboard-only; history would write a file

        using var vm = new SnipWindowViewModel(
            Colors.Red, 2.0, capture.Object, null, null, mainVm, null, null, null, null, coordinator.Object);
        vm.Surface.UseHeadlessBackdropForTest();
        vm.Surface.FreezeFromPreOverlayGrab(new SkiaSharp.SKBitmap(800, 600), 1.0);
        vm.SelectionRect = new Rect(10, 10, 40, 30);

        await vm.RunCopyCaptureForTestAsync();

        Assert.True(vm.Surface.IsFrozen);
        coordinator.Verify(
            c => c.HideAndWaitForCaptureAsync(It.IsAny<Action>(), It.IsAny<CancellationToken>()),
            Times.Never);
        capture.Verify(
            c => c.CaptureScreenWithAnnotationsAsync(
                It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>(),
                It.IsAny<IEnumerable<Annotation>>(), It.IsAny<IEnumerable<UserSelectionRect>>(),
                It.IsAny<IEnumerable<TranslatedBlock>>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Theory]
    [InlineData(true, "StatusCopied")]
    [InlineData(false, "StatusCopyFailed")]
    public async Task CopyCapture_ReportsWhetherTheClipboardWriteActuallyLanded(bool writeLanded, string expectedStatusKey)
    {
        // A clipboard write that loses the race for the clipboard (a clipboard manager / Win+V history / RDP
        // clipboard sync holding it open longer than the retry budget) leaves the PREVIOUS content in place. The
        // copy used to report "copied" regardless, so the next paste silently produced the previous capture with
        // nothing on screen saying otherwise. The status must follow the write's actual outcome.
        var capture = new Mock<IScreenCaptureService>();
        capture
            .Setup(c => c.CaptureScreenWithAnnotationsAsync(
                It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>(),
                It.IsAny<IEnumerable<Annotation>>(), It.IsAny<IEnumerable<UserSelectionRect>>(),
                It.IsAny<IEnumerable<TranslatedBlock>>(), It.IsAny<bool>()))
            .ReturnsAsync(new SkiaSharp.SKBitmap(40, 30));
        capture
            .Setup(c => c.CopyToClipboardAsync(It.IsAny<SkiaSharp.SKBitmap>()))
            .ReturnsAsync(writeLanded);

        var coordinator = new Mock<ICaptureVisibilityCoordinator>();
        coordinator
            .Setup(c => c.HideAndWaitForCaptureAsync(It.IsAny<Action>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mainVm = new MainWindowViewModel();
        await mainVm.InitialSettingsLoadTask;
        mainVm.EnableHistory = false; // keep the commit clipboard-only; history would write a file

        using var vm = new SnipWindowViewModel(
            Colors.Red, 2.0, capture.Object, null, null, mainVm, null, null, null, null, coordinator.Object);
        vm.SelectionRect = new Rect(10, 10, 40, 30);

        await vm.RunCopyCaptureForTestAsync();

        Assert.Equal(LocalizationService.Instance[expectedStatusKey], mainVm.StatusText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CopyCapture_AsksForAPreviewOfWhatLanded_OnlyWhenTheWriteSucceeded(bool writeLanded)
    {
        // "Copied!" never says WHICH image is on the clipboard, so the confirmation carries a thumbnail of what
        // was written. On a FAILED write the clipboard still holds its previous content — a thumbnail of the
        // image that did not land would assert exactly the wrong thing, so none is requested.
        var capture = new Mock<IScreenCaptureService>();
        capture
            .Setup(c => c.CaptureScreenWithAnnotationsAsync(
                It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>(),
                It.IsAny<IEnumerable<Annotation>>(), It.IsAny<IEnumerable<UserSelectionRect>>(),
                It.IsAny<IEnumerable<TranslatedBlock>>(), It.IsAny<bool>()))
            .ReturnsAsync(new SkiaSharp.SKBitmap(400, 300));
        capture
            .Setup(c => c.CopyToClipboardAsync(It.IsAny<SkiaSharp.SKBitmap>()))
            .ReturnsAsync(writeLanded);

        var coordinator = new Mock<ICaptureVisibilityCoordinator>();
        coordinator
            .Setup(c => c.HideAndWaitForCaptureAsync(It.IsAny<Action>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mainVm = new MainWindowViewModel();
        await mainVm.InitialSettingsLoadTask;
        mainVm.EnableHistory = false;

        int previewToasts = 0, plainToasts = 0;
        mainVm.ShowPreviewToastAction = (_, _, _) => previewToasts++;
        mainVm.ShowToastAction = (_, _) => plainToasts++;

        using var vm = new SnipWindowViewModel(
            Colors.Red, 2.0, capture.Object, null, null, mainVm, null, null, null, null, coordinator.Object);
        vm.SelectionRect = new Rect(10, 10, 400, 300);

        // The real thumbnail needs a render platform this test class does not stand up, so record the request
        // and hand back nothing — which doubles as the "thumbnail unavailable" case asserted below.
        var previewedSizes = new List<(int Width, int Height)>();
        vm.UseClipboardPreviewFactoryForTest(source =>
        {
            previewedSizes.Add((source.Width, source.Height));
            return null;
        });

        await vm.RunCopyCaptureForTestAsync();

        if (writeLanded)
        {
            // Asked exactly once, and for the image that was actually written — not a stale one.
            Assert.Equal([(400, 300)], previewedSizes);
        }
        else
        {
            Assert.Empty(previewedSizes);
        }

        // Either way the user is still told: a thumbnail that cannot be built must cost the preview, never the
        // confirmation itself.
        Assert.Equal(0, previewToasts);
        Assert.Equal(1, plainToasts);
        Assert.Equal(
            LocalizationService.Instance[writeLanded ? "StatusCopied" : "StatusCopyFailed"],
            mainVm.StatusText);
    }

    [Fact]
    public async Task CopyCapture_WhenTheClipboardWriteIsSlow_KeepsAStandInSpinnerUpUntilItLands()
    {
        // A live commit hides the overlay to grab clean pixels, taking the in-overlay spinner with it — so a
        // clipboard write that stalls behind another app holding the clipboard ran with a completely clear
        // screen. The clipboard still holds its PREVIOUS content for that whole window, so "looks finished"
        // meant "paste the previous capture". A stand-in spinner must be up while the write is in flight, and
        // gone once it lands.
        var capture = new Mock<IScreenCaptureService>();
        capture
            .Setup(c => c.CaptureScreenWithAnnotationsAsync(
                It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>(),
                It.IsAny<IEnumerable<Annotation>>(), It.IsAny<IEnumerable<UserSelectionRect>>(),
                It.IsAny<IEnumerable<TranslatedBlock>>(), It.IsAny<bool>()))
            .ReturnsAsync(new SkiaSharp.SKBitmap(40, 30));

        var writeLanded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture
            .Setup(c => c.CopyToClipboardAsync(It.IsAny<SkiaSharp.SKBitmap>()))
            .Returns(writeLanded.Task);

        var coordinator = new Mock<ICaptureVisibilityCoordinator>();
        coordinator
            .Setup(c => c.HideAndWaitForCaptureAsync(It.IsAny<Action>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mainVm = new MainWindowViewModel();
        await mainVm.InitialSettingsLoadTask;
        mainVm.EnableHistory = false; // keep the commit clipboard-only; history would write a file

        using var vm = new SnipWindowViewModel(
            Colors.Red, 2.0, capture.Object, null, null, mainVm, null, null, null, null, coordinator.Object);
        vm.SelectionRect = new Rect(10, 10, 40, 30);

        vm.UseStandaloneSpinnerDelayForTest(TimeSpan.Zero); // assert on the branch, not on a wall-clock timer

        int shown = 0, hidden = 0;
        vm.ShowProcessingWindowAction = () => shown++;
        vm.HideProcessingWindowAction = () => hidden++;

        var copy = vm.RunCopyCaptureForTestAsync();

        // The write is still in flight: the stand-in must come up and stay up.
        await WaitUntilAsync(() => Volatile.Read(ref shown) > 0, TimeSpan.FromSeconds(20));
        Assert.Equal(0, hidden);

        writeLanded.SetResult(true);
        await copy;

        Assert.Equal(1, shown);
        Assert.Equal(1, hidden);
        Assert.Equal(LocalizationService.Instance["StatusCopied"], mainVm.StatusText);
    }

    [Fact]
    public async Task CopyCapture_WhenTheClipboardWriteIsFast_NeverFlashesTheStandInSpinner()
    {
        // The stand-in exists for stalled writes; an ordinary instant copy must not pop a window on screen.
        var capture = new Mock<IScreenCaptureService>();
        capture
            .Setup(c => c.CaptureScreenWithAnnotationsAsync(
                It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>(),
                It.IsAny<IEnumerable<Annotation>>(), It.IsAny<IEnumerable<UserSelectionRect>>(),
                It.IsAny<IEnumerable<TranslatedBlock>>(), It.IsAny<bool>()))
            .ReturnsAsync(new SkiaSharp.SKBitmap(40, 30));
        capture
            .Setup(c => c.CopyToClipboardAsync(It.IsAny<SkiaSharp.SKBitmap>()))
            .ReturnsAsync(true);

        var coordinator = new Mock<ICaptureVisibilityCoordinator>();
        coordinator
            .Setup(c => c.HideAndWaitForCaptureAsync(It.IsAny<Action>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mainVm = new MainWindowViewModel();
        await mainVm.InitialSettingsLoadTask;
        mainVm.EnableHistory = false;

        using var vm = new SnipWindowViewModel(
            Colors.Red, 2.0, capture.Object, null, null, mainVm, null, null, null, null, coordinator.Object);
        vm.SelectionRect = new Rect(10, 10, 40, 30);

        // A delay that cannot elapse within the test: if the stand-in still appeared, the anti-flash gate is gone.
        vm.UseStandaloneSpinnerDelayForTest(TimeSpan.FromMinutes(5));

        int shown = 0;
        vm.ShowProcessingWindowAction = () => shown++;

        await vm.RunCopyCaptureForTestAsync();

        Assert.Equal(0, shown);
    }

    [Fact]
    public async Task CopyCapture_WhenFrozen_UsesTheOverlaySpinnerRatherThanAStandIn()
    {
        // A frozen commit crops the still in place and leaves the overlay — and the spinner it hosts — on screen.
        // The stand-in exists only to replace a spinner the live path hid; raising it here would put a second
        // spinner on top of the first.
        var capture = new Mock<IScreenCaptureService>();
        var writeLanded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture
            .Setup(c => c.CopyToClipboardAsync(It.IsAny<SkiaSharp.SKBitmap>()))
            .Returns(writeLanded.Task);

        var coordinator = new Mock<ICaptureVisibilityCoordinator>();
        var mainVm = new MainWindowViewModel();
        await mainVm.InitialSettingsLoadTask;
        mainVm.EnableHistory = false;

        using var vm = new SnipWindowViewModel(
            Colors.Red, 2.0, capture.Object, null, null, mainVm, null, null, null, null, coordinator.Object);
        vm.Surface.UseHeadlessBackdropForTest();
        vm.Surface.FreezeFromPreOverlayGrab(new SkiaSharp.SKBitmap(800, 600), 1.0);
        vm.SelectionRect = new Rect(10, 10, 40, 30);

        // Zero delay, so a stand-in would appear immediately if the frozen branch did not gate it.
        vm.UseStandaloneSpinnerDelayForTest(TimeSpan.Zero);

        int shown = 0;
        vm.ShowProcessingWindowAction = () => shown++;

        var copy = vm.RunCopyCaptureForTestAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(200)); // give a stand-in every chance to appear
        writeLanded.SetResult(true);
        await copy;

        Assert.Equal(0, shown);
        Assert.False(vm.ShowProcessingOverlay); // the overlay spinner is torn down at the end, as before
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met before the timeout elapsed.");
            }

            await Task.Delay(25);
        }
    }

    [Fact]
    public void SwitchingMode_DropsTheFrozenStill()
    {
        // Freeze is screenshot-only, but the gate used to be enforced only at the freeze entrances — so a
        // frozen overlay switched to Recording kept painting a stale still and stayed opaque with no
        // pass-through hole.
        var capture = new Mock<IScreenCaptureService>();
        using var vm = new SnipWindowViewModel(Colors.Red, 2.0, capture.Object);
        vm.Surface.UseHeadlessBackdropForTest();
        vm.Surface.FreezeFromPreOverlayGrab(new SkiaSharp.SKBitmap(800, 600), 1.0);
        Assert.True(vm.Surface.IsFrozen);

        vm.CurrentMode = SnipMode.Recording;

        Assert.False(vm.Surface.IsFrozen);
        Assert.False(vm.Surface.WantsOpaqueFullHitTest);
    }

    [Fact]
    public async Task CaptureCommit_WhenLive_HidesTheOverlayAndGrabsLive()
    {
        // The live path: hide the overlay, wait for it to be off-screen, then grab.
        var capture = new Mock<IScreenCaptureService>();
        capture
            .Setup(c => c.CaptureScreenWithAnnotationsAsync(
                It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>(),
                It.IsAny<IEnumerable<Annotation>>(), It.IsAny<IEnumerable<UserSelectionRect>>(),
                It.IsAny<IEnumerable<TranslatedBlock>>(), It.IsAny<bool>()))
            .ReturnsAsync(new SkiaSharp.SKBitmap(40, 30));
        var coordinator = new Mock<ICaptureVisibilityCoordinator>();
        coordinator
            .Setup(c => c.HideAndWaitForCaptureAsync(It.IsAny<Action>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mainVm = new MainWindowViewModel();
        await mainVm.InitialSettingsLoadTask;
        mainVm.EnableHistory = false; // keep the commit clipboard-only; history would write a file

        using var vm = new SnipWindowViewModel(
            Colors.Red, 2.0, capture.Object, null, null, mainVm, null, null, null, null, coordinator.Object);
        vm.SelectionRect = new Rect(10, 10, 40, 30);

        await vm.RunCopyCaptureForTestAsync();

        Assert.False(vm.Surface.IsFrozen);
        coordinator.Verify(
            c => c.HideAndWaitForCaptureAsync(It.IsAny<Action>(), It.IsAny<CancellationToken>()),
            Times.Once);
        capture.Verify(
            c => c.CaptureScreenWithAnnotationsAsync(
                It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>(),
                It.IsAny<IEnumerable<Annotation>>(), It.IsAny<IEnumerable<UserSelectionRect>>(),
                It.IsAny<IEnumerable<TranslatedBlock>>(), It.IsAny<bool>()),
            Times.Once);
    }

    private sealed class FakeWindowDetectionService : IWindowDetectionService
    {
        private readonly IReadOnlyList<WindowCandidate> _candidates;

        public FakeWindowDetectionService(IReadOnlyList<WindowCandidate> candidates)
        {
            _candidates = candidates;
        }

        public IReadOnlyList<WindowCandidate> GetVisibleWindowCandidates(IntPtr? excludeHWnd = null) => _candidates;

        public WindowCandidate? GetCandidateAtPoint(Point point, IReadOnlyList<WindowCandidate> candidates, WindowCandidate? previousCandidate = null)
            => WindowCandidateHitTester.GetCandidateAtPoint(point, candidates, previousCandidate);

        public IReadOnlyList<RecordableWindow> GetRecordableWindows(IntPtr? excludeHWnd = null) => [];
    }
}
