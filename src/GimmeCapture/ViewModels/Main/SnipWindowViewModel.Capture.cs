using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using System;
using System.Threading.Tasks;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
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
            HideAction?.Invoke();
            await Task.Delay(200); // Wait for UI update

            try
            {
                _isLocalProcessing = true;
                ShowProcessingOverlay = true;
                IsIndeterminate = true;
                ProcessingText = LocalizationService.Instance["StatusProcessing"] ?? "Processing...";
                using var bitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, ScreenOffset, VisualScaling, Annotations, UserSelections, TranslatedBlocks, _mainVm?.ShowSnipCursor ?? false);
                await _captureService.CopyToClipboardAsync(bitmap);
                _mainVm?.SetStatus("StatusCopied");
            }
            finally
            {
                _isLocalProcessing = false;
                ShowProcessingOverlay = false;
                CloseAction?.Invoke();
            }
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
            HideAction?.Invoke();
            await Task.Delay(200); // Wait for UI update

            try
            {
                _isLocalProcessing = true;
                ShowProcessingOverlay = true;
                IsIndeterminate = true;
                ProcessingText = LocalizationService.Instance["StatusSaving"] ?? "Saving...";
                using var bitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, ScreenOffset, VisualScaling, Annotations, UserSelections, TranslatedBlocks, _mainVm?.ShowSnipCursor ?? false);

                string? savedPath = null;

                if (_mainVm != null && _mainVm.AutoSave)
                {
                    var dir = _mainVm.SaveDirectory;
                    if (string.IsNullOrEmpty(dir))
                    {
                        dir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), "GimmeCapture");
                    }
                    try { System.IO.Directory.CreateDirectory(dir); } catch { }

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
                    FileLocationService.RevealInFileExplorer(savedPath);
                }
            }
            finally
            {
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

        // If recording is active or we are in recording mode with a valid path, use PinRecording
        if (CurrentMode == SnipMode.Recording)
        {
            var lastPath = _recordingService?.LastRecordingPath;
            bool hasVideo = !string.IsNullOrEmpty(lastPath) && System.IO.File.Exists(lastPath);

            if (RecState == RecordingState.Recording || RecState == RecordingState.Paused || hasVideo)
            {
                await PinRecording();
                return;
            }
        }

        if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
        {
            HideAction?.Invoke();
            await Task.Delay(200); // Wait for UI update

            try
            {
                using var skBitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, ScreenOffset, VisualScaling, Annotations, UserSelections, TranslatedBlocks, _mainVm?.ShowSnipCursor ?? false);

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
                CloseAction?.Invoke();
            }
        }
    }
}
