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
            _isLocalProcessing = true;
            ShowProcessingOverlay = true;
            IsIndeterminate = true;
            ProcessingText = LocalizationService.Instance["QuickOcrProcessing"];

            // Only a LIVE commit hides the overlay to grab clean pixels; a frozen one crops the still in place and
            // leaves the overlay up (see OverlaySurface.CommitCoreAsync). Read before the commit, because that is
            // the state the commit will act on.
            bool overlayWillBeHidden = !Surface.IsFrozen;

            using var bitmap = await Surface.CommitPlainAsync(cts.Token);

            // The standalone "recognizing…" spinner stands in for the in-overlay one ONLY while the overlay is
            // hidden — it hosts ShowProcessingOverlay, so hiding it takes that spinner away and the multi-second
            // recognition would look like a freeze. While frozen the overlay is still up and already showing its
            // own spinner, so this would be a second one on screen. Shown AFTER the capture either way, so it is
            // never grabbed into the OCR image; OCR runs on a background thread so the spinner stays animated.
            if (overlayWillBeHidden)
            {
                ShowProcessingWindowAction?.Invoke();
            }

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
    /// Toolbar "freeze screen" button: toggles THIS overlay's freeze state live, with immediate effect — unlike the
    /// FreezeScreenOnScreenshot setting, which only decides the initial state of the NEXT capture. The surface owns
    /// the mode gate, the grab, the ownership transfer and the fall-back-to-live when the still can't be shown;
    /// all that is left here is re-running the Win32 region logic so the overlay becomes opaque (or see-through).
    /// NOTE: freezing here happens AFTER the overlay is already up, so it can't recover a shell "light dismiss"
    /// popup (tray flyout / Start menu) the overlay already closed — that still needs the setting ON so the still
    /// predates the overlay. This button freezes/holds whatever is currently on screen.
    /// </summary>
    internal async Task ToggleFreezeFrameLiveAsync()
    {
        if (Surface.IsFrozen)
        {
            Surface.ReturnToLive();
        }
        else
        {
            await Surface.FreezeAsync(Geometry);
        }

        RefreshInteractionRegion();
    }

    /// <summary>
    /// The shared capture ritual: spinner up, commit the selection, hand the bitmap to <paramref name="consume"/>,
    /// then tear down and close. Copy / Save / Pin used to each re-implement these five beats, and whichever one
    /// drifted silently bypassed freeze-frame — so the ritual being a single entry point is the point, not a
    /// line-count saving.
    /// </summary>
    /// <param name="statusKey">Localization key for the processing spinner, or null for no spinner (Pin).</param>
    private async Task RunCaptureCommitAsync(string? statusKey, Func<SKBitmap, Task> consume)
    {
        bool showsSpinner = statusKey != null;
        try
        {
            if (showsSpinner)
            {
                _isLocalProcessing = true;
                ShowProcessingOverlay = true;
                IsIndeterminate = true;
                ProcessingText = LocalizationService.Instance[statusKey!] ?? "Processing...";
            }

            // The spinner is safe to raise BEFORE the commit: on the live path the surface hides the overlay
            // before grabbing, and on the frozen path the pixels come from a still, so it can't be captured either.
            using var bitmap = await Surface.CommitAsync();
            await consume(bitmap);
        }
        finally
        {
            PersistTranslatedSelectionsAfterCaptureIfNeeded();
            if (showsSpinner)
            {
                _isLocalProcessing = false;
                ShowProcessingOverlay = false;
            }
            CloseAction?.Invoke();
        }
    }

    private Task ExecuteCopyCaptureAsync() => RunCaptureCommitAsync("StatusProcessing", async bitmap =>
    {
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
    });

    // Test hook: drives the private copy-capture flow directly (the production trigger is a ReactiveCommand), so
    // the shared commit ritual can be unit-tested without a UI thread — specifically that a frozen commit reads
    // the still instead of hiding the overlay and grabbing live.
    internal Task RunCopyCaptureForTestAsync() => ExecuteCopyCaptureAsync();

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
            await RunCaptureCommitAsync("StatusSaving", async bitmap =>
            {
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
            });
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

    // Pin passes no status key: it opens a floating window immediately and never showed a processing spinner.
    private Task ExecutePinCaptureAsync(bool runAI, bool initialInteractive) =>
        RunCaptureCommitAsync(null, skBitmap =>
        {
            var avaloniaBitmap = ToAvaloniaBitmap(skBitmap);
            OpenPinWindowAction?.Invoke(avaloniaBitmap, SelectionRect, SelectionBorderColor, SelectionBorderThickness, runAI, initialInteractive, null, 12.0, null);
            return Task.CompletedTask;
        });

    /// <summary>Copies a physical-pixel SKBitmap into an Avalonia bitmap without a PNG stream roundtrip.
    /// The caller keeps ownership of <paramref name="skBitmap"/>.</summary>
    private static Avalonia.Media.Imaging.WriteableBitmap ToAvaloniaBitmap(SKBitmap skBitmap)
    {
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

        return avaloniaBitmap;
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

        var avaloniaBitmap = ToAvaloniaBitmap(skBitmap);

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
