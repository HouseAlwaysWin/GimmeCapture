using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using GimmeCapture.Services.Abstractions;

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
            await _captureVisibilityCoordinator.HideAndWaitForCaptureAsync(
                HideAction ?? (() => { }),
                cts.Token);

            _isLocalProcessing = true;
            ShowProcessingOverlay = true;
            IsIndeterminate = true;
            ProcessingText = LocalizationService.Instance["QuickOcrProcessing"];

            using var bitmap = await _captureService.CaptureScreenAsync(
                SelectionRect,
                ScreenOffset,
                VisualScaling,
                includeCursor: false);
            var result = await _quickOcrService.RecognizeAsync(
                bitmap,
                _mainVm.SourceLanguage,
                _mainVm.OcrTextLayout,
                cts.Token);

            switch (result.Status)
            {
                case QuickOcrStatus.Success:
                    await _captureService.CopyToClipboardAsync(result.Text);
                    _mainVm.SetStatus("QuickOcrCopied");
                    break;
                case QuickOcrStatus.ModuleMissing:
                    _mainVm.SetStatus("QuickOcrModuleMissing");
                    _mainVm.RequestOpenModulesAction?.Invoke();
                    break;
                case QuickOcrStatus.NoText:
                    _mainVm.SetStatus("QuickOcrNoText");
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
            CloseAction?.Invoke();
        }
    }

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

    private async Task ExecuteCopyCaptureAsync()
    {
        await _captureVisibilityCoordinator.HideAndWaitForCaptureAsync(
            HideAction ?? (() => { }));

        try
        {
            _isLocalProcessing = true;
            ShowProcessingOverlay = true;
            IsIndeterminate = true;
            ProcessingText = LocalizationService.Instance["StatusProcessing"] ?? "Processing...";
            using var bitmap = await _captureService.CaptureScreenWithAnnotationsAsync(
                SelectionRect,
                ScreenOffset,
                VisualScaling,
                Annotations,
                GetTranslationSelectionsForCapture(),
                TranslatedBlocks,
                _mainVm?.ShowSnipCursor ?? false);
            await _captureService.CopyToClipboardAsync(bitmap);
            _mainVm?.SetStatus("StatusCopied");

            // Copies are clipboard-only by default; when history is on, persist a managed copy so it
            // shows up in the history panel (and is cleaned up on remove/prune).
            if (_mainVm != null && _mainVm.EnableHistory)
            {
                var copyPath = _mainVm.CaptureHistory.CreateManagedCapturePath("png");
                await _captureService.SaveToFileAsync(bitmap, copyPath);
                _mainVm.CaptureHistory.AddImageAsync(copyPath).Forget("CaptureHistory.AddCopy");
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
            await _captureVisibilityCoordinator.HideAndWaitForCaptureAsync(
                HideAction ?? (() => { }));

            try
            {
                _isLocalProcessing = true;
                ShowProcessingOverlay = true;
                IsIndeterminate = true;
                ProcessingText = LocalizationService.Instance["StatusSaving"] ?? "Saving...";
                using var bitmap = await _captureService.CaptureScreenWithAnnotationsAsync(
                    SelectionRect,
                    ScreenOffset,
                    VisualScaling,
                    Annotations,
                    GetTranslationSelectionsForCapture(),
                    TranslatedBlocks,
                    _mainVm?.ShowSnipCursor ?? false);

                string? savedPath = null;

                if (_mainVm != null && _mainVm.AutoSave)
                {
                    var dir = _mainVm.SaveDirectory;
                    if (string.IsNullOrEmpty(dir))
                    {
                        dir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), "GimmeCapture");
                    }
                    FileLocationService.EnsureDirectory(dir, "SnipCapture.EnsureSaveDirectory");

                    var fileName = CaptureFileNameService.BuildFileName("png");
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
                    var fileName = CaptureFileNameService.BuildFileName("png");
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
        await _captureVisibilityCoordinator.HideAndWaitForCaptureAsync(
            HideAction ?? (() => { }));

        try
        {
            using var skBitmap = await _captureService.CaptureScreenWithAnnotationsAsync(
                SelectionRect,
                ScreenOffset,
                VisualScaling,
                Annotations,
                GetTranslationSelectionsForCapture(),
                TranslatedBlocks,
                _mainVm?.ShowSnipCursor ?? false);

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
            OpenPinWindowAction?.Invoke(avaloniaBitmap, SelectionRect, SelectionBorderColor, SelectionBorderThickness, runAI, initialInteractive, null, 12.0);
        }
        finally
        {
            PersistTranslatedSelectionsAfterCaptureIfNeeded();
            CloseAction?.Invoke();
        }
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
        var detachedSelections = TranslationResultLayerManager.GetCaptureSelectionSnapshots(
            ScreenOffset,
            VisualScaling);

        if (detachedSelections.Count == 0)
        {
            return UserSelections;
        }

        return UserSelections.Concat(detachedSelections).ToList();
    }
}
