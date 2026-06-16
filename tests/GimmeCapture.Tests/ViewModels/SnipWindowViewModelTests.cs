using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Platforms.Windows;
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

    [Theory]
    [InlineData(false, SnipAutoAction.None)]
    [InlineData(true, SnipAutoAction.Pin)]
    public void ResolveAutoActionMode_Normal_UsesAutoPinSetting(bool autoPinSelection, SnipAutoAction expectedAction)
    {
        var action = SnipWindowViewModel.ResolveAutoActionMode(CaptureMode.Normal, autoPinSelection);

        Assert.Equal(expectedAction, action);
    }

    [Theory]
    [InlineData(CaptureMode.Copy, SnipAutoAction.Copy)]
    [InlineData(CaptureMode.Pin, SnipAutoAction.Pin)]
    [InlineData(CaptureMode.Record, SnipAutoAction.EnterRecordMode)]
    [InlineData(CaptureMode.Translate, SnipAutoAction.None)]
    public void ResolveAutoActionMode_PreservesExplicitCaptureModes(CaptureMode mode, SnipAutoAction expectedAction)
    {
        var action = SnipWindowViewModel.ResolveAutoActionMode(mode, autoPinScreenshotSelection: true);

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

    private sealed class FakeWindowDetectionService : IWindowDetectionService
    {
        private readonly IReadOnlyList<WindowCandidate> _candidates;
        private readonly WindowDetectionService _service = new();

        public FakeWindowDetectionService(IReadOnlyList<WindowCandidate> candidates)
        {
            _candidates = candidates;
        }

        public IReadOnlyList<WindowCandidate> GetVisibleWindowCandidates(IntPtr? excludeHWnd = null) => _candidates;

        public WindowCandidate? GetCandidateAtPoint(Point point, IReadOnlyList<WindowCandidate> candidates, WindowCandidate? previousCandidate = null)
            => _service.GetCandidateAtPoint(point, candidates, previousCandidate);
    }
}
