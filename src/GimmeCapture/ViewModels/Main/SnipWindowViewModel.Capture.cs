using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using GimmeCapture.Services.Abstractions;
using SkiaSharp;
using GimmeCapture.Services.Platforms.Avalonia;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
    private CancellationTokenSource? _quickOcrCts;
    private int _quickOcrRunning;

    private async Task ExecuteTextCopyAsync()
    {
        if (_quickOcrService == null || _mainVm == null
            || SelectionRect.Width <= 0 || SelectionRect.Height <= 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _quickOcrRunning, 1, 0) != 0)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _quickOcrCts = cts;

        try
        {
            await HideOverlayForCaptureUnlessFrozenAsync(cts.Token);

            _isLocalProcessing = true;
            ShowProcessingOverlay = true;
            IsIndeterminate = true;
            ProcessingText = LocalizationService.Instance["QuickOcrProcessing"];

            using var bitmap = await CaptureOrCropSelectionAsync(includeAnnotations: false);

            // The snip overlay (which hosts ShowProcessingOverlay) was just hidden to grab a clean capture, so
            // surface a standalone "recognizing…" spinner while OCR runs — otherwise the multi-second recognition
            // looks like the app froze. Shown AFTER the capture so it isn't grabbed into the OCR image; OCR itself
            // runs on a background thread (RecognizeAsync uses Task.Run), so the spinner stays animated.
            ShowProcessingWindowAction?.Invoke();

            var result = await _quickOcrService.RecognizeAsync(
                bitmap,
                _mainVm.SourceLanguage,
                _mainVm.OcrTextLayout,
                cts.Token);

            switch (result.Status)
            {
                case QuickOcrStatus.Success:
                    bool copied = await _captureService.CopyToClipboardAsync(result.Text);
                    string statusKey = copied ? "QuickOcrCopied" : "QuickOcrCopyFailed";
                    if (copied && _mainVm.SaveOcrTextToFile)
                    {
                        // Best-effort side channel: also persist the text as a .txt in the save directory
                        // (same fallback as screenshot auto-save when no directory is configured).
                        var exportDir = _mainVm.SaveDirectory;
                        if (string.IsNullOrEmpty(exportDir))
                        {
                            exportDir = System.IO.Path.Combine(
                                System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures),
                                "GimmeCapture");
                        }

                        string? exportedPath = await OcrTextExportService.WriteAsync(
                            result.Text, exportDir, _mainVm.FileNameTemplate);
                        statusKey = exportedPath != null ? "QuickOcrSavedToFile" : "QuickOcrSaveFailed";
                    }
                    _mainVm.SetOcrStatus(statusKey, result.Language);
                    break;
                case QuickOcrStatus.ModuleMissing:
                    _mainVm.SetStatus("QuickOcrModuleMissing");
                    _mainVm.RequestOpenModulesAction?.Invoke();
                    break;
                case QuickOcrStatus.NoText:
                    _mainVm.SetOcrStatus("QuickOcrNoText", result.Language);
                    break;
                default:
                    _mainVm.SetStatus("QuickOcrFailed");
                    break;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Expected when the snip window is closed while OCR is still running.
        }
        finally
        {
            if (ReferenceEquals(_quickOcrCts, cts))
            {
                _quickOcrCts = null;
            }

            Volatile.Write(ref _quickOcrRunning, 0);
            _isLocalProcessing = false;
            ShowProcessingOverlay = false;
            HideProcessingWindowAction?.Invoke();
            CloseAction?.Invoke();
        }
    }

    // Test hook: drives the private Quick-OCR text-copy flow directly (the production trigger posts it to the
    // dispatcher), so the processing-spinner ordering/teardown invariants can be unit-tested without a UI thread.
    internal Task RunQuickOcrTextCopyForTestAsync() => ExecuteTextCopyAsync();


    private async Task ExecuteCopyAsync()
    {
        // If recording is processing, ignore copy command to prevent overwriting with screenshot
        if (_isProcessingRecording) return;

        // If recording is active or we are in recording mode with a valid path, use CopyRecording
        if (CurrentMode == SnipMode.Recording)
        {
            var lastPath = _recordingService?.LastRecordingPath;
            bool hasVideo = !string.IsNullOrEmpty(lastPath) && System.IO.File.Exists(lastPath);

            if (RecState == RecordingState.Recording || RecState == RecordingState.Paused || hasVideo)
            {
                await CopyRecording();
                return;
            }
        }

        if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
        {
            await RunCaptureActionAsync(CaptureMode.Copy, ExecuteCopyCaptureAsync);
        }
    }

    /// <summary>
    /// Hides the overlay before a LIVE grab — but in freeze-frame mode there is nothing to hide (the frozen still
    /// was taken before the overlay ever existed), so this is a no-op there.
    /// </summary>
    private async Task HideOverlayForCaptureUnlessFrozenAsync(System.Threading.CancellationToken ct = default)
    {
        if (IsFrozenFrameActive && FrozenScreenSkBitmap != null) return;
        await _captureVisibilityCoordinator.HideAndWaitForCaptureAsync(HideAction ?? (() => { }), ct);
    }

    /// <summary>
    /// Returns the selection as a bitmap: in freeze-frame mode, cropped from the pre-grabbed still (annotations
    /// composited on top); otherwise a live grab (annotated for screenshots, plain for OCR). Call after
    /// <see cref="HideOverlayForCaptureUnlessFrozenAsync"/>.
    /// </summary>
    private async Task<SkiaSharp.SKBitmap> CaptureOrCropSelectionAsync(bool includeAnnotations)
    {
        if (IsFrozenFrameActive && FrozenScreenSkBitmap != null)
        {
            return GimmeCapture.Services.Core.Rendering.FreezeFrameCompositor.CropWithAnnotations(
                FrozenScreenSkBitmap, SelectionRect, FrozenGrabScaling, includeAnnotations ? Annotations : null);
        }

        return includeAnnotations
            ? await _captureService.CaptureScreenWithAnnotationsAsync(
                SelectionRect, ScreenOffset, VisualScaling, Annotations,
                GetTranslationSelectionsForCapture(), TranslatedBlocks, _mainVm?.ShowSnipCursor ?? false)
            : await _captureService.CaptureScreenAsync(SelectionRect, ScreenOffset, VisualScaling, includeCursor: false);
    }

    /// <summary>
    /// Toolbar "freeze screen" button: toggles THIS overlay's freeze state live, with immediate effect — unlike the
    /// FreezeScreenOnScreenshot setting, which only decides the initial state of the NEXT capture. Unfreeze drops
    /// the still and returns to the live see-through overlay; freeze grabs the whole desktop right now (the overlay
    /// is excluded from that grab, so it isn't baked into the still and there is no flicker) and switches to the
    /// opaque still. Screenshot family only — recording/translation/scrolling are inherently live (the toolbar
    /// button is hidden there; these guards also make the command a no-op if invoked by hotkey in those modes).
    /// NOTE: freezing here happens AFTER the overlay is already up, so it can't recover a shell "light dismiss"
    /// popup (tray flyout / Start menu) the overlay already closed — that still needs the setting ON so the still
    /// predates the overlay. This button freezes/holds whatever is currently on screen.
    /// </summary>
    internal async Task ToggleFreezeFrameLiveAsync()
    {
        if (CurrentMode == SnipMode.Recording || CurrentMode == SnipMode.Translation || _manualScrollActive)
        {
            return;
        }

        if (IsFrozenFrameActive)
        {
            // Frozen → live: drop the still (SetFrozenScreenSnapshot disposes it) and re-run the region logic, which
            // now takes the live see-through / pass-through branch instead of the opaque freeze branch.
            SetFrozenScreenSnapshot(null, 1.0);
            RefreshInteractionRegion();
            return;
        }

        // Live → frozen: grab the whole desktop now. ViewportSize / ScreenOffset / VisualScaling are the same trio
        // the OCR full-screen grab and the factory's pre-overlay freeze grab use.
        if (ViewportSize.Width <= 1 || ViewportSize.Height <= 1)
        {
            return;
        }

        double grabScaling = VisualScaling <= 0 ? 1.0 : VisualScaling;
        var grabRegion = new Rect(0, 0, ViewportSize.Width, ViewportSize.Height);
        Func<Task<SKBitmap>> grab =
            () => _captureService.CaptureScreenAsync(grabRegion, ScreenOffset, grabScaling, includeCursor: false);

        SKBitmap? frozen;
        try
        {
            // The View-wired excluded-grab wrapper hides the overlay from the screen grab via WDA_EXCLUDEFROMCAPTURE
            // for ~50 ms so the still doesn't include the overlay's own chrome — the overlay stays visible, so the
            // user sees no flicker. Falls back to a bare grab off-Windows / in design.
            frozen = RunTranslationOcrGrabExcludedAsync != null
                ? await RunTranslationOcrGrabExcludedAsync(grab)
                : await grab();
        }
        catch (Exception ex)
        {
            AppLog.Warning("SnipWindowViewModel.ToggleFreezeFrameLive", ex);
            return;
        }

        // Transfers ownership + activates freeze-frame mode (or resets to live if the display conversion failed);
        // then re-run the region logic so the overlay becomes opaque + fully hit-testable over the still.
        SetFrozenScreenSnapshot(frozen, grabScaling);
        RefreshInteractionRegion();
    }

    private async Task ExecuteCopyCaptureAsync()
    {
        await HideOverlayForCaptureUnlessFrozenAsync();

        try
        {
            _isLocalProcessing = true;
            ShowProcessingOverlay = true;
            IsIndeterminate = true;
            ProcessingText = LocalizationService.Instance["StatusProcessing"] ?? "Processing...";
            using var bitmap = await CaptureOrCropSelectionAsync(includeAnnotations: true);
            await _captureService.CopyToClipboardAsync(bitmap);
            _mainVm?.SetStatus("StatusCopied");

            // Copies are clipboard-only by default; when history is on, persist a managed copy so it
            // shows up in the history panel (and is cleaned up on remove/prune).
            if (_mainVm != null && _mainVm.EnableHistory)
            {
                var copyPath = _mainVm.CaptureHistory.CreateManagedCapturePath("png");
                await _captureService.SaveToFileAsync(bitmap, copyPath);
                _mainVm.CaptureHistory.AddImageAsync(copyPath, GimmeCapture.Models.CaptureHistorySource.PlainCopy).Forget("CaptureHistory.AddCopy");
            }
        }
        finally
        {
            PersistTranslatedSelectionsAfterCaptureIfNeeded();
            _isLocalProcessing = false;
            ShowProcessingOverlay = false;
            CloseAction?.Invoke();
        }
    }

    /// <summary>Renders the selection, closes the snip overlay, and hands the PNG off to the
    /// app-lifetime MainWindowViewModel for a fire-and-forget Imgur upload — the fullscreen overlay
    /// must never stay open blocking on a network call.</summary>
    private async Task ExecuteUploadAsync()
    {
        if (_isProcessingRecording) return;
        if (CurrentMode != SnipMode.Screenshot) return;
        if (SelectionRect.Width <= 0 || SelectionRect.Height <= 0) return;
        if (_mainVm == null) return;

        // No Client-ID: tell the user before touching the window, so the snip stays open and
        // Copy/Save remain usable.
        if (string.IsNullOrWhiteSpace(_mainVm.ImgurClientId))
        {
            _mainVm.SetStatus("StatusImgurClientIdMissing");
            return;
        }

        await _captureVisibilityCoordinator.HideAndWaitForCaptureAsync(
            HideAction ?? (() => { }));

        try
        {
            _isLocalProcessing = true;
            ShowProcessingOverlay = true;
            IsIndeterminate = true;
            ProcessingText = LocalizationService.Instance["StatusUploading"] ?? "Uploading...";
            using var bitmap = await _captureService.CaptureScreenWithAnnotationsAsync(
                SelectionRect,
                ScreenOffset,
                VisualScaling,
                Annotations,
                GetTranslationSelectionsForCapture(),
                TranslatedBlocks,
                _mainVm.ShowSnipCursor);

            byte[] png;
            using (var image = SkiaSharp.SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
            {
                png = data.ToArray();
            }

            // The capture service is app-lifetime (owned by the factory), so its text-clipboard
            // delegate stays valid after this window closes.
            _mainVm.RunImgurUploadAsync(png, _captureService.CopyToClipboardAsync).Forget("Upload.Imgur");
        }
        finally
        {
            PersistTranslatedSelectionsAfterCaptureIfNeeded();
            _isLocalProcessing = false;
            ShowProcessingOverlay = false;
            CloseAction?.Invoke();
        }
    }

    private async Task ExecuteSaveAsync()
    {
        // If recording is active, stop recording instead of saving screenshot
        if (RecState == RecordingState.Recording || RecState == RecordingState.Paused)
        {
            await StopRecording();
            return;
        }

        if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
        {
            await HideOverlayForCaptureUnlessFrozenAsync();

            try
            {
                _isLocalProcessing = true;
                ShowProcessingOverlay = true;
                IsIndeterminate = true;
                ProcessingText = LocalizationService.Instance["StatusSaving"] ?? "Saving...";
                using var bitmap = await CaptureOrCropSelectionAsync(includeAnnotations: true);

                string? savedPath = null;

                if (_mainVm != null && _mainVm.AutoSave)
                {
                    var dir = _mainVm.SaveDirectory;
                    if (string.IsNullOrEmpty(dir))
                    {
                        dir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), "GimmeCapture");
                    }
                    FileLocationService.EnsureDirectory(dir, "SnipCapture.EnsureSaveDirectory");

                    var fileName = CaptureFileNameService.BuildFileName("png", _mainVm.FileNameTemplate);
                    var path = System.IO.Path.Combine(dir, fileName);
                    await _captureService.SaveToFileAsync(bitmap, path);
                    savedPath = path;
                    _mainVm?.SetStatus("StatusSaved");
                    System.Diagnostics.Debug.WriteLine($"Auto-saved to {path}");
                }
                else if (PickSaveFileAction != null)
                {
                    var path = await PickSaveFileAction.Invoke();
                    if (!string.IsNullOrEmpty(path))
                    {
                        await _captureService.SaveToFileAsync(bitmap, path);
                        savedPath = path;
                        _mainVm?.SetStatus("StatusSaved");
                    }
                    System.Diagnostics.Debug.WriteLine($"Saved to {path}");
                }
                else
                {
                    // Fallback
                    var fileName = CaptureFileNameService.BuildFileName("png", _mainVm?.FileNameTemplate);
                    var path = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), fileName);
                    await _captureService.SaveToFileAsync(bitmap, path);
                    savedPath = path;
                }

                if (!string.IsNullOrWhiteSpace(savedPath))
                {
                    _mainVm?.CaptureHistory.AddImageAsync(savedPath).Forget("CaptureHistory.AddImage");
                    if (_mainVm?.RevealAfterSave ?? true)
                    {
                        FileLocationService.RevealInFileExplorer(savedPath);
                    }
                }
            }
            finally
            {
                PersistTranslatedSelectionsAfterCaptureIfNeeded();
                _isLocalProcessing = false;
                ShowProcessingOverlay = false;
                CloseAction?.Invoke();
            }
        }
    }

    private async Task ExecutePinAsync(bool runAI = false, bool initialInteractive = false)
    {
        // During manual scrolling capture the Pin key (F6) finishes the session instead of pinning.
        if (_manualScrollActive)
        {
            FinishManualScrollCapture(cancelled: false);
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[SnipWindowViewModel] Pin() called. runAI={runAI}, SelectionRect={SelectionRect}");
        // Guard: If AI is disabled globally, prevent running it
        if (runAI && (_mainVm == null || !_mainVm.EnableAI))
        {
            runAI = false;
        }

        // Recording mode owns its own recording path. Do not consume a stale
        // LastRecordingPath left by a previously closed SnipWindow.
        if (CurrentMode == SnipMode.Recording)
        {
            bool hasCurrentRecording = !string.IsNullOrEmpty(_currentRecordingPath)
                                       && System.IO.File.Exists(_currentRecordingPath);

            switch (ResolveRecordingPinAction(RecState, CurrentState, hasCurrentRecording))
            {
                case RecordingPinAction.StartRecording:
                    await StartRecording();
                    break;
                case RecordingPinAction.PinRecording:
                    await PinRecording();
                    break;
            }

            return;
        }

        if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
        {
            await RunCaptureActionAsync(
                CaptureMode.Pin,
                () => ExecutePinCaptureAsync(runAI, initialInteractive));
        }
    }

    private async Task ExecutePinCaptureAsync(bool runAI, bool initialInteractive)
    {
        await HideOverlayForCaptureUnlessFrozenAsync();

        try
        {
            using var skBitmap = await CaptureOrCropSelectionAsync(includeAnnotations: true);

            // Convert SKBitmap to Avalonia Bitmap without PNG stream roundtrip
            var avaloniaBitmap = new Avalonia.Media.Imaging.WriteableBitmap(
                new Avalonia.PixelSize(skBitmap.Width, skBitmap.Height),
                new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            using var lockedOut = avaloniaBitmap.Lock();
            unsafe
            {
                Buffer.MemoryCopy(
                    (void*)skBitmap.GetPixels(),
                    (void*)lockedOut.Address,
                    lockedOut.RowBytes * lockedOut.Size.Height,
                    skBitmap.RowBytes * skBitmap.Height);
            }

            // Open Floating Window
            OpenPinWindowAction?.Invoke(avaloniaBitmap, SelectionRect, SelectionBorderColor, SelectionBorderThickness, runAI, initialInteractive, null, 12.0, null);
        }
        finally
        {
            PersistTranslatedSelectionsAfterCaptureIfNeeded();
            CloseAction?.Invoke();
        }
    }

    private Task ExecuteScrollingCapture()
    {
        if (CurrentMode == SnipMode.Recording || CurrentMode == SnipMode.Translation)
        {
            return Task.CompletedTask;
        }

        if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
        {
            // Manual scrolling capture: user scrolls the target by hand, F6 finishes.
            return StartManualScrollCaptureAsync();
        }

        return Task.CompletedTask;
    }

    // Converts a stitched physical-pixel SKBitmap into an Avalonia bitmap and opens it as a
    // pinned floating window (sizing/fit-to-screen handled inside OpenPinWindowAction).
    // The caller owns and disposes the source SKBitmap.
    internal void OpenStitchedPin(SKBitmap skBitmap)
    {
        if (skBitmap == null || skBitmap.Width <= 0 || skBitmap.Height <= 0)
        {
            return;
        }

        var avaloniaBitmap = new Avalonia.Media.Imaging.WriteableBitmap(
            new Avalonia.PixelSize(skBitmap.Width, skBitmap.Height),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);

        using (var lockedOut = avaloniaBitmap.Lock())
        {
            unsafe
            {
                Buffer.MemoryCopy(
                    (void*)skBitmap.GetPixels(),
                    (void*)lockedOut.Address,
                    lockedOut.RowBytes * lockedOut.Size.Height,
                    skBitmap.RowBytes * skBitmap.Height);
            }
        }

        // The stitched bitmap is in physical pixels; present it at logical size so DPI matches. The pin
        // window (viewport) matches the original selection, and the full-height stitch scrolls inside it —
        // so the viewport rect is the selection and the scrollable content is the whole stitched image.
        double scaling = VisualScaling <= 0 ? 1.0 : VisualScaling;
        var viewportRect = new Rect(
            SelectionRect.X,
            SelectionRect.Y,
            SelectionRect.Width,
            SelectionRect.Height);
        var contentSize = new Avalonia.Size(skBitmap.Width / scaling, skBitmap.Height / scaling);

        OpenPinWindowAction?.Invoke(
            avaloniaBitmap, viewportRect, SelectionBorderColor, SelectionBorderThickness, false, false, null, 12.0, contentSize);
    }

    private async Task RunCaptureActionAsync(CaptureMode mode, Func<Task> captureAsync)
    {
        if (_mainVm == null)
        {
            await captureAsync();
            return;
        }

        var result = await _mainVm.RunCaptureActionAsync(mode, captureAsync);
        if (result != CaptureLaunchResult.Launched)
        {
            ShowAction?.Invoke();
            FocusWindowAction?.Invoke();
        }
    }

    private void PersistTranslatedSelectionsAfterCaptureIfNeeded()
    {
        if (CurrentMode != SnipMode.Translation)
        {
            return;
        }

        PersistTranslationSelectionsAction?.Invoke();
    }

    private IReadOnlyList<UserSelectionRect> GetTranslationSelectionsForCapture()
    {
        var detachedSelections = TranslationResultLayer?.GetCaptureSelectionSnapshots(
            ScreenOffset,
            VisualScaling) ?? System.Array.Empty<UserSelectionRect>();

        if (detachedSelections.Count == 0)
        {
            return UserSelections;
        }

        return UserSelections.Concat(detachedSelections).ToList();
    }
}
