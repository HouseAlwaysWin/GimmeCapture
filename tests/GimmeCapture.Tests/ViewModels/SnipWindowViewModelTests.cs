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
