using Avalonia;
using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using System.Reactive.Linq;
using System;
using System.IO;
using SkiaSharp;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.Services.Core.Rendering;
using System.Linq;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingVideoViewModel
{
    // Base has CloseAction, SaveAction.
    // Base has SelectionRect, IsSelectionActive.
    // Base has CloseCommand, SaveCommand.

    private void InitializeActionCommands()
    {
        // CloseCommand is in Base.

        var canUseSelection = this.WhenAnyValue(x => x.IsSelectionActive);
        // Crop replaces (closes the source window); PinSelection is additive (keeps it).
        CropCommand = ReactiveCommand.CreateFromTask(CropAsync, canUseSelection);
        PinSelectionCommand = ReactiveCommand.CreateFromTask(PinSelectionAsync, canUseSelection);

        CopyCommand = ReactiveCommand.CreateFromTask(CopyAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        FreezeFrameCommand = ReactiveCommand.CreateFromTask(FreezeFrameAsync);
    }

    /// <summary>
    /// Pauses on the current frame and opens it as a new plain image pin (no AI) — just the current frame
    /// pinned out. The result is a still image, not a video edit; the user can enter point-removal /
    /// background-removal from the pinned image's own toolbar if they want to cut an object out.
    /// </summary>
    private async Task FreezeFrameAsync()
    {
        if (FreezeFrameToImagePinAction == null) return;

        // Pause so the displayed frame is stable while we snapshot it.
        if (_isPlaybackActive)
        {
            _isPlaybackActive = false;
            CancelPlaybackInBackground();
            this.RaisePropertyChanged(nameof(IsPlaying));
        }

        // GetFlattenedBitmapAsync returns a fresh detached bitmap (a copy of the current frame with any
        // annotations baked in), safe to hand to the image pin even as the player recycles its buffer.
        var snapshot = await GetFlattenedBitmapAsync();
        if (snapshot == null) return;

        FreezeFrameToImagePinAction.Invoke(snapshot);
    }

    private Task CropAsync() => ExportAndPinSelectionAsync(closeSource: true);

    private Task PinSelectionAsync() => ExportAndPinSelectionAsync(closeSource: false);

    /// <summary>
    /// Exports the current selection (cropped, honoring the active trim range) to a new
    /// video file and opens it as a new pinned floating window. Annotations are not burnt
    /// in, mirroring the image crop behavior (their coordinates would not align post-crop).
    /// </summary>
    private async Task ExportAndPinSelectionAsync(bool closeSource)
    {
        if (!IsSelectionActive || IsProcessing) return;
        if (OpenPinnedVideoWindowAction == null) return;
        if (!TryGetSelectionPixelRect(out var crop)) return;

        IsProcessing = true;
        ProcessingText = LocalizationService.Instance["StatusProcessing"] ?? "Processing...";
        try
        {
            string? exported = await ExportCroppedRegionAsync(crop, Path.GetExtension(VideoPath));
            if (string.IsNullOrEmpty(exported) || !File.Exists(exported))
            {
                System.Diagnostics.Debug.WriteLine("[Crop] Export produced no output.");
                return;
            }

            // Move the export to a stable per-pin temp file so the GUID export dir can be reused/cleaned.
            string pinDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Pins");
            Directory.CreateDirectory(pinDir);
            string pinnedPath = Path.Combine(pinDir, $"crop_{Guid.NewGuid():N}{Path.GetExtension(exported)}");
            File.Copy(exported, pinnedPath, true);

            OpenPinnedVideoWindowAction?.Invoke(
                pinnedPath,
                crop.Width,
                crop.Height,
                crop.Width,
                crop.Height,
                BorderColor,
                BorderThickness,
                HidePinDecoration,
                HidePinBorder);

            if (closeSource)
            {
                CloseAction?.Invoke();
            }
            else
            {
                SelectionRect = new Avalonia.Rect();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Crop] Failed: {ex}");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    /// <summary>
    /// Maps the display-space <see cref="FloatingWindowViewModelBase.SelectionRect"/> to even,
    /// in-bounds pixel coordinates suitable for an ffmpeg <c>crop</c> filter / yuv420p output.
    /// </summary>
    private bool TryGetSelectionPixelRect(out PixelRect crop)
    {
        crop = SelectionToPixelCropRect(SelectionRect, DisplayWidth, DisplayHeight, OriginalWidth, OriginalHeight);
        return crop.Width >= 2 && crop.Height >= 2;
    }

    internal static PixelRect SelectionToPixelCropRect(
        Avalonia.Rect selection,
        double displayWidth,
        double displayHeight,
        double originalWidth,
        double originalHeight)
    {
        int videoW = Math.Max(0, (int)Math.Round(originalWidth));
        int videoH = Math.Max(0, (int)Math.Round(originalHeight));
        if (videoW < 2 || videoH < 2) return new PixelRect(0, 0, 0, 0);

        double scaleX = displayWidth > 0 ? originalWidth / displayWidth : 1.0;
        double scaleY = displayHeight > 0 ? originalHeight / displayHeight : 1.0;

        int x = Math.Clamp((int)Math.Round(selection.X * scaleX), 0, videoW - 2);
        int y = Math.Clamp((int)Math.Round(selection.Y * scaleY), 0, videoH - 2);

        // Even width/height (yuv420p), clamped so x+w / y+h stay in bounds.
        int w = (int)Math.Round(selection.Width * scaleX);
        int h = (int)Math.Round(selection.Height * scaleY);
        w = Math.Min(w, videoW - x);
        h = Math.Min(h, videoH - y);
        w = (w / 2) * 2;
        h = (h / 2) * 2;

        return new PixelRect(x, y, Math.Max(0, w), Math.Max(0, h));
    }

    private async Task<string?> ExportCroppedRegionAsync(PixelRect crop, string? targetExtension)
    {
        if (string.IsNullOrEmpty(VideoPath) || !File.Exists(VideoPath)) return null;

        string ext = targetExtension ?? Path.GetExtension(VideoPath);
        if (!ext.StartsWith(".")) ext = "." + ext;

        // In-process crop + trim (no ffmpeg.exe) for plain video containers (mp4/mkv/mov).
        if (LibavClipExporter.ContainerForExtension(ext) == null)
        {
            AppLog.Error("FloatingVideo.ExportCrop", new NotSupportedException(
                $"Crop export to '{ext}' is not supported in-process yet."));
            return null;
        }

        // Kept runs (or the whole clip when no cut), plus the pixel crop rect.
        var runs = KeptRuns();
        IReadOnlyList<VideoEditSegment> segs = runs.Length > 0
            ? runs
            : VideoSegmentEditor.FromTrim(0, _totalDuration.TotalSeconds, _totalDuration.TotalSeconds);
        var ranges = segs
            .Select(r => new LibavClipExporter.SourceRange(r.SourceStart, r.SourceEnd, r.Speed))
            .ToList();
        var editCrop = new VideoEditCrop(crop.X, crop.Y, crop.Width, crop.Height);
        VideoQuality quality = _appSettingsService?.Settings.VideoQuality ?? VideoQuality.Medium;

        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string outputPath = Path.Combine(tempDir, "output" + ext);

        IsExporting = true;
        try
        {
            bool ok = await Task.Run(() => LibavClipExporter.TryExport(VideoPath, ranges, outputPath, quality, editCrop));
            if (ok)
            {
                return outputPath;
            }

            AppLog.Error("FloatingVideo.ExportCrop", new Exception("In-process crop export produced no output file."));
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("FloatingVideo.ExportCrop", ex);
            return null;
        }
        finally
        {
            IsExporting = false;
        }
    }
    
    private async Task CopyAsync()
    {
        if (IsProcessing) return;
        IsProcessing = true;

        // Use Dispatcher.Post to ensure we run AFTER the ContextMenu has fully closed.
        // This is the "standard" way to avoid PlatformImpl null or focus issues.
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                ProcessingText = LocalizationService.Instance["StatusExportingVideo"] ?? "Exporting Video...";
                bool hasAnnotations = Annotations.AsValueEnumerable().Any();
                bool cutRequested = EditChangesOutput;

                // Trim/cut copy runs fully in-process (no ffmpeg.exe) for plain video sources.
                string copyExt = Path.GetExtension(VideoPath).ToLowerInvariant();
                if (LibavClipExporter.ContainerForExtension(copyExt) == null) copyExt = ".mp4";
                if (cutRequested && CanExportTrimInProcess(hasAnnotations, copyExt))
                {
                    var trimmed = await ExportTrimmedInProcessAsync(copyExt);
                    if (!string.IsNullOrEmpty(trimmed) && System.IO.File.Exists(trimmed))
                    {
                        // Put the trimmed clip on the clipboard. The thumbnail is optional — never let a
                        // null/failed bitmap stop the file from being copied (a stale prior clipboard entry
                        // would otherwise paste the whole video).
                        var thumb = await GetFlattenedBitmapAsync() ?? VideoBitmap;
                        if (thumb != null)
                        {
                            await _clipboardService.CopyFileAndImageAsync(trimmed, thumb);
                        }
                        else
                        {
                            await _clipboardService.CopyFileAsync(trimmed);
                        }
                        AppLog.Information($"FloatingVideo.Copy trimmed clip -> {Path.GetFileName(trimmed)} ({new FileInfo(trimmed).Length} bytes)");

                        // Also persist the copied clip into History (like image copy) so it shows up there.
                        if (AddClipToHistoryAsync != null)
                        {
                            await AddClipToHistoryAsync(trimmed, (int)OriginalWidth, (int)OriginalHeight);
                        }
                        return;
                    }

                    ProcessingText = LocalizationService.Instance["StatusExportFailed"] ?? "Export failed";
                    await Task.Delay(2000);
                    return;
                }

                // Annotation/redaction burn-in copy runs in-process (Skia per frame), honoring any cut.
                if (hasAnnotations && LibavClipExporter.ContainerForExtension(copyExt) != null)
                {
                    var burnt = await ExportAnnotatedInProcessAsync(copyExt);
                    if (!string.IsNullOrEmpty(burnt) && System.IO.File.Exists(burnt))
                    {
                        var thumb = await GetFlattenedBitmapAsync() ?? VideoBitmap;
                        if (thumb != null)
                        {
                            await _clipboardService.CopyFileAndImageAsync(burnt, thumb);
                        }
                        else
                        {
                            await _clipboardService.CopyFileAsync(burnt);
                        }
                        return;
                    }

                    ProcessingText = LocalizationService.Instance["StatusExportFailed"] ?? "Export failed";
                    await Task.Delay(2000);
                    return;
                }

                // Trim/cut and annotation burn-in are handled fully in-process above and always return.
                // If a cut/annotation ever reaches here, surface the failure rather than silently putting
                // the untrimmed original on the clipboard.
                if (cutRequested || hasAnnotations)
                {
                    ProcessingText = LocalizationService.Instance["StatusExportFailed"] ?? "Export failed";
                    await Task.Delay(2000);
                    return;
                }

                if (!string.IsNullOrEmpty(VideoPath) && System.IO.File.Exists(VideoPath))
                {
                    await _clipboardService.CopyFileAsync(VideoPath);
                }
                else
                {
                    var bitmapToCopy = await GetFlattenedBitmapAsync();
                    if (bitmapToCopy != null)
                    {
                        await _clipboardService.CopyImageAsync(bitmapToCopy);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error copying video: {ex}");
            }
            finally
            {
                IsProcessing = false;
            }
        });
        
        await Task.CompletedTask;
    }

    // Trim/cut export is supported in-process when there are no annotations to burn in and the target is
    // a plain video container (mp4/mkv/mov). GIF/WebM and annotation/crop burn-in are not migrated yet.
    private bool CanExportTrimInProcess(bool hasAnnotations, string targetExt)
        => !hasAnnotations && LibavClipExporter.ContainerForExtension(targetExt) != null;

    /// <summary>
    /// Frame-accurate trim/concat of the kept runs into a temp file using the in-process libav exporter
    /// (no ffmpeg.exe). Returns the temp output path, or null on failure (logged).
    /// </summary>
    private async Task<string?> ExportTrimmedInProcessAsync(string targetExtension)
    {
        // Redaction alone (no cut/speed) still needs re-encoding the whole clip, so fall back to a
        // full-clip range when there are no kept runs but there is something to burn in.
        var runs = KeptRuns();
        var redactionComposite = BuildRedactionComposite();
        IReadOnlyList<VideoEditSegment> segs = runs.Length > 0
            ? runs
            : VideoSegmentEditor.FromTrim(0, _totalDuration.TotalSeconds, _totalDuration.TotalSeconds);
        var ranges = segs
            .Select(r => new LibavClipExporter.SourceRange(r.SourceStart, r.SourceEnd, r.Speed))
            .ToList();
        if (ranges.Count == 0) return null;

        string ext = targetExtension.StartsWith('.') ? targetExtension : "." + targetExtension;
        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string outputPath = Path.Combine(tempDir, "output" + ext);
        VideoQuality quality = _appSettingsService?.Settings.VideoQuality ?? VideoQuality.Medium;

        IsExporting = true;
        try
        {
            bool ok = await Task.Run(() => LibavClipExporter.TryExport(VideoPath, ranges, outputPath, quality, frameComposite: redactionComposite));
            if (ok)
            {
                return outputPath;
            }

            AppLog.Error("FloatingVideo.ExportTrim", new Exception("In-process clip export produced no output file."));
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("FloatingVideo.ExportTrim", ex);
            return null;
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>
    /// In-process export that burns the annotations/redactions into every frame via SkiaSharp (no
    /// ffmpeg.exe), honoring any cut. Returns a temp output path, or null on failure.
    /// </summary>
    private async Task<string?> ExportAnnotatedInProcessAsync(string targetExtension)
    {
        string ext = targetExtension.StartsWith('.') ? targetExtension : "." + targetExtension;
        if (LibavClipExporter.ContainerForExtension(ext) == null)
        {
            AppLog.Error("FloatingVideo.ExportAnnotated", new NotSupportedException(
                $"Annotation burn-in to '{ext}' is not supported in-process yet."));
            return null;
        }

        var runs = KeptRuns();
        IReadOnlyList<VideoEditSegment> segs = runs.Length > 0
            ? runs
            : VideoSegmentEditor.FromTrim(0, _totalDuration.TotalSeconds, _totalDuration.TotalSeconds);
        var ranges = segs
            .Select(r => new LibavClipExporter.SourceRange(r.SourceStart, r.SourceEnd, r.Speed))
            .ToList();

        // Snapshot annotations + display coords so the per-frame callback (runs on a worker thread) is safe.
        var annotationsSnapshot = Annotations.ToList();
        double displayW = DisplayWidth;
        double displayH = DisplayHeight;
        var redactionComposite = BuildRedactionComposite();
        Action<SkiaSharp.SKBitmap, double> composite = (sk, t) =>
        {
            AnnotationRenderService.Shared.RenderAnnotationsToBitmap(sk, annotationsSnapshot, displayW, displayH, sk.Width, sk.Height);
            redactionComposite?.Invoke(sk, t);
        };

        VideoQuality annQuality = _appSettingsService?.Settings.VideoQuality ?? VideoQuality.Medium;
        string annTempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(annTempDir);
        string annOutputPath = Path.Combine(annTempDir, "output" + ext);

        IsExporting = true;
        try
        {
            bool ok = await Task.Run(() => LibavClipExporter.TryExport(VideoPath, ranges, annOutputPath, annQuality, frameComposite: composite));
            if (ok)
            {
                return annOutputPath;
            }

            AppLog.Error("FloatingVideo.ExportAnnotated", new Exception("In-process annotated export produced no output file."));
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("FloatingVideo.ExportAnnotated", ex);
            return null;
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>
    /// In-process GIF/WebM export: trims the kept runs to a temp mp4 (when a cut was made), then runs the
    /// existing GIF/WebM transcoders on it (WebM re-muxes Opus audio when present). Returns a temp output
    /// path, or null on failure. No ffmpeg.exe.
    /// </summary>
    private async Task<string?> ExportGifWebmInProcessAsync(string targetExt)
    {
        bool isGif = targetExt.Equals(".gif", StringComparison.OrdinalIgnoreCase);
        VideoQuality quality = _appSettingsService?.Settings.VideoQuality ?? VideoQuality.Medium;
        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        IsExporting = true;
        try
        {
            // The transcode source is a trimmed mp4 when the edit changes the output (cut or speed),
            // else the original recording.
            string source;
            if (EditChangesOutput)
            {
                var trimmed = await ExportTrimmedInProcessAsync(".mp4");
                if (string.IsNullOrEmpty(trimmed) || !File.Exists(trimmed)) return null;
                source = trimmed;
            }
            else
            {
                source = VideoPath;
            }

            string outputPath = Path.Combine(tempDir, "output" + targetExt);

            if (isGif)
            {
                (int gifFps, int maxWidth) = LibavGifTranscoder.QualityLadder(quality);
                await Task.Run(() => LibavGifTranscoder.TranscodeToGif(source, outputPath, gifFps, maxWidth));
                return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0 ? outputPath : null;
            }

            // WebM: VP9 video (CFR at the source fps so duration/speed are preserved), then Opus mux.
            int webmFps = Math.Clamp(LibavClipExporter.ProbeFps(source), 1, 60);
            string videoOnly = Path.Combine(tempDir, "video.webm");
            await Task.Run(() => LibavWebmTranscoder.TranscodeToWebm(source, videoOnly, webmFps, quality));
            if (!File.Exists(videoOnly) || new FileInfo(videoOnly).Length == 0) return null;

            if (await EnsureSourceHasAudioAsync())
            {
                try
                {
                    var pcm = LibavPinAudioPcmDecoder.Decode(source, 0);
                    if (pcm.PcmBytes.Length > 0)
                    {
                        string wav = Path.Combine(tempDir, "audio.wav");
                        using (var writer = new NAudio.Wave.WaveFileWriter(wav, pcm.WaveFormat))
                        {
                            writer.Write(pcm.PcmBytes, 0, pcm.PcmBytes.Length);
                        }

                        await Task.Run(() => LibavWebmTranscoder.MuxWebmWithOpus(videoOnly, wav, outputPath, quality));
                        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0) return outputPath;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warning("FloatingVideo.ExportWebmAudio", ex); // fall back to video-only
                }
            }

            File.Copy(videoOnly, outputPath, true);
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0 ? outputPath : null;
        }
        catch (Exception ex)
        {
            AppLog.Error("FloatingVideo.ExportGifWebm", ex);
            return null;
        }
        finally
        {
            IsExporting = false;
        }
    }

    private async Task SaveAsync()
    {
        if (PickSaveFileAction == null || IsProcessing) return;

        // IMPORTANT: Let the UI event finish (menu close) before opening another modal dialog.
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            var targetPath = await PickSaveFileAction.Invoke();
            if (string.IsNullOrEmpty(targetPath)) return;

            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["StatusProcessing"] ?? "Processing...";

            try
            {
                bool hasAnnotations = Annotations.AsValueEnumerable().Any();
                string sourceExt = Path.GetExtension(VideoPath).ToLowerInvariant();
                string targetExt = Path.GetExtension(targetPath).ToLowerInvariant();
                bool needsConversion = sourceExt != targetExt;
                bool cutRequested = EditChangesOutput;

                // Trim/cut export runs fully in-process (no ffmpeg.exe) for plain video targets.
                if (cutRequested && CanExportTrimInProcess(hasAnnotations, targetExt))
                {
                    var trimmed = await ExportTrimmedInProcessAsync(targetExt);
                    if (!string.IsNullOrEmpty(trimmed) && System.IO.File.Exists(trimmed))
                    {
                        System.IO.File.Copy(trimmed, targetPath, true);
                        FileLocationService.RevealInFileExplorer(targetPath);
                        return;
                    }

                    ProcessingText = LocalizationService.Instance["StatusExportFailed"] ?? "Export failed";
                    await Task.Delay(2000);
                    return;
                }

                // Annotation/redaction burn-in runs in-process (Skia per frame), honoring any cut.
                if (hasAnnotations && LibavClipExporter.ContainerForExtension(targetExt) != null)
                {
                    var burnt = await ExportAnnotatedInProcessAsync(targetExt);
                    if (!string.IsNullOrEmpty(burnt) && System.IO.File.Exists(burnt))
                    {
                        System.IO.File.Copy(burnt, targetPath, true);
                        FileLocationService.RevealInFileExplorer(targetPath);
                        return;
                    }

                    ProcessingText = LocalizationService.Instance["StatusExportFailed"] ?? "Export failed";
                    await Task.Delay(2000);
                    return;
                }

                // GIF/WebM export also runs in-process (no ffmpeg.exe): trim to mp4 if a cut was made,
                // then transcode with the existing GIF/WebM encoders. Annotations excluded (handled above).
                if (!hasAnnotations && (targetExt == ".gif" || targetExt == ".webm"))
                {
                    var produced = await ExportGifWebmInProcessAsync(targetExt);
                    if (!string.IsNullOrEmpty(produced) && System.IO.File.Exists(produced))
                    {
                        System.IO.File.Copy(produced, targetPath, true);
                        FileLocationService.RevealInFileExplorer(targetPath);
                        return;
                    }

                    ProcessingText = LocalizationService.Instance["StatusExportFailed"] ?? "Export failed";
                    await Task.Delay(2000);
                    return;
                }

                // Trim/cut, annotation burn-in, and GIF/WebM are all handled in-process above. Anything
                // left that still requested an edit (e.g. annotations to a GIF/WebM target, which the
                // in-process paths don't cover) can no longer be produced now that the legacy ffmpeg.exe
                // path is gone — surface the failure rather than silently writing the unedited original.
                // A plain container change with no edits (needsConversion only) falls through to the copy.
                if (cutRequested || hasAnnotations)
                {
                    ProcessingText = LocalizationService.Instance["StatusExportFailed"] ?? "Export failed";
                    await Task.Delay(2000);
                    return;
                }

                if (System.IO.File.Exists(VideoPath))
                {
                    System.IO.File.Copy(VideoPath, targetPath, true);
                    FileLocationService.RevealInFileExplorer(targetPath);
                }
                else
                {
                    var bitmap = await GetFlattenedBitmapAsync();
                    if (bitmap != null)
                    {
                        using var stream = new System.IO.FileStream(targetPath, System.IO.FileMode.Create);
                        bitmap.Save(stream);
                        FileLocationService.RevealInFileExplorer(targetPath);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving video: {ex}");
                ProcessingText = "Save Failed: " + ex.Message;
                IsProcessing = true;
                await Task.Delay(2000);
            }
            finally
            {
                IsProcessing = false;
            }
        });
        
        await Task.CompletedTask;
    }

    private async Task<Bitmap?> GetFlattenedBitmapAsync()
    {
        if (VideoBitmap == null) return null;
        
        return await Task.Run(() => 
        {
            try 
            {
                if (!FloatingBitmapConversionHelper.TryCopyToSkBitmap(VideoBitmap, out var skBitmap, out _)
                    || skBitmap == null)
                    return null;

                using (skBitmap)
                {
                    AnnotationRenderService.Shared.RenderAnnotationsToBitmap(
                        skBitmap,
                        Annotations,
                        DisplayWidth,
                        DisplayHeight,
                        skBitmap.Width,
                        skBitmap.Height);

                    if (!FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(skBitmap, out var detachedBitmap, out _))
                        return null;

                    return detachedBitmap;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error flattening video frame: {ex}");
                return null;
            }
        });
    }
}
